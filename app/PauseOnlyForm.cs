using System.Diagnostics;
using System.ComponentModel;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace LeiGodAutoStop;

public sealed partial class PauseOnlyForm : Form
{
    private sealed record AutoSettings(bool AutoModeEnabled);
    private sealed record Observation(LeiGodProcess Client, string Root, string Logs, string? IdleKey, string State, string Reason, string? AccountIdentity = null);
    private sealed record MappingChoice(AccelerationSession Session, GameProcess Process)
    {
        public override string ToString() => $"{Session.GameName}（ID {Session.GameId}）— {Process.Name}，PID {Process.Pid}";
    }

    private readonly Label _headline = new() { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold) };
    private readonly Label _details = new() { AutoSize = true, MaximumSize = new Size(690, 0) };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(690, 0) };
    private readonly CheckBox _enabled = new() { Text = "自动暂停时长", AutoSize = true };
    private readonly Button _pauseMonitor = new() { Text = "暂停监控", AutoSize = true };
    private readonly Button _confirmMapping = new() { Text = "确认当前游戏进程", AutoSize = true };
    private readonly Button _manageMappings = new() { Text = "管理本机游戏映射", AutoSize = true };
    private readonly Button _runDiagnostics = new() { Text = "一键检查并开始", AutoSize = true };
    private readonly Label _diagnosticStatus = new();
    private readonly Button _installStartup = new() { Text = "安装为登录启动", AutoSize = true };
    private readonly Button _removeStartup = new() { Text = "取消登录启动", AutoSize = true };
    private readonly Button _installFollow = new() { Text = "启用随雷神启动和退出", AutoSize = true };
    private readonly Button _removeFollow = new() { Text = "关闭随雷神启动", AutoSize = true };
    private readonly System.Windows.Forms.Timer _followTimer = new() { Interval = 2000 };
    private readonly FollowExitGate _followExit = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _followClient;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly NotifyIcon _tray;
    private readonly SessionReader _sessions = new();
    private readonly GameDetector _games = new();
    private ClientLogPauseAdapter? _pauseAdapter;
    private string? _pauseAdapterKey;
    private readonly FirstUseDiagnosticRunner _diagnostics = new();
    private readonly PauseOnlyMonitor _monitor = new();
    private readonly bool _backgroundLaunch;
    private bool _polling;
    private bool _workflow;
    private bool _monitorPaused;
    private bool _exitRequested;
    private bool _setupRunning;
    private bool _diagnosticsRunning;
    private bool _diagnosticsReady;
    private DateTime _nextDiagnostics;
    private string _lastResult = "等待状态";
    private string? _lastClientKey;
    private string? _lastAccountIdentity;
    private string? _lastObservationSignature;

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop", "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LeiGodAutoStop";

    public PauseOnlyForm(bool preview = false)
    {
        _backgroundLaunch = Environment.GetCommandLineArgs().Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        _followClient = Environment.GetCommandLineArgs().Contains("--follow-leigod");
        _enabled.Checked = ReadAutoEnabled();
        Text = "雷神自动暂停时长";
        Width = 780;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开状态窗口", null, (_, _) => ShowStatusWindow());
        menu.Items.Add("退出程序", null, (_, _) => { _exitRequested = true; Close(); });
        _tray = new NotifyIcon { Text = "雷神自动暂停时长：监控中", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = !preview };
        _tray.DoubleClick += (_, _) => ShowStatusWindow();

        BuildDashboard();

        _enabled.CheckedChanged += (_, _) =>
        {
            SaveAutoEnabled(_enabled.Checked);
            _monitor.Reset(_enabled.Checked ? "已启用；重新计时120秒" : "自动监控已关闭");
            _lastResult = _enabled.Checked ? "自动暂停已启用" : "自动暂停已关闭";
            RefreshText();
        };
        _pauseMonitor.Click += (_, _) =>
        {
            if (_workflow) return;
            _monitorPaused = !_monitorPaused;
            _monitor.Reset(_monitorPaused ? "监控已暂停" : "监控已恢复；重新计时120秒");
            _pauseMonitor.Text = _monitorPaused ? "恢复监控" : "暂停监控";
            RefreshText();
        };
        _confirmMapping.Click += (_, _) => ConfirmCurrentGameProcess();
        _manageMappings.Click += (_, _) => ManageGameMappings();
        _runDiagnostics.Click += async (_, _) => await OneClickStartAsync();
        _installStartup.Click += (_, _) => InstallStartup();
        _removeStartup.Click += (_, _) => RemoveStartup();
        _installFollow.Click += (_, _) =>
        {
            try { FollowLeiGod.Install(); _followClient = true; RefreshFollowUi(); MessageBox.Show("已启用。雷神打开后自动监控，完全退出约6秒后监控退出。缩到托盘仍继续监控。", Text); }
            catch (Exception ex) { MessageBox.Show(SafeLog.Redact(ex.Message), "启用失败"); }
        };
        _removeFollow.Click += (_, _) =>
        {
            try { FollowLeiGod.Remove(); _followClient = false; RefreshFollowUi(); MessageBox.Show("已关闭随雷神启动；当前监控可从托盘退出。", Text); }
            catch (Exception ex) { MessageBox.Show(SafeLog.Redact(ex.Message), "关闭失败"); }
        };
        _followTimer.Tick += (_, _) =>
        {
            if (!_followClient || !_followExit.Tick(FollowLeiGod.ClientPresent())) return;
            _lifetime.Cancel();
            if (_workflow || _polling || _diagnosticsRunning) return;
            SafeLog.Write("follow monitor exiting: LeiGod client no longer running");
            _exitRequested = true;
            Close();
        };
        _timer.Tick += async (_, _) => await PollAsync();
        Shown += async (_, _) =>
        {
            if (preview) return;
            await PollAsync();
            if (!_backgroundLaunch) ShowFirstRunGuide();
            _timer.Start();
            _followTimer.Start();
            if (_backgroundLaunch) Hide();
        };
        FormClosing += (_, e) =>
        {
            if (_setupRunning && !_exitRequested) { e.Cancel = true; MessageBox.Show("一键检查和设置正在进行，请等结果返回。", Text); return; }
            if (_workflow) { e.Cancel = true; MessageBox.Show("正在验证账户暂停状态，请等处理完成。", Text); return; }
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            _tray.Visible = false;
        };
        FormClosed += (_, _) => { _tray.Dispose(); menu.Dispose(); _timer.Dispose(); _followTimer.Dispose(); _lifetime.Dispose(); };
        if (!preview) SafeLog.Write("pause-only monitor started; auto=" + _enabled.Checked + "; follow=" + _followClient + "; executable=" + Application.ExecutablePath);
    }

    private async Task PollAsync()
    {
        if (_polling || _workflow || _diagnosticsRunning) return;
        _polling = true;
        try
        {
            if (_monitorPaused) { _headline.Text = "监控已暂停"; _details.Text = "恢复监控后重新计时120秒。"; return; }
            if (!PauseOnlyClientDiscovery.TryFindRootClient(out var client, out var clientReason) || client is null)
            {
                _lastClientKey = null;
                _monitor.Reset(clientReason);
                LogObservation(clientReason, clientReason, idleEligible: false);
                _headline.Text = clientReason;
                _details.Text = "雷神运行后会自动开始监控。";
                RefreshText();
                return;
            }
            var clientKey = $"{client.Pid}/{client.StartTime.Ticks}";
            if (_lastClientKey != clientKey)
            {
                _lastClientKey = clientKey;
                _diagnosticsReady = false;
                _nextDiagnostics = DateTime.MinValue;
                _monitor.Reset("雷神客户端变化；重新计时120秒");
            }

            var observation = await ObserveAsync(client);
            if (observation.AccountIdentity is { } accountIdentity)
            {
                if (_lastAccountIdentity is not null && !_lastAccountIdentity.Equals(accountIdentity, StringComparison.Ordinal))
                {
                    _diagnosticsReady = false;
                    _nextDiagnostics = DateTime.MinValue;
                    _monitor.Reset("雷神账号已切换；重新检查并计时120秒");
                    _lastResult = "检测到雷神登录会话变化，正在重新检查账户";
                    SafeLog.Write("pause-only account session changed; automatic pause timer reset");
                }
                _lastAccountIdentity = accountIdentity;
            }
            if (DateTime.UtcNow >= _nextDiagnostics) await RunDiagnosticsAsync(showDetails: false);
            if (!_diagnosticsReady)
            {
                _monitor.Reset("环境检查未通过；等待自动复查");
                _headline.Text = "等待环境检查通过";
                _result.Text = _lastResult;
                return;
            }
            var due = _monitor.Tick(observation.IdleKey, observation.Reason, Stopwatch.GetTimestamp());
            LogObservation(observation.State, observation.Reason, observation.IdleKey is not null);
            _headline.Text = observation.IdleKey is null
                ? observation.State
                : $"{(_monitor.State == "本轮空闲已暂停" ? _monitor.State : observation.State)}  |  倒计时 {_monitor.Remaining:mm\\:ss}";
            _details.Text = $"{observation.Reason}\n没有已确认的加速游戏进程运行满 120 秒后，自动暂停账户时长。";
            _tray.Text = _enabled.Checked ? "雷神自动暂停时长：自动监控" : "雷神自动暂停时长：仅观察";
            _result.Text = _lastResult;
            if (due && _enabled.Checked) await PauseAtThresholdAsync(observation);
        }
        catch (Exception ex)
        {
            _monitor.Reset("状态未知；暂停倒计时");
            _lastResult = "读取失败：" + SafeLog.Redact(ex.GetType().Name + ": " + ex.Message);
            SafeLog.Write("pause-only monitor poll failed: " + _lastResult);
            RefreshText();
        }
        finally { _polling = false; }
    }

    private async Task<Observation> ObserveAsync(LeiGodProcess client, bool refreshAccountIdentity = false)
    {
        if (!IsSameClient(client)) return new(client, "", "", null, "雷神客户端已退出或变化", "旧会话不能继续暂停账户");
        var root = client.Path is null ? null : Path.GetDirectoryName(client.Path);
        root ??= LeiGodDiscovery.FindRoots().SingleOrDefault();
        var logs = LeiGodDiscovery.FindLogDirectory(root);
        if (root is null || logs is null) return new(client, root ?? "", logs ?? "", null, "雷神目录或日志不可读", "无法确认雷神日志目录");
        var accountIdentity = GetPauseAdapter(client, logs).ReadAccountIdentity(refreshAccountIdentity);

        bool SameClientWithoutActiveSession()
        {
            if (!IsSameClient(client)) return false;
            var now = _sessions.Read(logs, client.Pid, client.StartTime);
            return now.Knowledge == SessionKnowledge.Unknown && now.SdkIdleFallbackAllowed;
        }

        var snapshot = _sessions.Read(logs, client.Pid, client.StartTime);
        if (snapshot.Knowledge == SessionKnowledge.Accelerating)
        {
            var game = _games.Find(snapshot);
            if (!game.Known) return new(client, root, logs, null, "游戏进程识别不完整", game.Reason, accountIdentity);
            if (game.Processes.Count > 0)
                return new(client, root, logs, null, "加速游戏仍运行", string.Join(", ", game.Processes.Select(p => $"{p.Name} PID {p.Pid}")), accountIdentity);
            var set = snapshot.ActiveSessions.Count > 0
                ? string.Join(",", snapshot.ActiveSessions.OrderBy(s => s.GameId, StringComparer.Ordinal).Select(s => s.GameId + ":" + s.EventTime.Ticks))
                : snapshot.Session!.GameId + ":" + snapshot.Session.EventTime.Ticks;
            return new(client, root, logs, $"{client.Pid}/{client.StartTime.Ticks}/active/{set}", "加速游戏进程均已退出", game.Reason, accountIdentity);
        }

        if (snapshot.Knowledge == SessionKnowledge.NotAccelerating && snapshot.StopStateConfirmed && snapshot.StoppedGameIds.Count > 0)
        {
            var game = _games.FindStopped(snapshot);
            if (!game.Known) return new(client, root, logs, null, "已停止会话的进程映射不完整", game.Reason, accountIdentity);
            if (game.Processes.Count > 0)
                return new(client, root, logs, null, "雷神关联的游戏仍运行", string.Join(", ", game.Processes.Select(p => $"{p.Name} PID {p.Pid}")), accountIdentity);
            var set = string.Join(",", snapshot.StoppedGameIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return new(client, root, logs, $"{client.Pid}/{client.StartTime.Ticks}/stopped/{set}/{snapshot.Session!.EventTime.Ticks}", "雷神确认未加速", "停止日志与关联进程均已复核", accountIdentity);
        }

        if (!snapshot.SdkIdleFallbackAllowed)
            return new(client, root, logs, null, "会话日志未能证明安全空闲", snapshot.Reason, accountIdentity);

        var sdk = new PauseOnlySdkReader(root, client.Pid, client.StartTime, SameClientWithoutActiveSession);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var sdkState = await sdk.ProbeAsync(timeout.Token);
        if (sdkState == PauseOnlySdkReader.IdleObservation.NotAccelerating)
            return new(client, root, logs, $"{client.Pid}/{client.StartTime.Ticks}/sdk-idle", "SDK确认未加速", snapshot.Reason, accountIdentity);
        if (sdkState == PauseOnlySdkReader.IdleObservation.Accelerating)
            return new(client, root, logs, null, "SDK仍报告加速；会话日志不完整", snapshot.Reason, accountIdentity);
        return new(client, root, logs, null, "加速状态未知；暂停倒计时", "SDK query_nn_acc 未返回可用状态；" + snapshot.Reason, accountIdentity);
    }

    private async Task PauseAtThresholdAsync(Observation original)
    {
        if (_workflow || !_enabled.Checked || original.IdleKey is null || original.Root.Length == 0) return;
        var key = original.IdleKey;
        _workflow = true;
        _installFollow.Enabled = false; _removeFollow.Enabled = false;
        _enabled.Enabled = false; _pauseMonitor.Enabled = false; _confirmMapping.Enabled = false; _manageMappings.Enabled = false; _installStartup.Enabled = false; _removeStartup.Enabled = false;
        _lastResult = "复核无加速游戏状态…";
        RefreshText();
        try
        {
            var fresh = await ObserveAsync(original.Client, refreshAccountIdentity: true);
            if (!string.Equals(fresh.IdleKey, key, StringComparison.Ordinal)) throw new InvalidOperationException("暂停前游戏、会话或客户端状态变化");
            if (!string.Equals(fresh.AccountIdentity, original.AccountIdentity, StringComparison.Ordinal)) throw new InvalidOperationException("雷神账号已切换；本轮暂停取消并重新计时");
            var pause = GetPauseAdapter(original.Client, original.Logs);
            var first = await pause.ReadPauseStateAsync(_lifetime.Token);
            var pauseRequested = false;
            SafeLog.Write($"pause-only preflight client_pid={original.Client.Pid}; paused={first.IsPaused}; balance_available={first.RemainingSeconds.HasValue}");
            if (first.RemainingSeconds is null) throw new InvalidOperationException("账户预检状态未知：" + first.Evidence + "；本轮不发送暂停请求");
            if (!first.IsPaused)
            {
                if (!pause.CanControl("")) throw new InvalidOperationException("当前雷神客户端没有可用的暂停请求参数");
                fresh = await ObserveAsync(original.Client, refreshAccountIdentity: true);
                if (!string.Equals(fresh.IdleKey, key, StringComparison.Ordinal)) throw new InvalidOperationException("暂停请求发送前游戏或加速状态变化");
                if (!string.Equals(fresh.AccountIdentity, original.AccountIdentity, StringComparison.Ordinal)) throw new InvalidOperationException("雷神账号已切换；本轮暂停取消并重新计时");
                pauseRequested = true;
                try { await pause.RequestPauseAsync(_lifetime.Token); }
                catch (PauseRequestOutcomeUnknownException ex)
                {
                    SafeLog.Write($"pause-only request outcome unknown client_pid={original.Client.Pid}; disposition={SafeLog.Redact(ex.Message)}; checking account readback without retry");
                }
                SafeLog.Write($"pause-only pause request attempt ended client_pid={original.Client.Pid}; disposition={SafeLog.Redact(pause.LastRequestDisposition)}");
                var until = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < until)
                {
                    first = await pause.ReadPauseStateAsync(_lifetime.Token);
                    if (first.IsPaused && first.RemainingSeconds.HasValue) break;
                    await Task.Delay(1000, _lifetime.Token);
                }
            }
            if (!first.IsPaused || first.RemainingSeconds is null) throw new InvalidOperationException("账户没有确认暂停状态");
            // A single pause_status_id=1 read is not enough.  On some client
            // versions the status flips before the live countdown has stopped.
            // Keep the client untouched and require three fresh reads over 45s;
            // any real decrease is treated as a failed pause.
            var second = first;
            for (var sample = 1; sample <= 3; sample++)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _lifetime.Token);
                fresh = await ObserveAsync(original.Client, refreshAccountIdentity: true);
                if (!string.Equals(fresh.IdleKey, key, StringComparison.Ordinal)) throw new InvalidOperationException("账户读回期间游戏或加速状态变化");
                if (!string.Equals(fresh.AccountIdentity, original.AccountIdentity, StringComparison.Ordinal)) throw new InvalidOperationException("账户读回期间雷神账号已切换；暂停结果作废");
                second = await pause.ReadPauseStateAsync(_lifetime.Token);
                if (!PauseProofRules.ConfirmedStable(first, second, TimeSpan.FromSeconds(sample * 15)))
                    throw new InvalidOperationException($"账户暂停状态未通过第 {sample}/3 次真实余额复核；余额可能仍在倒计时");
                SafeLog.Write($"pause-only balance verification sample={sample}/3; paused={second.IsPaused}; remaining={second.RemainingSeconds:0}");
                _lastResult = $"暂停请求已确认，正在复核真实余额 {sample}/3…";
                RefreshText();
            }
            if (pauseRequested && original.AccountIdentity is { } verifiedAccount)
            {
                FirstUseDiagnosticRunner.RecordLivePauseSuccess(verifiedAccount);
                _diagnosticStatus.Text = "暂停功能已由当前账号真实请求和稳定余额回读验证";
            }
            _monitor.MarkPaused(key);
            _lastResult = pauseRequested
                ? $"已请求并确认暂停，余额稳定读回 {second.RemainingSeconds:0} 秒"
                : $"账户原已暂停，余额稳定读回 {second.RemainingSeconds:0} 秒";
            SafeLog.Write($"pause-only workflow completed client_pid={original.Client.Pid}; paused=true; pause_request_sent={pauseRequested}; remaining={second.RemainingSeconds:0}");
        }
        catch (Exception ex)
        {
            _monitor.RetryAfter(key, Stopwatch.GetTimestamp());
            _lastResult = "暂停未确认：" + SafeLog.Redact(ex.GetType().Name + ": " + ex.Message);
            SafeLog.Write("pause-only workflow failed: " + _lastResult);
        }
        finally
        {
            _workflow = false;
            _installFollow.Enabled = true; _removeFollow.Enabled = true;
            _enabled.Enabled = true; _pauseMonitor.Enabled = true; _confirmMapping.Enabled = true; _manageMappings.Enabled = true; _installStartup.Enabled = true; _removeStartup.Enabled = true;
            RefreshText();
        }
    }

    private void ConfirmCurrentGameProcess()
    {
        if (_workflow) return;
        try
        {
            if (!PauseOnlyClientDiscovery.TryFindRootClient(out var client, out var reason) || client is null) throw new InvalidOperationException(reason);
            var logs = LeiGodDiscovery.FindLogDirectory(Path.GetDirectoryName(client.Path));
            if (logs is null) throw new InvalidOperationException("当前雷神客户端日志不可读");
            var snapshot = _sessions.Read(logs, client.Pid, client.StartTime);
            if (snapshot.Knowledge != SessionKnowledge.Accelerating) throw new InvalidOperationException("请先在雷神中加速并运行要确认的游戏");
            var sessions = snapshot.ActiveSessions.Count > 0 ? snapshot.ActiveSessions : snapshot.Session is null ? [] : [snapshot.Session];
            var choices = new List<MappingChoice>();
            var mappingNotices = new List<string>();
            foreach (var session in sessions)
            {
                var inventory = _games.CandidateProcesses(session);
                if (!inventory.Complete) throw new InvalidOperationException($"游戏 ID {session.GameId}：{inventory.Reason}");
                if (inventory.Reason.Contains("已忽略不属于本次会话/已核实映射的旧项：", StringComparison.Ordinal))
                    mappingNotices.Add($"游戏 ID {session.GameId}：" + inventory.Reason[(inventory.Reason.IndexOf("已忽略不属于本次会话/已核实映射的旧项：", StringComparison.Ordinal))..]);
                choices.AddRange(inventory.Candidates.Select(process => new MappingChoice(session, process)));
            }
            if (choices.Count == 0) throw new InvalidOperationException("当前加速游戏进程尚未运行；进入游戏后再确认");

            using var dialog = new Form { Text = "确认雷神加速游戏进程", Width = 720, Height = 300, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
            var instruction = new Label { Width = 660, Height = mappingNotices.Count > 0 ? 70 : 45, Text =
                "列表只包含雷神本次会话记录的游戏进程名和已核实的内置游戏进程。请核对游戏 ID、进程名与路径后明确选择。" +
                (mappingNotices.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, mappingNotices) : "") };
            var picker = new ComboBox { Width = 660, DropDownStyle = ComboBoxStyle.DropDownList };
            picker.Items.AddRange(choices.Cast<object>().ToArray());
            var path = new Label { Width = 660, Height = 55 };
            picker.SelectedIndexChanged += (_, _) => path.Text = picker.SelectedItem is MappingChoice choice ? "完整路径：" + choice.Process.Path : "";
            var confirm = new Button { Text = "确认此进程", AutoSize = true, DialogResult = DialogResult.OK };
            confirm.Enabled = false;
            picker.SelectedIndexChanged += (_, _) => confirm.Enabled = picker.SelectedItem is MappingChoice;
            panel.Controls.AddRange([instruction, picker, path, confirm]);
            dialog.Controls.Add(panel);
            if (dialog.ShowDialog(this) != DialogResult.OK || picker.SelectedItem is not MappingChoice selected) return;

            if (!IsSameClient(client)) throw new InvalidOperationException("雷神客户端已变化，不能保存旧进程映射");
            var fresh = _sessions.Read(logs, client.Pid, client.StartTime);
            var freshSessions = fresh.ActiveSessions.Count > 0 ? fresh.ActiveSessions : fresh.Session is null ? [] : [fresh.Session];
            if (fresh.Knowledge != SessionKnowledge.Accelerating || !freshSessions.Any(s => s.GameId == selected.Session.GameId && s.EventTime == selected.Session.EventTime))
                throw new InvalidOperationException("雷神加速会话已变化，请重新选择");
            if (!GameDetector.StillMatchesLiveProcess(selected.Process))
                throw new InvalidOperationException("所选游戏进程已退出或路径发生变化");
            _games.ConfirmProcess(selected.Session.GameId, selected.Process);
            _monitor.Reset("游戏进程映射已确认；重新观察120秒");
            _lastResult = $"已确认游戏 ID {selected.Session.GameId} 的进程 {selected.Process.Name}";
            SafeLog.Write($"pause-only game mapping confirmed game_id={selected.Session.GameId} process={selected.Process.Name} pid={selected.Process.Pid}");
            RefreshText();
        }
        catch (Exception ex)
        {
            MessageBox.Show(SafeLog.Redact(ex.Message), "无法确认游戏进程", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ManageGameMappings()
    {
        try
        {
            using var dialog = new Form { Text = "管理本机游戏映射", Width = 820, Height = 430, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var hint = new Label { Dock = DockStyle.Fill, Text = "映射只保存在这台电脑，用于把雷神游戏 ID 对应到已确认的 EXE 完整路径。换电脑后请在目标电脑重新确认。", TextAlign = ContentAlignment.MiddleLeft };
            var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, Font = new Font("Consolas", 9) };
            var mappings = _games.GetConfirmedMappings().ToList();
            void RefreshList()
            {
                list.Items.Clear();
                foreach (var mapping in mappings)
                    list.Items.Add(new MappingListItem(mapping));
                list.DisplayMember = nameof(MappingListItem.Display);
            }
            RefreshList();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "关闭", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
            var remove = new Button { Text = "删除选中映射", Width = 140, Height = 32 };
            remove.Click += (_, _) =>
            {
                if (list.SelectedItem is not MappingListItem selected) return;
                if (MessageBox.Show(dialog, $"删除游戏 ID {selected.Mapping.GameId} 的映射？下次该游戏运行时需要重新确认。", "删除映射", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                if (_games.RemoveConfirmedMapping(selected.Mapping))
                {
                    mappings.Remove(selected.Mapping);
                    _monitor.Reset("游戏映射已修改；重新观察120秒");
                    _lastResult = "已删除所选本机游戏映射";
                    RefreshList();
                    RefreshText();
                }
            };
            buttons.Controls.Add(close); buttons.Controls.Add(remove);
            layout.Controls.Add(hint, 0, 0); layout.Controls.Add(list, 0, 1); layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout); dialog.CancelButton = close;
            dialog.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(SafeLog.Redact(ex.Message), "无法管理游戏映射", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ShowFirstRunGuide()
    {
        var guidePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop", "first-run-guide.json");
        if (File.Exists(guidePath)) return;
        const string message = "首次使用：\n\n1. 在这台电脑安装并登录雷神，保持客户端运行。\n2. 点“一键检查并开始”；程序会自行校验安装包、雷神登录/日志/账户接口并设置自动跟随。Windows 弹出权限提示时选“是”。\n3. 在雷神里加速并实际启动游戏；如应用提示需要映射，点“确认当前游戏进程”，核对游戏程序完整路径。每个游戏首次运行只需确认一次。\n4. 之后雷神启动时应用会自动开始监控；手动打开应用时点“开始监控”。\n5. 换电脑后，在目标电脑用它自己的雷神账号重新点“一键检查并开始”，游戏映射也在目标电脑确认。\n\n版本号只记录，不作为兼容性门槛。账户暂停只有在接口确认暂停且余额稳定读回后才显示成功。";
        MessageBox.Show(this, message, "首次使用", MessageBoxButtons.OK, MessageBoxIcon.Information);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guidePath)!);
            File.WriteAllText(guidePath, JsonSerializer.Serialize(new { ShownAt = DateTimeOffset.Now, Version = Application.ProductVersion }));
        }
        catch (Exception ex) { SafeLog.Write("first-run guide marker write failed: " + ex.GetType().Name); }
    }

    private sealed record MappingListItem(ConfirmedGameMapping Mapping)
    {
        public string Display => $"游戏 ID {Mapping.GameId}  |  {Mapping.Alias.Name}  |  {Mapping.Alias.Path ?? "（未绑定完整路径）"}";
        public override string ToString() => Display;
    }

    private bool IsSameClient(LeiGodProcess expected)
    {
        try
        {
            using var current = Process.GetProcessById(expected.Pid);
            return current.ProcessName.Equals("leigod", StringComparison.OrdinalIgnoreCase) && current.StartTime == expected.StartTime &&
                   string.Equals(LeiGodDiscovery.SafePath(current), expected.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private ClientLogPauseAdapter GetPauseAdapter(LeiGodProcess client, string logs)
    {
        var key = $"{client.Pid}/{client.StartTime.Ticks}/{logs}";
        if (_pauseAdapter is null || !string.Equals(_pauseAdapterKey, key, StringComparison.OrdinalIgnoreCase))
        {
            _pauseAdapterKey = key;
            _pauseAdapter = new ClientLogPauseAdapter(logs, client.Pid, client.StartTime);
        }
        return _pauseAdapter;
    }

    private void RefreshText()
    {
        if (_monitorPaused) _headline.Text = "监控已暂停";
        else if (!_enabled.Checked) _headline.Text = "自动暂停已关闭";
        else if (!_headline.Text.Contains("雷神未运行", StringComparison.Ordinal)) _headline.Text = _monitor.State;
        _details.Text = "仅在没有雷神加速的游戏进程时暂停账户时长；不会关闭游戏或雷神。";
        _result.Text = _lastResult;
        _tray.Text = _enabled.Checked ? "雷神自动暂停时长：自动监控" : "雷神自动暂停时长：仅观察";
    }

    private async Task OneClickStartAsync()
    {
        if (_workflow || _diagnosticsRunning) return;
        if (!PackageIntegrity.Verify(out var packageResult))
        {
            MessageBox.Show(packageResult, "安装包检查未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _runDiagnostics.Enabled = false;
        _setupRunning = true;
        _diagnosticStatus.Text = "正在检查安装包和运行权限…";
        try
        {
            if (!FollowLeiGod.IsAdministrator)
            {
                try
                {
                    if (File.Exists(FollowLeiGod.ResultFile)) File.Delete(FollowLeiGod.ResultFile);
                    var elevated = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--setup-and-start")
                    {
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    if (elevated is null) throw new InvalidOperationException("Windows 未启动管理员设置程序。");
                    _diagnosticStatus.Text = "请在 Windows 权限提示中选择“是”；正在完成检查和设置…";
                    await elevated.WaitForExitAsync();
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    _diagnosticStatus.Text = "你取消了权限授权；设置未更改。";
                    MessageBox.Show("你取消了 Windows 权限授权，软件没有更改设置。需要启用随雷神启动时，再点“一键检查并开始”并选择“是”。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var succeeded = false;
                var detail = "没有收到管理员设置结果，请重试。";
                try
                {
                    using var result = JsonDocument.Parse(File.ReadAllText(FollowLeiGod.ResultFile));
                    var root = result.RootElement;
                    succeeded = root.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("Message", out var message)) detail = message.GetString() ?? detail;
                }
                catch { }
                if (!succeeded)
                {
                    _diagnosticStatus.Text = "检查未通过；按提示修复后重试。";
                    MessageBox.Show(detail, "一键设置未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _followClient = true;
                _diagnosticsReady = true;
                _diagnosticStatus.Text = "检查通过；监控正在以所需权限启动。";
                _lastResult = detail;
                MessageBox.Show(detail + "\n\n设置完成，监控窗口将以所需权限重新启动。之后雷神启动时会自动开始监控。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _exitRequested = true;
                Close();
                return;
            }

            var report = await RunDiagnosticsAsync(showDetails: true);
            if (report is null || !report.CanMonitor) return;
            _enabled.Checked = true;
            FollowLeiGod.Install();
            _followClient = true;
            _diagnosticStatus.Text = "检查通过；雷神启动时将自动开始监控。";
            _lastResult = packageResult + "\n" + report.Summary;
            _runDiagnostics.Text = "开始监控";
            RefreshFollowUi();
            RefreshText();
            MessageBox.Show(packageResult + "\n" + report.Summary + "\n\n已完成设置。之后雷神启动时会自动开始监控。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            var message = SafeLog.Redact(ex.GetType().Name + ": " + ex.Message);
            _diagnosticStatus.Text = "设置未完成：" + ex.GetType().Name;
            MessageBox.Show(message, "一键检查未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _setupRunning = false; _runDiagnostics.Enabled = true; }
    }

    private async Task<DiagnosticReport?> RunDiagnosticsAsync(bool showDetails)
    {
        if (_diagnosticsRunning || _workflow) return null;
        _diagnosticsRunning = true;
        _runDiagnostics.Enabled = false;
        _diagnosticStatus.Text = "正在检查雷神版本、日志、登录状态和游戏识别…";
        _lastResult = "首次环境检查进行中…";
        RefreshText();
        try
        {
            var report = await _diagnostics.RunAsync(_lifetime.Token);
            _diagnosticsReady = report.CanMonitor;
            _nextDiagnostics = DateTime.UtcNow.AddSeconds(report.CanMonitor ? 300 : 30);
            if (!_diagnosticsReady) _monitor.Reset("环境检查未通过");
            _diagnosticStatus.Text = report.Summary;
            _lastResult = report.Summary;
            RefreshText();
            if (showDetails) MessageBox.Show(report.ToDisplay(), "首次使用环境检查", MessageBoxButtons.OK, report.Checks.Any(c => c.Severity == DiagnosticSeverity.Fail) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            return report;
        }
        catch (Exception ex)
        {
            _diagnosticsReady = false;
            _nextDiagnostics = DateTime.UtcNow.AddSeconds(30);
            _diagnosticStatus.Text = "检查失败：" + ex.GetType().Name;
            _lastResult = _diagnosticStatus.Text;
            RefreshText();
            if (showDetails) MessageBox.Show(SafeLog.Redact(ex.Message), "首次使用环境检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        finally
        {
            _diagnosticsRunning = false;
            _runDiagnostics.Enabled = true;
        }
    }


    private void LogObservation(string state, string reason, bool idleEligible)
    {
        var signature = $"{state}|{reason}|{idleEligible}";
        if (string.Equals(signature, _lastObservationSignature, StringComparison.Ordinal)) return;
        _lastObservationSignature = signature;
        SafeLog.Write($"pause-only observation state={SafeLog.Redact(state)} idle_eligible={idleEligible} reason={SafeLog.Redact(reason)}");
    }

    private static bool ReadAutoEnabled()
    {
        try { return JsonSerializer.Deserialize<AutoSettings>(File.ReadAllText(SettingsPath))?.AutoModeEnabled ?? true; }
        catch { return true; }
    }

    private static void SaveAutoEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new AutoSettings(enabled)));
            File.Move(temp, SettingsPath, true);
        }
        catch (Exception ex) { SafeLog.Write("cannot save auto-pause preference: " + ex.GetType().Name); }
    }

    private void InstallStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(RunValue, $"\"{Application.ExecutablePath}\" --background");
            MessageBox.Show("已设置为当前 Windows 用户登录时自动启动。", Text);
        }
        catch (Exception ex) { MessageBox.Show("设置登录启动失败：" + ex.GetType().Name, Text); }
    }

    private void RemoveStartup()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true); key?.DeleteValue(RunValue, throwOnMissingValue: false); MessageBox.Show("已取消本应用的登录启动。", Text); }
        catch (Exception ex) { MessageBox.Show("取消登录启动失败：" + ex.GetType().Name, Text); }
    }

    private void ShowStatusWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
}

