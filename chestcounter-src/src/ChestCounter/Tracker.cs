using System.Text;

namespace ChestCounter;

/// <summary>
/// Watches the Fortnite Demos folder, analyzes every finished replay once and accumulates the results.
/// </summary>
public sealed class Tracker
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Replay finalized by Fortnite (no longer live, real key written): only a few quiet seconds are needed.</summary>
    private static readonly TimeSpan FinalizedQuietPeriod = TimeSpan.FromSeconds(4);

    /// <summary>A finished replay not modified for this long is complete for sure (replays left from earlier).</summary>
    private static readonly TimeSpan OldReplayAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Fortnite appends to the replay of a match in progress about every 25 s. Still flagged "live" but untouched for
    /// this long: the recording was cut (Fortnite crashed or was killed) and will never be finished.
    /// </summary>
    private static readonly TimeSpan AbandonedLiveAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Dictionary<string, long> _lastSeenSize = new();

    public Tracker(Store store, Config config, TrackerData data)
    {
        _store = store;
        _config = config;
        _data = data;

        // Before version 1.1, matches of other accounts were rejected ("autre joueur"): they now get their own profile.
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value.StartsWith("autre joueur", StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }
    }

    public TrackerData Data => _data;

    /// <summary>True while a replay is waiting to be finished / to become stable.</summary>
    public bool HasPending { get; private set; }

    /// <summary>True when a match has just ended and its replay is about to be read: check again within seconds.</summary>
    public bool IsFinishing { get; private set; }

    /// <summary>Gives the memory used by the analysis back to Windows (the tool idles at a few MB).</summary>
    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    public static string FileKey(FileInfo f) => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    public IEnumerable<FileInfo> ReplayFiles()
    {
        var dir = new DirectoryInfo(_config.DemosFolder);
        if (!dir.Exists)
        {
            return Enumerable.Empty<FileInfo>();
        }
        return dir.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc);
    }

    public IEnumerable<FileInfo> UnprocessedFiles() => ReplayFiles().Where(f => !_data.ProcessedFiles.ContainsKey(FileKey(f)));

    /// <summary>Replays not analyzed yet and not being recorded (what the next scan will read).</summary>
    public int BacklogCount() => UnprocessedFiles().Count(f => ReplayHeaderProbe.IsLive(f.FullName) != true);

    public void MarkSkipped(IEnumerable<FileInfo> files)
    {
        foreach (var f in files)
        {
            _data.ProcessedFiles[FileKey(f)] = "skipped";
        }
        _store.SaveData(_data);
    }

    /// <summary>Result of the analysis of one replay.</summary>
    private sealed record Outcome(MatchRecord? Match = null, bool Updated = false, string? Skipped = null, string? Error = null);

    /// <summary>
    /// One pass over the folder. Returns the number of new matches recorded. Several replays at once (replays left
    /// from earlier) are shown as a progress bar and a summary; a single one (end of a match) gets its own line.
    /// </summary>
    /// <param name="ignoreQuietPeriod">Process finished replays immediately (used by --once).</param>
    public int Scan(bool ignoreQuietPeriod = false)
    {
        HasPending = false;
        IsFinishing = false;
        var ready = new List<FileInfo>();
        foreach (var file in UnprocessedFiles().ToList())
        {
            file.Refresh();
            if (!file.Exists)
            {
                continue;
            }
            if (!IsReady(file, ignoreQuietPeriod))
            {
                HasPending = true;
                continue;
            }
            ready.Add(file);
        }
        if (ready.Count == 0)
        {
            return 0;
        }

        var batch = ready.Count >= 2;
        var profilesBefore = Profiles.List(_data).Count;
        var bar = batch ? new ProgressBar(L.T("Analyse des replays", "Analyzing replays", "Analizando repeticiones"), ready.Count) : null;
        var outcomes = new List<Outcome>();
        for (var i = 0; i < ready.Count; i++)
        {
            var file = ready[i];
            if (batch)
            {
                bar!.Update(i, file.Name);
                ReportWriter.Progress = new ScanProgress(i, ready.Count);
                ReportWriter.WriteAll(_store, _config, _data);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                var mb = (file.Length / 1024 / 1024.0).ToString("F1", L.Culture);
                var justEnded = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(2);
                Console.WriteLine(justEnded
                    ? L.T($"[{DateTime.Now:HH:mm:ss}] Partie terminée : analyse du replay ({mb} Mo)…", $"[{DateTime.Now:HH:mm:ss}] Match over: analyzing the replay ({mb} MB)…", $"[{DateTime.Now:HH:mm:ss}] Partida terminada: analizando la repetición ({mb} MB)…")
                    : L.T($"[{DateTime.Now:HH:mm:ss}] Analyse de {file.Name} ({mb} Mo)…", $"[{DateTime.Now:HH:mm:ss}] Analyzing {file.Name} ({mb} MB)…", $"[{DateTime.Now:HH:mm:ss}] Analizando {file.Name} ({mb} MB)…"));
                Console.ResetColor();
            }

            var outcome = Process(file);
            outcomes.Add(outcome);
            if (!batch)
            {
                PrintOutcome(outcome);
            }
        }

        if (batch)
        {
            bar!.Update(ready.Count);
            bar.Clear();
            ReportWriter.Progress = null;
            PrintBatchSummary(outcomes);
            if (profilesBefore < 2 && Profiles.List(_data).Count >= 2)
            {
                NewProfilesFound = true;
            }
        }

        var added = outcomes.Count(o => o.Match != null);
        if (added > 0 || batch)
        {
            ReportWriter.WriteAll(_store, _config, _data);
        }
        return added;
    }

    /// <summary>Set when a scan found replays of a second account: the player should choose their profile.</summary>
    public bool NewProfilesFound { get; set; }

    private bool IsReady(FileInfo file, bool ignoreQuietPeriod)
    {
        var live = ReplayHeaderProbe.IsLive(file.FullName);
        var quietFor = DateTime.UtcNow - file.LastWriteTimeUtc;
        if (live == true)
        {
            // Match still being recorded, unless the recording was cut long ago (Process deals with it).
            return quietFor > AbandonedLiveAge;
        }

        if (live == false && (ignoreQuietPeriod || quietFor > OldReplayAge))
        {
            return true;
        }

        // The file must not have changed for a while and its size must be stable between two scans.
        var stable = _lastSeenSize.TryGetValue(file.FullName, out var size) && size == file.Length;
        _lastSeenSize[file.FullName] = file.Length;

        if (live == null)
        {
            // Header could not be read: be extra careful.
            return stable && quietFor > TimeSpan.FromMinutes(2);
        }

        // Fortnite clears the "live" flag and writes the real encryption key when it stops recording: the replay
        // is complete. Read it as soon as its size has not changed for a few seconds (two checks in a row).
        if (!ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            IsFinishing = true;
            return stable && quietFor >= FinalizedQuietPeriod;
        }
        return stable && quietFor > QuietPeriod;
    }

    private Outcome Process(FileInfo file)
    {
        var key = FileKey(file);

        // Recording cut in the middle of the match: Fortnite never wrote the decryption key, nothing can be read.
        // Without this, the replay would stay "pending" forever (no auto update, auto close delayed every time).
        if (ReplayHeaderProbe.IsLive(file.FullName) == true && ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            _data.ProcessedFiles[key] = "incomplete";
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("replay incomplet (Fortnite s'est fermé pendant la partie) : cette partie ne peut pas être comptée",
                "incomplete replay (Fortnite closed during the match): this match cannot be counted",
                "repetición incompleta (Fortnite se cerró durante la partida): esta partida no se puede contar"));
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, ParseTimeout);
        }
        catch (Exception ex)
        {
            var message = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
            ErrorLog.Write($"Unreadable replay: {file.FullName} ({file.Length} bytes, {file.LastWriteTime:yyyy-MM-dd HH:mm:ss})", ex);
            _data.ProcessedFiles[key] = "error: " + message;
            _store.SaveData(_data);
            return new Outcome(Error: $"{file.Name} : {message}");
        }

        record.ReplayFile = file.Name;

        if (!record.IsBattleRoyale)
        {
            // Also drop a record left by an older version of the tool for this replay.
            _data.Matches.RemoveAll(m => m.SessionId == record.SessionId);
            _data.ProcessedFiles[key] = "autre mode";
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("pas une partie Battle Royale (créatif / autre mode)", "not a Battle Royale match (Creative / other mode)", "no es una partida de Battle Royale (Creativo / otro modo)"));
        }

        var existing = _data.Matches.FirstOrDefault(m => m.SessionId == record.SessionId);
        if (existing != null && existing.AnalyzerVersion >= record.AnalyzerVersion)
        {
            _data.ProcessedFiles[key] = record.SessionId;
            _store.SaveData(_data);
            return new Outcome(Skipped: L.T("partie déjà comptée (même partie, fichier renommé)", "match already counted (same match, renamed file)", "partida ya contada (misma partida, archivo renombrado)"));
        }

        if (existing != null)
        {
            // Re-analysis with a newer version of the tool: replace the old record.
            record.AnalyzedAtUtc = existing.AnalyzedAtUtc;
            _data.Matches.Remove(existing);
        }

        _data.Matches.Add(record);
        _data.ProcessedFiles[key] = record.SessionId;
        _store.SaveData(_data);
        if (_liveFile == file.FullName)
        {
            ClearLive(); // the final analysis replaces the live estimate
        }

        // First match ever: its account becomes the profile.
        if (string.IsNullOrEmpty(_config.Profile))
        {
            _config.Profile = Profiles.Key(record);
            try { _store.SaveConfig(_config); } catch { /* ignore */ }
        }

        return new Outcome(record, Updated: existing != null);
    }

    private void PrintOutcome(Outcome o)
    {
        if (o.Error != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.T($"   Impossible de lire ce replay : {o.Error}", $"   Cannot read this replay: {o.Error}", $"   No se puede leer esta repetición: {o.Error}"));
            Console.ResetColor();
            PrintErrorLogHint();
            return;
        }
        if (o.Skipped != null)
        {
            Console.WriteLine($"   {L.T("Ignoré", "Skipped", "Ignorada")} : {o.Skipped}.");
            return;
        }

        var m = o.Match!;
        PrintMatch(m, o.Updated);
        var current = Profiles.Current(_data, _config);
        if (current != null && Profiles.Key(m) != current.Key)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            var name = m.PlayerName ?? L.T("compte inconnu", "unknown account", "cuenta desconocida");
            Console.WriteLine(L.T($"   Partie jouée sur un autre compte ({name}) : elle compte pour son propre profil. Profil affiché : {current.Name} (« Changer de profil » dans le menu).",
                $"   Match played on another account ({name}): it counts for its own profile. Profile shown: {current.Name} (\"Switch profile\" in the menu).",
                $"   Partida jugada con otra cuenta ({name}): cuenta para su propio perfil. Perfil mostrado: {current.Name} («Cambiar de perfil» en el menú)."));
            Console.ResetColor();
        }
    }

    private void PrintErrorLogHint()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(L.T($"   Détails dans {_store.ErrorLogPath}. Souvent dû à une mise à jour de Fortnite : signale-le sur {AppInfo.IssuesUrl}",
            $"   Details in {_store.ErrorLogPath}. Usually caused by a Fortnite update: report it at {AppInfo.IssuesUrl}",
            $"   Detalles en {_store.ErrorLogPath}. Suele deberse a una actualización de Fortnite: avísalo en {AppInfo.IssuesUrl}"));
        Console.ResetColor();
    }

    private void PrintBatchSummary(List<Outcome> outcomes)
    {
        var matches = outcomes.Where(o => o.Match != null).Select(o => o.Match!).ToList();
        var skipped = outcomes.Count(o => o.Skipped != null);
        var errors = outcomes.Where(o => o.Error != null).Select(o => o.Error!).ToList();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  ✔ {L.Count(outcomes.Count, "replay analysé", "replays analysés", "replay analyzed", "replays analyzed", "repetición analizada", "repeticiones analizadas")}");
        Console.ResetColor();
        Console.Write(L.T(" : ", ": ", ": "));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{L.Count(matches.Count, "partie", "parties", "match", "matches", "partida", "partidas")}, {L.Count(matches.Sum(m => m.ChestsOpened), "coffre", "chest", "cofre")}");
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        if (treasures > 0)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write($", ★ {L.Count(treasures, "trésor", "treasure", "tesoro")}");
        }
        Console.ResetColor();
        Console.WriteLine();
        if (skipped > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(L.T($"    {skipped} ignoré(s) : modes créatifs, parties déjà comptées ou replays incomplets.", $"    {skipped} skipped: Creative modes, matches already counted or incomplete replays.", $"    {skipped} ignorada(s): modos creativos, partidas ya contadas o repeticiones incompletas."));
            Console.ResetColor();
        }
        if (errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(L.T($"    {errors.Count} replay(s) illisible(s) :", $"    {errors.Count} unreadable replay(s):", $"    {errors.Count} repetición(es) ilegible(s):"));
            foreach (var e in errors.Take(5))
            {
                Console.WriteLine($"      {e}");
            }
            Console.ResetColor();
            PrintErrorLogHint();
        }
    }

    /// <summary>Reads the file (even while Fortnite writes it) and parses it on a lowest-priority background thread.</summary>
    private MatchRecord Analyze(FileInfo file, TimeSpan timeout)
    {
        byte[] bytes;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        MatchRecord? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                // The replays saved on a PC are those of its player: the replay owner is tracked (one profile per account).
                result = new ReplayAnalyzer().Analyze(ms, file.Name, "");
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "ReplayAnalysis",
        };
        worker.Start();
        if (!worker.Join(timeout))
        {
            throw new TimeoutException(L.T("analyse trop longue", "analysis took too long", "análisis demasiado largo"));
        }
        bytes = Array.Empty<byte>();
        ReleaseMemory();
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); // keep the original stack
        }
        return result!;
    }

    // ---- Live match ---------------------------------------------------------------------------
    private string? _liveFile;
    private long _liveSize = -1;
    private DateTime _liveParsedUtc = DateTime.MinValue;

    /// <summary>Estimate of the match being played (replay still recorded), or null. Not saved.</summary>
    public MatchRecord? LiveMatch { get; private set; }

    /// <summary>True while a replay is being recorded (a match is in progress).</summary>
    public bool IsLive => _liveFile != null;

    private void ClearLive()
    {
        _liveFile = null;
        _liveSize = -1;
        LiveMatch = null;
    }

    /// <summary>
    /// Re-reads the replay being recorded when Fortnite has appended data to it (about every 25 s), at most once
    /// every <see cref="Config.LiveIntervalSeconds"/>. Returns true when the live figures changed.
    /// </summary>
    public bool UpdateLive()
    {
        // The newest replay still flagged "live" and written recently (an old one left by a crash is ignored).
        var file = UnprocessedFiles()
            .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(3))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => ReplayHeaderProbe.IsLive(f.FullName) == true);

        if (file == null)
        {
            var had = _liveFile != null;
            // Match over: keep the estimate on screen until the final analysis replaces it, unless it went stale.
            if (had && (!File.Exists(_liveFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_liveFile!) > TimeSpan.FromMinutes(5)))
            {
                ClearLive();
                return true;
            }
            return false;
        }

        var started = false;
        if (file.FullName != _liveFile)
        {
            ClearLive();
            _liveFile = file.FullName;
            started = true;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(L.T($"[{DateTime.Now:HH:mm:ss}] Partie en cours… (ses coffres seront comptés à la fin)", $"[{DateTime.Now:HH:mm:ss}] Match in progress… (its chests will be counted at the end)", $"[{DateTime.Now:HH:mm:ss}] Partida en curso… (sus cofres se contarán al final)"));
            Console.ResetColor();
        }

        // Fortnite writes the encryption key only at the end of the match: until then the data cannot be read,
        // so we only know that a match is in progress (no useless re-reading).
        if (!_config.LiveUpdate || ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            return started;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.LiveIntervalSeconds));
        if (file.Length == _liveSize || DateTime.UtcNow - _liveParsedUtc < interval)
        {
            return started;
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            _liveParsedUtc = DateTime.UtcNow; // try again at the next interval
            if (Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") is { Length: > 0 })
            {
                Console.Error.WriteLine($"DBG live parse failed: {ex}");
            }
            return started;
        }
        _liveSize = file.Length;
        _liveParsedUtc = DateTime.UtcNow;

        var before = LiveMatch;
        LiveMatch = record;
        var changed = before?.ChestsOpened != LiveMatch?.ChestsOpened || before?.TreasureFinds.Count != LiveMatch?.TreasureFinds.Count || (before == null) != (LiveMatch == null);
        if (changed && LiveMatch != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {L.T("Partie en cours", "Match in progress", "Partida en curso")}: {L.Count(LiveMatch.ChestsOpened, "coffre", "chest", "cofre")}"
                + (LiveMatch.TreasureFinds.Count > 0 ? $", {L.Count(LiveMatch.TreasureFinds.Count, "trésor", "treasure", "tesoro")}" : ""));
            Console.ResetColor();
        }
        return changed || started;
    }

    /// <summary>
    /// When the analysis logic changed, re-analyze the replays still on disk (records are replaced, not duplicated).
    /// Must run before any scan. Returns true when existing matches will be re-analyzed (worth telling the player).
    /// </summary>
    public bool UpgradeIfNeeded()
    {
        if (_data.AnalyzerVersion >= ReplayAnalyzer.CurrentVersion)
        {
            return false;
        }

        var hadData = _data.Matches.Count > 0;
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value != "skipped").Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }
        _data.AnalyzerVersion = ReplayAnalyzer.CurrentVersion;
        _store.SaveData(_data);
        return hadData;
    }

    private void PrintMatch(MatchRecord m, bool updated)
    {
        var total = _data.Matches.Where(x => Profiles.Key(x) == Profiles.Key(m)).Sum(x => x.ChestsOpened);
        var when = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"   {(updated ? "↻" : "✔")} {L.T("Partie du", "Match of", "Partida del")} {L.DateTimeShort(when)}");
        Console.ResetColor();
        Console.Write($" ({Pretty.Playlist(m.Playlist)}, {Pretty.Placement(m)}){L.T(" : ", ": ", ": ")}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        var destroyed = m.Chests.Count(c => c.Destroyed);
        Console.Write(L.Count(m.ChestsOpened, "coffre", "chest", "cofre")
            + (destroyed > 0 ? L.T($" (dont {L.Count(destroyed, "détruit", "", "")})", $" ({destroyed} destroyed)", $" ({L.Count(destroyed, "", "", "destruido")})") : ""));
        Console.ResetColor();
        Console.WriteLine($"  →  total{L.T(" : ", ": ", ": ")}{L.N(total)}");

        foreach (var find in m.TreasureFinds)
        {
            Console.ForegroundColor = find.FromGoldenChest ? ConsoleColor.Yellow : ConsoleColor.Magenta;
            Console.WriteLine($"   {(find.FromGoldenChest ? "★★" : "★")} {L.T("Trésor obtenu : ", "Treasure obtained: ", "Tesoro obtenido: ")}{find.DisplayName} ({Pretty.Obtained(find)})");
            if (!find.SourceIsChest)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(L.T($"   ⚑ Obtenu autrement qu'avec un coffre ({find.SourceLabel}) : fiabilité faible, sans doute une erreur d'analyse",
                    $"   ⚑ Obtained other than from a chest ({find.SourceLabel}): low reliability, probably an analysis mistake",
                    $"   ⚑ Obtenido de otra forma que con un cofre ({find.SourceLabel}): fiabilidad baja, probablemente un error de análisis"));
            }
            Console.ResetColor();
        }
        if (m.TeamTreasureFinds.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine(L.T($"   + {L.Count(m.TeamTreasureFinds.Count, "trésor trouvé", "trésors trouvés", "", "", "", "")} par l'équipe (estimation, voir le rapport)",
                $"   + {L.Count(m.TeamTreasureFinds.Count, "", "", "treasure", "treasures", "", "")} found by the team (estimate, see the report)",
                $"   + {L.Count(m.TeamTreasureFinds.Count, "", "", "", "", "tesoro encontrado", "tesoros encontrados")} por el equipo (estimación, ver el informe)"));
            Console.ResetColor();
        }
    }

    /// <summary>Totals of the profile shown.</summary>
    public void PrintSummary()
    {
        var profile = Profiles.Current(_data, _config);
        var matches = Profiles.View(_data, _config).Matches;
        var chests = matches.Sum(m => m.ChestsOpened);
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        var sb = new StringBuilder();
        if (profile != null)
        {
            sb.Append($"{profile.Name}   |   ");
        }
        sb.Append(L.T($"Parties : {L.N(matches.Count)}   |   Coffres : {L.N(chests)}   |   Trésors : {L.N(treasures)}",
            $"Matches: {L.N(matches.Count)}   |   Chests: {L.N(chests)}   |   Treasures: {L.N(treasures)}",
            $"Partidas: {L.N(matches.Count)}   |   Cofres: {L.N(chests)}   |   Tesoros: {L.N(treasures)}"));
        if (matches.Count > 0)
        {
            var avg = ((double)chests / matches.Count).ToString("F1", L.Culture);
            sb.Append(L.T($"   |   Moyenne : {avg} / partie", $"   |   Average: {avg} / match", $"   |   Media: {avg} / partida"));
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  " + sb);
        Console.ResetColor();
    }
}

/// <summary>Reads just enough of the replay file header to know whether the match is still being recorded.</summary>
public static class ReplayHeaderProbe
{
    private const uint FileMagic = 0x1CA2E27F;

    /// <summary>
    /// True when the replay is encrypted and its key is not written yet. Fortnite writes the real key only when
    /// the match ends (all zeros before): the data of a match in progress cannot be read.
    /// </summary>
    public static bool KeyMissing(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic) return true;
            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64) return true;
                fs.Seek(count * 20L, SeekOrigin.Current);
            }
            fs.Seek(12, SeekOrigin.Current);
            var len = br.ReadInt32();
            fs.Seek(len < 0 ? -2L * len : len, SeekOrigin.Current);
            br.ReadUInt32(); // bIsLive
            br.ReadInt64(); // timestamp
            br.ReadUInt32(); // compressed
            if (br.ReadUInt32() == 0) return false; // not encrypted
            var keyLen = br.ReadInt32();
            if (keyLen <= 0 || keyLen > 256) return true;
            return br.ReadBytes(keyLen).All(b => b == 0);
        }
        catch
        {
            return true;
        }
    }

    public static bool? IsLive(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != FileMagic)
            {
                return null;
            }

            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32();
                if (count < 0 || count > 64)
                {
                    return null;
                }
                fs.Seek(count * 20L, SeekOrigin.Current);
            }

            br.ReadUInt32(); // LengthInMS
            br.ReadUInt32(); // NetworkVersion
            br.ReadUInt32(); // Changelist

            var len = br.ReadInt32(); // FriendlyName (FString)
            if (len < 0)
            {
                fs.Seek(-2L * len, SeekOrigin.Current);
            }
            else
            {
                fs.Seek(len, SeekOrigin.Current);
            }

            return br.ReadUInt32() != 0;
        }
        catch
        {
            return null;
        }
    }
}
