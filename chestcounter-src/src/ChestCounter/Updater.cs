using System.Security.Cryptography;
using System.Text.Json;

namespace ChestCounter;

public sealed record UpdateInfo(Version Version, string Tag, string AssetUrl, long Size, string? Sha256, string PageUrl, string? Notes = null)
{
    /// <summary>"What's new" of this release in the chosen language (empty when the notes have no such list).</summary>
    public IReadOnlyList<string> WhatsNew => ReleaseNotes.WhatsNew(Notes, L.Code);
}

/// <summary>
/// Release notes are written in three sections separated by "---", each starting with "**English**", "**Français**"
/// or "**Español**", with a bold "What's new" line followed by a bullet list. This picks the list of one language.
/// </summary>
public static class ReleaseNotes
{
    private static readonly Dictionary<string, string> SectionTitle = new() { ["en"] = "English", ["fr"] = "Français", ["es"] = "Español" };

    /// <summary>RELEASE_NOTES.md of this version, built into the exe (also the text of its GitHub release).</summary>
    public static string? Embedded { get; } = LoadEmbedded();

    private static string? LoadEmbedded()
    {
        try
        {
            using var s = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("ReleaseNotes.md");
            return s == null ? null : new StreamReader(s).ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> WhatsNew(string? body, string language)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<string>();
        }
        var sections = System.Text.RegularExpressions.Regex.Split(body.Replace("\r\n", "\n"), @"^\s*-{3,}\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        string? Section(string code) => SectionTitle.TryGetValue(code, out var title)
            ? sections.FirstOrDefault(sec => sec.TrimStart().StartsWith($"**{title}**", StringComparison.OrdinalIgnoreCase))
            : null;
        var section = Section(language) ?? Section("en") ?? (sections.Length == 1 ? sections[0] : null);
        if (section == null)
        {
            return Array.Empty<string>();
        }

        // The bullet list that follows the bold "What's new" line (or, failing that, every bullet of the section).
        var lines = section.Split('\n').Select(l => l.TrimEnd()).ToList();
        var start = lines.FindIndex(l => l.StartsWith("**", StringComparison.Ordinal) && l.EndsWith("**", StringComparison.Ordinal) && l.Length > 4);
        var bullets = new List<string>();
        foreach (var line in start >= 0 ? lines.Skip(start + 1) : lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
            {
                bullets.Add(Plain(t[2..]));
            }
            else if (bullets.Count > 0 && t.Length == 0 && start >= 0)
            {
                break;
            }
        }
        return bullets;
    }

    /// <summary>Markdown to plain console text: **bold**, *italic*, `code` and [text](link) lose their marks.</summary>
    private static string Plain(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        return text.Replace("**", "").Replace("`", "").Replace("*", "");
    }
}

/// <summary>
/// Checks the latest GitHub release and replaces ChestCounter.exe with it. The running exe is renamed (Windows allows
/// that), the new one takes its place and is started; the old file is deleted at the next launch.
/// This is the only network access of the tool, and it can be turned off in the settings.
/// </summary>
public static class Updater
{
    private static string ApiUrl => Environment.GetEnvironmentVariable("CHESTCOUNTER_UPDATE_API")
        ?? $"https://api.github.com/repos/{AppInfo.Repo}/releases/latest";

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounter/{AppInfo.VersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The newer release, or null (up to date, offline, repository not reachable…).</summary>
    public static async Task<UpdateInfo?> CheckAsync() => (await CheckDetailedAsync()).Update;

    /// <summary>
    /// The newer release (or null), and whether GitHub answered at all: lets the U key tell
    /// "you already have the latest version" apart from "GitHub cannot be reached".
    /// </summary>
    public static async Task<(UpdateInfo? Update, bool Reached)> CheckDetailedAsync()
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(ApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }
            return (Parse(await response.Content.ReadAsStringAsync()), true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>The release described by the GitHub API answer, if it is newer and carries ChestCounter.exe.</summary>
    private static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()
                || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                return null;
            }
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(AppInfo.Version))
            {
                return null;
            }
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), "ChestCounter.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string? sha = null;
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha = d[7..];
                }
                return new UpdateInfo(version, tag, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64(), sha,
                    root.TryGetProperty("html_url", out var page) ? page.GetString() ?? AppInfo.RepoUrl : AppInfo.RepoUrl,
                    root.TryGetProperty("body", out var body) ? body.GetString() : null);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Downloads and puts the new exe in place. Returns null when done, else the reason it failed.</summary>
    public static string? Install(UpdateInfo update)
    {
        var exe = AppInfo.ExePath;
        if (exe == null)
        {
            return L.T("mise à jour possible seulement depuis ChestCounter.exe", "update only possible from ChestCounter.exe", "actualización posible solo desde ChestCounter.exe");
        }
        var download = exe + ".download";
        var old = exe + ".old";
        try
        {
            using (var http = NewClient(TimeSpan.FromMinutes(5)))
            using (var source = http.GetStreamAsync(update.AssetUrl).GetAwaiter().GetResult())
            using (var target = File.Create(download))
            {
                source.CopyTo(target);
            }

            var info = new FileInfo(download);
            if (info.Length != update.Size)
            {
                throw new IOException(L.T("téléchargement incomplet", "incomplete download", "descarga incompleta"));
            }
            using (var fs = File.OpenRead(download))
            {
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                {
                    throw new IOException(L.T("le fichier téléchargé n'est pas un exe", "the downloaded file is not an exe", "el archivo descargado no es un exe"));
                }
                if (update.Sha256 != null)
                {
                    fs.Position = 0;
                    var hash = Convert.ToHexString(SHA256.HashData(fs));
                    if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException(L.T("empreinte SHA-256 incorrecte", "wrong SHA-256 checksum", "suma de comprobación SHA-256 incorrecta"));
                    }
                }
            }

            // The standby process may run the current exe: stop it (it is restarted from the new exe).
            if (OperatingSystem.IsWindows())
            {
                FortniteWatch.StopStandby();
            }
            if (File.Exists(old))
            {
                try { File.Delete(old); } catch { old = exe + $".{DateTime.Now:yyyyMMddHHmmss}.old"; }
            }
            File.Move(exe, old);
            File.Move(download, exe);
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(download); } catch { }
            return ex.Message;
        }
    }

    /// <summary>Deletes the files left by the previous update.</summary>
    public static void CleanupOld()
    {
        var exe = AppInfo.ExePath;
        var dir = exe != null ? Path.GetDirectoryName(exe) : null;
        if (dir == null)
        {
            return;
        }
        foreach (var f in Directory.EnumerateFiles(dir, "ChestCounter.exe.*"))
        {
            if (f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(f); } catch { /* still in use: next time */ }
            }
        }
    }
}
