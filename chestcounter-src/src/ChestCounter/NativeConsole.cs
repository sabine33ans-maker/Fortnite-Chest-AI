using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>
/// The exe is a Windows application, so that the "start with Fortnite" standby process has no window at all.
/// The normal tool gives itself a console window here, before anything is written.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeConsole
{
    /// <param name="attachToParent">Command line use (--once, --help): write into the terminal that started the exe.</param>
    public static void Ensure(bool attachToParent)
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            return; // already in a console (dotnet host, inherited)
        }
        if (IsRedirected(STD_OUTPUT_HANDLE))
        {
            return; // output goes to a pipe or a file: Console uses it as is
        }
        if (attachToParent && AttachConsole(ATTACH_PARENT_PROCESS))
        {
            return;
        }
        AllocConsole();
    }

    /// <summary>Minimized without taking the focus (started with Fortnite: the game keeps the screen).</summary>
    public static void Minimize()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
        }
    }

    private static bool IsRedirected(int std)
    {
        var h = GetStdHandle(std);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            return false;
        }
        var type = GetFileType(h);
        return type is FILE_TYPE_DISK or FILE_TYPE_PIPE;
    }

    private const int STD_OUTPUT_HANDLE = -11;
    private const int ATTACH_PARENT_PROCESS = -1;
    private const uint FILE_TYPE_DISK = 1, FILE_TYPE_PIPE = 3;
    private const int SW_SHOWMINNOACTIVE = 7;

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int std);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
