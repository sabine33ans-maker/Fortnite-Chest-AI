using System.Text;

namespace ChestCounter;

/// <summary>errors.log in the data folder: full details of unreadable replays and crashes, to attach to a bug report.</summary>
public static class ErrorLog
{
    public static string? FilePath { get; set; }

    public static void Write(string what, object error)
    {
        if (FilePath == null)
        {
            return;
        }
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · ChestCounter {AppInfo.VersionText} · {Environment.OSVersion.VersionString}");
            sb.AppendLine(what);
            sb.AppendLine(error.ToString());
            sb.AppendLine();
            File.AppendAllText(FilePath, Anonymize(sb.ToString()), new UTF8Encoding(false));

            // Keep the file small: only the most recent part.
            var info = new FileInfo(FilePath);
            if (info.Length > 1_000_000)
            {
                var text = File.ReadAllText(FilePath);
                File.WriteAllText(FilePath, text[^500_000..], new UTF8Encoding(false));
            }
        }
        catch
        {
            // logging must never stop the tool
        }
    }

    /// <summary>The file is meant to be attached to a public bug report: hide the Windows user name in paths.</summary>
    private static string Anonymize(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3)
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        var user = Environment.UserName;
        if (user.Length >= 3)
        {
            text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
