global using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace LeiGodAutoStop;

public sealed record UiAutomationElementSnapshot(int ProcessId, int WindowHandle, string WindowName, string Name, string ControlType, string AutomationId, bool IsEnabled, bool InvokeAvailable);
public sealed record UiAutomationWindowSnapshot(int ProcessId, int Handle, string Name, bool IsModal, IReadOnlyList<UiAutomationElementSnapshot> Elements);

public interface ILeiGodUiAutomationProvider
{
    bool ReadbackContractEnabled { get; }
    bool ValidateClientIdentity(int processId, DateTime startTime, string executablePath);
    int GetMainWindowHandle(int processId);
    IReadOnlyList<UiAutomationWindowSnapshot> ReadProcessWindows(int processId);
    void InvokeExact(UiAutomationElementSnapshot target);
}

/// <summary>Read-only-by-default provider. The caller must explicitly enable the live-readback contract after supervised validation.</summary>
public sealed class WindowsLeiGodUiAutomationProvider(bool liveReadbackContractEnabled = false) : ILeiGodUiAutomationProvider
{
    public bool ReadbackContractEnabled { get; } = liveReadbackContractEnabled;
    public bool ValidateClientIdentity(int processId, DateTime startTime, string executablePath)
    {
        try { using var p = Process.GetProcessById(processId); return p.ProcessName.Equals("leigod", StringComparison.OrdinalIgnoreCase) && p.StartTime == startTime && LeiGodDiscovery.SafePath(p) is { } path && Path.GetFullPath(path).Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    public int GetMainWindowHandle(int processId) { using var p = Process.GetProcessById(processId); return p.MainWindowHandle.ToInt32(); }
    public bool ProbeStopActionContract(int processId, DateTime startTime, string executablePath)
    {
        try
        {
            if (!ValidateClientIdentity(processId, startTime, executablePath)) return false;
            var mainHandle = GetMainWindowHandle(processId);
            if (mainHandle == 0) return false;
            var windows = ReadProcessWindows(processId).Where(w => !w.IsModal && w.Handle == mainHandle).ToArray();
            return windows.Length == 1 && windows[0].Elements.Count(e => e.ProcessId == processId && e.WindowHandle == mainHandle && e.ControlType == ControlType.Button.ProgrammaticName && e.Name == "停止加速" && e.IsEnabled && e.InvokeAvailable) == 1;
        }
        catch { return false; }
    }

    public IReadOnlyList<UiAutomationWindowSnapshot> ReadProcessWindows(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, processId), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)));
        var result = new List<UiAutomationWindowSnapshot>();
        for (var i = 0; i < windows.Count; i++)
        {
            var root = windows[i];
            if (root.Current.ProcessId != processId) continue;
            var handle = root.Current.NativeWindowHandle;
            var nodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var elements = new List<UiAutomationElementSnapshot>();
            for (var j = 0; j < nodes.Count; j++)
            {
                var node = nodes[j];
                if (node.Current.ProcessId != processId) continue;
                var type = node.Current.ControlType;
                if (type != ControlType.Button && type != ControlType.Text && type != ControlType.MenuItem && type != ControlType.Edit) continue;
                bool invoke = false;
                try { invoke = node.TryGetCurrentPattern(InvokePattern.Pattern, out _); } catch { }
                elements.Add(new(processId, handle, root.Current.Name ?? "", node.Current.Name ?? "", type.ProgrammaticName, node.Current.AutomationId ?? "", node.Current.IsEnabled, invoke));
            }
            bool modal = false;
            try { if (root.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern)) modal = ((WindowPattern)pattern).Current.IsModal; } catch { }
            result.Add(new(processId, handle, root.Current.Name ?? "", modal, elements));
        }
        return result;
    }

    public void InvokeExact(UiAutomationElementSnapshot target)
    {
        if (!target.InvokeAvailable || !target.IsEnabled || target.ProcessId <= 0 || target.WindowHandle == 0) throw new InvalidOperationException("UIA 控件不可操作");
        var windows = ReadProcessWindows(target.ProcessId).Where(w => w.Handle == target.WindowHandle).ToArray();
        if (windows.Length != 1) throw new InvalidOperationException("目标 UIA 窗口已变化或不唯一");
        var elements = windows[0].Elements.Where(e => e.Name == target.Name && e.ControlType == target.ControlType && e.AutomationId == target.AutomationId && e.InvokeAvailable && e.IsEnabled).ToArray();
        if (elements.Length != 1) throw new InvalidOperationException("目标 UIA 控件已变化或不唯一");
        var root = AutomationElement.FromHandle(new IntPtr(target.WindowHandle));
        var found = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var matching = new List<AutomationElement>();
        for (var i = 0; i < found.Count; i++)
        {
            var e = found[i];
            if (e.Current.ProcessId == target.ProcessId && e.Current.Name == target.Name && e.Current.AutomationId == target.AutomationId && e.Current.ControlType.ProgrammaticName == target.ControlType && e.Current.IsEnabled && e.TryGetCurrentPattern(InvokePattern.Pattern, out _)) matching.Add(e);
        }
        if (matching.Count != 1) throw new InvalidOperationException("目标 UIA InvokePattern 控件不唯一");
        ((InvokePattern)matching[0].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }
}

public interface ILeiGodVerifiedExitController
{
    Task RequestVerifiedExitAsync(CancellationToken ct);
    Task RequestPausedSessionTreeExitAsync(CancellationToken ct) => RequestVerifiedExitAsync(ct);
    Task<bool> AreOwnedProcessesGoneAsync(CancellationToken ct);
}

/// <summary>Official UI route for stop-acc, gated by both explicit registration and supervised live readback validation.</summary>
public sealed class LeiGodUiAutomationStopAdapter : IStopAdapter
{
    private readonly int _pid;
    private readonly DateTime _start;
    private readonly string _expectedPath;
    private readonly string _expectedStopPrompt;
    private readonly IReadOnlySet<string> _expectedGameIds;
    private readonly ILeiGodUiAutomationProvider _uia;
    private readonly Func<SessionSnapshot> _readSession;
    private readonly Func<bool> _sameClientAndNoGame;
    private readonly Func<bool> _stoppedSessionAndNoGame;
    private readonly Func<Task<bool>> _pauseStillConfirmed;
    private readonly ILeiGodVerifiedExitController _exit;
    private readonly bool _registered;
    private readonly bool _liveReadbackValidated;

    public LeiGodUiAutomationStopAdapter(int pid, DateTime start, string expectedPath, string expectedGameName, IEnumerable<string> expectedGameIds,
        ILeiGodUiAutomationProvider uia, Func<SessionSnapshot> readSession, Func<bool> sameClientAndNoGame, Func<bool> stoppedSessionAndNoGame,
        Func<Task<bool>> pauseStillConfirmed, ILeiGodVerifiedExitController exit,
        bool registered = false, bool liveReadbackValidated = false)
    {
        _pid = pid; _start = start; _expectedPath = Path.GetFullPath(expectedPath); _expectedStopPrompt = $"当前正在加速 {expectedGameName}，是否确定要停止加速?"; _expectedGameIds = expectedGameIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _uia = uia; _readSession = readSession; _sameClientAndNoGame = sameClientAndNoGame; _stoppedSessionAndNoGame = stoppedSessionAndNoGame; _pauseStillConfirmed = pauseStillConfirmed; _exit = exit;
        _registered = registered; _liveReadbackValidated = liveReadbackValidated;
    }
    public string Id => "leigod-ui-automation-stop-with-log-readback";
    public int Version => 1;
    public bool CanStopAcceleration => _registered && _liveReadbackValidated && _uia.ReadbackContractEnabled && _expectedGameIds.Count > 0;

    public async Task RequestStopAccelerationAsync(CancellationToken ct)
    {
        EnsureEnabled(); EnsureClient();
        if (!_sameClientAndNoGame()) throw new InvalidOperationException("发送停止动作前客户端/游戏状态未通过复核");
        var windows = _uia.ReadProcessWindows(_pid);
        var main = GetMainWindow(windows, _uia.GetMainWindowHandle(_pid));
        var actions = main.Elements.Where(e => e.ProcessId == _pid && e.WindowHandle == main.Handle && e.ControlType == ControlType.Button.ProgrammaticName && e.Name == "停止加速" && e.IsEnabled && e.InvokeAvailable).ToArray();
        if (actions.Length != 1) throw new InvalidOperationException("主窗口中精确的‘停止加速’按钮缺失或不唯一");
        _uia.InvokeExact(actions[0]);
        await ResolveConfirmationAsync(ct);
        if (!await WaitForStopProofAsync(ct)) throw new InvalidOperationException("客户端日志未确认 stop-acc 与每个活动游戏 ID 的 normal/stopped 状态");
    }

    public Task<bool> IsAccelerationStoppedAsync(CancellationToken ct) => Task.FromResult(HasStopProof());

    public async Task RequestNormalExitAsync(CancellationToken ct)
    {
        EnsureEnabled(); EnsureClient();
        if (!HasStopProof()) throw new InvalidOperationException("退出前缺少客户端 stop-acc/status normal 独立证据");
        if (!_stoppedSessionAndNoGame() || !await _pauseStillConfirmed()) throw new InvalidOperationException("退出前 stop proof、游戏或暂停状态复核失败");
        await _exit.RequestVerifiedExitAsync(ct);
    }
    public Task<bool> AreOwnedClientProcessesGoneAsync(CancellationToken ct) => _exit.AreOwnedProcessesGoneAsync(ct);

    private void EnsureEnabled() { if (!CanStopAcceleration) throw new NotSupportedException("UI Automation 停止协议尚未注册并经现场 readback 验证；自动控制保持关闭"); }
    private void EnsureClient()
    {
        if (!_uia.ValidateClientIdentity(_pid, _start, _expectedPath)) throw new InvalidOperationException("雷神客户端 PID/启动时间/路径与已选会话不符");
    }
    private static UiAutomationWindowSnapshot GetMainWindow(IReadOnlyList<UiAutomationWindowSnapshot> windows, int expectedHwnd)
    {
        var main = windows.Where(w => !w.IsModal && w.Handle == expectedHwnd && w.Handle != 0).ToArray();
        if (main.Length != 1) throw new InvalidOperationException("当前客户端主窗口缺失或不唯一");
        return main[0];
    }
    private async Task ResolveConfirmationAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested(); EnsureClient();
            var windows = _uia.ReadProcessWindows(_pid);
            var prompts = windows.Where(w => w.Elements.Any(e => e.ControlType == ControlType.Text.ProgrammaticName && e.Name.Contains(_expectedStopPrompt, StringComparison.Ordinal))).ToArray();
            if (prompts.Length > 1) throw new InvalidOperationException("停止确认对话框不唯一");
            if (prompts.Length == 1)
            {
                var confirms = prompts[0].Elements.Where(e => e.ControlType == ControlType.Button.ProgrammaticName && e.Name == "确定" && e.IsEnabled && e.InvokeAvailable).ToArray();
                if (confirms.Length != 1) throw new InvalidOperationException("预期停止确认文案存在，但唯一‘确定’按钮缺失或不唯一");
                if (!_sameClientAndNoGame() || !await _pauseStillConfirmed()) throw new InvalidOperationException("确认停止前游戏或暂停状态变化");
                _uia.InvokeExact(confirms[0]);
                return;
            }
            if (HasStopProof()) return;
            if (windows.Any(w => w.IsModal)) throw new InvalidOperationException("出现了无法识别的雷神模态窗口，拒绝确认");
            await Task.Delay(150, ct);
        }
        if (!HasStopProof()) throw new TimeoutException("等待停止确认窗口或日志停止证明超时");
    }
    private async Task<bool> WaitForStopProofAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested(); EnsureClient();
            if (HasStopProof()) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }
    private bool HasStopProof()
    {
        var snap = _readSession();
        return snap.Knowledge == SessionKnowledge.NotAccelerating && snap.StopStateConfirmed && snap.StoppedGameIds.SetEquals(_expectedGameIds);
    }
}

public sealed record OwnedProcessIdentity(int Pid, string Name, string Path, DateTime StartTime, string UserSid, int SessionId);

public static class ProcessOwnership
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    public static OwnedProcessIdentity Read(int pid)
    {
        using var p = Process.GetProcessById(pid);
        var path = LeiGodDiscovery.SafePath(p) ?? throw new InvalidOperationException("LeiGod 进程路径不可读");
        var userSid = ReadUserSid(pid) ?? throw new InvalidOperationException("LeiGod 进程用户不可读");
        return new(p.Id, p.ProcessName, Path.GetFullPath(path), p.StartTime, userSid, p.SessionId);
    }
    public static int ParentPid(int pid)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new InvalidOperationException("无法读取进程父 PID");
        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        if (!Process32First(snapshot, ref entry)) throw new InvalidOperationException("无法读取进程父 PID 清单");
        do { if (entry.th32ProcessID == (uint)pid) return checked((int)entry.th32ParentProcessID); } while (Process32Next(snapshot, ref entry));
        throw new InvalidOperationException("目标进程已退出，无法读取父 PID");
    }
    public static bool SdkParentTopologyMatches(int sdkPid, int sdkParentPid, DateTime sdkStart, int clientPid, int clientParentPid, DateTime clientStart)
    {
        if (sdkParentPid == clientPid) return true;
        var delta = (sdkStart - clientStart).TotalSeconds;
        return clientParentPid > 0 && sdkParentPid == clientParentPid && delta >= -2 && delta <= 10;
    }
    public static int ListenerPid(int port)
    {
        using var process = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p tcp") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
        if (process is null) return -1;
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(1500)) { try { process.Kill(); } catch { } return -1; }
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = Regex.Split(line.Trim(), @"\s+");
            if (columns.Length < 5 || !columns[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) || !columns[^2].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
            var local = columns[1]; var colon = local.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(local[(colon + 1)..], out var localPort) && localPort == port && int.TryParse(columns[^1], out var pid)) return pid;
        }
        return -1;
    }
    public static bool IsCurrentSdkForClient(OwnedProcessIdentity sdk, int clientPid, DateTime clientStart, string installRoot, int listenerPid)
    {
        try
        {
            // Pausing may close the SDK listener before the process itself
            // exits. A live listener must still match this PID; with no
            // listener, the caller additionally requires a unique SDK and
            // we rely on the same client topology, path, SID and session.
            if ((listenerPid > 0 && sdk.Pid != listenerPid) || !sdk.Name.Equals("leishenSdk", StringComparison.OrdinalIgnoreCase)) return false;
            var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(sdk.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase) || sdk.UserSid != CurrentUserSid || sdk.SessionId != CurrentSessionId) return false;
            var client = Read(clientPid);
            if (!client.Name.Equals("leigod", StringComparison.OrdinalIgnoreCase) || client.StartTime != clientStart || !client.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || client.UserSid != sdk.UserSid || client.SessionId != sdk.SessionId) return false;
            return SdkParentTopologyMatches(sdk.Pid, ParentPid(sdk.Pid), sdk.StartTime, clientPid, ParentPid(clientPid), clientStart);
        }
        catch { return false; }
    }
    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("当前用户 SID 不可读");
    public static int CurrentSessionId => Process.GetCurrentProcess().SessionId;
    public static bool Same(OwnedProcessIdentity a, OwnedProcessIdentity b) => a.Pid == b.Pid && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase) && a.StartTime == b.StartTime && a.UserSid == b.UserSid && a.SessionId == b.SessionId;
    private static string? ReadUserSid(int pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid); if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var size);
                if (size == 0) return null;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetTokenInformation(token, TokenUser, buffer, size, out _)) return null;
                    var sid = Marshal.ReadIntPtr(buffer);
                    if (!ConvertSidToStringSid(sid, out var text)) return null;
                    try { return Marshal.PtrToStringUni(text); } finally { LocalFree(text); }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool Process32First(SafeSnapshotHandle snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool Process32Next(SafeSnapshotHandle snapshot, ref PROCESSENTRY32 entry);
    private sealed class SafeSnapshotHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeSnapshotHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseSnapshotHandle(handle);
    }
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)] private static extern bool CloseSnapshotHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint infoLength, out uint returnLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>
/// Fallback for LeiGod builds whose main window is a self-drawn surface and
/// exposes no UI Automation stop control. It never invents a stop action:
/// pause must independently produce the same-client stop-acc/normal proof.
/// The workflow then delegates the already-gated owned-process exit.
/// </summary>
public sealed class PauseInducedStopAdapter : IStopAdapter
{
    private readonly Func<bool> _stopProof;
    private readonly ILeiGodVerifiedExitController _exit;
    public PauseInducedStopAdapter(Func<bool> stopProof, ILeiGodVerifiedExitController exit)
    {
        _stopProof = stopProof;
        _exit = exit;
    }

    public string Id => "leigod-pause-induced-stop";
    public int Version => 1;
    public bool CanStopAcceleration => true;
    public async Task RequestStopAccelerationAsync(CancellationToken ct)
    {
        // Some 11.x clients stop asynchronously after the account pause is
        // accepted. Wait for the same-client normal/stop proof instead of
        // clicking an unverified self-drawn control or declaring failure at
        // the 12-second pause-proof checkpoint.
        // Leave a short opportunity for an immediate log proof, then let the
        // outer workflow use its strictly gated paused-session tree exit.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (_stopProof()) return;
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("暂停后 5 秒内没有出现独立停止证明；交由暂停会话退出回退路径复核");
    }
    public Task<bool> IsAccelerationStoppedAsync(CancellationToken ct) => Task.FromResult(_stopProof());
    public Task RequestNormalExitAsync(CancellationToken ct) => _exit.RequestVerifiedExitAsync(ct);
    public Task RequestPausedSessionExitFallbackAsync(CancellationToken ct) => _exit.RequestPausedSessionTreeExitAsync(ct);
    public Task<bool> AreOwnedClientProcessesGoneAsync(CancellationToken ct) => _exit.AreOwnedProcessesGoneAsync(ct);
}

public sealed class OwnedProcessExitAdapter : ILeiGodVerifiedExitController
{
    private readonly string _root; private readonly int _mainPid; private readonly DateTime _mainStart;
    private readonly Func<bool> _stopProof; private readonly Func<bool> _sameSessionNoGame; private readonly Func<bool> _originalSessionNoGame; private readonly Func<Task<bool>> _pauseConfirmed;
    public OwnedProcessExitAdapter(string root, int mainPid, DateTime mainStart, Func<bool> stopProof, Func<bool> sameSessionNoGame, Func<Task<bool>> pauseConfirmed, Func<bool>? originalSessionNoGame = null)
    { _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; _mainPid = mainPid; _mainStart = mainStart; _stopProof = stopProof; _sameSessionNoGame = sameSessionNoGame; _originalSessionNoGame = originalSessionNoGame ?? sameSessionNoGame; _pauseConfirmed = pauseConfirmed; }

    public async Task RequestVerifiedExitAsync(CancellationToken ct)
        => await RequestExitAsync(ct, false);

    public async Task RequestPausedSessionTreeExitAsync(CancellationToken ct)
        => await RequestExitAsync(ct, true);

    private async Task RequestExitAsync(CancellationToken ct, bool pausedFallback)
    {
        if (!await ChecksAsync(pausedFallback)) throw new InvalidOperationException("退出前停止证明/原会话/游戏或暂停状态复核失败");
        var snapshot = CaptureOwned();
        if (snapshot.Count == 0 || snapshot.Count(x => x.Pid == _mainPid && x.Name.Equals("leigod", StringComparison.OrdinalIgnoreCase) && x.StartTime == _mainStart) != 1) throw new InvalidOperationException("当前安装主客户端快照缺失或不唯一");
        var snapshotPids = snapshot.Select(x => x.Pid).ToHashSet();
        var listenerPids = new[] { ProcessOwnership.ListenerPid(10908), ProcessOwnership.ListenerPid(5000) }
            .Where(pid => pid > 0).Distinct().ToArray();
        if (listenerPids.Length > 1) throw new InvalidOperationException("雷神 SDK 监听端口归属冲突，拒绝退出");
        var listenerPid = listenerPids.Length == 1 ? listenerPids[0] : -1;
        if (listenerPid <= 0 && snapshot.Count(x => x.Name.Equals("leishenSdk", StringComparison.OrdinalIgnoreCase)) > 1)
            throw new InvalidOperationException("SDK 本机监听消失且存在多个候选引擎，拒绝退出");
        var roots = snapshot.Where(x => x.Name.Equals("leigod", StringComparison.OrdinalIgnoreCase) && x.Path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) && x.UserSid == ProcessOwnership.CurrentUserSid && x.SessionId == ProcessOwnership.CurrentSessionId && !IsDescendantOfMain(x.Pid, snapshotPids)).ToArray();
        if (roots.Length != 1 || roots[0].Pid != _mainPid) throw new InvalidOperationException("发现额外主客户端根实例，拒绝整体退出");
        foreach (var candidate in snapshot.Where(x => x.Pid != _mainPid))
            if (!IsDescendantOfMain(candidate.Pid, snapshotPids) && !(candidate.Name.Equals("leishenSdk", StringComparison.OrdinalIgnoreCase) && ProcessOwnership.IsCurrentSdkForClient(candidate, _mainPid, _mainStart, _root, listenerPid)))
                throw new InvalidOperationException("发现无法证明属于本轮主客户端的雷神进程，拒绝退出");
        // The live client respawns its SDK when that SDK is ended first.
        // End the already verified main client first so its shutdown cannot
        // keep creating fresh children during the bounded exit sequence.
        var mainGone = false;
        var targets = snapshot.OrderBy(x => x.Pid == _mainPid ? 0 : 1).ToList();
        for (var qi = 0; qi < targets.Count; qi++)
        {
            var target = targets[qi];
            ct.ThrowIfCancellationRequested();
            if (!(mainGone ? await _pauseConfirmed() : await ChecksAsync(pausedFallback))) throw new InvalidOperationException("进程退出过程中游戏、暂停或安全证明发生变化");
            var now = CaptureOwned();
            foreach (var observed in now)
            {
                var known = targets.FirstOrDefault(t => t.Pid == observed.Pid);
                if (known is not null && !ProcessOwnership.Same(known, observed))
                    throw new InvalidOperationException($"雷神进程身份在退出过程中变化 PID={observed.Pid} name={observed.Name}");
            }
            // A shutting-down Electron client can still spawn children of its own main process.
            // Adopt those into the exit set only when the parent chain proves they belong to this
            // run's main client; a second client instance, a foreign process or a reused PID keeps
            // failing closed and aborts the exit instead of being killed.
            var knownPids = new HashSet<int>(targets.Select(t => t.Pid));
            var pending = now.Where(o => !knownPids.Contains(o.Pid)).ToList();
            var adopted = true;
            while (pending.Count > 0 && adopted)
            {
                adopted = false;
                foreach (var observed in pending.ToArray())
                {
                    if (!IsDescendantOfMain(observed.Pid, knownPids)) continue;
                    SafeLog.Write($"exit snapshot adopted a new owned process pid={observed.Pid} name={observed.Name}");
                    targets.Add(observed); knownPids.Add(observed.Pid);
                    pending.Remove(observed); adopted = true;
                }
            }
            if (pending.Count > 0)
                throw new InvalidOperationException("发现快照之外的新雷神进程 " + string.Join(", ", pending.Select(x => $"PID={x.Pid} name={x.Name}")) + "；停止退出");
            var same = now.SingleOrDefault(x => x.Pid == target.Pid);
            if (same is null) { if (target.Pid == _mainPid) mainGone = true; continue; }
            if (!ProcessOwnership.Same(target, same) || same.UserSid != ProcessOwnership.CurrentUserSid || same.SessionId != ProcessOwnership.CurrentSessionId) throw new InvalidOperationException("雷神进程 PID/start/path/user/session 与退出快照不符");
            using var p = Process.GetProcessById(target.Pid);
            var recheck = ProcessOwnership.Read(target.Pid);
            if (!ProcessOwnership.Same(target, recheck) || !(mainGone ? await _pauseConfirmed() : await ChecksAsync(pausedFallback))) throw new InvalidOperationException("进程身份或安全条件在终止前变化");
            p.Kill();
            await p.WaitForExitAsync(ct);
            if (target.Pid == _mainPid) mainGone = true;
        }
        if (CaptureAllLeiGod().Count != 0) throw new InvalidOperationException("雷神客户端/引擎仍有进程或无法完成退出盘点");
    }
    public Task<bool> AreOwnedProcessesGoneAsync(CancellationToken ct) => Task.FromResult(CaptureAllLeiGod().Count == 0);

    private async Task<bool> ChecksAsync(bool pausedFallback = false) => (pausedFallback ? _originalSessionNoGame() : _stopProof() && _sameSessionNoGame()) && await _pauseConfirmed();
    private bool IsDescendantOfMain(int pid, HashSet<int> snapshotPids)
    {
        var seen = new HashSet<int>();
        var current = pid;
        while (seen.Add(current))
        {
            int parent;
            try { parent = ProcessOwnership.ParentPid(current); }
            catch { return false; }
            if (parent == _mainPid) return true;
            if (!snapshotPids.Contains(parent)) return false;
            current = parent;
        }
        return false;
    }
    private List<OwnedProcessIdentity> CaptureOwned()
    {
        var all = CaptureAllLeiGod();
        if (all.Any(p => !p.Path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("存在安装目录之外的 LeiGod 进程，拒绝整体结束");
        if (all.Any(p => p.UserSid != ProcessOwnership.CurrentUserSid || p.SessionId != ProcessOwnership.CurrentSessionId)) throw new InvalidOperationException("存在其他用户或 Windows Session 的 LeiGod 进程");
        return all;
    }
    private static List<OwnedProcessIdentity> CaptureAllLeiGod()
    {
        // The SDK can restart immediately after the stop command.  A process
        // returned by GetProcesses() may therefore disappear before its
        // identity is read. Retry the whole snapshot for that transient race;
        // an existing process whose identity is unreadable remains fatal.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Process[] all;
            try { all = Process.GetProcesses(); } catch (Exception ex) { throw new InvalidOperationException("系统拒绝读取完整进程清单", ex); }
            var result = new List<OwnedProcessIdentity>();
            var transient = false;
            foreach (var p in all)
            {
                try
                {
                    var name = p.ProcessName;
                    if (!name.Equals("leigod", StringComparison.OrdinalIgnoreCase) && !name.Equals("leishenSdk", StringComparison.OrdinalIgnoreCase)) continue;
                    try { result.Add(ProcessOwnership.Read(p.Id)); }
                    catch (ArgumentException) when (!ProcessStillExists(p.Id)) { transient = true; SafeLog.Write($"exit snapshot vanished pid={p.Id}"); }
                    catch (InvalidOperationException) when (!ProcessStillExists(p.Id)) { transient = true; SafeLog.Write($"exit snapshot vanished pid={p.Id}"); }
                    catch (System.ComponentModel.Win32Exception) when (!ProcessStillExists(p.Id)) { transient = true; SafeLog.Write($"exit snapshot vanished pid={p.Id}"); }
                    catch (Exception ex) { throw new InvalidOperationException($"LeiGod 进程候选 PID {p.Id} 的 PID/start/path/user/session 读取失败", ex); }
                }
                catch (InvalidOperationException) when (!ProcessStillExists(p.Id)) { transient = true; SafeLog.Write($"exit snapshot vanished pid={p.Id}"); }
                finally { p.Dispose(); }
            }
            if (!transient) return result;
            // A disappearing SDK is absent from the current process table.
            // Accept the identity snapshot only if every *currently live*
            // LeiGod process was read; an SDK born during enumeration forces
            // another pass and the exit loop also rejects new identities.
            var current = Process.GetProcessesByName("leigod").Concat(Process.GetProcessesByName("leishenSdk")).ToArray();
            try
            {
                if (current.All(p => result.Any(x => x.Pid == p.Id)) &&
                    result.All(x => current.Any(p => p.Id == x.Pid))) return result;
            }
            finally { foreach (var p in current) p.Dispose(); }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("雷神进程清单在 SDK 重启期间持续变化，无法建立稳定退出快照");
    }

    private static bool ProcessStillExists(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    public static bool StillMatchesValidatedSnapshot(LeiGodProcess expected, int currentPid, string currentName, DateTime currentStartTime, string? currentPath, string installRoot)
    {
        try
        {
            var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return expected.Path is not null && Path.GetFullPath(expected.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase) && currentPid == expected.Pid && currentName.Equals(expected.Name, StringComparison.OrdinalIgnoreCase) && currentStartTime == expected.StartTime && currentPath is not null && Path.GetFullPath(currentPath).Equals(Path.GetFullPath(expected.Path), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
