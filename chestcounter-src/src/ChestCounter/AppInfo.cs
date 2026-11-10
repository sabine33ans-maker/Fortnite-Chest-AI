namespace ChestCounter;

/// <summary>Version, GitHub repository and names shared by the tool's processes.</summary>
public static class AppInfo
{
    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";

    public const string Repo = "LED-N/Fortnite-ChestCounter";
    public static string RepoUrl => "https://github.com/" + Repo;
    public static string IssuesUrl => RepoUrl + "/issues";

    /// <summary>Path of ChestCounter.exe when running as the published exe (null under "dotnet ChestCounter.dll").</summary>
    public static string? ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            return p != null && Path.GetFileName(p).Equals("ChestCounter.exe", StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    // Names of the per-user kernel objects that let the processes find each other.
    /// <summary>One instance per data folder (the normal folder, or a test folder given with --data).</summary>
    public static string InstanceMutexFor(string dataDir)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant()));
        return @"Local\ChestCounter.Instance." + Convert.ToHexString(hash, 0, 8);
    }
    public const string StandbyMutex = @"Local\ChestCounter.Standby";
    public const string StandbyStopEvent = @"Local\ChestCounter.StandbyStop";
}
