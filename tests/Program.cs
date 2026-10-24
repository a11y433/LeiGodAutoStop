using LeiGodAutoStop;
if (args.Length > 1 && args[0] == "--account-state")
{
    using var client = System.Diagnostics.Process.GetProcessById(int.Parse(args[1]));
    var state = await new ClientLogPauseAdapter(@"E:\LeiGod_Acc\resources\llogs", client.Id, client.StartTime).ReadPauseStateAsync(CancellationToken.None);
    Console.WriteLine($"paused={state.IsPaused} remaining={state.RemainingSeconds?.ToString() ?? "unavailable"} observed_utc={state.ObservedAt:O}");
    return 0;
}
if (args.Length > 1 && args[0] == "--session")
{
    using var client = System.Diagnostics.Process.GetProcessById(int.Parse(args[1]));
    var snapshot = new SessionReader().Read(@"E:\LeiGod_Acc\resources\llogs", client.Id, client.StartTime);
    Console.WriteLine($"knowledge={snapshot.Knowledge} reason={snapshot.Reason} session={snapshot.Session?.GameId} stop={snapshot.StopStateConfirmed}");
    return 0;
}
if (args.Length > 3 && args[0] == "--pause-shape")
{
    var clientStart = DateTime.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal);
    var shapeAdapter = new ClientLogPauseAdapter(args[1], int.Parse(args[2]), clientStart);
    var shape = shapeAdapter.Inspect();
    Console.WriteLine($"files={shape.FilesScanned} client_lines={shape.ClientLines} pause_lines={shape.PausePathLines} status_lines={shape.StatusRequestLines} usable={shape.UsableBodyLines} can_control={shapeAdapter.CanControl("")}");
    Console.WriteLine("fingerprint: " + shape.Fingerprint);
    return 0;
}
if (args.Length > 0 && args[0] == "--report")
{
    var diag = await new FirstUseDiagnosticRunner().RunAsync(CancellationToken.None);
    Console.WriteLine("summary: " + diag.Summary);
    Console.WriteLine("can_monitor=" + diag.CanMonitor);
    foreach (var check in diag.Checks) Console.WriteLine($"[{check.Prefix}] {check.Title}: {check.Detail}");
    Console.WriteLine("marker: " + FirstUseDiagnosticRunner.MarkerPath);
    Console.WriteLine("fingerprint_path: " + FirstUseDiagnosticRunner.FingerprintPath);
    return 0;
}
if (args.Length > 1 && args[0] == "--sdk-probe")
{
    using var client = System.Diagnostics.Process.GetProcessById(int.Parse(args[1]));
    var path = LeiGodDiscovery.SafePath(client) ?? throw new InvalidOperationException("client path unknown");
    var root = Path.GetDirectoryName(path)!;
    var logs = LeiGodDiscovery.FindLogDirectory(root);
    bool SameUnknownClient()
    {
        using var now = System.Diagnostics.Process.GetProcessById(client.Id);
        return now.StartTime == client.StartTime &&
            string.Equals(LeiGodDiscovery.SafePath(now), path, StringComparison.OrdinalIgnoreCase) &&
            new SessionReader().Read(logs, client.Id, client.StartTime).Knowledge == SessionKnowledge.Unknown;
    }
    var dummy = new OwnedProcessExitAdapter(root, client.Id, client.StartTime, () => false, () => false, () => Task.FromResult(false));
    var probe = new ClientSdkSocketStopAdapter(root, client.Id, client.StartTime, SameUnknownClient, () => false, () => false, () => Task.FromResult(false), dummy);
    var result = $"same={SameUnknownClient()} available={probe.CanStopAcceleration} off={await probe.ProbeNoAccelerationAsync(CancellationToken.None)}";
    Console.WriteLine(result);
    if (args.Length > 2) File.WriteAllText(args[2], result);
    return 0;
}
if (args.Length > 0 && args[0] == "--probe-legacy-task")
{
    var method = typeof(StatusForm).GetMethod("ReadLegacyTaskState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    if (method is null) { Console.Error.WriteLine("Install gate probe method not found."); return 2; }
    var state = method.Invoke(null, null)?.ToString();
    Console.WriteLine("App install-gate legacy task result: " + state);
    return state == "Disabled" ? 0 : 1;
}
await OfflineScenarios.RunAsync();
return 0;
