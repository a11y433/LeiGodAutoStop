namespace LeiGodAutoStop;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--preview")
        {
            ApplicationConfiguration.Initialize();
            using var preview = new PauseOnlyForm(preview: true);
            preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        if (args.Contains("--diagnostics"))
        {
            ApplicationConfiguration.Initialize();
            var report = new FirstUseDiagnosticRunner().RunAsync().GetAwaiter().GetResult();
            MessageBox.Show(report.ToDisplay(), "雷神自动暂停时长 · 环境检查", MessageBoxButtons.OK, report.Checks.Any(c => c.Severity == DiagnosticSeverity.Fail) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            return;
        }
        if (args.Contains("--setup-and-start")) { FollowLeiGod.SetupAndStartFromCommand(); return; }
        if (args.Contains("--enable-follow")) { FollowLeiGod.InstallFromCommand(); return; }
        var watch = args.Contains("--watch-leigod");
        SafeLog.Write("program startup mode=" + (watch ? "watcher" : "monitor") + "; session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        using var mutex = new Mutex(true, watch ? "Local\\LeiGodAutoStop.FollowWatcher.v1" : FollowLeiGod.MonitorMutex, out var created);
        SafeLog.Write("program singleton created=" + created + "; watcher=" + watch);
        if (!created) { if (!args.Contains("--background") && !watch) MessageBox.Show("雷神自动停时长已在运行。", "雷神自动停时长"); return; }
        if (watch) { ApplicationConfiguration.Initialize(); Application.Run(new FollowWatcher()); return; }
        ApplicationConfiguration.Initialize(); Application.Run(new PauseOnlyForm());
    }
}

