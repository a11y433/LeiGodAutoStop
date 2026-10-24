using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace LeiGodAutoStop;

public sealed class FollowExitGate
{
    private int _missing;
    public bool Tick(bool? clientPresent)
    {
        // Enumeration failures must never be treated as a confirmed exit.
        if (clientPresent != false) { _missing = 0; return false; }
        return ++_missing >= 3;
    }
}

public static class FollowLeiGod
{
    public const string MonitorMutex = "Local\\LeiGodAutoStop.Singleton.v1";
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeiGodAutoStop");
    public const string StopEvent = "Local\\LeiGodAutoStop.FollowStop.v1";
    public static string ResultFile => Path.Combine(Folder, "follow-install-result.json");
    private static string TaskName => "LeiGodAutoStopFollow-" + WindowsIdentity.GetCurrent().User!.Value;
    public static bool Enabled
    {
        get
        {
            try
            {
                dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                scheduler.Connect();
                return (bool)scheduler.GetFolder("\\").GetTask(TaskName).Enabled;
            }
            catch { return false; }
        }
    }

    public static bool IsAdministrator
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    public static string? ConfiguredExecutable
    {
        get
        {
            try
            {
                dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                scheduler.Connect();
                dynamic task = scheduler.GetFolder("\\").GetTask(TaskName);
                return (string)task.Definition.Actions.Item(1).Path;
            }
            catch { return null; }
        }
    }

    public static bool PointsToCurrentExecutable => Enabled && ConfiguredExecutable is { } path &&
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);

    private static void SignalWatcherExit()
    {
        if (EventWaitHandle.TryOpenExisting(StopEvent, out var signal))
        { using (signal) signal!.Set(); }
    }

    public static bool? ClientPresent()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName("leigod");
            try { return processes.Any(p => p.SessionId == self.SessionId); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return null; }
    }

    public static void Install()
    {
        var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("请以管理员身份启动本应用后启用随雷神启动。");
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "当前用户打开雷神时启动自动暂停监控；雷神完全退出后监控退出。";
        task.Principal.UserId = identity.User!.Value;
        task.Principal.LogonType = 3; // InteractiveToken; no stored credentials.
        task.Principal.RunLevel = 1;
        task.Settings.Enabled = true;
        task.Settings.StartWhenAvailable = true;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.MultipleInstances = 2;
        dynamic trigger = task.Triggers.Create(9); // Current-user logon.
        trigger.UserId = identity.User.Value;
        dynamic action = task.Actions.Create(0);
        action.Path = Application.ExecutablePath;
        action.Arguments = "--watch-leigod";
        action.WorkingDirectory = AppContext.BaseDirectory;
        dynamic folder = scheduler.GetFolder("\\");
        dynamic registered = folder.RegisterTaskDefinition(TaskName, task, 6, identity.User.Value, null, 3, null);
        Directory.CreateDirectory(Folder);
        // Replace this application's old unconditional logon entry.
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        run?.DeleteValue("LeiGodAutoStop", false);
        SignalWatcherExit();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!Mutex.TryOpenExisting("Local\\LeiGodAutoStop.FollowWatcher.v1", out var old)) break;
            old!.Dispose(); Thread.Sleep(200);
        }
        for (var attempt = 0; attempt < 20 && (int)registered.State == 4; attempt++) Thread.Sleep(100);
        registered.Run(null);
        SafeLog.Write("follow mode installed; task=" + TaskName);
    }

    public static void Remove()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        try { folder.DeleteTask(TaskName, 0); }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
        SignalWatcherExit();
        SafeLog.Write("follow mode disabled");
    }

    public static void InstallFromCommand()
    {
        Directory.CreateDirectory(Folder);
        try
        {
            Install();
            File.WriteAllText(ResultFile, JsonSerializer.Serialize(new { Success = true, Executable = Application.ExecutablePath, Time = DateTimeOffset.Now }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(ResultFile, JsonSerializer.Serialize(new { Success = false, Error = SafeLog.Redact(ex.Message), Time = DateTimeOffset.Now }));
            Environment.ExitCode = 1;
        }
    }

    public static void SetupAndStartFromCommand()
    {
        Directory.CreateDirectory(Folder);
        try
        {
            if (!IsAdministrator) throw new InvalidOperationException("管理员权限没有生效；请在 Windows 权限提示中选择“是”。");
            if (!PackageIntegrity.Verify(out var packageResult)) throw new InvalidOperationException(packageResult);
            var report = new FirstUseDiagnosticRunner().RunAsync().GetAwaiter().GetResult();
            if (!report.CanMonitor) throw new InvalidOperationException("雷神环境检查未通过：\n" + report.ToDisplay());
            Install();
            File.WriteAllText(ResultFile, JsonSerializer.Serialize(new
            {
                Success = true,
                Executable = Application.ExecutablePath,
                Time = DateTimeOffset.Now,
                Message = packageResult + "\n" + report.Summary + "\n已启用随雷神启动并开始监控。"
            }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(ResultFile, JsonSerializer.Serialize(new
            {
                Success = false,
                Executable = Application.ExecutablePath,
                Time = DateTimeOffset.Now,
                Message = SafeLog.Redact(ex.Message)
            }));
            Environment.ExitCode = 1;
        }
    }
}

internal sealed class FollowWatcher : ApplicationContext
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset, FollowLeiGod.StopEvent);
    private DateTime _nextConfigurationCheck;
    private DateTime _retryAfter;

    public FollowWatcher()
    {
        SafeLog.Write("follow watcher started; executable=" + Application.ExecutablePath);
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        if (_stop.WaitOne(0)) { ExitThread(); return; }
        if (DateTime.UtcNow >= _nextConfigurationCheck)
        {
            if (!FollowLeiGod.Enabled) { ExitThread(); return; }
            _nextConfigurationCheck = DateTime.UtcNow.AddSeconds(30);
        }
        var present = FollowLeiGod.ClientPresent();
        if (present is null) return;
        if (present == false || DateTime.UtcNow < _retryAfter) return;
        try
        {
            if (Mutex.TryOpenExisting(FollowLeiGod.MonitorMutex, out var running))
            {
                running!.Dispose();
                return;
            }
            using var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--follow-leigod --background") { UseShellExecute = false, CreateNoWindow = true });
            if (child is null) throw new InvalidOperationException("Monitor process was not created.");
            // Wait for its mutex on the next poll, allowing startup failures to retry.
            _retryAfter = DateTime.UtcNow.AddSeconds(10);
            SafeLog.Write("follow watcher launched monitor pid=" + child.Id);
        }
        catch (Exception ex)
        {
            _retryAfter = DateTime.UtcNow.AddSeconds(30);
            SafeLog.Write("follow watcher launch failed: " + ex.GetType().Name);
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop(); _timer.Dispose(); _stop.Dispose();
        SafeLog.Write("follow watcher exited");
        base.ExitThreadCore();
    }
}
