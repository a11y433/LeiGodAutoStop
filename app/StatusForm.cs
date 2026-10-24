using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;

namespace LeiGodAutoStop;
public sealed class StatusForm : Form
{
    private readonly Label _headline = new() { AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) };
    private readonly Label _details = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly Button _observe = new() { Text = "停止监控", AutoSize = true };
    private readonly CheckBox _autoMode = new() { Text = "模式：仅观察", AutoSize = true, Checked = false };
    private readonly Button _verifyWorkflow = new() { Text = "验证自动流程（一次）", AutoSize = true };
    private readonly Label _workflowResults = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly Button _confirmGame = new() { Text = "确认当前游戏进程", AutoSize = true };
    private readonly Button _install = new() { Text = "安装为当前用户登录启动", AutoSize = true };
    private readonly Button _uninstall = new() { Text = "卸载登录启动", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly NotifyIcon _trayIcon;
    private bool _exitRequested;
    private bool _sdkIdleProbeRunning;
    private long? _sdkIdleSince;
    private long? _sdkIdleLastProof;
    private string? _sdkIdleClientKey;
    private readonly bool _backgroundLaunch;
    private readonly bool _verificationLaunch;
    private bool _autoModeWanted;
    private string? _lastAutoArmClient;
    private readonly SessionReader _reader = new(); private readonly GameDetector _games = new(); private IdleMonitor _monitor = new(); private readonly StoppedSessionIdleMonitor _stoppedSessionMonitor = new(); private string? _root; private bool _stopped; private AccelerationSession? _currentSession; private LeiGodProcess? _currentClient; private bool _workflowRunning; private bool _verificationMode; private AccelerationSession? _verificationSession; private string? _verificationClientVersion; private string? _verificationClientHash; private string? _verificationAppSha256; private bool _workflowGateLatched; private bool _changingMode; private CancellationTokenSource? _workflowCts; private string? _cachedHashKey; private string? _cachedClientHash; private string? _cachedAppHashKey; private string? _cachedAppSha256;
 private System.Windows.Forms.Timer? _verifyRetryTimer; private int _verifyAttempts; private string? _lastVerifyBlockReason;
    private string _pauseResult = "未执行"; private string _stopResult = "未执行"; private string _exitResult = "未执行"; private string _workflowReason = ""; private string _modeReason = "";
    public StatusForm()
    {
        var args = Environment.GetCommandLineArgs();
        _backgroundLaunch = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        _verificationLaunch = args.Any(a => a.Equals("--verify", StringComparison.OrdinalIgnoreCase));
        _autoModeWanted = ReadAutoModePreference();
        Text = "雷神自动停时长"; Width = 760; Height = 440; StartPosition = FormStartPosition.CenterScreen;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("打开状态窗口", null, (_, _) => ShowStatusWindow());
        trayMenu.Items.Add("退出程序", null, (_, _) => { _exitRequested = true; Close(); });
        _trayIcon = new NotifyIcon { Text = "雷神自动停时长：监控中", Icon = SystemIcons.Application, ContextMenuStrip = trayMenu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => ShowStatusWindow();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(18), WrapContents = false, AutoScroll = true };
        panel.Controls.AddRange([_headline, _details, _workflowResults, _autoMode, _verifyWorkflow, _observe, _confirmGame, _install, _uninstall]); Controls.Add(panel);
        _observe.Click += (_, _) =>
        {
            if (_workflowRunning) return;
            if (_verificationMode) { MessageBox.Show("一次性自动流程验证正在进行，期间不能停止监控。", "验证中"); return; }
            _stopped = !_stopped;
            _monitor = new IdleMonitor(); // Never carry elapsed idle time across a user-requested monitoring pause.
            _stoppedSessionMonitor.Reset();
            _observe.Text = _stopped ? "恢复监控" : "停止监控";
            _modeReason = _stopped ? "监控已暂停；恢复后重新计满120秒" : "监控已恢复；重新开始完整120秒观察";
            SafeLog.Write(_stopped ? "monitor paused; idle timer reset" : "monitor resumed; fresh idle interval started");
            UpdateTrayStatus();
        };
        _autoMode.CheckedChanged += (_, _) => AutoModeChanged(); _verifyWorkflow.Click += (_, _) => ArmVerification(); _confirmGame.Click += (_, _) => ConfirmGame(); _install.Click += (_, _) => Install(); _uninstall.Click += (_, _) => Uninstall();
        _timer.Tick += (_, _) => RefreshStatus(); Shown += (_, _) => { RefreshStatus(); _timer.Start(); if (_backgroundLaunch) Hide(); };
        FormClosing += (_, e) =>
        {
            if (_workflowRunning) { e.Cancel = true; MessageBox.Show("自动处理正在进行，等待其完成并检查各阶段结果后再关闭窗口。", "操作进行中"); return; }
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            _exitRequested = true;
            _trayIcon.Visible = false;
        };
        FormClosed += (_, _) => { _trayIcon.Dispose(); trayMenu.Dispose(); _timer.Dispose(); _verifyRetryTimer?.Dispose(); };
        SafeLog.Write($"monitor started; background={_backgroundLaunch} verify={_verificationLaunch} args={string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        if (_verificationLaunch) Shown += (_, _) => BeginInvoke(new Action(StartVerificationRetryLoop));
    }

    private void StartVerificationRetryLoop()
    {
        // A single startup attempt can lose the race against the client log (session not yet
        // written, game processes not enumerated yet).  Keep trying quietly for up to five
        // minutes so a supervised run does not depend on one lucky timer tick.
        _verifyAttempts = 0;
        _verifyRetryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _verifyRetryTimer.Tick += (_, _) =>
        {
            if (_verificationMode || _workflowRunning || _verifyAttempts >= 60) { _verifyRetryTimer?.Stop(); return; }
            _verifyAttempts++;
            ArmVerification(quiet: true);
            if (_verificationMode) SafeLog.Write($"verification armed on retry attempt {_verifyAttempts}");
        };
        _verifyRetryTimer.Start();
        ArmVerification(quiet: true);
        if (_verificationMode) SafeLog.Write("verification armed on the first attempt");
    }
    private void ShowStatusWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void UpdateTrayStatus()
    {
        if (_stopped) _trayIcon.Text = "雷神自动停时长：监控已暂停";
        else if (_workflowRunning) _trayIcon.Text = "雷神自动停时长：处理工作流中";
        else if (_autoMode.Checked) _trayIcon.Text = "雷神自动停时长：自动监控";
        else _trayIcon.Text = "雷神自动停时长：仅观察";
    }
    private void RefreshStatus()
    {
        UpdateTrayStatus();
        if (_stopped) { _headline.Text = "监控已停止"; _details.Text = "没有读取或控制雷神客户端。"; return; }
        if (!TryFindRootClient(out var client, out var clientReason) || client is null)
        {
            ResetSdkIdle();
            if (!_workflowRunning) { _currentClient = null; _currentSession = null; }
            _monitor.Tick(new(SessionKnowledge.Unknown, null, clientReason), new(false, [], clientReason), Stopwatch.GetTimestamp(), _autoMode.Checked || _verificationMode);
            _headline.Text = clientReason.Contains("未运行") ? "等待雷神运行" : "雷神实例状态未知";
            _details.Text = clientReason + "。自动处理不会执行。"; UpdateWorkflowResults(); return;
        }
        _currentClient = client;
        var clientKey = $"{client.Pid}/{client.StartTime.Ticks}";
        if (_autoModeWanted && !_autoMode.Checked && !string.Equals(_lastAutoArmClient, clientKey, StringComparison.Ordinal))
        {
            _lastAutoArmClient = clientKey;
            _autoMode.Checked = true;
        }
        _root = client.Path is null ? null : Path.GetDirectoryName(client.Path); _root ??= LeiGodDiscovery.FindRoots().SingleOrDefault();
        if (_autoMode.Checked && !AcceptanceFor(client, out var gateReason)) DisarmAutoMode(gateReason, showMessage: false);
        var (snap, game) = ReadObservation(client);
        if (snap.Knowledge == SessionKnowledge.Unknown && !_workflowRunning && _autoMode.Checked)
        {
            if (!_sdkIdleProbeRunning) _ = ProbeSdkIdleAsync(client);
        }
        else ResetSdkIdle();
        _currentSession = snap.Knowledge == SessionKnowledge.Accelerating ? snap.Session : null;
        if (_verificationMode && _verificationSession is not null && snap.Session is not null && !SameSession(_verificationSession, snap.Session)) DisarmVerification("加速会话已切换，本次验证已取消。", false);
        var due = _monitor.Tick(snap, game, Stopwatch.GetTimestamp(), _autoMode.Checked || _verificationMode);
        var stoppedGame = new GameSnapshot(true, [], "已证实没有雷神加速会话；无关游戏不阻挡此分支");
        var stoppedDue = _stoppedSessionMonitor.Tick(snap, stoppedGame, client.Pid, client.StartTime, Stopwatch.GetTimestamp());
        var displayed = snap.Session?.GameName ?? "未知"; var proc = game.Processes.Count == 0 ? "无匹配进程" : string.Join(", ", game.Processes.Select(p => $"{p.Name} (PID {p.Pid})"));
        _headline.Text = snap.Knowledge == SessionKnowledge.NotAccelerating && snap.StopStateConfirmed
            ? $"已停止会话空闲观察  |  倒计时 {_stoppedSessionMonitor.Remaining:mm\\:ss}"
            : $"{_monitor.State}  |  倒计时 {_monitor.Remaining:mm\\:ss}";
        if (_sdkIdleSince is long sdkSince && _sdkIdleClientKey == clientKey)
        {
            var left = TimeSpan.FromSeconds(Math.Max(0, 120 - Stopwatch.GetElapsedTime(sdkSince).TotalSeconds));
            _headline.Text = $"SDK确认未加速  |  倒计时 {left:mm\\:ss}";
        }
        _details.Text = $"当前游戏: {displayed} [{snap.Session?.GameId ?? "无"}]\n进程: {proc}\n会话: {snap.Reason}\n识别: {game.Reason}\n状态: {_monitor.Reason}\n雷神目录: {_root ?? "未发现"}\n模式: {(_verificationMode ? "一次性自动流程验收" : _autoMode.Checked ? "自动处理已授权" : "仅观察")}{(_modeReason.Length == 0 ? "" : "\n模式说明: " + _modeReason)}";
        UpdateWorkflowResults();
        if (due && !_autoMode.Checked && !_verificationMode) SafeLog.Write("idle-threshold reached in observation mode; state=" + _monitor.State + "; reason=" + _monitor.Reason);
        if (due && (_autoMode.Checked || _verificationMode) && _monitor.State == MonitorState.Blocked && !_workflowRunning && snap.Session is not null) StartAutoWorkflow(client, snap.Session);
        if (stoppedDue && (_autoMode.Checked || _verificationMode) && !_workflowRunning) StartStoppedSessionWorkflow(client, snap);
    }

    private void ResetSdkIdle()
    {
        if (_sdkIdleSince.HasValue) SafeLog.Write("sdk idle interval reset");
        _sdkIdleSince = null;
        _sdkIdleLastProof = null;
        _sdkIdleClientKey = null;
    }

    private async Task ProbeSdkIdleAsync(LeiGodProcess client)
    {
        _sdkIdleProbeRunning = true;
        try
        {
            var root = client.Path is null ? null : Path.GetDirectoryName(client.Path);
            var logs = LeiGodDiscovery.FindLogDirectory(root);
            if (root is null || logs is null) { SafeLog.Write("sdk idle probe unavailable: client root or logs missing"); ResetSdkIdle(); return; }
            bool SameUnknownClient()
            {
                if (!TryGetClientByPid(client.Pid, client.StartTime, out var now) || now is null ||
                    !string.Equals(now.Path, client.Path, StringComparison.OrdinalIgnoreCase)) return false;
                return _reader.Read(logs, client.Pid, client.StartTime).Knowledge == SessionKnowledge.Unknown;
            }
            if (!SameUnknownClient()) { SafeLog.Write("sdk idle probe unavailable: client identity or log state changed"); ResetSdkIdle(); return; }
            var unusedExit = new OwnedProcessExitAdapter(root, client.Pid, client.StartTime, () => false, () => false, () => Task.FromResult(false));
            var probe = new ClientSdkSocketStopAdapter(root, client.Pid, client.StartTime, SameUnknownClient,
                () => false, () => false, () => Task.FromResult(false), unusedExit);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var observation = await probe.ProbeIdleObservationAsync(timeout.Token);
            if (!_autoMode.Checked || _workflowRunning) { ResetSdkIdle(); return; }
            if (observation == ClientSdkSocketStopAdapter.IdleObservation.Accelerating)
            { SafeLog.Write("sdk idle interval reset: SDK reports acceleration"); ResetSdkIdle(); return; }
            if (observation == ClientSdkSocketStopAdapter.IdleObservation.Unknown)
            {
                var recent = _sdkIdleLastProof is long proof &&
                    Stopwatch.GetElapsedTime(proof) <= TimeSpan.FromSeconds(15);
                SafeLog.Write("sdk idle probe inconclusive; recent independent proof=" + recent);
                if (!recent) ResetSdkIdle();
                return;
            }
            var key = $"{client.Pid}/{client.StartTime.Ticks}";
            if (_sdkIdleClientKey != key) { _sdkIdleClientKey = key; _sdkIdleSince = Stopwatch.GetTimestamp(); }
            _sdkIdleLastProof = Stopwatch.GetTimestamp();
            if (_sdkIdleSince is long since && Stopwatch.GetElapsedTime(since) >= TimeSpan.FromSeconds(120))
                StartSdkIdleWorkflow(client, root, logs, SameUnknownClient, probe);
        }
        catch (Exception ex)
        {
            var recent = _sdkIdleLastProof is long proof &&
                Stopwatch.GetElapsedTime(proof) <= TimeSpan.FromSeconds(15);
            SafeLog.Write("sdk idle probe error=" + ex.GetType().Name + "; recent independent proof=" + recent);
            if (!recent) ResetSdkIdle();
        }
        finally { _sdkIdleProbeRunning = false; }
    }

    private void StartSdkIdleWorkflow(LeiGodProcess client, string root, string logs, Func<bool> sameUnknownClient, ClientSdkSocketStopAdapter probe)
    {
        if (_workflowRunning || !_autoMode.Checked || !AcceptanceFor(client, out _)) return;
        _workflowRunning = true;
        _workflowCts = new CancellationTokenSource(AutoStopWorkflow.ActionBudget);
        _pauseResult = "验证中"; _stopResult = "SDK已证实未加速"; _exitResult = "等待账户暂停";
        _workflowReason = "当前客户端 SDK 连续120秒确认未加速；复核账户后退出";
        _autoMode.Enabled = false; _verifyWorkflow.Enabled = false; _observe.Enabled = false;
        _confirmGame.Enabled = false; _install.Enabled = false; _uninstall.Enabled = false;
        UpdateWorkflowResults(); UpdateTrayStatus();
        _ = RunSdkIdleWorkflowAsync(client, root, logs, sameUnknownClient, probe, _workflowCts.Token);
    }

    private async Task RunSdkIdleWorkflowAsync(LeiGodProcess client, string root, string logs,
        Func<bool> sameUnknownClient, ClientSdkSocketStopAdapter probe, CancellationToken ct)
    {
        AutoStopResult result;
        try
        {
            if (!sameUnknownClient() || !await probe.ProbeNoAccelerationAsync(ct)) throw new InvalidOperationException("无加速状态或客户端身份已变化");
            var pause = new ClientLogPauseAdapter(logs, client.Pid, client.StartTime);
            var first = await pause.ReadPauseStateAsync(ct);
            if (!first.IsPaused)
            {
                if (!pause.CanControl("")) throw new InvalidOperationException("本次客户端没有可验证的暂停参数");
                if (!sameUnknownClient() || !await probe.ProbeNoAccelerationAsync(ct)) throw new InvalidOperationException("暂停前出现加速会话");
                await pause.RequestPauseAsync(ct);
                first = await pause.ReadPauseStateAsync(ct);
            }
            if (!first.IsPaused || first.RemainingSeconds is null) throw new InvalidOperationException("账户尚未确认暂停");
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
            var second = await pause.ReadPauseStateAsync(ct);
            if (second.ObservedAt - first.ObservedAt < TimeSpan.FromSeconds(12) || !PauseProofRules.Confirmed(first, second))
                throw new InvalidOperationException("暂停状态或余额未稳定12秒");
            if (!sameUnknownClient() || !await probe.ProbeNoAccelerationAsync(ct)) throw new InvalidOperationException("退出前 SDK 未确认无加速");
            var proofAt = DateTime.UtcNow;
            bool FreshProof() => DateTime.UtcNow - proofAt < TimeSpan.FromSeconds(15) && sameUnknownClient();
            var exit = new OwnedProcessExitAdapter(root, client.Pid, client.StartTime, FreshProof, sameUnknownClient,
                async () => (await pause.ReadPauseStateAsync(ct)).IsPaused, FreshProof);
            await exit.RequestVerifiedExitAsync(ct);
            if (!await exit.AreOwnedProcessesGoneAsync(ct)) throw new InvalidOperationException("客户端或引擎仍在运行");
            var final = await pause.ReadPauseStateAsync(ct);
            if (!final.IsPaused || final.RemainingSeconds is null || final.RemainingSeconds < second.RemainingSeconds)
                throw new InvalidOperationException("退出后账户时长未确认暂停");
            result = new(true, "complete", "未加速客户端空闲120秒后，账户暂停且雷神完全退出");
        }
        catch (Exception ex) { result = new(false, "sdk-idle", SafeLog.Redact(ex.GetType().Name + ": " + ex.Message)); }
        ApplyWorkflowResult(result);
        SafeLog.Write($"sdk idle workflow result stage={result.Stage} completed={result.Completed}; {result.Reason}");
        _workflowRunning = false; _workflowCts?.Dispose(); _workflowCts = null; ResetSdkIdle();
        _autoMode.Enabled = true; _verifyWorkflow.Enabled = true; _observe.Enabled = true;
        _confirmGame.Enabled = true; _install.Enabled = true; _uninstall.Enabled = true;
        if (!result.Completed) DisarmAutoMode("未加速客户端退出失败；请检查处理结果。", false);
        RefreshStatus();
    }

    private (SessionSnapshot Session, GameSnapshot Game) ReadObservation(LeiGodProcess expectedClient)
    {
        if (!TryGetClientByPid(expectedClient.Pid, expectedClient.StartTime, out var current) || current is null)
        {
            var unknown = new SessionSnapshot(SessionKnowledge.Unknown, null, "雷神主进程数量、PID 或启动时间已变化");
            return (unknown, new(false, [], unknown.Reason));
        }
        var root = current.Path is null ? _root : Path.GetDirectoryName(current.Path);
        var snap = _reader.Read(LeiGodDiscovery.FindLogDirectory(root), expectedClient.Pid, expectedClient.StartTime);
        return (snap, _games.Find(snap));
    }

    internal static bool TryFindRootClient(out LeiGodProcess? client, out string reason)
    {
        client = null;
        Process[] candidates;
        try { candidates = Process.GetProcessesByName("leigod"); }
        catch { reason = "雷神进程列表不可读"; return false; }
        if (candidates.Length == 0) { reason = "雷神主客户端未运行"; return false; }
        var roots = new List<LeiGodProcess>(); var unreadable = false;
        foreach (var process in candidates)
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue; // Electron renderers have no top-level client window.
                var path = LeiGodDiscovery.SafePath(process);
                roots.Add(new(process.Id, process.ProcessName, path, process.StartTime));
            }
            catch { unreadable = true; }
            finally { process.Dispose(); }
        }
        if (unreadable) { reason = "雷神主窗口或进程身份读取失败"; return false; }
        if (roots.Count == 0)
        {
            // A hidden-to-tray Electron client can have no top-level window. Find the earliest
            // same-installation leigod PID that parents another Electron leigod process.
            try { candidates = Process.GetProcessesByName("leigod"); }
            catch { reason = "托盘客户端进程列表不可读"; return false; }
            var byPid = new Dictionary<int, LeiGodProcess>(); var parentByPid = new Dictionary<int, int>(); var parentReadFailed = false;
            foreach (var process in candidates)
            {
                try
                {
                    var path = LeiGodDiscovery.SafePath(process);
                    if (path is null || !TryGetParentPid(process, out var parent)) { parentReadFailed = true; continue; }
                    byPid[process.Id] = new(process.Id, process.ProcessName, path, process.StartTime); parentByPid[process.Id] = parent;
                }
                catch { parentReadFailed = true; }
                finally { process.Dispose(); }
            }
            if (!parentReadFailed)
            {
                var paths = byPid.Values.Where(p => p.Path is not null).Select(p => Path.GetDirectoryName(p.Path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var parents = byPid.Values.Where(parent => parent.Path is not null && byPid.Values.Any(child => child.Pid != parent.Pid && child.Path is not null && parentByPid.GetValueOrDefault(child.Pid) == parent.Pid && SameInstall(parent.Path, child.Path))).OrderBy(p => p.StartTime).ToArray();
                if (paths.Length == 1 && parents.Select(p => p.Pid).Distinct().Count() == 1) { client = parents[0]; reason = "按同一安装目录内的 Electron 父进程识别托盘客户端"; return true; }
            }
            reason = parentReadFailed ? "托盘客户端父进程或安装路径不可验证" : "没有可识别的主窗口或 Electron 父进程"; return false;
        }
        if (roots.Count != 1) { reason = "发现多个雷神主窗口，无法安全选择主客户端"; return false; }
        client = roots[0]; reason = "已按唯一主窗口识别雷神客户端"; return true;
    }

    private static bool SameInstall(string? first, string? second) => first is not null && second is not null && string.Equals(Path.GetDirectoryName(first), Path.GetDirectoryName(second), StringComparison.OrdinalIgnoreCase);
    private static bool TryGetParentPid(Process process, out int parentPid)
    {
        parentPid = 0; var handle = OpenProcess(0x0400, false, process.Id);
        if (handle == IntPtr.Zero) return false;
        try { var status = NtQueryInformationProcess(handle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _); if (status != 0 || info.InheritedFromUniqueProcessId == IntPtr.Zero) return false; parentPid = info.InheritedFromUniqueProcessId.ToInt32(); return true; }
        catch { return false; }
        finally { CloseNativeHandle(handle); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation { public IntPtr ExitStatus; public IntPtr PebBaseAddress; public IntPtr AffinityMask; public IntPtr BasePriority; public IntPtr UniqueProcessId; public IntPtr InheritedFromUniqueProcessId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseNativeHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out ProcessBasicInformation information, int informationLength, out int returnLength);

    private static bool TryGetClientByPid(int pid, DateTime startTime, out LeiGodProcess? client)
    {
        client = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("leigod", StringComparison.OrdinalIgnoreCase) || process.StartTime != startTime) return false;
            client = new(process.Id, process.ProcessName, LeiGodDiscovery.SafePath(process), process.StartTime);
            return true;
        }
        catch { return false; }
    }

    private static bool SameSession(AccelerationSession left, AccelerationSession right) => left.ClientPid == right.ClientPid && left.ClientStart == right.ClientStart && left.GameId == right.GameId && left.EventTime == right.EventTime;

    private (SessionSnapshot Session, GameSnapshot Game) ObserveForWorkflow(AccelerationSession expected, IReadOnlyList<string>? expectedActiveSet = null)
    {
        if (!_workflowGateLatched || (!_autoMode.Checked && !_verificationMode))
        {
            var unknown = new SessionSnapshot(SessionKnowledge.Unknown, null, "工作流空闲阈值或操作授权已失效");
            return (unknown, new(false, [], unknown.Reason));
        }
        var client = _currentClient;
        if (client is null || client.Pid != expected.ClientPid || client.StartTime != expected.ClientStart)
        {
            var unknown = new SessionSnapshot(SessionKnowledge.Unknown, null, "当前雷神客户端与待处理会话不一致");
            return (unknown, new(false, [], unknown.Reason));
        }
        var observation = ReadObservation(client);
        if (expectedActiveSet is not null)
        {
            var ids = observation.Session.ActiveSessions.Count > 0 ? observation.Session.ActiveSessions.Select(s => s.GameId + ":" + s.EventTime.Ticks).OrderBy(x => x, StringComparer.Ordinal).ToArray() : observation.Session.Session is { Active: true } single ? [single.GameId + ":" + single.EventTime.Ticks] : [];
            var expectedIds = expectedActiveSet.Select(x => x.Split(':', 2)[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sameOriginalActiveSet = observation.Session.Knowledge == SessionKnowledge.Accelerating && ids.SequenceEqual(expectedActiveSet.OrderBy(x => x, StringComparer.Ordinal));
            var sameOriginalStoppedSet = observation.Session.Knowledge == SessionKnowledge.NotAccelerating && observation.Session.StopStateConfirmed && observation.Session.StoppedGameIds.SetEquals(expectedIds);
            if (!sameOriginalActiveSet && !sameOriginalStoppedSet)
            {
                var unknown = new SessionSnapshot(SessionKnowledge.Unknown, null, "活动加速游戏 ID 或会话启动时间集合发生变化");
                return (unknown, new(false, [], unknown.Reason));
            }
            if (sameOriginalStoppedSet)
            {
                // Pause can itself stop acceleration. The ordinary live-game
                // detector marks a stopped session unknown, so use the paired
                // stop event's original process mapping for the same safety
                // check while completing pause and exit verification.
                return (observation.Session, _games.FindStopped(observation.Session));
            }
        }
        return observation;
    }

    private void StartAutoWorkflow(LeiGodProcess client, AccelerationSession session)
    {
        // The timer may have crossed 120 seconds before this UI callback; enforce the threshold again.
        if (_workflowRunning || (!_autoMode.Checked && !_verificationMode) || _monitor.State != MonitorState.Blocked || _monitor.Remaining > TimeSpan.Zero) return;
        if (_verificationMode && (_verificationSession is null || !SameSession(_verificationSession, session))) { DisarmVerification("验证会话已变化，未执行自动流程。", false); return; }
        if (!_verificationMode && !AcceptanceFor(client, out var reason)) { DisarmAutoMode(reason, showMessage: false); return; }
        var logDirectory = LeiGodDiscovery.FindLogDirectory(_root);
        if (logDirectory is null || _root is null) { _pauseResult = "阻止：雷神目录/日志未知"; _stopResult = "未执行"; _exitResult = "未执行"; _workflowReason = "雷神目录或日志目录不可用"; UpdateWorkflowResults(); return; }
        var before = ReadObservation(client);
        if (before.Session.Knowledge != SessionKnowledge.Accelerating || before.Session.Session is null || !SameSession(session, before.Session.Session) || !before.Game.Known || before.Game.Processes.Count != 0 || _monitor.State != MonitorState.Blocked || _monitor.Remaining > TimeSpan.Zero)
        { _pauseResult = "阻止：120秒阈值复核失败"; _stopResult = "未执行"; _exitResult = "未执行"; _workflowReason = "再次检查时游戏仍运行、会话变化或进程状态未知"; UpdateWorkflowResults(); return; }
        _workflowGateLatched = true;
        var expectedActiveSet = before.Session.ActiveSessions.Count > 0 ? before.Session.ActiveSessions.Select(s => s.GameId + ":" + s.EventTime.Ticks).OrderBy(x => x, StringComparer.Ordinal).ToArray() : [session.GameId + ":" + session.EventTime.Ticks];
        _workflowRunning = true;
        UpdateTrayStatus();
        _workflowCts = new CancellationTokenSource();
        _autoMode.Enabled = false; _verifyWorkflow.Enabled = false; _observe.Enabled = false; _confirmGame.Enabled = false; _install.Enabled = false; _uninstall.Enabled = false;
        _pauseResult = "执行中"; _stopResult = "等待暂停验证"; _exitResult = "等待停止加速"; _workflowReason = "工作流进行中"; UpdateWorkflowResults();
        SafeLog.Write($"auto workflow started game_id={session.GameId} client_pid={session.ClientPid}");
        var pause = new ClientLogPauseAdapter(logDirectory, client.Pid, client.StartTime);
        var expectedSessions = before.Session.ActiveSessions.Count > 0 ? before.Session.ActiveSessions : [session];
        var expectedGameIds = expectedSessions.Select(s => s.GameId).ToArray();
        var readSession = () => _reader.Read(logDirectory, client.Pid, client.StartTime);
        ClientSdkSocketStopAdapter? sdkSocketStop = null;
        bool SdkStopReadback() => sdkSocketStop?.StopReadbackConfirmed == true;
        bool HasStopProof() { var s = readSession(); return (s.Knowledge == SessionKnowledge.NotAccelerating && s.StopStateConfirmed && expectedGameIds.All(s.StoppedGameIds.Contains)) || SdkStopReadback(); }
        bool CanExitStopped() => CanExitStoppedSession(expectedSessions, expectedGameIds, readSession, SdkStopReadback);
        var hasPersistentAcceptance = _autoMode.Checked && AcceptanceFor(client, out _);
        var exit = new OwnedProcessExitAdapter(_root, client.Pid, session.ClientStart, HasStopProof, CanExitStopped, async () => (await pause.ReadPauseStateAsync(_workflowCts?.Token ?? CancellationToken.None)).IsPaused,
            () => CanTerminateCurrentSession(session, expectedActiveSet) || CanExitStopped());
        sdkSocketStop = new ClientSdkSocketStopAdapter(_root, client.Pid, client.StartTime, () => CanTerminateCurrentSession(session, expectedActiveSet), HasStopProof, CanExitStopped,
            async () => (await pause.ReadPauseStateAsync(_workflowCts?.Token ?? CancellationToken.None)).IsPaused, exit);
        // Prefer the current client's verified local SDK route.  Some LeiGod
        // builds expose a self-drawn main window whose UIA tree never
        // completes; probing that tree before checking the SDK would stall a
        // ready workflow indefinitely.  Only probe UIA when the SDK route is
        // unavailable.
        var sdkAvailable = (_verificationMode || hasPersistentAcceptance) && sdkSocketStop.CanStopAcceleration;
        var readbackProbe = !sdkAvailable && client.Path is not null && new WindowsLeiGodUiAutomationProvider().ProbeStopActionContract(client.Pid, client.StartTime, client.Path);
        var enableContractForThisRun = readbackProbe && (_verificationMode || hasPersistentAcceptance);
        var uiAutomation = new WindowsLeiGodUiAutomationProvider(liveReadbackContractEnabled: enableContractForThisRun);
        IStopAdapter stop;
        if (client.Path is null)
        {
            stop = new DisabledStopAdapter();
        }
        else if (sdkAvailable)
        {
            stop = sdkSocketStop;
        }
        else if (enableContractForThisRun)
        {
            stop = new LeiGodUiAutomationStopAdapter(client.Pid, client.StartTime, client.Path, session.GameName, expectedGameIds, uiAutomation, readSession,
                () => CanTerminateCurrentSession(session, expectedActiveSet), CanExitStopped,
                async () => (await pause.ReadPauseStateAsync(_workflowCts?.Token ?? CancellationToken.None)).IsPaused, exit,
                registered: true, liveReadbackValidated: true);
        }
        else if ((_verificationMode || hasPersistentAcceptance) && pause.CanControl(""))
        {
            // The real LeiGod window is a self-drawn surface on some builds and
            // exposes no UIA stop button. Permit the supervised route to prove
            // that pause itself stopped acceleration; no unknown click is sent.
            stop = new PauseInducedStopAdapter(HasStopProof, exit);
        }
        else
        {
            stop = new DisabledStopAdapter();
        }
        _ = RunAutoWorkflowAsync(client, session, pause, stop, _workflowCts.Token, expectedActiveSet, expectedSessions);
    }

    private bool CanExitStoppedSession(IReadOnlyList<AccelerationSession> expectedSessions, IReadOnlyCollection<string> expectedGameIds, Func<SessionSnapshot> readSession, Func<bool>? sdkStopReadback = null)
    {
        if (!_workflowGateLatched || (!_autoMode.Checked && !_verificationMode) || _currentClient is null) return false;
        var snap = readSession();
        var logStopped = snap.Knowledge == SessionKnowledge.NotAccelerating && snap.StopStateConfirmed && snap.StoppedGameIds.SetEquals(expectedGameIds) && snap.Session?.ClientPid == _currentClient.Pid && snap.Session.ClientStart == _currentClient.StartTime;
        var sdkStopped = sdkStopReadback?.Invoke() == true && snap.Knowledge == SessionKnowledge.Accelerating &&
            snap.Session?.ClientPid == _currentClient.Pid && snap.Session.ClientStart == _currentClient.StartTime &&
            StopSessionIdentity.MatchesOriginal(expectedSessions, snap);
        if (!logStopped && !sdkStopped) return false;
        var primary = expectedSessions.OrderBy(s => s.EventTime).LastOrDefault();
        if (primary is null) return false;
        var expectedActiveSnapshot = new SessionSnapshot(SessionKnowledge.Accelerating, primary, "validated pre-stop game set") { ActiveSessions = expectedSessions };
        var game = _games.Find(expectedActiveSnapshot);
        return game.Known && game.Processes.Count == 0;
    }

    private bool CanTerminateCurrentSession(AccelerationSession expected, IReadOnlyList<string> expectedActiveSet)
    {
        if (!_workflowGateLatched || (!_autoMode.Checked && !_verificationMode)) return false;
        var client = _currentClient; if (client is null) return false;
        var observation = ReadObservation(client);
        var ids = observation.Session.ActiveSessions.Count > 0 ? observation.Session.ActiveSessions.Select(s => s.GameId + ":" + s.EventTime.Ticks).OrderBy(x => x, StringComparer.Ordinal).ToArray() : observation.Session.Session is { Active: true } single ? [single.GameId + ":" + single.EventTime.Ticks] : [];
        return observation.Session.Knowledge == SessionKnowledge.Accelerating && ids.SequenceEqual(expectedActiveSet.OrderBy(x => x, StringComparer.Ordinal)) &&
               observation.Session.Session is not null && SameSession(observation.Session.Session, expected) &&
               observation.Game.Known && observation.Game.Processes.Count == 0;
    }

    private async Task RunAutoWorkflowAsync(LeiGodProcess expectedClient, AccelerationSession session, IPauseAdapter pause, IStopAdapter stop, CancellationToken ct, IReadOnlyList<string> expectedActiveSet, IReadOnlyList<AccelerationSession> expectedSessions)
    {
        var wasVerification = _verificationMode; var verificationVersion = _verificationClientVersion; var verificationHash = _verificationClientHash; var verificationAppHash = _verificationAppSha256;
        AutoStopResult result;
        try { result = await new AutoStopWorkflow().RunAsync(session, () => ObserveForWorkflow(session, expectedActiveSet), pause, stop, ct, expectedSessions); }
        catch (Exception ex) { result = new(false, "error", SafeLog.Redact(ex.GetType().Name + ": " + ex.Message)); }
        var acceptanceWritten = false;
        // A successful workflow has exited the original client, so checking that
        // its PID still exists here would reject every genuine success. The
        // workflow already bound each action to expectedClient PID/start and
        // verified that all owned LeiGod processes are gone. Recheck the two
        // immutable file fingerprints before recording that completed result.
        if (result.Completed && wasVerification && result.AutomatedPauseRequest && verificationVersion is not null && verificationHash is not null && verificationAppHash is not null && GetClientHash(expectedClient) == verificationHash && GetApplicationSha256() == verificationAppHash)
        {
            try { WriteAcceptance(expectedClient, verificationVersion, verificationHash, verificationAppHash, session); acceptanceWritten = true; }
            catch (Exception ex) { result = new(false, "acceptance-record", "工作流完成，但验收记录写入失败: " + SafeLog.Redact(ex.Message)); }
        }
        else if (result.Completed && wasVerification) result = new(false, "acceptance-record", "流程完成，但未能证明本应用发送暂停请求，或客户端/程序 SHA-256 发生变化，未记录验收");
        ApplyWorkflowResult(result);
        if (acceptanceWritten) _workflowResults.Text += "\n已记录当前客户端自动处理验收。";
        SafeLog.Write($"auto workflow result stage={result.Stage} completed={result.Completed}; {result.Reason}");
        _workflowRunning = false; _workflowCts?.Dispose(); _workflowCts = null;
        _workflowGateLatched = false;
        _autoMode.Enabled = true; _verifyWorkflow.Enabled = true; _observe.Enabled = true; _confirmGame.Enabled = true; _install.Enabled = true; _uninstall.Enabled = true;
        if (wasVerification) DisarmVerification(result.Completed && acceptanceWritten ? "验收完成；已返回仅观察模式。" : "一次性验证结束但未记录通过；保持仅观察模式。", false);
        else DisarmAutoMode(result.Completed ? "自动处理完成；已返回仅观察模式。" : "自动处理未完成，请检查暂停、停止和退出结果。", false);
        RefreshStatus();
    }

    private void StartStoppedSessionWorkflow(LeiGodProcess client, SessionSnapshot original)
    {
        if (_root is null || original.Session is null || original.StoppedGameIds.Count == 0 || !_workflowGateLatched && !_autoMode.Checked && !_verificationMode) return;
        _workflowGateLatched = true;
        _workflowRunning = true;
        _workflowCts = new CancellationTokenSource(AutoStopWorkflow.ActionBudget);
        _pauseResult = "验证中"; _stopResult = "已有日志证明停止"; _exitResult = "等待账户暂停";
        _workflowReason = "已停止会话连续空闲达到120秒；复核账户暂停与进程归属";
        UpdateWorkflowResults(); UpdateTrayStatus();
        _autoMode.Enabled = false; _verifyWorkflow.Enabled = false; _observe.Enabled = false; _confirmGame.Enabled = false; _install.Enabled = false; _uninstall.Enabled = false;
        _ = RunStoppedSessionWorkflowAsync(client, original, _workflowCts.Token);
    }

    private async Task RunStoppedSessionWorkflowAsync(LeiGodProcess client, SessionSnapshot original, CancellationToken ct)
    {
        var root = _root!;
        bool SameStoppedEpoch()
        {
            if (!TryGetClientByPid(client.Pid, client.StartTime, out var now) || now is null || !string.Equals(now.Path, client.Path, StringComparison.OrdinalIgnoreCase)) return false;
            var snap = _reader.Read(LeiGodDiscovery.FindLogDirectory(root), client.Pid, client.StartTime);
            return snap.Knowledge == SessionKnowledge.NotAccelerating && snap.StopStateConfirmed && snap.Session?.ClientPid == client.Pid && snap.Session.ClientStart == client.StartTime &&
                snap.Session.EventTime == original.Session!.EventTime && snap.StoppedGameIds.SetEquals(original.StoppedGameIds);
        }
        bool StoppedStateStillValid() => SameStoppedEpoch();
        var logDirectory = LeiGodDiscovery.FindLogDirectory(root) ?? throw new InvalidOperationException("客户端日志目录不可用");
        var pause = new ClientLogPauseAdapter(logDirectory, client.Pid, client.StartTime);
        AutoStopResult result;
        try
        {
            if (!SameStoppedEpoch()) throw new InvalidOperationException("最后停止事件或客户端身份发生变化");
            var first = await pause.ReadPauseStateAsync(ct);
            if (!first.IsPaused)
            {
                if (!pause.CanControl("")) throw new InvalidOperationException("当前客户端没有可用的暂停控制参数");
                if (!SameStoppedEpoch()) throw new InvalidOperationException("暂停前停止会话状态发生变化");
                await pause.RequestPauseAsync(ct);
                var until = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < until)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!SameStoppedEpoch()) throw new InvalidOperationException("暂停确认期间出现新会话或日志缺口");
                    first = await pause.ReadPauseStateAsync(ct);
                    if (first.IsPaused && first.RemainingSeconds.HasValue) break;
                    await Task.Delay(750, ct);
                }
            }
            if (!first.IsPaused || first.RemainingSeconds is null) throw new InvalidOperationException("账户暂停状态未确认");
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
            if (!SameStoppedEpoch()) throw new InvalidOperationException("暂停稳定读回期间停止会话状态发生变化");
            var second = await pause.ReadPauseStateAsync(ct);
            if (second.ObservedAt - first.ObservedAt < TimeSpan.FromSeconds(12) || !PauseProofRules.Confirmed(first, second)) throw new InvalidOperationException("账户暂停或余额未稳定至少12秒");
            var exit = new OwnedProcessExitAdapter(root, client.Pid, client.StartTime, SameStoppedEpoch, StoppedStateStillValid,
                async () => (await pause.ReadPauseStateAsync(ct)).IsPaused, StoppedStateStillValid);
            if (!SameStoppedEpoch()) throw new InvalidOperationException("退出前出现新会话或日志缺口");
            await exit.RequestVerifiedExitAsync(ct);
            if (!await exit.AreOwnedProcessesGoneAsync(ct)) throw new InvalidOperationException("雷神进程树未完全退出");
            var final = await pause.ReadPauseStateAsync(ct);
            if (!final.IsPaused || !final.RemainingSeconds.HasValue || final.RemainingSeconds < second.RemainingSeconds) throw new InvalidOperationException("退出后账户暂停或余额证明失败");
            result = new(true, "complete", "已停止会话持续空闲、账户暂停余额稳定且雷神进程树已退出");
        }
        catch (Exception ex) { result = new(false, "stopped-idle-exit", SafeLog.Redact(ex.GetType().Name + ": " + ex.Message)); }
        ApplyWorkflowResult(result);
        _workflowRunning = false; _workflowGateLatched = false;
        _autoMode.Enabled = true; _verifyWorkflow.Enabled = true; _observe.Enabled = true; _confirmGame.Enabled = true; _install.Enabled = true; _uninstall.Enabled = true;
        _workflowCts?.Dispose(); _workflowCts = null;
        if (_verificationMode) DisarmVerification(result.Completed ? "已停止会话空闲退出流程完成；返回仅观察模式。" : "已停止会话退出未完成；返回仅观察模式。", false);
        else DisarmAutoMode(result.Completed ? "已停止会话空闲退出完成；返回仅观察模式。" : "已停止会话退出未完成；返回仅观察模式。", false);
        RefreshStatus();
    }

    private void ApplyWorkflowResult(AutoStopResult result)
    {
        switch (result.Stage)
        {
            case "complete": _pauseResult = "已确认"; _stopResult = "已确认"; _exitResult = "已确认"; break;
            case "precheck": case "pause-precheck": case "pause": _pauseResult = "未确认/未执行"; _stopResult = "未执行"; _exitResult = "未执行"; break;
            case "pause-proof": _pauseResult = "未确认"; _stopResult = "未执行"; _exitResult = "未执行"; break;
            case "stop-precheck": _pauseResult = "已确认"; _stopResult = "未执行"; _exitResult = "未执行"; break;
            case "stop": _pauseResult = "已确认"; _stopResult = "未确认"; _exitResult = "未执行"; break;
            case "exit": _pauseResult = "已确认"; _stopResult = "已确认"; _exitResult = "未确认"; break;
            case "final-proof": _pauseResult = "退出后未确认"; _stopResult = "已确认"; _exitResult = "已确认"; break;
            default: _pauseResult = "需人工核验"; _stopResult = "需人工核验"; _exitResult = "需人工核验"; break;
        }
        _workflowReason = result.Reason;
        _workflowResults.Text = $"工作流结果：暂停时长 {_pauseResult}  |  停止加速 {_stopResult}  |  退出雷神 {_exitResult}\n{_workflowReason}";
    }

    private void UpdateWorkflowResults()
    {
        if (!_workflowRunning) _workflowResults.Text = $"处理结果：暂停时长 {_pauseResult}  |  停止加速 {_stopResult}  |  退出雷神 {_exitResult}{(_workflowReason.Length == 0 ? "" : "\n" + _workflowReason)}";
    }

    private void AutoModeChanged()
    {
        if (_changingMode || _workflowRunning) return;
        _stoppedSessionMonitor.Reset();
        if (_autoMode.Checked)
        {
            var found = TryFindRootClient(out var client, out var clientReason);
            if (!found || client is null) { DisarmAutoMode(clientReason, showMessage: !_backgroundLaunch); return; }
            if (!AcceptanceFor(client, out var reason)) { DisarmAutoMode(reason, showMessage: !_backgroundLaunch); return; }
            _lastAutoArmClient = $"{client.Pid}/{client.StartTime.Ticks}";
            _autoModeWanted = true; SaveAutoModePreference(true);
            _autoMode.Text = "模式：自动处理（已验收）";
            _monitor = new IdleMonitor(); // entering auto mode starts a fresh full 120-second interval
            _pauseResult = _stopResult = _exitResult = "未执行";
            _workflowReason = ""; _modeReason = "";
            SafeLog.Write("automatic mode armed after versioned live acceptance check; fresh 120-second interval started");
        }
        else
        {
            _autoModeWanted = false; SaveAutoModePreference(false);
            _monitor = new IdleMonitor();
            _autoMode.Text = "模式：仅观察";
            _workflowReason = ""; _modeReason = "";
            SafeLog.Write("automatic mode disarmed; observation mode active");
        }
        UpdateWorkflowResults(); RefreshStatus();
    }

    private void ArmVerification() => ArmVerification(false);

    private void ArmVerification(bool quiet)
    {
        void Block(string reason, string title)
        {
            if (!string.Equals(_lastVerifyBlockReason, reason, StringComparison.Ordinal))
            {
                _lastVerifyBlockReason = reason;
                SafeLog.Write("verification arm blocked: " + reason);
            }
            if (!quiet) MessageBox.Show(reason, title);
        }
        if (_workflowRunning || _autoMode.Checked || _verificationMode) { Block("已有验证、自动流程或自动模式在进行中。", "无法开始验证"); return; }
        if (_stopped)
        {
            _stopped = false; _observe.Text = "停止监控"; _monitor = new IdleMonitor(); _stoppedSessionMonitor.Reset();
            SafeLog.Write("verification arm resumed a paused monitor");
        }
        if (!TryFindRootClient(out var client, out var clientReason) || client is null) { Block(clientReason, "无法开始验证"); return; }
        var root = client.Path is null ? null : Path.GetDirectoryName(client.Path);
        var logDirectory = LeiGodDiscovery.FindLogDirectory(root);
        var version = GetClientVersion(client); var hash = GetClientHash(client);
        var appHash = GetApplicationSha256();
        if (root is null || logDirectory is null || version is null || hash is null || appHash is null) { Block("当前客户端目录、日志、版本或程序 SHA-256 不可验证。", "无法开始验证"); return; }
        var observation = ReadObservation(client);
        if (observation.Session.Knowledge != SessionKnowledge.Accelerating || observation.Session.Session is null)
        { Block("当前没有已确认的加速会话（状态: " + observation.Session.Reason + "）。", "无法开始验证"); return; }
        if (!observation.Game.Known)
        { Block("游戏进程识别不完整，不能开始验证（" + observation.Game.Reason + "）。", "无法开始验证"); return; }
        var adapter = new ClientLogPauseAdapter(logDirectory, client.Pid, client.StartTime);
        if (!adapter.CanControl(version)) { Block("当前客户端日志没有可验证的本次暂停请求参数。", "无法开始验证"); return; }
        var armedSession = observation.Session.Session!;
        _verificationMode = true; _verificationSession = armedSession; _verificationClientVersion = version; _verificationClientHash = hash; _verificationAppSha256 = appHash;
        _verifyWorkflow.Enabled = false;
        _workflowReason = "首次现场验证已启动；计时从现在重新开始"; _modeReason = "";
        _monitor = new IdleMonitor(); _pauseResult = _stopResult = _exitResult = "等待120秒验证";
        SafeLog.Write($"one-time automatic workflow verification armed game_id={armedSession.GameId} client_pid={client.Pid} client_version={version} client_sha256={hash} app_sha256={appHash}");
        _verifyRetryTimer?.Stop();
        UpdateWorkflowResults(); RefreshStatus();
    }

    private void DisarmVerification(string reason, bool showMessage)
    {
        _modeReason = reason;
        _verificationMode = false; _verificationSession = null; _verificationClientVersion = null; _verificationClientHash = null; _verificationAppSha256 = null;
        if (!_workflowRunning) _verifyWorkflow.Enabled = true;
        _monitor = new IdleMonitor();
        if (showMessage) MessageBox.Show(reason, "验证模式已结束");
        _details.Text += "\n一次性验证: " + reason; UpdateWorkflowResults();
    }

    private bool AcceptanceFor(LeiGodProcess client, out string reason)
    {
        var version = GetClientVersion(client); var hash = GetClientHash(client); var appHash = GetApplicationSha256();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop", "live-automatic-acceptance.json");
        if (version is null || hash is null || appHash is null) { reason = "无法读取客户端或本应用 SHA-256"; return false; }
        string machineFingerprint;
        try { machineFingerprint = AutomaticAcceptanceGate.GetMachineFingerprint(); }
        catch (Exception ex) { reason = "本机指纹不可读；保持仅观察模式: " + ex.GetType().Name; return false; }
        return AutomaticAcceptanceGate.Validate(path, version, hash, appHash, machineFingerprint, out reason);
    }

    private static string? GetClientVersion(LeiGodProcess client)
    { try { return client.Path is null ? null : FileVersionInfo.GetVersionInfo(client.Path).FileVersion; } catch { return null; } }

    private string? GetClientHash(LeiGodProcess client)
    {
        try
        {
            if (client.Path is null) return null;
            var root = Path.GetDirectoryName(Path.GetFullPath(client.Path)); if (root is null) return null;
            var files = new[]
            {
                (Name: "leigod.exe", Path: Path.Combine(root, "leigod.exe")),
                (Name: "resources/app.asar", Path: Path.Combine(root, "resources", "app.asar")),
                (Name: "resources/renderer.asar", Path: Path.Combine(root, "resources", "renderer.asar"))
            };
            var infos = files.Select(f => new FileInfo(f.Path)).ToArray();
            if (infos.Any(i => !i.Exists)) return null;
            var cacheKey = string.Join("|", infos.Select(i => i.FullName + ":" + i.Length + ":" + i.LastWriteTimeUtc.Ticks));
            if (_cachedHashKey == cacheKey) return _cachedClientHash;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var i = 0; i < files.Length; i++)
            {
                digest.AppendData(System.Text.Encoding.UTF8.GetBytes(files[i].Name + "\0"));
                using var stream = infos[i].OpenRead(); var buffer = new byte[64 * 1024]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) digest.AppendData(buffer, 0, read);
            }
            _cachedClientHash = Convert.ToHexString(digest.GetHashAndReset());
            _cachedHashKey = cacheKey;
            return _cachedClientHash;
        }
        catch { return null; }
    }

    private void WriteAcceptance(LeiGodProcess client, string version, string hash, string appHash, AccelerationSession session)
    {
        var record = new AutomaticAcceptanceRecord(AutomaticAcceptanceGate.RequiredProtocol, version, $"{session.GameId}-{session.ClientPid}-{Guid.NewGuid():N}", DateTime.UtcNow, hash, appHash, AutomaticAcceptanceGate.GetMachineFingerprint(), true, true, true, true);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop", "live-automatic-acceptance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string? GetApplicationSha256()
    {
        try
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            var assemblyPath = assemblyName is null ? null : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            var path = assemblyPath is not null && File.Exists(assemblyPath) ? assemblyPath : Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var key = info.FullName + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            if (_cachedAppHashKey == key) return _cachedAppSha256;
            using var stream = info.OpenRead();
            _cachedAppSha256 = Convert.ToHexString(SHA256.HashData(stream));
            _cachedAppHashKey = key;
            return _cachedAppSha256;
        }
        catch { return null; }
    }

    private void DisarmAutoMode(string reason, bool showMessage)
    {
        _modeReason = reason;
        _changingMode = true; _autoMode.Checked = false; _changingMode = false;
        _autoMode.Text = "模式：仅观察";
        _monitor = new IdleMonitor();
        if (showMessage) MessageBox.Show(reason, "当前为仅观察模式");
        _details.Text += "\n自动处理未启用: " + reason;
        UpdateWorkflowResults();
    }

    private sealed record AutoModeSettings(bool AutoModeEnabled);
    private static string AutoModeSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop", "settings.json");
    private static bool ReadAutoModePreference()
    {
        try { return JsonSerializer.Deserialize<AutoModeSettings>(File.ReadAllText(AutoModeSettingsPath))?.AutoModeEnabled == true; }
        catch { return false; }
    }
    private static void SaveAutoModePreference(bool enabled)
    {
        try
        {
            var path = AutoModeSettingsPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(new AutoModeSettings(enabled)));
            File.Move(temp, path, true);
        }
        catch (Exception ex) { SafeLog.Write("could not persist automatic mode preference: " + SafeLog.Redact(ex.Message)); }
    }
    private void ConfirmGame()
    {
        if (_currentSession is null) { MessageBox.Show("先在雷神中启动加速，等待当前会话被确认。"); return; }
        var session = _currentSession;
        var loggedNames = (session.GameProcesses ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (loggedNames.Count == 0) { MessageBox.Show("当前会话日志没有 launch-local-game 的 game_process 证据，不能手动绑定系统进程。"); return; }
        var choices = new List<GameProcess>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { MessageBox.Show("无法读取完整进程列表，不能安全提供确认候选：" + ex.GetType().Name); return; }
        var enumerationFailed = false;
        foreach (var p in all)
        {
            try
            {
                if (p.Id == session.ClientPid || p.ProcessName.Equals("leigod", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("leishenSdk", StringComparison.OrdinalIgnoreCase)) continue;
                var executable = p.ProcessName + ".exe";
                if (!loggedNames.Contains(executable)) continue;
                choices.Add(new(p.Id, executable, LeiGodDiscovery.SafePath(p), p.StartTime, "雷神日志关联的候选进程"));
            }
            catch { enumerationFailed = true; }
            finally { p.Dispose(); }
        }
        if (enumerationFailed) { MessageBox.Show("部分进程名称、路径或启动时间不可读。候选列表不完整，因此不允许按它进行确认。"); return; }
        using var dialog = new Form { Text = $"确认游戏进程：{session.GameName} [{session.GameId}]", Width = 720, Height = 460, StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill, DisplayMember = "Display" };
        var entries = choices.OrderBy(p => p.Name).Select(p => new ProcessChoice(p)).ToArray();
        list.Items.AddRange(entries);
        var note = new Label { Text = "候选名称仅来自本次会话 launch-local-game 的 game_process 日志；手动确认仅补充该日志允许的路径。", Dock = DockStyle.Top, Height = 42 };
        var ok = new Button { Text = "确认所选进程", Dock = DockStyle.Bottom, Height = 36 };
        ok.Click += (_, _) => { if (list.SelectedItem is ProcessChoice) { dialog.DialogResult = DialogResult.OK; dialog.Close(); } };
        dialog.Controls.Add(list); dialog.Controls.Add(note); dialog.Controls.Add(ok);
        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedItem is not ProcessChoice chosen) return;
        try { _games.ConfirmProcess(session.GameId, chosen.Process); SafeLog.Write($"confirmed game process alias game_id={session.GameId} name={chosen.Process.Name}"); RefreshStatus(); }
        catch (Exception ex) { MessageBox.Show("确认失败：" + SafeLog.Redact(ex.Message)); }
    }
    private sealed record ProcessChoice(GameProcess Process)
    {
        public string Display => $"{Process.Name}  |  PID {Process.Pid}  |  {Process.Path ?? "路径不可读"}";
        public override string ToString() => Display;
    }
    private void Install()
    {
        try
        {
            var legacy = ReadLegacyTaskState();
            if (legacy is not (LegacyTaskState.Missing or LegacyTaskState.Disabled))
            { MessageBox.Show("旧计划任务仍启用，或系统拒绝读取状态：LeiGodAutoStopMonitor。为避免双实例，本应用未注册登录启动。请先完成管理员迁移并确认任务已禁用。", "需要迁移"); return; }
            var exe = Environment.ProcessPath!; using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)!; key.SetValue("LeiGodAutoStop", "\"" + exe + "\" --background"); SafeLog.Write("installed current-user background startup entry"); MessageBox.Show("已设置当前用户登录后台启动。程序会显示在系统托盘中。", "完成");
        }
        catch (Exception ex) { MessageBox.Show("安装失败: " + SafeLog.Redact(ex.Message)); }
    }
    private enum LegacyTaskState { Unknown, Missing, Disabled, Enabled }
    private static LegacyTaskState ReadLegacyTaskState()
    {
        object? serviceObject = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            serviceObject = Activator.CreateInstance(type)!;
            dynamic service = serviceObject;
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask("LeiGodAutoStopMonitor");
            var taskState = (int)task.State;
            if (taskState == 4) return LegacyTaskState.Enabled; // Never install beside an already running instance.
            if (!(bool)task.Enabled) return LegacyTaskState.Disabled;
            if (taskState == 3) // TASK_STATE_READY: idle and waiting for a trigger.
            {
                dynamic actions = task.Definition.Actions;
                if ((int)actions.Count == 1)
                {
                    dynamic action = actions.Item(1);
                    if (LegacyTaskGuard.IsSafelyDisabledWrapper((string)action.Path, (string)action.Arguments))
                    {
                        SafeLog.Write("enabled legacy task is guarded by its existing immediate-exit marker; allowing current-user app startup");
                        return LegacyTaskState.Disabled;
                    }
                }
            }
            return LegacyTaskState.Enabled;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { return LegacyTaskState.Missing; }
        catch { return LegacyTaskState.Unknown; }
        finally { if (serviceObject is not null && Marshal.IsComObject(serviceObject)) Marshal.FinalReleaseComObject(serviceObject); }
    }
    private void Uninstall()
    {
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); key?.DeleteValue("LeiGodAutoStop", false); SafeLog.Write("removed current-user startup entry"); MessageBox.Show("已移除登录启动项。日志保留在本地应用数据目录。", "完成"); } catch (Exception ex) { MessageBox.Show("卸载失败: " + ex.Message); }
    }
}

internal static class LegacyTaskGuard
{
    private static readonly System.Text.RegularExpressions.Regex FileArgument = new("""(?i)(?:^|\s)-File\s+(?:"(?<quoted>[^"]+)"|(?<plain>\S+))""", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MarkerGuard = new("""(?i)^if\s*\(\s*Test-Path\s+-LiteralPath\s+\(Join-Path\s+\$PSScriptRoot\s+['"]LeiGodAutoStop\.legacy-disabled['"]\s*\)\s*\)\s*\{\s*return\s*\}\s*(?:#.*)?$""", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static bool IsSafelyDisabledWrapper(string executable, string arguments)
    {
        try
        {
            if (!Path.GetFileNameWithoutExtension(executable).Equals("powershell", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileNameWithoutExtension(executable).Equals("pwsh", StringComparison.OrdinalIgnoreCase)) return false;
            var match = FileArgument.Match(arguments);
            var scriptPath = match.Success ? match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value : "";
            if (!Path.IsPathRooted(scriptPath) || !File.Exists(scriptPath) || !File.Exists(Path.Combine(Path.GetDirectoryName(scriptPath)!, "LeiGodAutoStop.legacy-disabled"))) return false;
            foreach (var raw in File.ReadLines(scriptPath).Take(16))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.Equals("[CmdletBinding()]", StringComparison.OrdinalIgnoreCase) || line.Equals("param()", StringComparison.OrdinalIgnoreCase)) continue;
                return MarkerGuard.IsMatch(line);
            }
        }
        catch { }
        return false;
    }
}

public sealed record AutomaticAcceptanceRecord(
    string Protocol,
    string ClientVersion,
    string EvidenceId,
    DateTime AcceptedUtc,
    string ClientSha256,
    string ApplicationSha256,
    string MachineFingerprint,
    bool AutomatedPauseRequestVerified,
    bool PauseStateAndStableBalanceVerified,
    bool StopAccelerationVerified,
    bool OwnedClientExitVerified);

public static class AutomaticAcceptanceGate
{
    public const string RequiredProtocol = "leigod-auto-workflow-v3-machine-bound";
    public static string GetMachineFingerprint()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
        var machineGuid = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machineGuid)) throw new InvalidOperationException("Windows MachineGuid is unavailable; cannot bind acceptance to this computer");
        var material = machineGuid.Trim() + "\0" + ProcessOwnership.CurrentUserSid;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }
    public static bool Validate(string path, string currentClientVersion, string currentClientSha256, string currentApplicationSha256, string currentMachineFingerprint, out string reason)
    {
        reason = "未记录本机自动暂停/退出验收；保持仅观察模式";
        try
        {
            if (string.IsNullOrWhiteSpace(currentClientVersion) || string.IsNullOrWhiteSpace(currentClientSha256) || !File.Exists(path)) return false;
            var record = JsonSerializer.Deserialize<AutomaticAcceptanceRecord>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (record is null || record.Protocol != RequiredProtocol || record.ClientVersion != currentClientVersion || !record.ClientSha256.Equals(currentClientSha256, StringComparison.OrdinalIgnoreCase) || !record.ApplicationSha256.Equals(currentApplicationSha256, StringComparison.OrdinalIgnoreCase) || !record.MachineFingerprint.Equals(currentMachineFingerprint, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(record.EvidenceId) || record.AcceptedUtc == default || record.AcceptedUtc > DateTime.UtcNow.AddMinutes(5) || !record.AutomatedPauseRequestVerified || !record.PauseStateAndStableBalanceVerified || !record.StopAccelerationVerified || !record.OwnedClientExitVerified)
            { reason = "验收记录缺失、版本不匹配或未覆盖全部暂停/停止/退出证据"; return false; }
            reason = "已找到与当前客户端版本匹配的完整自动处理验收记录";
            return true;
        }
        catch { reason = "验收记录不可读；保持仅观察模式"; return false; }
    }
}
