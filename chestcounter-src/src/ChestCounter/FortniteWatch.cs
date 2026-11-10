using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ChestCounter;

/// <summary>
/// "Start with Fortnite". Windows cannot start a program when another one starts (not without admin rights), so a
/// tiny invisible process ("standby") is started at Windows logon: every few seconds it checks whether Fortnite is
/// running, and opens the tool when it is. The tool closes itself once Fortnite is closed and the last match counted.
/// The standby reads nothing from the game: it only looks at the list of running programs.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FortniteWatch
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ChestCounter";
    // CHESTCOUNTER_GAME_PROCESS: another process name, to test the standby without Fortnite.
    private static readonly string[] GameProcesses =
        { Environment.GetEnvironmentVariable("CHESTCOUNTER_GAME_PROCESS") is { Length: > 0 } test ? test : "FortniteClient-Win64-Shipping" };

    public static bool IsFortniteRunning()
    {
        foreach (var name in GameProcesses)
        {
            var found = Process.GetProcessesByName(name);
            var any = found.Length > 0;
            foreach (var p in found)
            {
                p.Dispose();
            }
            if (any)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Turns "start with Fortnite" on or off: entry at Windows logon + standby started or stopped right away.
    /// Returns false when it cannot be done (tool not run from ChestCounter.exe).
    /// </summary>
    public static bool Apply(bool enabled, string dataDir)
    {
        var exe = AppInfo.ExePath;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                if (exe == null)
                {
                    return false;
                }
                key.SetValue(RunValue, $"\"{exe}\" --standby {DataArg(dataDir)}"); // refreshed at each launch: follows the exe if it is moved
                StartStandby(exe, dataDir);
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
                StopStandby();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DataArg(string dataDir) => $"--data \"{Path.GetFullPath(dataDir).TrimEnd('\\', '/')}\"";

    public static void StartStandby(string exe, string dataDir)
    {
        if (Mutex.TryOpenExisting(AppInfo.StandbyMutex, out var running))
        {
            running.Dispose();
            return;
        }
        Process.Start(new ProcessStartInfo(exe, $"--standby {DataArg(dataDir)}") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    public static void StopStandby()
    {
        if (EventWaitHandle.TryOpenExisting(AppInfo.StandbyStopEvent, out var stop))
        {
            stop.Set();
            stop.Dispose();
        }
    }

    /// <summary>The standby process: no window, waits for Fortnite, opens the tool, waits for Fortnite to close.</summary>
    public static int RunStandby(string dataDir)
    {
        using var single = new Mutex(false, AppInfo.StandbyMutex);
        try
        {
            if (!single.WaitOne(0))
            {
                return 0; // already waiting
            }
        }
        catch (AbandonedMutexException)
        {
            // previous standby was killed: take over
        }

        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.StandbyStopEvent);
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }
        try { EcoQos.Enable(); } catch { }
        GC.Collect();

        var exe = AppInfo.ExePath;
        if (exe == null)
        {
            return 1;
        }

        while (true)
        {
            if (stop.WaitOne(TimeSpan.FromSeconds(5)))
            {
                return 0;
            }
            if (!IsFortniteRunning())
            {
                continue;
            }

            // Turned off in the settings meanwhile: stop waiting.
            if (new Store(dataDir).LoadConfig() is { StartWithFortnite: false })
            {
                return 0;
            }

            if (Mutex.TryOpenExisting(AppInfo.InstanceMutexFor(dataDir), out var open))
            {
                open.Dispose(); // the tool is already open
            }
            else
            {
                try { Process.Start(new ProcessStartInfo(exe, $"--auto {DataArg(dataDir)}") { UseShellExecute = false })?.Dispose(); } catch { }
            }

            // Arm again only for the next Fortnite launch (the player may have closed the tool on purpose).
            while (IsFortniteRunning())
            {
                if (stop.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    return 0;
                }
            }
            GC.Collect();
        }
    }
}
