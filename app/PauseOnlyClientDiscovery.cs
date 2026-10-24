using System.Diagnostics;

namespace LeiGodAutoStop;

// Discovers the one client that owns the current log epoch. No legacy window or stop workflow is required.
public static class PauseOnlyClientDiscovery
{
    public static bool TryFindRootClient(out LeiGodProcess? client, out string reason)
    {
        client = null;
        Process[] processes;
        try { processes = Process.GetProcessesByName("leigod"); }
        catch { reason = "雷神进程列表不可读"; return false; }
        if (processes.Length == 0) { reason = "雷神主客户端未运行"; return false; }

        var candidates = new List<(LeiGodProcess Client, bool HasWindow)>();
        var incomplete = false;
        foreach (var process in processes)
        {
            try
            {
                var path = LeiGodDiscovery.SafePath(process);
                if (path is null) { incomplete = true; continue; }
                candidates.Add((new LeiGodProcess(process.Id, process.ProcessName, path, process.StartTime), process.MainWindowHandle != IntPtr.Zero));
            }
            catch { incomplete = true; }
            finally { process.Dispose(); }
        }
        if (incomplete || candidates.Count == 0) { reason = "雷神主窗口或进程身份读取失败"; return false; }

        var windows = candidates.Where(c => c.HasWindow).ToArray();
        if (windows.Length == 1)
        {
            client = windows[0].Client;
            reason = "已按唯一主窗口识别雷神客户端";
            return true;
        }
        if (windows.Length > 1) { reason = "发现多个雷神主窗口，无法安全选择主客户端"; return false; }

        var parentByPid = new Dictionary<int, int>();
        try { foreach (var candidate in candidates) parentByPid[candidate.Client.Pid] = PauseOnlyProcessOwnership.ParentPid(candidate.Client.Pid); }
        catch { reason = "托盘客户端父进程不可验证"; return false; }
        var roots = candidates.Select(c => Path.GetDirectoryName(c.Client.Path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parents = candidates.Where(parent => candidates.Any(child =>
            child.Client.Pid != parent.Client.Pid && parentByPid[child.Client.Pid] == parent.Client.Pid &&
            string.Equals(Path.GetDirectoryName(child.Client.Path), Path.GetDirectoryName(parent.Client.Path), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Client.StartTime).ToArray();
        if (roots.Length == 1 && parents.Length == 1)
        {
            client = parents[0].Client;
            reason = "按同一安装目录内的 Electron 父进程识别托盘客户端";
            return true;
        }
        reason = "没有可唯一识别的雷神主窗口或 Electron 父进程";
        return false;
    }

    /// <summary>
    /// PIDs of running LeiGod processes installed next to the given client executable. The client logs its own
    /// HTTP calls from more than one of these processes, so a line may belong to the instance without using
    /// the root PID. Identity stays bounded to the same install directory.
    /// </summary>
    public static IReadOnlyCollection<int> SiblingPids(string? clientPath)
    {
        if (string.IsNullOrWhiteSpace(clientPath)) return [];
        string directory;
        try { directory = Path.GetDirectoryName(Path.GetFullPath(clientPath)) ?? ""; }
        catch { return []; }
        if (directory.Length == 0) return [];
        var result = new List<int>();
        Process[] processes;
        try { processes = Process.GetProcessesByName("leigod"); }
        catch { return []; }
        foreach (var process in processes)
        {
            try
            {
                var path = LeiGodDiscovery.SafePath(process);
                if (path is null || !string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(process.Id);
            }
            catch { }
            finally { process.Dispose(); }
        }
        return result;
    }
}
