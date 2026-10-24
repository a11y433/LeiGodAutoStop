using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace LeiGodAutoStop;

public sealed record PauseOnlyOwnedProcessIdentity(int Pid, string Name, string Path, DateTime StartTime, string UserSid, int SessionId);

public static class PauseOnlyProcessOwnership
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    public static PauseOnlyOwnedProcessIdentity Read(int pid)
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
    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("当前用户 SID 不可读");
    public static int CurrentSessionId => Process.GetCurrentProcess().SessionId;
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


