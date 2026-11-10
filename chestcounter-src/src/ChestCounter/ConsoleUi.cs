using System.Text;

namespace ChestCounter;

/// <summary>
/// Interactive console helpers: screens that replace each other, arrow-key menus, a flicker-free full-screen frame
/// and a progress bar. Everything falls back to plain text when the console is redirected (no keyboard / no cursor).
/// All drawing goes to the real console, even while <see cref="ActivityLog"/> captures what the rest of the tool prints.
/// </summary>
public static class ConsoleUi
{
    /// <summary>The real console output, captured before anything replaces Console.Out.</summary>
    public static readonly TextWriter Out = Console.Out;

    // Explicit static constructor: Out is read at first use (after the UTF-8 output encoding is set), not earlier.
    static ConsoleUi() { }

    /// <summary>Where keys come from: the console, or the queue fed by the keyboard thread once it runs.</summary>
    public static Func<ConsoleKeyInfo> ReadKey { get; set; } = () => Console.ReadKey(intercept: true);

    /// <summary>True when keys can be read and the cursor moved.</summary>
    public static bool Interactive { get; } = !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static int Width
    {
        get
        {
            try { return Math.Max(40, Console.WindowWidth); } catch { return 100; }
        }
    }

    public static int Height
    {
        get
        {
            try { return Math.Max(15, Console.WindowHeight); } catch { return 30; }
        }
    }

    private static void Write(string text, ConsoleColor? fg = null, ConsoleColor? bg = null)
    {
        if (fg is { } f) Console.ForegroundColor = f;
        if (bg is { } b) Console.BackgroundColor = b;
        Out.Write(text);
        if (fg != null || bg != null) Console.ResetColor();
    }

    private static void WriteLine(string text = "", ConsoleColor? fg = null)
    {
        Write(text, fg);
        Out.WriteLine();
    }

    /// <summary>Makes the window at least this big (when the screen allows it): the main screen needs some room.</summary>
    public static void EnsureSize(int columns, int rows)
    {
        if (!Interactive || !OperatingSystem.IsWindows() || !OwnConsoleWindow())
        {
            return; // a terminal (Windows Terminal…) sizes its own window: resizing the buffer would break the layout
        }
        try
        {
            var w = Math.Min(Math.Max(Console.WindowWidth, columns), Console.LargestWindowWidth);
            var h = Math.Min(Math.Max(Console.WindowHeight, rows), Console.LargestWindowHeight - 1);
            if (w == Console.WindowWidth && h == Console.WindowHeight)
            {
                return;
            }
            if (Console.BufferWidth < w) Console.BufferWidth = w;
            if (Console.BufferHeight < h) Console.BufferHeight = h;
            Console.SetWindowSize(w, h);
        }
        catch
        {
            // Windows Terminal and some hosts decide the size themselves
        }
    }

    /// <summary>True for the classic console window (conhost), whose size the tool may set.</summary>
    private static bool OwnConsoleWindow()
    {
        try
        {
            var hwnd = GetConsoleWindow();
            var name = new StringBuilder(64);
            return hwnd != IntPtr.Zero && GetClassNameW(hwnd, name, name.Capacity) > 0 && name.ToString() == "ConsoleWindowClass";
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    /// <summary>Clears the window and draws the title bar.</summary>
    public static void Screen(string title, string? step = null)
    {
        if (Interactive)
        {
            try { Console.Clear(); } catch { /* not a real console */ }
        }
        Frames.Invalidate();
        WriteLine();
        Write($"  {title}", ConsoleColor.Yellow);
        if (step != null)
        {
            Write($"   {step}", ConsoleColor.DarkGray);
        }
        WriteLine();
        WriteLine("  " + new string('─', Math.Min(Width - 4, 72)), ConsoleColor.DarkGray);
        WriteLine();
    }

    /// <summary>Window title (ignored when there is no console window, e.g. output redirected).</summary>
    public static void SetTitle(string title)
    {
        try { Console.Title = title; } catch { }
    }

    public static void Text(string text, ConsoleColor? color = null)
    {
        foreach (var line in text.Split('\n'))
        {
            WriteLine("  " + line, color);
        }
    }

    public static void Hint(string text) => Text(text, ConsoleColor.DarkGray);

    public static void Blank() => WriteLine();

    /// <summary>
    /// Arrow-key menu (↑ ↓ then Enter; digits also work). Returns the chosen index, or -1 for Esc when
    /// <paramref name="escapeIndex"/> is not given. Optional descriptions are shown under the menu for the selected item.
    /// Without a keyboard, returns <paramref name="selected"/>.
    /// </summary>
    public static int Menu(IReadOnlyList<string> options, int selected = 0, string? help = null, IReadOnlyList<string?>? descriptions = null, int? escapeIndex = null)
    {
        if (!Interactive)
        {
            return selected;
        }

        WriteLine();
        var top = Console.CursorTop;
        var cursorVisible = HideCursor();
        var lastHeight = 0;
        try
        {
            while (true)
            {
                Console.SetCursorPosition(0, top);
                var lines = 0;
                for (var i = 0; i < options.Count; i++)
                {
                    var line = $"  {(i == selected ? "›" : " ")} {options[i]}";
                    if (i == selected)
                    {
                        Write(Fit(line), ConsoleColor.Black, ConsoleColor.Yellow);
                        WriteLine(new string(' ', Math.Max(0, Width - Fit(line).Length - 1)));
                    }
                    else
                    {
                        WriteLine(Pad(line));
                    }
                    lines++;
                }
                WriteLine(Pad(""));
                lines++;
                if (descriptions != null && selected < descriptions.Count && descriptions[selected] is { Length: > 0 } d)
                {
                    foreach (var l in Wrap(d, Width - 6))
                    {
                        WriteLine(Pad("  " + l), ConsoleColor.Gray);
                        lines++;
                    }
                    WriteLine(Pad(""));
                    lines++;
                }
                Write(Pad("  " + (help ?? Keys.MenuHelp)), ConsoleColor.DarkGray);
                lines++;
                // Erase what a longer description left below.
                for (var i = lines; i < lastHeight; i++)
                {
                    WriteLine();
                    Write(Pad(""));
                }
                lastHeight = lines;

                var key = ReadKey();
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: selected = (selected + options.Count - 1) % options.Count; break;
                    case ConsoleKey.DownArrow:
                    case ConsoleKey.Tab: selected = (selected + 1) % options.Count; break;
                    case ConsoleKey.Home: selected = 0; break;
                    case ConsoleKey.End: selected = options.Count - 1; break;
                    case ConsoleKey.Escape:
                        WriteLine();
                        WriteLine();
                        return escapeIndex ?? -1;
                    case ConsoleKey.Enter:
                        WriteLine();
                        WriteLine();
                        return selected;
                    default:
                        if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' < options.Count)
                        {
                            selected = key.KeyChar - '1';
                        }
                        break;
                }
            }
        }
        finally
        {
            RestoreCursor(cursorVisible);
        }
    }

    /// <summary>Yes / no question; "no" comes first so that Enter by reflex changes nothing.</summary>
    public static bool Confirm(string yes, string no)
        => Menu(new[] { no, yes }, 0, escapeIndex: 0) == 1;

    /// <summary>
    /// Free text answer, typed with the keys of <see cref="ReadKey"/> (works while the keyboard thread runs).
    /// Enter alone keeps <paramref name="defaultValue"/>, Esc cancels (returns it too).
    /// </summary>
    public static string Input(string prompt, string defaultValue = "")
    {
        if (!Interactive)
        {
            return defaultValue;
        }
        Write($"  {prompt}");
        var left = Console.CursorLeft;
        var top = Console.CursorTop;
        var text = new StringBuilder(defaultValue);
        bool visible = true;
        try { visible = Console.CursorVisible; Console.CursorVisible = true; } catch { }
        try
        {
            while (true)
            {
                var room = Math.Max(10, Width - left - 2);
                var shown = text.Length > room ? "…" + text.ToString()[^(room - 1)..] : text.ToString();
                Console.SetCursorPosition(left, top);
                Out.Write(shown + new string(' ', Math.Max(0, room - shown.Length)));
                Console.SetCursorPosition(left + shown.Length, top);

                var key = ReadKey();
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        WriteLine();
                        var answer = text.ToString().Trim().Trim('"');
                        return answer.Length == 0 ? defaultValue : answer;
                    case ConsoleKey.Escape:
                        WriteLine();
                        return defaultValue;
                    case ConsoleKey.Backspace:
                        if (text.Length > 0) text.Length--;
                        break;
                    default:
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.U)
                        {
                            text.Clear();
                        }
                        else if (!char.IsControl(key.KeyChar))
                        {
                            text.Append(key.KeyChar);
                        }
                        break;
                }
            }
        }
        finally
        {
            try { Console.CursorVisible = visible; } catch { }
        }
    }

    public static void WaitEnter(string text)
    {
        if (!Interactive)
        {
            return;
        }
        WriteLine();
        Write($"  {text}", ConsoleColor.Yellow);
        while (ReadKey().Key != ConsoleKey.Enter) { }
        WriteLine();
    }

    public static bool HideCursor()
    {
        var visible = true;
        try { visible = Console.CursorVisible; Console.CursorVisible = false; } catch { }
        return visible;
    }

    public static void RestoreCursor(bool visible)
    {
        try { Console.CursorVisible = visible; } catch { }
    }

    /// <summary>Cut to the window width (one character is kept free so that the console never wraps).</summary>
    public static string Fit(string s)
    {
        var max = Width - 1;
        return s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
    }

    private static string Pad(string s)
    {
        s = Fit(s);
        return s + new string(' ', Math.Max(0, Width - 1 - s.Length));
    }

    /// <summary>Word wrap to <paramref name="width"/> columns (keeps explicit line breaks).</summary>
    public static IEnumerable<string> Wrap(string text, int width)
    {
        width = Math.Max(20, width);
        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            yield return line.ToString();
        }
    }
}

/// <summary>Key help shown under menus, in the chosen language.</summary>
public static class Keys
{
    public static string MenuHelp => L.T("↑ ↓ pour choisir, Entrée pour valider", "↑ ↓ to choose, Enter to confirm", "↑ ↓ para elegir, Intro para confirmar");
}

/// <summary>A piece of text with its colors.</summary>
public readonly record struct Seg(string Text, ConsoleColor Fg = ConsoleColor.Gray, ConsoleColor? Bg = null);

/// <summary>The lines of one full screen, drawn by <see cref="Frames.Draw"/>.</summary>
public sealed class Frame
{
    public List<Seg[]> Lines { get; } = new();

    public Frame Add(params Seg[] segs)
    {
        Lines.Add(segs);
        return this;
    }

    public Frame Add(string text, ConsoleColor fg = ConsoleColor.Gray) => Add(new Seg(text, fg));

    public Frame Blank() => Add(Array.Empty<Seg>());
}

/// <summary>
/// Draws a <see cref="Frame"/> over the whole window without clearing it: only the lines that changed are rewritten,
/// so the screen never flickers. A resize (or a screen drawn by something else) triggers a full redraw.
/// </summary>
public static class Frames
{
    private static readonly object Gate = new();
    private static List<string> _previous = new();
    private static int _width, _height;

    /// <summary>Forget what is on screen (something else drew there): the next frame is drawn in full.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _previous = new List<string>();
            _width = 0;
        }
    }

    public static void Draw(Frame frame)
    {
        if (!ConsoleUi.Interactive)
        {
            return;
        }
        lock (Gate)
        {
            var width = ConsoleUi.Width;
            var height = ConsoleUi.Height;
            if (width != _width || height != _height)
            {
                try { Console.Clear(); } catch { }
                _previous = new List<string>();
                _width = width;
                _height = height;
            }

            ConsoleUi.HideCursor(); // the main screen never shows the cursor
            ConsoleColor saved;
            try { saved = Console.ForegroundColor; } catch { saved = ConsoleColor.Gray; }
            var max = width - 1;
            var rows = Math.Min(height - 1, Math.Max(frame.Lines.Count, _previous.Count));
            var now = new List<string>(rows);
            for (var row = 0; row < rows; row++)
            {
                var segs = row < frame.Lines.Count ? frame.Lines[row] : Array.Empty<Seg>();
                var key = string.Join("\u0001", segs.Select(s => $"{(int)s.Fg},{(s.Bg is { } b ? (int)b : -1)},{s.Text}"));
                now.Add(key);
                if (row < _previous.Count && _previous[row] == key)
                {
                    continue;
                }
                try { Console.SetCursorPosition(0, row); } catch { break; }
                var used = 0;
                foreach (var s in segs)
                {
                    if (used >= max) break;
                    var text = s.Text.Length > max - used ? s.Text[..Math.Max(0, max - used - 1)] + "…" : s.Text;
                    Console.ForegroundColor = s.Fg;
                    if (s.Bg is { } bg) Console.BackgroundColor = bg;
                    ConsoleUi.Out.Write(text);
                    Console.ResetColor();
                    used += text.Length;
                }
                ConsoleUi.Out.Write(new string(' ', Math.Max(0, max - used)));
            }
            _previous = now;
            try { Console.SetCursorPosition(0, Math.Min(rows, height - 1)); } catch { }
            try { Console.ForegroundColor = saved; } catch { } // a colored text may be half written (activity log)
        }
    }
}

/// <summary>
/// Replaces Console.Out while the main screen is shown: what the counter prints (match counted, errors…) is kept as
/// colored lines and shown in the "activity" part of the screen instead of scrolling the window.
/// A "\r" rewrites the current line (progress bar).
/// </summary>
public sealed class ActivityLog : TextWriter
{
    private const int Max = 300;
    private readonly object _gate = new();
    private readonly List<Seg[]> _lines = new();
    private readonly List<Seg> _current = new();

    /// <summary>Called after each write (the main screen redraws itself).</summary>
    public event Action? Changed;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        ConsoleColor fg;
        try { fg = Console.ForegroundColor; } catch { fg = ConsoleColor.Gray; }
        lock (_gate)
        {
            var start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c is '\n' or '\r')
                {
                    if (i > start) _current.Add(new Seg(value[start..i], fg));
                    if (c == '\n')
                    {
                        Commit();
                    }
                    else
                    {
                        _current.Clear(); // progress bar: the line is drawn again
                    }
                    start = i + 1;
                }
            }
            if (start < value.Length)
            {
                _current.Add(new Seg(value[start..], fg));
            }
        }
        Changed?.Invoke();
    }

    public override void WriteLine(string? value)
    {
        Write((value ?? "") + "\n");
    }

    public override void WriteLine() => Write("\n");

    private void Commit()
    {
        _lines.Add(_current.ToArray());
        _current.Clear();
        if (_lines.Count > Max)
        {
            _lines.RemoveRange(0, _lines.Count - Max);
        }
    }

    /// <summary>The last <paramref name="count"/> lines (the line being written, e.g. a progress bar, comes last).</summary>
    public List<Seg[]> Last(int count)
    {
        lock (_gate)
        {
            var all = new List<Seg[]>(_lines);
            if (_current.Any(s => s.Text.Trim().Length > 0))
            {
                all.Add(_current.ToArray());
            }
            // Blank lines are only spacing in the old scrolling layout.
            all = all.Where(l => l.Any(s => s.Text.Trim().Length > 0)).ToList();
            return all.Skip(Math.Max(0, all.Count - count)).ToList();
        }
    }
}

/// <summary>One line progress bar, redrawn in place: "  Analyse  [██████░░░░]  12 / 31  file".</summary>
public sealed class ProgressBar
{
    private readonly string _label;
    private readonly int _total;
    private int _lastLength;

    public ProgressBar(string label, int total)
    {
        _label = label;
        _total = Math.Max(1, total);
    }

    public void Update(int done, string? detail = null)
    {
        const int barWidth = 28;
        var filled = (int)Math.Round(barWidth * Math.Clamp(done, 0, _total) / (double)_total);
        var bar = new string('█', filled) + new string('░', barWidth - filled);
        var count = $"{done} / {_total}";
        if (!ConsoleUi.Interactive)
        {
            return; // no redraw possible: the summary line at the end is enough
        }

        int width;
        try { width = Console.WindowWidth; } catch { width = 80; }
        var text = $"  {_label}  {bar}  {count}";
        if (!string.IsNullOrEmpty(detail))
        {
            var room = width - text.Length - 4;
            if (room > 8)
            {
                text += "  " + (detail.Length > room ? "…" + detail[^(room - 1)..] : detail);
            }
        }
        Console.Write("\r");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"  {_label}  ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(bar);
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(text[(4 + _label.Length + bar.Length)..]);
        Console.ResetColor();
        if (text.Length < _lastLength)
        {
            Console.Write(new string(' ', _lastLength - text.Length));
        }
        _lastLength = text.Length;
    }

    /// <summary>Erases the bar so the summary can be written on the same line.</summary>
    public void Clear()
    {
        if (!ConsoleUi.Interactive || _lastLength == 0)
        {
            return;
        }
        Console.Write("\r" + new string(' ', _lastLength) + "\r");
        _lastLength = 0;
    }
}
