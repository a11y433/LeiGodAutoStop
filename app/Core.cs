using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace LeiGodAutoStop;

public enum SessionKnowledge { Unknown, NotAccelerating, Accelerating }
public sealed record LeiGodProcess(int Pid, string Name, string? Path, DateTime StartTime);
public sealed record AccelerationSession(string GameId, string GameName, int ClientPid, DateTime ClientStart, DateTime EventTime, bool Active, IReadOnlyList<string>? GameProcesses = null);
public sealed record SessionSnapshot(SessionKnowledge Knowledge, AccelerationSession? Session, string Reason)
{
    public IReadOnlyList<AccelerationSession> ActiveSessions { get; init; } = [];
    public IReadOnlySet<string> StoppedGameIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> StoppedGameMappings { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    public bool StopStateConfirmed { get; init; }
    public bool SdkIdleFallbackAllowed { get; init; }
}
public sealed record GameProcess(int Pid, string Name, string? Path, DateTime StartTime, string Evidence);
public sealed record GameSnapshot(bool Known, IReadOnlyList<GameProcess> Processes, string Reason);
public sealed record GameProcessAlias(string Name, string? Path);
public sealed record GameProcessInventory(bool Complete, IReadOnlyList<GameProcess> Candidates, string Reason);
public sealed record ConfirmedGameMapping(string GameId, GameProcessAlias Alias);
public sealed record LogFormatInfo(bool Supported, string Format, string Detail, int FilesExamined, int EventsRead);

public static class LeiGodDiscovery
{
    private static readonly string[] ClientNames = ["leigod", "leishensdk"];
    public static IReadOnlyList<LeiGodProcess> FindProcesses()
    {
        var result = new List<LeiGodProcess>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception ex) { throw new InvalidOperationException("无法读取雷神客户端进程清单", ex); }
        foreach (var p in processes)
        {
            try
            {
                var name = p.ProcessName;
                if (!ClientNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var path = SafePath(p);
                if (path is null) throw new InvalidOperationException($"无法确认雷神进程 PID {p.Id} 的路径");
                result.Add(new(p.Id, name, path, p.StartTime));
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { throw new InvalidOperationException("读取雷神进程身份失败", ex); }
            finally { p.Dispose(); }
        }
        return result;
    }
    public static string? SafePath(Process p)
    {
        try { var path = p.MainModule?.FileName; if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path); } catch { }
        if (!OperatingSystem.IsWindows()) return null;
        var handle = OpenProcess(0x1000, false, p.Id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(32768); var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? Path.GetFullPath(buffer.ToString()) : null;
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder imageName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    public static IReadOnlyList<string> FindRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in FindProcesses()) if (p.Path is not null) roots.Add(Path.GetDirectoryName(p.Path)!);
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        foreach (var keyPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try { using var key = hive.OpenSubKey(keyPath); foreach (var name in key?.GetSubKeyNames() ?? []) { using var sub = key!.OpenSubKey(name); if ((sub?.GetValue("DisplayName") as string)?.Contains("雷神", StringComparison.OrdinalIgnoreCase) == true || (sub?.GetValue("DisplayName") as string)?.Contains("LeiGod", StringComparison.OrdinalIgnoreCase) == true) { var loc = sub?.GetValue("InstallLocation") as string; if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc)) roots.Add(Path.GetFullPath(loc)); } } } catch { }
        }
        return roots.ToArray();
    }
    public static IReadOnlyList<string> FindLogDirectories(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return [];
        var candidates = new[]
        {
            Path.Combine(root, "resources", "llogs"),
            Path.Combine(root, "resources", "logs"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "log")
        };
        return candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string? FindLogDirectory(string? root) => FindLogDirectories(root)
        .OrderByDescending(HasLogFiles)
        .FirstOrDefault();
    private static bool HasLogFiles(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly).Any(file => Path.GetExtension(file) is ".log" or ".txt"); }
        catch { return false; }
    }
}

public sealed class SessionReader
{
    private static readonly Regex Prefix = new(@"^\[(?<pid>\d+)\]\[(?<time>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex JsonString = new("[\\\"'](?<key>game_id|id|game_name|game_process|status)[\\\"']\\s*:\\s*[\\\"'](?<value>[^\\\"']*)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GameIdNumber = new("[\\\"'](?<key>game_id|id)[\\\"']\\s*:\\s*(?<value>\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] TimeKeys = ["time", "timestamp", "created_at", "createdAt", "ts"];
    private static readonly string[] PidKeys = ["pid", "process_id", "processId", "client_pid", "clientPid"];

    public LogFormatInfo Probe(string? logDirectory, int currentClientPid, DateTime clientStart)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
            return new(false, "missing", "雷神日志目录不存在或不可访问", 0, 0);
        try
        {
            var files = FindLogFiles(logDirectory).ToArray();
            if (files.Length == 0) return new(false, "missing", "日志目录中没有可读取的日志文件", 0, 0);
            var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var events = 0;
            foreach (var file in files)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!TryReadEvent(line, out var pid, out var when, out _, out var format)) continue;
                    formats.Add(format);
                    if (pid == currentClientPid && when >= clientStart.AddSeconds(-2)) events++;
                }
            }
            if (events == 0) return new(false, formats.Count == 0 ? "unknown" : string.Join("+", formats), "日志格式可读，但没有本次客户端 PID 的可用事件", files.Length, 0);
            var formatName = string.Join("+", formats.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return new(true, formatName, formatName.Contains("json", StringComparison.OrdinalIgnoreCase) ? "已识别 JSON 日志格式" : "已识别雷神括号前缀日志格式", files.Length, events);
        }
        catch (UnauthorizedAccessException) { return new(false, "denied", "系统拒绝读取雷神日志文件", 0, 0); }
        catch (IOException ex) { return new(false, "io-error", "读取雷神日志失败：" + ex.GetType().Name, 0, 0); }
    }

    public SessionSnapshot Read(string? logDirectory, int currentClientPid, DateTime clientStart)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory)) return new(SessionKnowledge.Unknown, null, "雷神日志目录不可用");
        try
        {
            var files = FindLogFiles(logDirectory).ToArray();
            if (files.Length == 0) return new(SessionKnowledge.Unknown, null, "没有当前会话日志");
            var lines = new List<(DateTime Time, string Text)>();
            foreach (var file in files)
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!TryReadEvent(line, out var pid, out var when, out var normalized, out _))
                    {
                        if (line.Contains("start-acc", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("stop-acc", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("launch-local-game", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("accelerating-status-change", StringComparison.OrdinalIgnoreCase))
                            return new(SessionKnowledge.Unknown, null, "发现无法解析身份或时间的会话事件；禁止使用空闲回退");
                        continue;
                    }
                    if (pid != currentClientPid) continue;
                    if (when >= clientStart.AddSeconds(-2)) lines.Add((when, normalized));
                }
            }
            if (lines.Count == 0) return new(SessionKnowledge.Unknown, null, "当前 PID 与本次启动没有可用日志事件");
            var active = new Dictionary<string, AccelerationSession>(StringComparer.OrdinalIgnoreCase);
            var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var launchMaps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var stopRequested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The client can emit status=normal before the later stop-acc
            // IPC line (and before its op=0 launch teardown). Keep that
            // observation so the subsequent stop event can complete the
            // same-session proof without relying on log ordering.
            var normalObserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unpairedNormalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalConfirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stopProofTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var knownGameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var latestStopGroup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var teardownPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sawEvent = false; var contradiction = false;
            foreach (var (when, line) in lines.OrderBy(x => x.Time))
            {
                bool start = line.Contains("ipc handle start-acc", StringComparison.OrdinalIgnoreCase);
                bool stop = line.Contains("ipc handle stop-acc", StringComparison.OrdinalIgnoreCase);
                bool launch = line.Contains("ipc handle launch-local-game", StringComparison.OrdinalIgnoreCase);
                bool statusEvent = line.Contains("[accelerating-status-change]", StringComparison.OrdinalIgnoreCase);
                if (!start && !stop && !launch && !statusEvent) continue;
                sawEvent = true;
                var fields = JsonString.Matches(line).Cast<Match>().GroupBy(m => m.Groups["key"].Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(m => m.Groups["value"].Value).Last(), StringComparer.OrdinalIgnoreCase);
                string? id = fields.GetValueOrDefault("game_id") ?? fields.GetValueOrDefault("id");
                if (id is null)
                {
                    var numeric = GameIdNumber.Matches(line).Cast<Match>().LastOrDefault();
                    if (numeric is not null) id = numeric.Groups["value"].Value;
                }
                // LeiGod emits a teardown launch-local-game event after a stop:
                // {"op":0,"game_process":"",...}.  It intentionally has no
                // game_id and must not make an otherwise valid later session
                // unknown.  Any other id-less launch event remains fail-closed.
                if (launch && string.IsNullOrWhiteSpace(id))
                {
                    var op = Regex.Match(line, "\\\"op\\\"\\s*:\\s*(?<value>\\d+)");
                    var raw = fields.GetValueOrDefault("game_process");
                    var names = (raw ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (op.Success && op.Groups["value"].Value == "0" && fields.ContainsKey("game_process") && names.Length == 0 && active.Count == 0 && started.Count == 0) continue;
                    contradiction = true;
                    continue;
                }
                var hasAny = start || launch || statusEvent || stop &&
                    (active.Count > 0 || started.Count > 0 ||
                     !string.IsNullOrWhiteSpace(id) && knownGameIds.Contains(id) ||
                     string.IsNullOrWhiteSpace(id) && unpairedNormalIds.Count > 0);
                if (!hasAny) continue;
                if (string.IsNullOrWhiteSpace(id) && stop)
                {
                    if (active.Count == 1)
                    {
                        var only = active.Keys.Single(); stopRequested.Add(only); knownGameIds.Add(only); active.Clear(); started.Clear(); teardownPending.Remove(only);
                        if (normalObserved.Contains(only)) { normalConfirmed.Add(only); latestStopGroup.Add(only); stopProofTimes[only] = when; unpairedNormalIds.Remove(only); }
                        continue;
                    }
                    if (active.Count == 0 && started.Count == 0 && unpairedNormalIds.Count > 0)
                    {
                        foreach (var normalId in unpairedNormalIds) { stopRequested.Add(normalId); normalConfirmed.Add(normalId); latestStopGroup.Add(normalId); stopProofTimes[normalId] = when; teardownPending.Remove(normalId); }
                        unpairedNormalIds.Clear();
                        continue;
                    }
                    if (active.Count > 1 || knownGameIds.Count > 0) contradiction = true;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit)) { contradiction = true; continue; }
                if (launch)
                {
                    var raw = fields.GetValueOrDefault("game_process");
                    var names = (raw ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (names.Length == 0)
                    {
                        var op = Regex.Match(line, "\\\"op\\\"\\s*:\\s*(?<value>\\d+)");
                        if (active.ContainsKey(id) && op.Success && op.Groups["value"].Value == "0") teardownPending.Add(id);
                        else if (active.ContainsKey(id)) contradiction = true;
                        continue;
                    }
                    foreach (var name in names) if (Path.GetFileName(name) != name || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) contradiction = true;
                    var currentNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (launchMaps.TryGetValue(id, out var prior) && !prior.SetEquals(currentNames)) { contradiction = true; continue; }
                    launchMaps[id] = currentNames;
                    continue;
                }
                if (start)
                {
                    if (active.Count == 0 && started.Count == 0)
                    {
                        // Start a new client-session epoch. Do not let a
                        // previous failed attempt poison a later complete
                        // running session, or reuse its process mapping.
                        if (knownGameIds.Count > 0) launchMaps.Clear();
                        contradiction = false;
                        knownGameIds.Clear(); stopRequested.Clear(); normalObserved.Clear(); normalConfirmed.Clear(); latestStopGroup.Clear(); stopProofTimes.Clear(); teardownPending.Clear(); unpairedNormalIds.Clear();
                    }
                    started.Add(id); knownGameIds.Add(id); stopRequested.Remove(id); normalObserved.Remove(id); normalConfirmed.Remove(id); latestStopGroup.Remove(id); stopProofTimes.Remove(id); teardownPending.Remove(id); unpairedNormalIds.Remove(id); continue;
                }
                if (stop)
                {
                    stopRequested.Add(id); knownGameIds.Add(id); active.Remove(id); started.Remove(id);
                    teardownPending.Remove(id);
                    if (normalObserved.Contains(id)) { normalConfirmed.Add(id); latestStopGroup.Add(id); stopProofTimes[id] = when; unpairedNormalIds.Remove(id); }
                    continue;
                }
                if (statusEvent)
                {
                    var state = fields.GetValueOrDefault("status");
                    if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!started.Contains(id)) { contradiction = true; continue; }
                        var gameName = fields.GetValueOrDefault("game_name") ?? id;
                        active[id] = new(id, gameName, currentClientPid, clientStart, when, true);
                        started.Remove(id);
                    }
                    else if (string.Equals(state, "normal", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        active.Remove(id); started.Remove(id); knownGameIds.Add(id);
                        normalObserved.Add(id);
                        if (stopRequested.Contains(id)) { normalConfirmed.Add(id); latestStopGroup.Add(id); stopProofTimes[id] = when; unpairedNormalIds.Remove(id); }
                        else unpairedNormalIds.Add(id);
                    }
                    else if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
                    { started.Remove(id); if (active.ContainsKey(id)) contradiction = true; }
                }
            }
            if (!sawEvent) return new(SessionKnowledge.Unknown, null, "当前日志尚无已识别的加速会话；等待完整会话证据") { SdkIdleFallbackAllowed = false };
            if (contradiction || started.Count > 0 || teardownPending.Count > 0) return new(SessionKnowledge.Unknown, null, "当前客户端会话日志缺少完整、无矛盾的加速状态证据");
            foreach (var id in active.Keys.ToArray())
            {
                if (launchMaps.TryGetValue(id, out var names) && names.Count > 0)
                    active[id] = active[id] with { GameProcesses = names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() };
            }
            if (active.Count == 0)
            {
                if (knownGameIds.Count == 0 || !knownGameIds.All(id => stopRequested.Contains(id) && normalConfirmed.Contains(id)) || !latestStopGroup.SetEquals(knownGameIds)) return new(SessionKnowledge.Unknown, null, "没有活动会话，但缺少每个游戏 ID 的 stop-acc 与 normal/stopped 日志配对证据");
                if (latestStopGroup.Any(id => !stopProofTimes.ContainsKey(id))) return new(SessionKnowledge.Unknown, null, "停止事件证明时间不完整");
                var last = latestStopGroup.Max(id => stopProofTimes[id]);
                var stopped = new AccelerationSession("", "", currentClientPid, clientStart, last, false, []);
                return new(SessionKnowledge.NotAccelerating, stopped, "当前客户端日志确认所有加速会话均已停止") { StopStateConfirmed = true, StoppedGameIds = latestStopGroup.ToHashSet(StringComparer.OrdinalIgnoreCase), StoppedGameMappings = latestStopGroup.ToDictionary(id => id, id => (IReadOnlyList<string>)(launchMaps.GetValueOrDefault(id) ?? []).ToArray(), StringComparer.OrdinalIgnoreCase) };
            }
            var sessions = active.Values.OrderBy(x => x.EventTime).ToArray();
            var latest = sessions[^1];
            return new(SessionKnowledge.Accelerating, latest, $"本次客户端日志确认 {sessions.Length} 个运行中的加速游戏会话") { ActiveSessions = sessions };
        }
        catch (Exception ex) { return new(SessionKnowledge.Unknown, null, "读取会话日志失败: " + ex.GetType().Name); }
    }

    private static IEnumerable<string> FindLogFiles(string logDirectory)
    {
        var patterns = new[] { "*.log", "*.txt" };
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(logDirectory, pattern, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryReadEvent(string raw, out int pid, out DateTime when, out string normalized, out string format)
    {
        pid = 0; when = default; normalized = raw; format = "unknown";
        var prefix = Prefix.Match(raw);
        if (prefix.Success && int.TryParse(prefix.Groups["pid"].Value, out pid))
        {
            if (!DateTime.TryParse(prefix.Groups["time"].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out when)) return false;
            format = "legacy-bracket";
            return true;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;
            JsonElement pidNode = default, timeNode = default;
            foreach (var key in PidKeys) if (root.TryGetProperty(key, out pidNode)) break;
            foreach (var key in TimeKeys) if (root.TryGetProperty(key, out timeNode)) break;
            if (pidNode.ValueKind == JsonValueKind.Undefined || timeNode.ValueKind == JsonValueKind.Undefined) return false;
            if (pidNode.ValueKind == JsonValueKind.Number) { if (!pidNode.TryGetInt32(out pid)) return false; }
            else if (!int.TryParse(pidNode.ToString(), out pid)) return false;
            if (!TryParseLogTime(timeNode, out when)) return false;
            format = "json-lines";
            var message = root.TryGetProperty("message", out var messageNode) ? messageNode.ToString() : raw;
            var processNames = root.TryGetProperty("game_process", out var processNode) && processNode.ValueKind == JsonValueKind.Array
                ? string.Join(",", processNode.EnumerateArray().Select(item => item.ToString()))
                : null;
            normalized = $"[{pid}][{when:yyyy-MM-dd HH:mm:ss.fff}] {message} {raw}" + (string.IsNullOrWhiteSpace(processNames) ? "" : $" \"game_process\":\"{processNames}\"");
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseLogTime(JsonElement node, out DateTime when)
    {
        when = default;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var number))
        {
            try { when = number > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number).LocalDateTime : DateTimeOffset.FromUnixTimeSeconds(number).LocalDateTime; return true; }
            catch { return false; }
        }
        return DateTime.TryParse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out when);
    }
}

public sealed class GameDetector
{
    private readonly string _aliasFile;
    private static readonly Dictionary<string, GameProcessAlias[]> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["231"] = [new("DeadByDaylight.exe", null), new("DeadByDaylight-Win64-Shipping.exe", null), new("DeadByDaylight-EGS-Shipping.exe", null)],
        // Verified from LeiGod's game 1727 launch-local-game history on this machine.
        // These names are portable; paths are intentionally resolved on each computer.
        ["1727"] = [new("r5apex.exe", null), new("r5apex_dx12.exe", null)],
        // LeiGod's local catalog maps game ID 917 to Left 4 Dead 2 (Steam app 550).
        // The installed game executable was verified on this machine; use its name
        // without a source-machine path so other installations can resolve it.
        ["917"] = [new("left4dead2.exe", null)],
        // LeiGod's local catalog maps Terraria's Steam app 105600 to IDs 502,
        // 921, and child ID 2246. The installed portable executable is Terraria.exe.
        ["502"] = [new("Terraria.exe", null)],
        ["921"] = [new("Terraria.exe", null)],
        ["2246"] = [new("Terraria.exe", null)]
    };
    private readonly Dictionary<string, List<GameProcessAlias>> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlySet<string>, GameProcessInventory>? _inventoryProvider;
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    { "game.exe", "game-win64-shipping.exe", "launcher.exe", "start.exe", "client.exe", "gameclient.exe", "bootstrapper.exe", "shipping.exe" };
    private static readonly HashSet<string> NonGameProcessNames = new(StringComparer.OrdinalIgnoreCase)
    { "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe", "start_protected_game.exe", "steam.exe", "steamwebhelper.exe", "eadesktop.exe", "eaapp.exe", "epicgameslauncher.exe", "ACCStd.exe" };
    public GameDetector(Func<IReadOnlySet<string>, GameProcessInventory>? inventoryProvider = null, string? aliasFilePath = null)
    {
        _inventoryProvider = inventoryProvider;
        _aliasFile = aliasFilePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop", "aliases.json");
        foreach (var (id, entries) in BuiltIn) _aliases[id] = entries.ToList();
        try
        {
            if (!File.Exists(_aliasFile)) return;
            var json = File.ReadAllText(_aliasFile);
            Dictionary<string, List<GameProcessAlias>>? saved;
            try { saved = JsonSerializer.Deserialize<Dictionary<string, List<GameProcessAlias>>>(json); }
            catch { saved = null; }
            // Upgrade the first app build's name-only configuration in memory.
            if (saved is null || saved.Values.Any(v => v is null))
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
                if (legacy is not null) saved = legacy.ToDictionary(kv => kv.Key, kv => kv.Value.Select(name => new GameProcessAlias(name, null)).ToList(), StringComparer.OrdinalIgnoreCase);
            }
            if (saved is null) return;
            foreach (var (id, entries) in saved)
            {
                if (!_aliases.TryGetValue(id, out var aliases)) _aliases[id] = aliases = [];
                foreach (var entry in entries.Where(IsValidAlias)) AddOrReplace(aliases, new(entry.Name, NormalizePath(entry.Path)));
            }
        }
        catch { /* A damaged mapping must never enable an idle decision. */ }
    }
    public void ConfirmProcess(string gameId, GameProcess process)
    {
        if (!int.TryParse(gameId, out var id) || id <= 0 || !process.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(process.Name) != process.Name) throw new ArgumentException("无效游戏 ID 或进程名");
        using (var current = Process.GetProcessById(process.Pid))
        {
            if (!current.ProcessName.Equals(Path.GetFileNameWithoutExtension(process.Name), StringComparison.OrdinalIgnoreCase) || current.StartTime != process.StartTime) throw new InvalidOperationException("所选游戏进程已变化，请重新选择");
            var currentPath = LeiGodDiscovery.SafePath(current);
            if (process.Path is not null && (currentPath is null || !SamePath(process.Path, currentPath))) throw new InvalidOperationException("所选进程路径当前无法验证或已变化，请重新选择");
            process = process with { Path = currentPath ?? process.Path };
        }
        if (process.Path is null && GenericNames.Contains(process.Name)) throw new InvalidOperationException("该通用进程名必须读取到可执行文件路径后才能确认，避免误匹配其他程序。");
        var saved = LoadSavedAliases();
        var entries = saved.TryGetValue(gameId, out var existing) ? existing.ToList() : [];
        AddOrReplace(entries, new(process.Name, NormalizePath(process.Path)));
        saved[gameId] = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_aliasFile)!);
        File.WriteAllText(_aliasFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        if (!_aliases.TryGetValue(gameId, out var aliases)) _aliases[gameId] = aliases = [];
        AddOrReplace(aliases, new(process.Name, NormalizePath(process.Path)));
    }

    public IReadOnlyList<ConfirmedGameMapping> GetConfirmedMappings()
    {
        var saved = LoadSavedAliases();
        return saved.SelectMany(pair => pair.Value.Select(alias => new ConfirmedGameMapping(pair.Key, alias)))
            .OrderBy(mapping => mapping.GameId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mapping => mapping.Alias.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool RemoveConfirmedMapping(ConfirmedGameMapping mapping)
    {
        var saved = LoadSavedAliases();
        if (!saved.TryGetValue(mapping.GameId, out var entries)) return false;
        var removed = entries.RemoveAll(entry => entry.Name.Equals(mapping.Alias.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizePath(entry.Path), NormalizePath(mapping.Alias.Path), StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) return false;
        if (entries.Count == 0) saved.Remove(mapping.GameId);
        else saved[mapping.GameId] = entries;
        Directory.CreateDirectory(Path.GetDirectoryName(_aliasFile)!);
        File.WriteAllText(_aliasFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        if (_aliases.TryGetValue(mapping.GameId, out var inMemory))
        {
            inMemory.RemoveAll(entry => entry.Name.Equals(mapping.Alias.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizePath(entry.Path), NormalizePath(mapping.Alias.Path), StringComparison.OrdinalIgnoreCase));
            if (inMemory.Count == 0) _aliases.Remove(mapping.GameId);
        }
        return true;
    }
    public GameProcessInventory CandidateProcesses(AccelerationSession session)
    {
        var names = CandidateNames(session);
        if (names.Count == 0) return new(false, [], "雷神日志没有可用游戏进程名，且此游戏 ID 没有已核实的内置映射；不会列出全部系统进程");
        try
        {
            var inventory = _inventoryProvider?.Invoke(names) ?? CaptureProcesses(names);
            if (_aliases.TryGetValue(session.GameId, out var configured))
            {
                var ignored = configured.Where(entry => !names.Contains(entry.Name)).Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (ignored.Length > 0) inventory = inventory with { Reason = inventory.Reason + "；已忽略不属于本次会话/已核实映射的旧项：" + string.Join("、", ignored) };
            }
            return inventory;
        }
        catch { return new(false, [], "无法读取当前游戏进程清单"); }
    }
    private static HashSet<string> ReliableProcessNames(AccelerationSession session)
    {
        var names = (session.GameProcesses ?? []).Where(IsGameProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (BuiltIn.TryGetValue(session.GameId, out var known))
            foreach (var alias in known) names.Add(alias.Name);
        return names;
    }
    private HashSet<string> CandidateNames(AccelerationSession session)
    {
        var names = ReliableProcessNames(session);
        // A previously confirmed mapping is a bounded fallback for an unknown game ID;
        // never use it to override the verified set for a known ID such as Apex.
        if (names.Count == 0 && !BuiltIn.ContainsKey(session.GameId) && _aliases.TryGetValue(session.GameId, out var confirmed))
            foreach (var entry in confirmed.Where(entry => IsGameProcessName(entry.Name))) names.Add(entry.Name);
        return names;
    }
    private static bool IsGameProcessName(string name) => !string.IsNullOrWhiteSpace(name) &&
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(name) == name &&
        !NonGameProcessNames.Contains(name);

    public static bool StillMatchesLiveProcess(GameProcess expected)
    {
        try
        {
            using var current = Process.GetProcessById(expected.Pid);
            return current.StartTime == expected.StartTime &&
                   current.ProcessName.Equals(Path.GetFileNameWithoutExtension(expected.Name), StringComparison.OrdinalIgnoreCase) &&
                   expected.Path is not null && string.Equals(LeiGodDiscovery.SafePath(current), expected.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    public GameSnapshot Find(AccelerationSession? session) => Find(session, null);

    private GameSnapshot Find(AccelerationSession? session, DateTime? processStartedBefore)
    {
        if (session is null) return new(false, [], "无已确认会话");
        var configured = _aliases.TryGetValue(session.GameId, out var saved) ? saved : [];
        // The current client's launch-local-game mapping is authoritative for
        // this session. Persisted aliases only contribute a previously
        // confirmed full path for one of those current names; stale names are
        // never unioned into the process watch set.
        var currentNames = CandidateNames(session);
        if (currentNames.Count == 0) return new(false, [], "此游戏 ID 缺少可用的雷神进程名或已核实的内置映射；等待识别，不判定空闲");
        // Built-in names override stale saved aliases for the same game ID. In particular,
        // a historical mistaken mapping must not make an unrelated utility count as Apex.
        var aliases = configured.Where(a => currentNames.Contains(a.Name))
            .Select(a => new GameProcessAlias(a.Name, a.Path)).ToList();
        foreach (var (id, entries) in BuiltIn)
        {
            if (!id.Equals(session.GameId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var entry in entries) AddOrReplace(aliases, entry);
        }
        foreach (var name in currentNames) AddOrReplace(aliases, new(name, null));
        if (aliases.Count == 0) return new(false, [], "此游戏 ID 缺少本次雷神日志或已确认的进程映射");
        var aliasNames = aliases.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        GameProcessInventory inventory;
        try { inventory = _inventoryProvider?.Invoke(aliasNames) ?? CaptureProcesses(aliasNames); }
        catch { return new(false, [], "进程清单读取失败；不能安全判定游戏已退出"); }
        if (!inventory.Complete) return new(false, [], inventory.Reason);
        // A new process instance launched after the confirmed stop belongs to an
        // unaccelerated run, not the stopped accelerated session being verified.
        if (processStartedBefore is { } stopTime)
            inventory = inventory with { Candidates = inventory.Candidates.Where(p => p.StartTime <= stopTime).ToArray() };
        var matches = new Dictionary<int, GameProcess>(); var ambiguous = new List<string>();
        var sameNameCandidates = inventory.Candidates.Where(p => aliases.Any(a => a.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var process in sameNameCandidates)
        {
            var n = process.Name; var applicable = aliases.Where(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (applicable.Length == 0) continue;
            var path = process.Path;
            if (path is null) return new(false, [], "候选进程路径读取失败；不能安全判定游戏已退出");
            var pathBound = applicable.Where(a => a.Path is not null).ToArray();
            var selected = pathBound.Where(a => SamePath(a.Path!, path)).ToArray();
            if (selected.Length > 0) { matches[process.Pid] = process with { Evidence = "用户确认的完整可执行文件路径" }; continue; }
            // A known executable name appearing at a different path is contradictory evidence,
            // not proof that the mapped executable is absent.
            if (pathBound.Length > 0) { ambiguous.Add($"{n} (PID {process.Pid}, 路径与已确认映射不同)"); continue; }
            if (applicable.Any(a => a.Path is null))
            {
                var sameNameCount = inventory.Candidates.Count(other => other.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (GenericNames.Contains(n) || sameNameCount != 1) { ambiguous.Add($"{n} (PID {process.Pid}, {sameNameCount} 个同名进程)"); continue; }
                matches[process.Pid] = process with { Evidence = "雷神日志/已核实规则或已确认映射的唯一进程名" };
            }
        }
        if (ambiguous.Count > 0) return new(false, matches.Values.ToArray(), "同名或通用可执行文件无法唯一匹配，请重新确认完整路径: " + string.Join(", ", ambiguous.Distinct()));
        return new(true, matches.Values.ToArray(), matches.Count > 0 ? "当前加速游戏进程匹配" : "已确认路径/别名下的游戏进程均不存在");
    }
    public GameSnapshot Find(SessionSnapshot snapshot)
    {
        if (snapshot.Knowledge != SessionKnowledge.Accelerating) return new(false, [], "无已确认的活动加速会话");
        var sessions = snapshot.ActiveSessions.Count > 0 ? snapshot.ActiveSessions : snapshot.Session is null ? [] : [snapshot.Session];
        if (sessions.Count == 0) return new(false, [], "活动加速游戏清单缺失");
        var all = new Dictionary<int, GameProcess>();
        foreach (var session in sessions)
        {
            var part = Find(session);
            if (!part.Known) return new(false, [], $"加速游戏 {session.GameId} 的进程映射不完整: {part.Reason}");
            foreach (var process in part.Processes) all[process.Pid] = process;
        }
        return new(true, all.Values.ToArray(), $"已检查 {sessions.Count} 个活动加速游戏的进程并集");
    }

    public GameSnapshot FindStopped(SessionSnapshot snapshot)
    {
        if (snapshot.Knowledge != SessionKnowledge.NotAccelerating || !snapshot.StopStateConfirmed || snapshot.StoppedGameIds.Count == 0 || snapshot.Session is null) return new(false, [], "缺少完整停止事件证据");
        var all = new Dictionary<int, GameProcess>();
        foreach (var id in snapshot.StoppedGameIds)
        {
            // A confirmed stop event can omit game_process even when the game ID
            // has a verified portable mapping. Use that bounded mapping for known
            // games; unknown IDs still fail closed in Find.
            var names = snapshot.StoppedGameMappings.TryGetValue(id, out var mappedNames)
                ? mappedNames
                : Array.Empty<string>();
            var part = Find(new AccelerationSession(id, id, snapshot.Session.ClientPid, snapshot.Session.ClientStart, snapshot.Session.EventTime, false, names), snapshot.Session.EventTime);
            if (!part.Known) return part;
            foreach (var process in part.Processes) all[process.Pid] = process;
        }
        return new(true, all.Values.ToArray(), "已检查全部已停止游戏的本次客户端进程映射");
    }
    private static GameProcessInventory CaptureProcesses(IReadOnlySet<string> names)
    {
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return new(false, [], "系统拒绝或无法读取进程列表"); }
        var byPid = new Dictionary<int, string>();
        try { foreach (var p in all) byPid[p.Id] = p.ProcessName + ".exe"; }
        catch { foreach (var p in all) p.Dispose(); return new(false, [], "系统拒绝读取部分进程名称；不能安全判定游戏已退出"); }
        var candidates = new List<GameProcess>(); var complete = true;
        foreach (var p in all)
        {
            try
            {
                var name = byPid[p.Id]; if (!names.Contains(name)) continue;
                var path = LeiGodDiscovery.SafePath(p);
                try { candidates.Add(new(p.Id, name, path, p.StartTime, "系统进程清单")); if (path is null) complete = false; }
                catch { complete = false; }
            }
            catch { complete = false; }
            finally { p.Dispose(); }
        }
        return complete ? new(true, candidates, "当前进程清单已采集") : new(false, [], "候选进程信息读取失败；不能安全判定游戏已退出");
    }
    private static bool IsValidAlias(GameProcessAlias e) => e is not null && !string.IsNullOrWhiteSpace(e.Name) && e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(e.Name) == e.Name;
    private static string? NormalizePath(string? path) { try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path); } catch { return null; } }
    private static bool SamePath(string first, string second) => string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);
    private static void AddOrReplace(List<GameProcessAlias> aliases, GameProcessAlias entry) { var i = aliases.FindIndex(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) && (e.Path is null || entry.Path is null || SamePath(e.Path, entry.Path))); if (i >= 0) { if (entry.Path is not null || aliases[i].Path is null) aliases[i] = entry; } else aliases.Add(entry); }
    private Dictionary<string, List<GameProcessAlias>> LoadSavedAliases()
    {
        if (!File.Exists(_aliasFile)) return new(StringComparer.OrdinalIgnoreCase);
        var json = File.ReadAllText(_aliasFile);
        try { return JsonSerializer.Deserialize<Dictionary<string, List<GameProcessAlias>>>(json) ?? new(StringComparer.OrdinalIgnoreCase); }
        catch { var legacy = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? new(); return legacy.ToDictionary(kv => kv.Key, kv => kv.Value.Select(n => new GameProcessAlias(n, null)).ToList(), StringComparer.OrdinalIgnoreCase); }
    }
}

public interface IPauseAdapter { string Id { get; } int Version { get; } bool CanControl(string clientVersion); Task RequestPauseAsync(CancellationToken ct); Task<PauseProof> ReadPauseStateAsync(CancellationToken ct); }
public sealed record PauseProof(bool IsPaused, decimal? RemainingSeconds, DateTime ObservedAt, string Evidence);
#if LEGACY_STOP
public interface IStopAdapter { string Id { get; } int Version { get; } bool CanStopAcceleration => true; Task RequestStopAccelerationAsync(CancellationToken ct); Task<bool> IsAccelerationStoppedAsync(CancellationToken ct); Task RequestNormalExitAsync(CancellationToken ct); Task<bool> AreOwnedClientProcessesGoneAsync(CancellationToken ct); Task RequestPausedSessionExitFallbackAsync(CancellationToken ct) => throw new NotSupportedException("受控进程树退出路径未提供"); }
public sealed class DisabledPauseAdapter : IPauseAdapter { public string Id => "disabled-unverified"; public int Version => 1; public bool CanControl(string clientVersion) => false; public Task RequestPauseAsync(CancellationToken ct) => throw new NotSupportedException("No pause-state adapter has passed live acceptance."); public Task<PauseProof> ReadPauseStateAsync(CancellationToken ct) => Task.FromResult(new PauseProof(false, null, DateTime.UtcNow, "暂停状态适配器未验证")); }
/// <summary>Read-only, allowlisted account status probe. Mutation intentionally remains unavailable.</summary>
public sealed class WebApiPauseAdapter : IPauseAdapter
{
    private static readonly Uri Endpoint = new("https://webapi.leigod.com/api/user/info");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _logDirectory;
    public WebApiPauseAdapter(string logDirectory) => _logDirectory = logDirectory;
    public string Id => "leigod-user-info-readonly";
    public int Version => 1;
    public bool CanControl(string clientVersion) => false;
    public async Task RequestPauseAsync(CancellationToken ct) => await Task.FromException(new NotSupportedException("Read-only status adapter; no pause request is implemented."));
    public async Task<PauseProof> ReadPauseStateAsync(CancellationToken ct)
    {
        var token = ReadLocalToken(); if (token is null) return new(false, null, DateTime.UtcNow, "当前日志未找到 account_token");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["account_token"] = token, ["lang"] = "zh_CN", ["os_type"] = "4" }) };
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri?.Host != "webapi.leigod.com") return new(false, null, DateTime.UtcNow, "状态响应来源不符合主机白名单");
        if (!response.IsSuccessStatusCode) return new(false, null, DateTime.UtcNow, "状态接口 HTTP " + (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var root = doc.RootElement;
        if (!TryNumber(root, "code", out var code) || code != 0 || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !TryNumber(data, "pause_status_id", out var pause)) return new(false, null, DateTime.UtcNow, "响应缺少已知成功/暂停字段");
        decimal? remaining = TryNumber(data, "user_pause_time", out var remain) ? remain : null;
        return new(pause == 1, remaining, DateTime.UtcNow, "allowlisted account status response; pause_status_id=" + pause.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private string? ReadLocalToken()
    {
        if (!Directory.Exists(_logDirectory)) return null;
        foreach (var file in Directory.GetFiles(_logDirectory, "http-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
        {
            try { using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); if (stream.Length > 8_000_000) stream.Seek(-8_000_000, SeekOrigin.End); using var reader = new StreamReader(stream); var matches = Regex.Matches(reader.ReadToEnd(), @"(?i)account_token[\""' :=]+([^&\s,;\""']{12,})"); if (matches.Count > 0) return matches[^1].Groups[1].Value; } catch { }
        }
        return null;
    }
    private static bool TryNumber(JsonElement root, string name, out decimal value) { value = 0; if (!root.TryGetProperty(name, out var node)) return false; return node.ValueKind == JsonValueKind.Number ? node.TryGetDecimal(out value) : decimal.TryParse(node.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value); }
}
#endif
public static class PauseProofRules
{
    public static bool Confirmed(PauseProof first, PauseProof second) => first.IsPaused && second.IsPaused && first.RemainingSeconds.HasValue && second.RemainingSeconds.HasValue && second.ObservedAt - first.ObservedAt >= TimeSpan.FromSeconds(12) && second.RemainingSeconds.Value >= first.RemainingSeconds.Value;

    /// <summary>
    /// Stronger proof used after an automatic request.  LeiGod can briefly report
    /// pause_status_id=1 before the billing counter has stopped, so a small
    /// rounding drop is tolerated but a real countdown is rejected.
    /// </summary>
    public static bool ConfirmedStable(PauseProof first, PauseProof latest, TimeSpan minimumAge, decimal allowedDropSeconds = 2m)
        => first.IsPaused && latest.IsPaused && first.RemainingSeconds.HasValue && latest.RemainingSeconds.HasValue &&
           latest.ObservedAt - first.ObservedAt >= minimumAge &&
           latest.RemainingSeconds.Value + allowedDropSeconds >= first.RemainingSeconds.Value;
}
#if LEGACY_STOP
public sealed class DisabledStopAdapter : IStopAdapter { public string Id => "disabled-unverified"; public int Version => 1; public bool CanStopAcceleration => false; public Task RequestStopAccelerationAsync(CancellationToken ct) => throw new NotSupportedException("No stop adapter has passed live acceptance."); public Task<bool> IsAccelerationStoppedAsync(CancellationToken ct) => Task.FromResult(false); public Task RequestNormalExitAsync(CancellationToken ct) => throw new NotSupportedException("No stop adapter has passed live acceptance."); public Task<bool> AreOwnedClientProcessesGoneAsync(CancellationToken ct) => Task.FromResult(false); }

public enum MonitorState { Waiting, Unknown, NotAccelerating, GameRunning, IdleCandidate, Blocked, AutoStopDisabled }
public sealed class IdleMonitor
{
    private readonly TimeSpan _idle; private long? _since; private string? _key;
    public IdleMonitor(TimeSpan? idle = null) => _idle = idle ?? TimeSpan.FromSeconds(120);
    public MonitorState State { get; private set; } = MonitorState.Waiting;
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromSeconds(120);
    public string Reason { get; private set; } = "等待雷神";
    public bool Tick(SessionSnapshot session, GameSnapshot game, long now, bool autoEnabled = false)
    {
        if (session.Knowledge == SessionKnowledge.Unknown) return Reset(MonitorState.Unknown, "当前会话无法确认: " + session.Reason);
        if (session.Knowledge == SessionKnowledge.NotAccelerating) return Reset(MonitorState.NotAccelerating, "未确认加速; 不倒计时");
        if (session.Session is null || !game.Known) return Reset(MonitorState.Unknown, "游戏进程识别不完整: " + game.Reason);
        var activeIds = session.ActiveSessions.Count > 0 ? string.Join(",", session.ActiveSessions.OrderBy(s => s.GameId, StringComparer.Ordinal).Select(s => s.GameId + ":" + s.EventTime.Ticks)) : session.Session.GameId + ":" + session.Session.EventTime.Ticks;
        var key = $"{session.Session.ClientPid}/{session.Session.ClientStart.Ticks}/{activeIds}";
        if (!string.Equals(key, _key, StringComparison.Ordinal)) { _key = key; _since = null; }
        if (game.Processes.Count > 0) return Reset(MonitorState.GameRunning, "当前加速游戏仍在运行; 计时清零");
        _since ??= now; var elapsed = Stopwatch.GetElapsedTime(_since.Value, now); Remaining = elapsed >= _idle ? TimeSpan.Zero : _idle - elapsed;
        if (elapsed < _idle) { State = MonitorState.IdleCandidate; Reason = "当前加速游戏进程均已消失; 空闲计时中"; return false; }
        var thresholdCrossed = State == MonitorState.IdleCandidate;
        if (!autoEnabled) { State = MonitorState.AutoStopDisabled; Reason = "已达到空闲阈值; 暂停状态未现场验证，自动操作禁用"; return thresholdCrossed; }
        State = MonitorState.Blocked; Reason = "自动操作仍被未验证的暂停/停止适配器阻止"; return thresholdCrossed;
    }
    private bool Reset(MonitorState state, string reason) { _since = null; _key = null; State = state; Reason = reason; Remaining = _idle; return false; }
}

public sealed class StoppedSessionIdleMonitor
{
    private readonly TimeSpan _idle;
    private long? _startedAt;
    private (int Pid, DateTime Start, string Epoch)? _identity;
    private bool _forceFresh;
    public StoppedSessionIdleMonitor(TimeSpan? idle = null) => _idle = idle ?? TimeSpan.FromSeconds(120);
    public TimeSpan Remaining { get; private set; }
    public void Reset() { _startedAt = null; _identity = null; _forceFresh = true; Remaining = _idle; }
    public bool Tick(SessionSnapshot snapshot, GameSnapshot targetGame, int clientPid, DateTime clientStart, long now)
    {
        var valid = snapshot.Knowledge == SessionKnowledge.NotAccelerating && snapshot.StopStateConfirmed &&
            snapshot.StoppedGameIds.Count > 0 && snapshot.Session is { Active: false } stopped &&
            stopped.ClientPid == clientPid && stopped.ClientStart == clientStart;
        if (!valid) { _startedAt = null; _identity = null; _forceFresh = true; Remaining = _idle; return false; }
        var epoch = string.Join(",", snapshot.StoppedGameIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) + ":" + snapshot.Session!.EventTime.Ticks;
        var identity = (clientPid, clientStart, epoch);
        if (_identity != identity)
        {
            _identity = identity;
            var eventAge = _forceFresh ? TimeSpan.Zero : DateTime.Now - snapshot.Session!.EventTime;
            _forceFresh = false;
            if (eventAge < TimeSpan.Zero) eventAge = TimeSpan.Zero;
            if (eventAge > _idle) eventAge = _idle;
            _startedAt = now - (long)(eventAge.TotalSeconds * Stopwatch.Frequency);
        }
        var elapsed = TimeSpan.FromSeconds((now - _startedAt!.Value) / (double)Stopwatch.Frequency);
        Remaining = elapsed >= _idle ? TimeSpan.Zero : _idle - elapsed;
        return elapsed >= _idle;
    }
}

#endif
public static class SafeLog
{
    private static readonly Regex Sensitive = new(@"(?i)(token|access_token|refresh_token|hardware[_-]?id|device[_-]?id)([=:\""' ]+)[^&\s,;\""']+", RegexOptions.Compiled);
    public static string Redact(string text) => Sensitive.Replace(text, "$1$2[REDACTED]");
    public static void Write(string text) { var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop", "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + ".log"), $"{DateTimeOffset.Now:O} {Redact(text)}{Environment.NewLine}"); }
}
