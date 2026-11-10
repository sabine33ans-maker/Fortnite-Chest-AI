using System.Text.Json;
using ChestCounter;

static int Fail(string message)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message }));
    return 2;
}

if (args.Length != 2)
    return Fail("Usage: ChestReplayCli <replay-file> <expected-player-name>");

var replayPath = Path.GetFullPath(args[0]);
var expectedPlayer = args[1].Trim();
if (!File.Exists(replayPath))
    return Fail("Replay file not found.");
if (string.IsNullOrWhiteSpace(expectedPlayer))
    return Fail("Expected player name is empty.");

try
{
    using var stream = new FileStream(replayPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    var record = new ReplayAnalyzer().Analyze(stream, Path.GetFileName(replayPath), expectedPlayer);

    // ChestCounter only tracks the exact configured Epic display name when one is supplied.
    // Never fall back to the replay recorder or a teammate if the requested player was not found.
    if (!record.TrackedByUsername ||
        string.IsNullOrWhiteSpace(record.PlayerName) ||
        !string.Equals(record.PlayerName.Trim(), expectedPlayer, StringComparison.OrdinalIgnoreCase))
        return Fail("The requested Epic player was not found in this replay.");

    if (!record.IsBattleRoyale)
        return Fail("This replay is not a supported Battle Royale match.");

    // Prefer a replay-name-independent identity when the replay exposes both a start time
    // and Epic account ID. This catches renamed copies of the same player's match.
    var stableMatchId = record.MatchStartUtc is not null && !string.IsNullOrWhiteSpace(record.EpicId)
        ? $"stable:{record.MatchStartUtc.Value.ToUniversalTime():O}:{record.EpicId}"
        : record.SessionId;

    Console.Out.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        player_name = record.PlayerName,
        match_id = stableMatchId,
        chests_opened = record.ChestsOpened,
        playlist = record.Playlist,
        match_start_utc = record.MatchStartUtc
    }));
    return 0;
}
catch (Exception ex)
{
    // Keep stdout machine-readable; diagnostics go to stderr and are truncated by the web app.
    Console.Error.WriteLine(ex.ToString());
    return Fail("ChestCounter could not decode this replay.");
}
