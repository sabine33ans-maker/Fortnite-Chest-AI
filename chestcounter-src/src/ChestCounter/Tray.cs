using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ChestCounter;

/// <summary>
/// Minimizing the console window sends the counter to the notification area (system tray) instead of the taskbar:
/// an icon with the chest, a click brings the window back, a right-click offers open / report / quit.
/// Only for the classic console window (a terminal such as Windows Terminal owns its own window).
/// One small thread, asleep in its message loop until something happens.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Tray
{
    private static IntPtr _console, _hwnd, _iconBig, _iconSmall, _hook;
    private static Thread? _thread;
    private static WndProcDelegate? _wndProc;      // keep the delegates alive
    private static WinEventDelegate? _eventProc;
    private static bool _hidden, _balloonShown, _notifyNext;
    private static uint _taskbarCreated;

    /// <summary>Minimizing goes to the tray (setting). When off, the window minimizes to the taskbar as usual.</summary>
    public static volatile bool Enabled = true;

    public static Action? OpenReport { get; set; }
    public static Action? Quit { get; set; }

    /// <summary>True when the counter has its own console window (and so can go to the tray).</summary>
    public static bool Available { get; private set; }

    public static void Start()
    {
        if (_thread != null)
        {
            return;
        }
        _console = GetConsoleWindow();
        var cls = new StringBuilder(64);
        if (_console == IntPtr.Zero || GetClassNameW(_console, cls, cls.Capacity) == 0 || cls.ToString() != "ConsoleWindowClass")
        {
            return;
        }
        if (Environment.ProcessPath is { } exe)
        {
            ExtractIconExW(exe, 0, out _iconBig, out _iconSmall, 1);
            // The console window shows the chest too (title bar and taskbar).
            if (_iconBig != IntPtr.Zero) SendMessageW(_console, WM_SETICON, (IntPtr)1, _iconBig);
            if (_iconSmall != IntPtr.Zero) SendMessageW(_console, WM_SETICON, IntPtr.Zero, _iconSmall);
        }
        Available = true;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Sends the window to the tray now (opened with Fortnite: no notification, the game keeps the screen).</summary>
    public static void Hide(bool notify)
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_HIDE, (IntPtr)(notify ? 1 : 0), IntPtr.Zero);
        }
    }

    public static void Stop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static void Run(ManualResetEventSlim ready)
    {
        try
        {
            var hInstance = GetModuleHandleW(null);
            _wndProc = WndProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = "ChestCounterTray",
            };
            RegisterClassExW(ref wc);
            _hwnd = CreateWindowExW(0, wc.lpszClassName, "Chest Counter tray", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            // Told when any window starts minimizing; only the console window is handled.
            _eventProc = (_, _, hwnd, idObject, _, _, _) =>
            {
                if (hwnd == _console && idObject == 0 && Enabled && !_hidden)
                {
                    HideNow(notify: true);
                }
            };
            _hook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, _eventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        finally
        {
            ready.Set();
        }
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    private static NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _iconSmall != IntPtr.Zero ? _iconSmall : _iconBig,
        szTip = L.T("Compteur de coffres Fortnite", "Fortnite Chest Counter", "Contador de cofres de Fortnite"),
        szInfo = "",
        szInfoTitle = "",
    };

    private static void HideNow(bool notify)
    {
        ShowWindow(_console, SW_HIDE);
        _hidden = true;
        _notifyNext = notify && !_balloonShown;
        AddIcon();
    }

    private static void AddIcon()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (_notifyNext)
        {
            // Once per session: the window is not gone, it waits in the tray.
            data.uFlags |= NIF_INFO;
            data.szInfoTitle = L.T("Compteur de coffres Fortnite", "Fortnite Chest Counter", "Contador de cofres de Fortnite");
            data.szInfo = L.T("Le compteur continue de compter dans la zone de notification. Clique sur le coffre pour le rouvrir.",
                "The counter keeps counting in the notification area. Click the chest to open it again.",
                "El contador sigue contando en el área de notificación. Haz clic en el cofre para volver a abrirlo.");
            data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
            data.hBalloonIcon = _iconBig;
            _balloonShown = true;
            _notifyNext = false;
        }
        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private static void Restore()
    {
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _hidden = false;
        ShowWindow(_console, SW_RESTORE);
        SetForegroundWindow(_console);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_HIDE)
        {
            if (!_hidden) HideNow(wParam != IntPtr.Zero);
            return IntPtr.Zero;
        }
        if (msg == WM_APP_TRAY)
        {
            switch ((uint)((long)lParam & 0xFFFF))
            {
                case WM_LBUTTONUP:
                    Restore();
                    break;
                case WM_RBUTTONUP:
                    Menu();
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _hidden)
        {
            AddIcon(); // Explorer restarted: the icon must be added again
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_CLOSE:
                if (_hidden)
                {
                    var data = Data(0);
                    Shell_NotifyIconW(NIM_DELETE, ref data);
                }
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void Menu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, (UIntPtr)1, L.T("Ouvrir le compteur", "Open the counter", "Abrir el contador"));
        AppendMenuW(menu, MF_STRING, (UIntPtr)2, L.T("Ouvrir le rapport", "Open the report", "Abrir el informe"));
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)3, L.T("Quitter", "Quit", "Salir"));
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // so that the menu closes when clicking elsewhere
        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case 1: Restore(); break;
            case 2: OpenReport?.Invoke(); break;
            case 3: Quit?.Invoke(); break;
        }
    }

    // ---- Win32 ------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private const uint WS_POPUP = 0x80000000;
    private const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_SETICON = 0x0080, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const uint WM_APP_TRAY = 0x8101, WM_APP_HIDE = 0x8102;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, WINEVENT_OUTOFCONTEXT = 0;
    private const int SW_HIDE = 0, SW_RESTORE = 9;
    private const uint NIM_ADD = 0, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
