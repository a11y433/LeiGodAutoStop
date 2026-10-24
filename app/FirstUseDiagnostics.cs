using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LeiGodAutoStop;

public enum DiagnosticSeverity { Pass, Warning, Fail }

public sealed record DiagnosticCheck(string Key, string Title, DiagnosticSeverity Severity, string Detail)
{
    public string Prefix => Severity switch
    {
        DiagnosticSeverity.Pass => "通过",
        DiagnosticSeverity.Warning => "提醒",
        _ => "失败"
    };
}

public sealed record DiagnosticReport(DateTimeOffset StartedAt, IReadOnlyList<DiagnosticCheck> Checks)
{
    public bool CanMonitor => new[] { "logs", "format", "account", "pause-control" }.All(key =>
        Checks.Any(c => c.Key == key && c.Severity == DiagnosticSeverity.Pass)) &&
        !Checks.Any(c => c.Key == "runner");
    public bool HasBlockingFailure => Checks.Any(c => c.Severity == DiagnosticSeverity.Fail);
    public bool HasLivePauseVerification => Checks.Any(c => c.Key == "pause-live" && c.Severity == DiagnosticSeverity.Pass);
    public string Summary => HasBlockingFailure
        ? "环境检查发现阻断项；已停止自动暂停，先处理失败原因。"
        : !CanMonitor
            ? "环境已就绪，但还缺自动暂停所需的证据；自动暂停保持关闭，程序每 30 秒自动复查。"
            : !HasLivePauseVerification
                ? "基础环境和暂停参数已就绪；暂停功能尚未通过本账号的真实暂停与余额回读验证。"
            : Checks.Any(c => c.Severity == DiagnosticSeverity.Warning)
                ? "环境基本可用，但有提醒项；请在开始游戏前复核。"
                : "环境检查全部通过。";

    public string ToDisplay()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Summary);
        builder.AppendLine();
        foreach (var check in Checks) builder.AppendLine($"[{check.Prefix}] {check.Title}：{check.Detail}");
        return builder.ToString().TrimEnd();
    }
}

public sealed class FirstUseDiagnosticRunner
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop");
    public static string MarkerPath => Path.Combine(Folder, "first-use-check.json");
    public static string FingerprintPath => Path.Combine(Folder, "pause-log-shape.txt");
    private static string LivePausePath => Path.Combine(Folder, "pause-live-verification.json");
    private static string BuildIdentity => typeof(FirstUseDiagnosticRunner).Module.ModuleVersionId.ToString("N");

    public static void RecordLivePauseSuccess(string accountIdentity)
    {
        if (string.IsNullOrWhiteSpace(accountIdentity)) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(LivePausePath, JsonSerializer.Serialize(new
            {
                AccountIdentity = accountIdentity,
                BuildIdentity,
                ConfirmedAt = DateTimeOffset.Now,
                Proof = "pause_status_and_stable_balance_readback"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static bool HasCurrentLivePauseSuccess(string? accountIdentity)
    {
        if (string.IsNullOrWhiteSpace(accountIdentity) || !File.Exists(LivePausePath)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LivePausePath));
            return doc.RootElement.TryGetProperty("AccountIdentity", out var identity) && identity.GetString() == accountIdentity &&
                   doc.RootElement.TryGetProperty("BuildIdentity", out var build) && build.GetString() == BuildIdentity &&
                   doc.RootElement.TryGetProperty("Proof", out var proof) && proof.GetString() == "pause_status_and_stable_balance_readback";
        }
        catch { return false; }
    }
    public static bool NeedsRun
    {
        get
        {
            if (!File.Exists(MarkerPath)) return true;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(MarkerPath));
                if (!doc.RootElement.TryGetProperty("Checks", out var checks) || checks.ValueKind != JsonValueKind.Array) return true;
                return checks.EnumerateArray().Any(check => check.TryGetProperty("Severity", out var severity) && severity.GetInt32() != (int)DiagnosticSeverity.Pass);
            }
            catch { return true; }
        }
    }

    public async Task<DiagnosticReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DiagnosticCheck>();
        var started = DateTimeOffset.Now;
        LeiGodProcess? client = null;
        try
        {
            if (!PauseOnlyClientDiscovery.TryFindRootClient(out client, out var discoveryReason) || client is null)
            {
                checks.Add(new("client", "雷神客户端", DiagnosticSeverity.Warning, discoveryReason + "；打开雷神后可重新检查。"));
                checks.Add(new("version", "雷神版本", DiagnosticSeverity.Warning, "客户端未运行，暂时无法读取版本。"));
                checks.Add(new("logs", "日志权限", DiagnosticSeverity.Warning, "客户端未运行，暂时无法检查日志目录。"));
                checks.Add(new("account", "登录与账户接口", DiagnosticSeverity.Warning, "客户端未运行，暂时无法读取本机登录会话。"));
                checks.Add(new("game", "游戏识别", DiagnosticSeverity.Warning, "当前没有可检查的雷神会话。"));
                return Save(new(started, checks));
            }

            var version = ReadVersion(client.Path);
            checks.Add(version is null
                ? new("version", "雷神版本", DiagnosticSeverity.Warning, "版本信息不可读；兼容性以实际功能检查为准。")
                : new("version", "雷神版本", DiagnosticSeverity.Pass, version));

            var root = client.Path is null ? null : Path.GetDirectoryName(client.Path);
            var logDirectories = LeiGodDiscovery.FindLogDirectories(root);
            if (logDirectories.Count == 0)
            {
                checks.Add(new("logs", "日志权限", DiagnosticSeverity.Fail, "找不到 resources\\llogs、resources\\logs、logs 或 log 目录。"));
                checks.Add(new("format", "日志格式", DiagnosticSeverity.Fail, "没有可探测的雷神日志；等待客户端生成日志后重新检查。"));
                checks.Add(new("account", "登录与账户接口", DiagnosticSeverity.Fail, "没有日志目录，无法读取当前客户端账号会话。"));
                checks.Add(new("game", "游戏识别", DiagnosticSeverity.Warning, "日志不可用，无法判断加速游戏。"));
                return Save(new(started, checks));
            }

            var logs = LeiGodDiscovery.FindLogDirectory(root)!;
            var probe = new SessionReader().Probe(logs, client.Pid, client.StartTime);
            checks.Add(probe.FilesExamined > 0
                ? new("logs", "日志权限", DiagnosticSeverity.Pass, $"可读取 {probe.FilesExamined} 个日志文件。")
                : new("logs", "日志权限", DiagnosticSeverity.Fail, probe.Detail));
            checks.Add(probe.Supported
                ? new("format", "日志格式", DiagnosticSeverity.Pass, $"{probe.Format}；{probe.Detail}。")
                : new("format", "日志格式", DiagnosticSeverity.Fail, $"{probe.Format}；{probe.Detail}。等待日志生成后重试；不据此判定版本不兼容。"));

            var pause = new ClientLogPauseAdapter(logs, client.Pid, client.StartTime);
            var pauseEvidence = pause.Inspect();
            WriteFingerprint(pauseEvidence, version);
            var account = await pause.ReadPauseStateAsync(cancellationToken);
            checks.Add(account.RemainingSeconds.HasValue
                ? new("account", "登录与账户接口", DiagnosticSeverity.Pass, $"账户接口可读，暂停状态={(account.IsPaused ? "已暂停" : "未暂停")}，剩余 {account.RemainingSeconds:0} 秒。")
                : new("account", "登录与账户接口", DiagnosticSeverity.Fail, account.Evidence + "；请确认已登录雷神并重新打开客户端。"));
            // 暂停能力不再绑定单一客户端版本或单一日志形态：任何一条本客户端请求行都能提供同一套账号参数，
            // 所以这里只分“可以构造请求”“日志还没写出来”“日志在写但读不到参数”三种情况，且都不判版本不兼容。
            var parameterSource = pauseEvidence.RequestSource switch
            {
                "pause-line" => "本机日志已出现 /client/pause 请求行",
                "client-http-line" => "本机日志没有 /client/pause 请求行，已改用同一客户端其它请求行里的同一套账号参数",
                _ => ""
            };
            var bodySource = pauseEvidence.BodySource == "observed"
                ? $"本机实测正文 {pauseEvidence.UsableBodyLines} 条"
                : "开发机日志样本回退正文（目标账号尚未验证；仍以账户两次回读为准）";
            if (pauseEvidence.HasUsableRequest)
                checks.Add(new("pause-control", "暂停请求参数", DiagnosticSeverity.Pass, $"{parameterSource}，{bodySource}。这表示请求可以构造；是否被当前账号接受，需由真实请求和余额回读确认。"));
            else if (pauseEvidence.ClientLines == 0)
                checks.Add(new("pause-control", "暂停能力", DiagnosticSeverity.Warning,
                    $"还没有读到本客户端日志行（已扫 {pauseEvidence.FilesScanned} 个日志）。客户端刚启动时属正常，程序每 30 秒自动复查。"));
            else if (pauseEvidence.PausePathLines > 0)
                checks.Add(new("pause-control", "暂停能力", DiagnosticSeverity.Warning,
                    $"读到 {pauseEvidence.PausePathLines} 条含 /client/pause 的日志行，但都没有可解析的账号参数。可在雷神里手动暂停再恢复一次，让客户端写出完整请求行；脱敏结构指纹：{FingerprintPath}。"));
            else
                checks.Add(new("pause-control", "暂停能力", DiagnosticSeverity.Warning,
                    $"已读 {pauseEvidence.ClientLines} 条本客户端日志，但还没有带 account_token 的请求行。在雷神里手动暂停或恢复一次通常就会生成；脱敏结构指纹：{FingerprintPath}。"));

            checks.Add(HasCurrentLivePauseSuccess(pause.ReadAccountIdentity())
                ? new("pause-live", "暂停功能现场验证", DiagnosticSeverity.Pass, "当前账号、本程序构建已完成真实暂停请求，并由账户接口确认暂停及余额稳定。")
                : new("pause-live", "暂停功能现场验证", DiagnosticSeverity.Warning, "尚无当前账号、本程序构建的真实暂停及余额稳定回读记录；首次自动暂停成功后会记录。"));

            var snapshot = new SessionReader().Read(logs, client.Pid, client.StartTime);
            if (snapshot.Knowledge == SessionKnowledge.Accelerating)
            {
                var game = new GameDetector().Find(snapshot);
                // 识别不到进程只影响“自动判定空闲”，不影响能否暂停账户时长，所以这里是提醒而不是阻断项。
                checks.Add(game.Known
                    ? new("game", "游戏识别", DiagnosticSeverity.Pass, game.Reason + "。")
                    : new("game", "游戏识别", DiagnosticSeverity.Warning, game.Reason + "；此时会保持等待，不会猜测或列出全部系统进程。若雷神日志后续提供进程名，或该游戏有已核实映射，程序会自动恢复识别。"));
            }
            else if (snapshot.Knowledge == SessionKnowledge.NotAccelerating)
                checks.Add(new("game", "游戏识别", DiagnosticSeverity.Warning, "当前没有活动的雷神加速游戏会话；从雷神启动游戏后会自动识别。"));
            else
                checks.Add(new("game", "游戏识别", DiagnosticSeverity.Warning, snapshot.Reason + "；从雷神启动游戏后会自动复查。"));
        }
        catch (OperationCanceledException)
        {
            checks.Add(new("runner", "检查过程", DiagnosticSeverity.Warning, "检查被取消；没有改变雷神或账户状态。"));
        }
        catch (Exception ex)
        {
            checks.Add(new("runner", "检查过程", DiagnosticSeverity.Fail, $"{ex.GetType().Name}：{SafeLog.Redact(ex.Message)}"));
        }
        return Save(new(started, checks));
    }

    private static DiagnosticReport Save(DiagnosticReport report)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new { report.StartedAt, report.Summary, Checks = report.Checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return report;
    }

    /// <summary>
    /// Writes a token-free description of the pause request lines found in the current client logs.
    /// The file lets a different computer report its log structure without sharing account tokens or request bodies.
    /// </summary>
    private static void WriteFingerprint(PauseLogEvidence evidence, string? version)
    {
        if (string.IsNullOrWhiteSpace(evidence.Fingerprint)) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var text = new StringBuilder()
                .AppendLine("雷神暂停请求日志结构指纹（不含令牌和请求正文）")
                .AppendLine("生成时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
                .AppendLine("客户端版本：" + (version ?? "unknown"))
                .AppendLine($"日志文件 {evidence.FilesScanned} 个；本客户端日志行 {evidence.ClientLines} 条；含 /client/pause 的行 {evidence.PausePathLines} 条；匹配的请求行 {evidence.StatusRequestLines} 条；可解析正文 {evidence.UsableBodyLines} 条；参数来源={evidence.RequestSource}；正文来源={evidence.BodySource}")
                .AppendLine("结构：" + evidence.Fingerprint)
                .ToString();
            File.WriteAllText(FingerprintPath, text);
        }
        catch { }
    }

    private static string? ReadVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var values = new[] { info.ProductName, info.ProductVersion }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            return values.Length == 0 ? null : string.Join(" ", values);
        }
        catch { return null; }
    }
}
