using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace LeiGodAutoStop;
internal static class OfflineScenarios
{
    public static async Task RunAsync()
    {
        var t = Stopwatch.GetTimestamp(); var monitor = new IdleMonitor();
        var session = new SessionSnapshot(SessionKnowledge.Accelerating, new("DeadByDaylight", "Dead by Daylight", 12, DateTime.UtcNow, DateTime.UtcNow, true), "fixture");
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Scenario failed: " + name); Console.WriteLine("PASS " + name); }
        var followExit = new FollowExitGate();
        var capabilityChecks = new[] { "logs", "format", "account", "pause-control" }.Select(key => new DiagnosticCheck(key, key, DiagnosticSeverity.Pass, "fixture")).ToArray();
        Check(new DiagnosticReport(DateTimeOffset.Now, capabilityChecks.Append(new("version", "version", DiagnosticSeverity.Warning, "unknown version")).ToArray()).CanMonitor, "unreadable version does not block verified capabilities");
        Check(!new DiagnosticReport(DateTimeOffset.Now, capabilityChecks.Where(c => c.Key != "account").ToArray()).CanMonitor, "missing account check blocks automatic pause");
        Check(!new DiagnosticReport(DateTimeOffset.Now, capabilityChecks.Select(c => c.Key == "pause-control" ? c with { Severity = DiagnosticSeverity.Fail } : c).ToArray()).CanMonitor, "missing pause parameters block automatic pause");
        var waitingChecks = capabilityChecks.Select(c => c.Key == "pause-control" ? c with { Severity = DiagnosticSeverity.Warning, Detail = "waiting for the first pause request" } : c).ToArray();
        var waiting = new DiagnosticReport(DateTimeOffset.Now, waitingChecks);
        Check(!waiting.CanMonitor && !waiting.HasBlockingFailure && waiting.Summary.Contains("复查"), "waiting for the first pause request blocks automatic pause without reporting a blocking failure");
        Check(!followExit.Tick(false) && !followExit.Tick(false) && followExit.Tick(false), "follow mode waits for three confirmed absent samples before exiting");
        Check(!followExit.Tick(true) && !followExit.Tick(false) && !followExit.Tick(null) && !followExit.Tick(false) && !followExit.Tick(false), "client restart or unreadable process list resets the exit grace period");
        using (var sdkNotification = System.Text.Json.JsonDocument.Parse("{\"code\":200,\"method\":100000,\"data\":{\"is_onacc\":0}}"))
        {
            Check(!ClientSdkSocketStopAdapter.HasExpectedSdkCorrelation(sdkNotification.RootElement, 100009, 1), "SDK push notification is not mistaken for query_nn_acc response");
            Check(!PauseOnlySdkReader.MatchesReply(sdkNotification.RootElement), "pause-only SDK reader rejects a push notification");
        }
        using (var sdkReply = System.Text.Json.JsonDocument.Parse("{\"code\":200,\"method\":100009,\"id\":1,\"data\":{\"is_onacc\":0}}"))
        {
            Check(ClientSdkSocketStopAdapter.HasExpectedSdkCorrelation(sdkReply.RootElement, 100009, 1), "query_nn_acc response is matched by method and request id");
            Check(PauseOnlySdkReader.MatchesReply(sdkReply.RootElement), "pause-only SDK reader accepts its matching query response");
        }
        var sdkFrames = new Queue<string>([
            "{\"code\":200,\"method\":100000,\"data\":{\"is_onacc\":0}}",
            "{\"code\":200,\"method\":100009,\"id\":1,\"data\":{\"is_onacc\":0}}"
        ]);
        var matchedSdkFrame = await ClientSdkSocketStopAdapter.ReceiveMatchingSdkResponseAsync(_ => Task.FromResult(sdkFrames.Dequeue()), 100009, 1, 5000, CancellationToken.None);
        using (var matchedSdkReply = System.Text.Json.JsonDocument.Parse(matchedSdkFrame))
            Check(ClientSdkSocketStopAdapter.HasExpectedSdkCorrelation(matchedSdkReply.RootElement, 100009, 1) && sdkFrames.Count == 0, "SDK push notification is skipped and the matching query response is consumed");
        TestPauseOnlyMonitor(Check);
        var originalSession = session.Session!;
        var originalSet = new[] { originalSession };
        Check(StopSessionIdentity.MatchesOriginal(originalSet, session with { ActiveSessions = originalSet }), "SDK stop readback remains bound to original active game epoch");
        var replacementEpoch = originalSession with { EventTime = originalSession.EventTime.AddSeconds(1) };
        Check(!StopSessionIdentity.MatchesOriginal(originalSet, session with { ActiveSessions = [replacementEpoch] }), "SDK stop readback cannot authorize a replacement session epoch");
        Check(!StopSessionIdentity.MatchesOriginal(originalSet, session with { ActiveSessions = [originalSession, replacementEpoch] }), "SDK stop readback rejects a newly added active session");
        var topologyStart = DateTime.UtcNow;
        Check(ProcessOwnership.SdkParentTopologyMatches(201, 77, topologyStart, 100, 77, topologyStart), "SDK sibling topology accepts same live launcher and near-simultaneous starts");
        Check(ProcessOwnership.SdkParentTopologyMatches(201, 100, topologyStart.AddMinutes(8), 100, 77, topologyStart), "SDK restart topology accepts direct child of the same main PID");
        Check(!ProcessOwnership.SdkParentTopologyMatches(201, 88, topologyStart, 100, 77, topologyStart), "SDK ownership rejects unrelated parent process");
        Check(!ProcessOwnership.SdkParentTopologyMatches(201, 77, topologyStart.AddSeconds(11), 100, 77, topologyStart), "SDK sibling topology rejects a late unrelated launch");
        var stoppedAnchor = DateTime.Now;
        var stoppedProof = new SessionSnapshot(SessionKnowledge.NotAccelerating, new("", "", 12, stoppedAnchor, stoppedAnchor, false), "paired stop proof")
        { StopStateConfirmed = true, StoppedGameIds = new HashSet<string>(["231"]) };
        var stoppedIdle = new StoppedSessionIdleMonitor();
        var unrelatedRunningGame = new GameSnapshot(true, [new(812, "Apex.exe", @"C:\Games\Apex.exe", stoppedAnchor, "unrelated unaccelerated game")], "other game still running");
        Check(!stoppedIdle.Tick(stoppedProof, unrelatedRunningGame, 12, stoppedAnchor, t), "paired stopped session starts a separate idle interval despite another game running");
        Check(!stoppedIdle.Tick(stoppedProof, unrelatedRunningGame, 12, stoppedAnchor, t + 119L * Stopwatch.Frequency), "stopped-session branch waits the full 120 seconds");
        Check(stoppedIdle.Tick(stoppedProof, unrelatedRunningGame, 12, stoppedAnchor, t + 120L * Stopwatch.Frequency), "stopped-session branch triggers at 120 seconds without terminating unrelated game");
        var newStopEpoch = stoppedProof with { Session = stoppedProof.Session! with { EventTime = stoppedAnchor.AddSeconds(1) } };
        Check(!stoppedIdle.Tick(newStopEpoch, unrelatedRunningGame, 12, stoppedAnchor, t + 130L * Stopwatch.Frequency), "new stop epoch restarts stopped-session idle interval");
        Check(!stoppedIdle.Tick(new(SessionKnowledge.Unknown, null, "log gap"), unrelatedRunningGame, 12, stoppedAnchor, t + 131L * Stopwatch.Frequency), "log gap blocks and resets stopped-session branch");
        Check(!stoppedIdle.Tick(stoppedProof, unrelatedRunningGame, 99, stoppedAnchor, t + 132L * Stopwatch.Frequency), "client PID change resets stopped-session branch");
        Check(120 + 5 + (int)AutoStopWorkflow.ActionBudget.TotalSeconds <= 180, "idle plus polling plus bounded action budget targets completion within 180 seconds");
        Check(monitor.Remaining == TimeSpan.FromSeconds(120), "default idle interval is 120 seconds");
        Check(monitor.Tick(new(SessionKnowledge.Unknown, null, "log missing"), new(false, [], "unknown"), t) == false && monitor.State == MonitorState.Unknown, "unknown evidence blocks timer");
        Check(!monitor.Tick(session, new(true, [new(1, "game.exe", null, DateTime.UtcNow, "alias")], "running"), t), "accelerated game keeps timer reset");
        Check(!monitor.Tick(session, new(true, [], "none"), t), "idle candidate begins");
        Check(!monitor.Tick(session, new(true, [], "none"), t + (long)(119 * (double)Stopwatch.Frequency)), "119 seconds does not trigger");
        Check(monitor.Tick(session, new(true, [], "none"), t + (long)(120 * (double)Stopwatch.Frequency)) && monitor.State == MonitorState.AutoStopDisabled, "120 seconds triggers observation-only state");
        var pausedMonitor = new IdleMonitor();
        Check(!pausedMonitor.Tick(session, new(true, [], "none"), t), "monitoring pause test begins an idle interval");
        pausedMonitor = new IdleMonitor(); // Same reset performed by the UI Stop/Resume button.
        Check(!pausedMonitor.Tick(session, new(true, [], "none"), t + (long)(50 * (double)Stopwatch.Frequency)), "resuming monitoring discards time spent stopped");
        Check(!pausedMonitor.Tick(session, new(true, [], "none"), t + (long)(169 * (double)Stopwatch.Frequency)), "resumed monitoring requires a fresh 120-second interval");
        Check(pausedMonitor.Tick(session, new(true, [], "none"), t + (long)(170 * (double)Stopwatch.Frequency)), "resumed monitoring reaches threshold only after fresh interval");
        var restarted = session with { Session = session.Session! with { ClientPid = 99 } };
        Check(!monitor.Tick(restarted, new(true, [], "none"), t + (long)(150 * (double)Stopwatch.Frequency)) && monitor.Remaining == TimeSpan.FromSeconds(120), "session switch resets timer");
        Check(!monitor.Tick(session, new(true, [new(2, "game.exe", null, DateTime.UtcNow, "running")], "running"), t + (long)(200 * (double)Stopwatch.Frequency)), "game restart resets timer");
        var gameIdSwitch = session with { Session = session.Session! with { GameId = "232" } };
        Check(!monitor.Tick(gameIdSwitch, new(true, [], "none"), t + (long)(250 * (double)Stopwatch.Frequency)) && monitor.Remaining == TimeSpan.FromSeconds(120), "game ID switch resets timer");
        Check(SafeLog.Redact("token=secret hardware_id:ABC123").Contains("[REDACTED]") && !SafeLog.Redact("token=secret").Contains("secret"), "sensitive log redaction");
        Check(!new DisabledPauseAdapter().CanControl("11.3.2.9"), "unverified pause adapter cannot control client");
        var engineStart = DateTime.UtcNow;
        var engineSnapshot = new LeiGodProcess(9123, "leishenSdk", @"E:\LeiGod_Acc\leishenSdk.exe", engineStart);
        Check(!OwnedProcessExitAdapter.StillMatchesValidatedSnapshot(engineSnapshot, 9123, "leishenSdk", engineStart, null, @"E:\LeiGod_Acc"), "exit snapshot rejects unreadable engine path even when PID/start match");
        Check(!OwnedProcessExitAdapter.StillMatchesValidatedSnapshot(engineSnapshot, 9124, "leishenSdk", engineStart, null, @"E:\LeiGod_Acc"), "engine snapshot rejects PID reuse");
        Check(!OwnedProcessExitAdapter.StillMatchesValidatedSnapshot(engineSnapshot, 9123, "leishenSdk", engineStart.AddSeconds(1), null, @"E:\LeiGod_Acc"), "engine snapshot rejects a changed process start time");
        Check(!OwnedProcessExitAdapter.StillMatchesValidatedSnapshot(engineSnapshot, 9123, "leishenSdk", engineStart, @"C:\Other\leishenSdk.exe", @"E:\LeiGod_Acc"), "engine snapshot rejects a changed executable path");
        var currentGame231 = session.Session! with { GameId = "231", ClientPid = 2284, ClientStart = DateTime.UtcNow };
        var fixtureDir = Directory.CreateTempSubdirectory("leigod-session-");
        try
        {
            var clientStart = DateTime.Now; var oldEventTime = clientStart.AddSeconds(-5);
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), $"[26292][{oldEventTime:yyyy-MM-dd HH:mm:ss}] ipc handle start-acc {{\"game_id\":\"231\"}}\n");
            var parsed = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(parsed.Knowledge == SessionKnowledge.Unknown, "old PID session log event is not adopted by restarted client");
            var overlapping = $"[2284][{clientStart.AddSeconds(1):yyyy-MM-dd HH:mm:ss}] ipc handle start-acc {{\"game_id\":\"231\"}}\n[2284][{clientStart.AddSeconds(2):yyyy-MM-dd HH:mm:ss}] ipc handle start-acc {{\"game_id\":\"232\"}}\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), overlapping);
            var concurrent = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(concurrent.Knowledge == SessionKnowledge.Unknown, "overlapping active game IDs fail closed");

            var sessionEvents = $"[2284][{clientStart.AddSeconds(1):yyyy-MM-dd HH:mm:ss.fff}] ipc handle launch-local-game with [{{\"game_id\":\"908171\",\"game_process\":\"GameA.exe, GameA-Win64.exe\"}}]\n" +
                $"[2284][{clientStart.AddSeconds(2):yyyy-MM-dd HH:mm:ss.fff}] ipc handle start-acc {{\"game_id\":\"908171\"}}\n" +
                $"[2284][{clientStart.AddSeconds(3):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908171\",\"status\":\"before-start\"}}\n" +
                $"[2284][{clientStart.AddSeconds(4):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908171\",\"status\":\"running\",\"game_name\":\"Fixture A\"}}\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), sessionEvents);
            var oneMapped = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(oneMapped.Knowledge == SessionKnowledge.Accelerating && oneMapped.ActiveSessions.Count == 1 && oneMapped.Session!.GameProcesses!.OrderBy(x => x).SequenceEqual(new[] { "GameA.exe", "GameA-Win64.exe" }.OrderBy(x => x)), "running acceleration requires same-client launch-local-game executable mapping");
            var gameA = new GameProcess(801, "GameA.exe", @"C:\Games\A\GameA.exe", DateTime.UtcNow, "fixture A");
            var gameB = new GameProcess(802, "GameB.exe", @"C:\Games\B\GameB.exe", DateTime.UtcNow, "fixture B");
            var unionLog = sessionEvents +
                $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] ipc handle launch-local-game with [{{\"game_id\":\"908172\",\"game_process\":\"GameB.exe\"}}]\n" +
                $"[2284][{clientStart.AddSeconds(6):yyyy-MM-dd HH:mm:ss.fff}] ipc handle start-acc {{\"game_id\":\"908172\"}}\n" +
                $"[2284][{clientStart.AddSeconds(7):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908172\",\"status\":\"running\",\"game_name\":\"Fixture B\"}}\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), unionLog);
            var multi = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            var unionGame = new GameDetector(_ => new GameProcessInventory(true, [gameA, gameB], "fixture multi-game union")).Find(multi);
            Check(multi.Knowledge == SessionKnowledge.Accelerating && multi.ActiveSessions.Count == 2 && unionGame.Known && unionGame.Processes.Select(p => p.Pid).Order().SequenceEqual([801, 802]), "multiple active game IDs use the union of their log-bound processes");
            var missingMap = unionLog.Replace("GameB.exe", "", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), missingMap);
            var missingGameMapping = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(missingGameMapping.Knowledge == SessionKnowledge.Accelerating && !new GameDetector(_ => new GameProcessInventory(true, [gameA], "missing game mapping fixture")).Find(missingGameMapping).Known, "active game ID with empty launch process evidence cannot authorize idle");
            var noIdStop = unionLog + $"[2284][{clientStart.AddSeconds(8):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{}}\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), noIdStop);
            Check(new SessionReader().Read(fixtureDir.FullName, 2284, clientStart).Knowledge == SessionKnowledge.Unknown, "uncorrelated stop event cannot clear multiple active game IDs");
            var conflictingMapping = sessionEvents + $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] ipc handle launch-local-game with [{{\"game_id\":\"908171\",\"game_process\":\"OtherGame.exe\"}}]\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), conflictingMapping);
            var conflictingReadback = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(conflictingReadback.Knowledge == SessionKnowledge.Unknown && !conflictingReadback.SdkIdleFallbackAllowed, "conflicting process mappings cannot fall back to SDK idle");
            var restartedSameId = sessionEvents +
                $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908171\",\"status\":\"normal\"}}\n" +
                $"[2284][{clientStart.AddSeconds(6):yyyy-MM-dd HH:mm:ss.fff}] ipc handle start-acc {{\"game_id\":\"908171\"}}\n" +
                $"[2284][{clientStart.AddSeconds(7):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908171\",\"status\":\"running\",\"game_name\":\"Fixture A\"}}\n" +
                $"[2284][{clientStart.AddSeconds(8):yyyy-MM-dd HH:mm:ss.fff}] ipc handle launch-local-game with [{{\"game_id\":\"908171\",\"game_process\":\"GameA.exe, GameA-Win64.exe\"}}]\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), restartedSameId);
            var restartedSession = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(restartedSession.Knowledge == SessionKnowledge.Accelerating && restartedSession.Session!.EventTime > oneMapped.Session!.EventTime, "same game ID restart has a new event time for a fresh idle interval");
            var interruptedStart =
                $"[2284][{clientStart.AddSeconds(1):yyyy-MM-dd HH:mm:ss.fff}] ipc handle start-acc {{\"game_id\":1727}}\n" +
                $"[2284][{clientStart.AddSeconds(2):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{\"reason\":\"other\"}}\n" +
                $"[2284][{clientStart.AddSeconds(3):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"1727\",\"status\":\"normal\"}}\n" +
                $"[2284][{clientStart.AddSeconds(4):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"1727\",\"status\":\"running\"}}\n" +
                $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] ipc handle start-acc {{\"game_id\":1727}}\n" +
                $"[2284][{clientStart.AddSeconds(6):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"1727\",\"status\":\"running\"}}\n" +
                $"[2284][{clientStart.AddSeconds(7):yyyy-MM-dd HH:mm:ss.fff}] ipc handle launch-local-game with [{{\"game_id\":1727,\"game_process\":\"r5apex_dx12.exe\"}}]\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), interruptedStart);
            var recovered = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(recovered.Knowledge == SessionKnowledge.Accelerating && recovered.Session?.GameId == "1727" && recovered.Session.GameProcesses!.Contains("r5apex_dx12.exe"), "fresh complete start recovers after interrupted LeiGod acceleration attempt");
            var stopAndNormal = sessionEvents +
                $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{\"game_id\":\"908171\"}}\n" +
                $"[2284][{clientStart.AddSeconds(6):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908171\",\"status\":\"normal\"}}\n";
            var teardownBeforeStop = sessionEvents +
                $"[2284][{clientStart.AddSeconds(4):yyyy-MM-dd HH:mm:ss.fff}] ipc handle launch-local-game {{\"game_id\":\"908171\",\"op\":0,\"game_process\":\"\"}}\n" +
                $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{\"game_id\":\"908171\"}}\n" +
                $"[2284][{clientStart.AddSeconds(7):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908171\",\"status\":\"normal\"}}\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), teardownBeforeStop);
            var teardownReadback = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(teardownReadback.Knowledge == SessionKnowledge.NotAccelerating && teardownReadback.StopStateConfirmed, "op=0 empty launch mapping teardown before stop/status does not poison active session");
            var invalidTeardown = teardownBeforeStop.Replace("\"op\":0", "\"op\":1", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), invalidTeardown);
            Check(new SessionReader().Read(fixtureDir.FullName, 2284, clientStart).Knowledge == SessionKnowledge.Unknown, "nonzero op with empty active launch mapping remains contradictory");
            var normalThenNoIdStop = sessionEvents +
                $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908171\",\"status\":\"normal\"}}\n" +
                $"[2284][{clientStart.AddSeconds(6):yyyy-MM-dd HH:mm:ss.fff}] ipc handle launch-local-game {{\"op\":0,\"game_process\":\"\"}}\n" +
                $"[2284][{clientStart.AddSeconds(7):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{}}\n";
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), normalThenNoIdStop);
            var normalThenNoIdStopResult = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(normalThenNoIdStopResult.Knowledge == SessionKnowledge.NotAccelerating && normalThenNoIdStopResult.StopStateConfirmed && normalThenNoIdStopResult.StoppedGameIds.SetEquals(["908171"]), "status normal then id-less op=0 teardown and id-less stop-acc proves the same stopped game ID");
            var priorNormalThenNewRun = normalThenNoIdStop.Replace(
                $"[2284][{clientStart.AddSeconds(7):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{}}\n",
                $"[2284][{clientStart.AddSeconds(7):yyyy-MM-dd HH:mm:ss.fff}] ipc handle start-acc {{\"game_id\":\"908172\"}}\n" +
                $"[2284][{clientStart.AddSeconds(8):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908172\",\"status\":\"running\"}}\n" +
                $"[2284][{clientStart.AddSeconds(9):yyyy-MM-dd HH:mm:ss.fff}] [accelerating-status-change] {{\"id\":\"908172\",\"status\":\"normal\"}}\n" +
                $"[2284][{clientStart.AddSeconds(10):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{}}\n");
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), priorNormalThenNewRun);
            var newRunStop = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(newRunStop.Knowledge == SessionKnowledge.NotAccelerating && newRunStop.StopStateConfirmed && newRunStop.StoppedGameIds.SetEquals(["908172"]), "new acceleration epoch excludes an earlier run's normal status from id-less stop proof");
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), "[2284][" + clientStart.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss.fff") + "] unrelated log line\n");
            var noSessionReadback = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(noSessionReadback.Knowledge == SessionKnowledge.Unknown && !noSessionReadback.SdkIdleFallbackAllowed, "unrecognized or absent session events cannot authorize SDK idle fallback");
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), sessionEvents + $"[2284][{clientStart.AddSeconds(5):yyyy-MM-dd HH:mm:ss.fff}] ipc handle stop-acc {{\"game_id\":\"908171\"}}\n");
            Check(new SessionReader().Read(fixtureDir.FullName, 2284, clientStart).Knowledge == SessionKnowledge.Unknown, "stop-acc without normal/stopped readback is insufficient");
            File.WriteAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), stopAndNormal);
            var stoppedWithProof = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(stoppedWithProof.Knowledge == SessionKnowledge.NotAccelerating && stoppedWithProof.StopStateConfirmed && stoppedWithProof.StoppedGameIds.Contains("908171"), "stop-acc plus same-ID normal status confirms acceleration stopped");
            var confirmedStopTime = stoppedWithProof.Session!.EventTime;
            File.AppendAllText(Path.Combine(fixtureDir.FullName, "main-fixture.log"), $"[2284][{clientStart.AddSeconds(20):yyyy-MM-dd HH:mm:ss.fff}] periodic heartbeat\n");
            var afterHeartbeat = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
            Check(afterHeartbeat.Knowledge == SessionKnowledge.NotAccelerating && afterHeartbeat.Session!.EventTime == confirmedStopTime, "unrelated later log writes do not move the confirmed stop epoch anchor");
             var jsonEvents = string.Join(Environment.NewLine, [
                 JsonSerializer.Serialize(new { pid = 2284, timestamp = clientStart.AddSeconds(1), message = "ipc handle launch-local-game", game_id = "908174", game_process = new[] { "JsonGame.exe" } }),
                 JsonSerializer.Serialize(new { pid = 2284, timestamp = clientStart.AddSeconds(2), message = "ipc handle start-acc", game_id = "908174" }),
                 JsonSerializer.Serialize(new { pid = 2284, timestamp = clientStart.AddSeconds(3), message = "[accelerating-status-change]", id = "908174", status = "running", game_name = "JSON Fixture" })
             ]) + Environment.NewLine;
             File.Delete(Path.Combine(fixtureDir.FullName, "main-fixture.log"));
             File.WriteAllText(Path.Combine(fixtureDir.FullName, "client.log"), jsonEvents);
             var jsonProbe = new SessionReader().Probe(fixtureDir.FullName, 2284, clientStart);
             var jsonSession = new SessionReader().Read(fixtureDir.FullName, 2284, clientStart);
             Check(jsonProbe.Supported && jsonProbe.Format.Contains("json", StringComparison.OrdinalIgnoreCase) && jsonSession.Knowledge == SessionKnowledge.Accelerating && jsonSession.Session!.GameProcesses!.Contains("JsonGame.exe"), "JSON-line client logs are recognized across LeiGod log layouts");
        }
        finally { fixtureDir.Delete(true); }
        var previousLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var isolatedLocal = Directory.CreateTempSubdirectory("leigod-local-");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", isolatedLocal.FullName);
        var detector = new GameDetector(_ => new GameProcessInventory(true, [], "offline empty process inventory"));
        var absent = detector.Find(currentGame231);
        Check(absent.Known && absent.Processes.Count == 0, "deterministic process inventory: current matching game executable absent");
        Check(!new GameDetector(_ => new GameProcessInventory(false, [], "fixture process scan denied")).Find(currentGame231).Known, "process scan failure blocks idle");
        var mappedNameWrongPath = new GameProcess(787, "DeadByDaylight-EGS-Shipping.exe", @"C:\OtherInstall\DeadByDaylight-EGS-Shipping.exe", DateTime.UtcNow, "same name, unrelated path");
        var strictAliasFile = Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "strict-aliases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(strictAliasFile)!);
        File.WriteAllText(strictAliasFile, "{\"231\":[{\"Name\":\"DeadByDaylight-EGS-Shipping.exe\",\"Path\":\"C:\\\\Games\\\\DeadByDaylight\\\\DeadByDaylight-EGS-Shipping.exe\"}]}");
        var pathConflict = new GameDetector(_ => new GameProcessInventory(true, [mappedNameWrongPath], "same executable name at different path"), strictAliasFile).Find(currentGame231);
        Check(!pathConflict.Known, "same mapped executable name at a different path is contradictory and blocks idle");
        var otherProcess = new GameProcess(77, "OtherUnaccelerated.exe", @"C:\Games\Other\OtherUnaccelerated.exe", DateTime.UtcNow, "fixture");
        var otherProcess2 = new GameProcess(79, "SecondUnaccelerated.exe", @"C:\Games\Second\SecondUnaccelerated.exe", DateTime.UtcNow, "fixture");
        var unrelated = new GameDetector(_ => new GameProcessInventory(true, [otherProcess, otherProcess2], "multiple unrelated games only")).Find(currentGame231);
        var unrelatedTimer = new IdleMonitor(TimeSpan.FromSeconds(200));
        Check(unrelated.Known && unrelated.Processes.Count == 0 && !unrelatedTimer.Tick(session, unrelated, t), "unaccelerated unrelated game does not block target idle timer");
        Check(unrelatedTimer.Tick(session, unrelated, t + (long)(200 * (double)Stopwatch.Frequency)), "unaccelerated unrelated game still permits target idle threshold");
        var targetProcess = new GameProcess(78, "DeadByDaylight-EGS-Shipping.exe", @"C:\Games\DeadByDaylight\DeadByDaylight-EGS-Shipping.exe", DateTime.UtcNow, "fixture target game");
        var targetAndOther = new GameDetector(_ => new GameProcessInventory(true, [otherProcess, targetProcess], "target plus unrelated game")).Find(currentGame231);
        var simultaneousTimer = new IdleMonitor(TimeSpan.FromSeconds(200));
        Check(targetAndOther.Known && targetAndOther.Processes.Select(p => p.Pid).SequenceEqual([78]) && !simultaneousTimer.Tick(session, targetAndOther, t), "target accelerated game keeps timer reset while unrelated game also runs");
        var noAlias = new GameDetector(_ => new GameProcessInventory(true, [otherProcess], "unmapped game ID"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "no-aliases.json")).Find(session.Session! with { GameId = "908173" });
        Check(!noAlias.Known, "unmapped accelerated game blocks idle decision until user confirms executable");
        var apexAliasPath = Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "apex-old-alias.json");
        Directory.CreateDirectory(Path.GetDirectoryName(apexAliasPath)!);
        File.WriteAllText(apexAliasPath, "{\"1727\":[{\"Name\":\"ACCStd.exe\",\"Path\":\"C:\\\\Program Files (x86)\\\\Acer\\\\Care Center\\\\ACCStd.exe\"}]}");
        var apexProcess = new GameProcess(17270, "r5apex_dx12.exe", @"D:\Games\Apex\r5apex_dx12.exe", DateTime.UtcNow, "verified Apex fixture");
        var acerProcess = new GameProcess(14256, "ACCStd.exe", @"C:\Program Files (x86)\Acer\Care Center\ACCStd.exe", DateTime.UtcNow, "Acer background utility fixture");
        var apexSession = session.Session! with { GameId = "1727", GameProcesses = ["EasyAntiCheat.exe"] };
        var apexRunning = new GameDetector(_ => new GameProcessInventory(true, [apexProcess, acerProcess], "Apex and unrelated Acer utility"), apexAliasPath).Find(apexSession);
        var apexTimer = new IdleMonitor(TimeSpan.FromSeconds(120));
        Check(apexRunning.Known && apexRunning.Processes.Any(p => p.Name == "r5apex_dx12.exe") && !apexTimer.Tick(session, apexRunning, t), "Apex game ID fallback detects the live game despite anti-cheat-only metadata and prevents idle countdown");
        var apexOnlyAcer = new GameDetector(_ => new GameProcessInventory(true, [acerProcess], "only stale Acer utility"), apexAliasPath).Find(apexSession with { GameProcesses = [] });
        Check(apexOnlyAcer.Known && apexOnlyAcer.Processes.Count == 0, "stale Acer utility alias cannot block Apex idle once every verified Apex executable is absent");
        var stoppedApexWithoutProcessNames = new SessionSnapshot(SessionKnowledge.NotAccelerating, session.Session! with { EventTime = apexProcess.StartTime.AddSeconds(1) }, "stop event has no game_process")
        {
            StopStateConfirmed = true,
            StoppedGameIds = new HashSet<string>(["1727"], StringComparer.OrdinalIgnoreCase)
        };
        var stoppedApexIdle = new GameDetector(_ => new GameProcessInventory(true, [acerProcess], "stopped Apex with Acer utility only"), apexAliasPath).FindStopped(stoppedApexWithoutProcessNames);
        Check(stoppedApexIdle.Known && stoppedApexIdle.Processes.Count == 0, "confirmed Apex stop without process names uses its verified game-ID mapping and ignores Acer utility");
        var stoppedApexRunning = new GameDetector(_ => new GameProcessInventory(true, [apexProcess], "stopped Apex process remains running"), apexAliasPath).FindStopped(stoppedApexWithoutProcessNames);
        Check(stoppedApexRunning.Known && stoppedApexRunning.Processes.Any(p => p.Name == "r5apex_dx12.exe"), "confirmed Apex stop does not count down while a verified Apex executable remains running");
        var apexLaunchedAfterStop = apexProcess with { Pid = 17271, StartTime = apexProcess.StartTime.AddSeconds(3) };
        var stoppedApexThenNewLaunch = stoppedApexWithoutProcessNames with { Session = stoppedApexWithoutProcessNames.Session! with { EventTime = apexProcess.StartTime.AddSeconds(2) } };
        var stoppedApexNewUnacceleratedRun = new GameDetector(_ => new GameProcessInventory(true, [apexLaunchedAfterStop], "Apex relaunched after the confirmed stop"), apexAliasPath).FindStopped(stoppedApexThenNewLaunch);
        Check(stoppedApexNewUnacceleratedRun.Known && stoppedApexNewUnacceleratedRun.Processes.Count == 0, "a game instance launched after the stop event is unaccelerated and does not block idle");
        var stoppedUnknownWithoutNames = new GameDetector(_ => new GameProcessInventory(true, [], "unknown stopped game"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "no-aliases.json")).FindStopped(stoppedApexWithoutProcessNames with { StoppedGameIds = new HashSet<string>(["908175"], StringComparer.OrdinalIgnoreCase) });
        Check(!stoppedUnknownWithoutNames.Known, "unknown stopped game with no process names remains fail-closed");
        var apexOnAnotherInstall = new GameProcess(17271, "r5apex.exe", @"E:\DifferentPC\InstalledGames\r5apex.exe", DateTime.UtcNow, "different-computer path fixture");
        var apexOtherPath = new GameDetector(_ => new GameProcessInventory(true, [apexOnAnotherInstall], "portable executable-name mapping"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(apexSession with { GameProcesses = [] });
        Check(apexOtherPath.Known && apexOtherPath.Processes.Any(p => p.Name == "r5apex.exe"), "built-in Apex executable names work on another computer without source-machine paths");
        var left4DeadProcess = new GameProcess(9170, "left4dead2.exe", @"E:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2.exe", DateTime.UtcNow, "verified installed game executable");
        var left4DeadSession = session.Session! with { GameId = "917", GameName = "Left 4 Dead 2", GameProcesses = [] };
        var left4DeadRunning = new GameDetector(_ => new GameProcessInventory(true, [left4DeadProcess], "LeiGod game ID 917 and installed game executable"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(left4DeadSession);
        Check(left4DeadRunning.Known && left4DeadRunning.Processes.Any(p => p.Name == "left4dead2.exe"), "LeiGod Left 4 Dead 2 game ID recognizes its installed executable without per-machine path mapping");
        var stoppedLeft4Dead = new SessionSnapshot(SessionKnowledge.NotAccelerating, left4DeadSession with { EventTime = left4DeadProcess.StartTime.AddSeconds(1) }, "confirmed Left 4 Dead 2 stop")
        {
            StopStateConfirmed = true,
            StoppedGameIds = new HashSet<string>(["917"], StringComparer.OrdinalIgnoreCase)
        };
        var stoppedLeft4DeadRunning = new GameDetector(_ => new GameProcessInventory(true, [left4DeadProcess], "stopped Left 4 Dead 2 while executable remains"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).FindStopped(stoppedLeft4Dead);
        Check(stoppedLeft4DeadRunning.Known && stoppedLeft4DeadRunning.Processes.Any(p => p.Name == "left4dead2.exe"), "confirmed Left 4 Dead 2 stop does not start idle countdown while the game process remains");
        var stoppedLeft4DeadIdle = new GameDetector(_ => new GameProcessInventory(true, [], "Left 4 Dead 2 executable exited"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).FindStopped(stoppedLeft4Dead);
        Check(stoppedLeft4DeadIdle.Known && stoppedLeft4DeadIdle.Processes.Count == 0, "confirmed Left 4 Dead 2 stop becomes idle only after its game process exits");
        var left4DeadOnAnotherInstall = new GameProcess(9171, "left4dead2.exe", @"D:\Games\Steam\Left 4 Dead 2\left4dead2.exe", DateTime.UtcNow, "portable executable-name mapping");
        var left4DeadOtherPath = new GameDetector(_ => new GameProcessInventory(true, [left4DeadOnAnotherInstall], "different computer install path"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(left4DeadSession);
        Check(left4DeadOtherPath.Known && left4DeadOtherPath.Processes.Any(p => p.Name == "left4dead2.exe"), "Left 4 Dead 2 built-in mapping works at a different computer path");
        var terrariaProcess = new GameProcess(5020, "Terraria.exe", @"E:\SteamLibrary\steamapps\common\Terraria\Terraria.exe", DateTime.UtcNow, "installed Steam app 105600 executable");
        foreach (var terrariaId in new[] { "502", "921", "2246" })
        {
            var terrariaSession = session.Session! with { GameId = terrariaId, GameName = "Terraria", GameProcesses = [] };
            var terrariaRunning = new GameDetector(_ => new GameProcessInventory(true, [terrariaProcess], "LeiGod Terraria catalog ID and installed executable"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(terrariaSession);
            Check(terrariaRunning.Known && terrariaRunning.Processes.Any(p => p.Name == "Terraria.exe"), $"LeiGod Terraria game ID {terrariaId} recognizes the installed executable without a per-machine path");
        }
        var terrariaOtherPath = new GameProcess(5021, "Terraria.exe", @"D:\Games\Terraria\Terraria.exe", DateTime.UtcNow, "different computer path fixture");
        var portableTerraria = new GameDetector(_ => new GameProcessInventory(true, [terrariaOtherPath], "Terraria at another computer path"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(session.Session! with { GameId = "502", GameName = "Terraria", GameProcesses = [] });
        Check(portableTerraria.Known && portableTerraria.Processes.Any(p => p.Name == "Terraria.exe"), "Terraria built-in mapping works on another computer path");
        var apexConcurrentSession = apexSession with { EventTime = DateTime.UtcNow.AddSeconds(-1) };
        var left4DeadConcurrentSession = left4DeadSession with { EventTime = DateTime.UtcNow };
        var concurrentGames = new SessionSnapshot(SessionKnowledge.Accelerating, left4DeadConcurrentSession, "Apex and Left 4 Dead 2 accelerated")
        {
            ActiveSessions = [apexConcurrentSession, left4DeadConcurrentSession]
        };
        var bothGamesRunning = new GameDetector(_ => new GameProcessInventory(true, [apexProcess, left4DeadProcess], "both accelerated games live"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(concurrentGames);
        var concurrentTimer = new IdleMonitor(TimeSpan.FromSeconds(120));
        Check(bothGamesRunning.Known && bothGamesRunning.Processes.Count == 2 && !concurrentTimer.Tick(concurrentGames, bothGamesRunning, t), "Apex and Left 4 Dead 2 simultaneous sessions monitor the union of both game processes");
        var onlyLeft4DeadRunning = new GameDetector(_ => new GameProcessInventory(true, [left4DeadProcess], "Apex exited while Left 4 Dead 2 remains"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(concurrentGames);
        Check(onlyLeft4DeadRunning.Known && onlyLeft4DeadRunning.Processes.Count == 1 && onlyLeft4DeadRunning.Processes[0].Name == "left4dead2.exe" && !concurrentTimer.Tick(concurrentGames, onlyLeft4DeadRunning, t + 60 * Stopwatch.Frequency), "one remaining accelerated game prevents the all-games idle countdown");
        var noAcceleratedGamesRunning = new GameDetector(_ => new GameProcessInventory(true, [], "both accelerated games exited"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "clean-aliases.json")).Find(concurrentGames);
        Check(noAcceleratedGamesRunning.Known && noAcceleratedGamesRunning.Processes.Count == 0 && !concurrentTimer.Tick(concurrentGames, noAcceleratedGamesRunning, t + 61 * Stopwatch.Frequency), "all accelerated games exiting begins a fresh 120-second idle period");
        Check(!concurrentTimer.Tick(concurrentGames, noAcceleratedGamesRunning, t + 180 * Stopwatch.Frequency) && concurrentTimer.Remaining == TimeSpan.FromSeconds(1), "Apex and Left 4 Dead 2 idle period remains active before 120 seconds");
        Check(concurrentTimer.Tick(concurrentGames, noAcceleratedGamesRunning, t + 181 * Stopwatch.Frequency), "Apex and Left 4 Dead 2 idle threshold is reached only after all games exit");
        var filteredUnknown = new GameDetector(_ => new GameProcessInventory(true, [otherProcess], "anti-cheat only for unknown game"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "no-aliases.json")).Find(session.Session! with { GameId = "908174", GameProcesses = ["EasyAntiCheat.exe"] });
        Check(!filteredUnknown.Known, "unknown game with only filtered anti-cheat metadata remains unknown instead of idle");
        var filteredCandidates = new GameDetector(_ => new GameProcessInventory(true, [otherProcess], "anti-cheat only candidates"), Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "no-aliases.json")).CandidateProcesses(session.Session! with { GameId = "908174", GameProcesses = ["EasyAntiCheat.exe"] });
        Check(!filteredCandidates.Complete, "unknown game without metadata or confirmed mapping does not enumerate arbitrary processes");
        using (var self = Process.GetCurrentProcess())
        {
            var selfGame = new GameProcess(self.Id, self.ProcessName + ".exe", LeiGodDiscovery.SafePath(self), self.StartTime, "offline user-confirmed mapping fixture");
            var aliasPath = Path.Combine(isolatedLocal.FullName, "LeiGodAutoStop", "aliases.json");
            var confirm = new GameDetector(_ => new GameProcessInventory(true, [selfGame], "offline fixture"), aliasPath);
            var candidateSession = session.Session! with { GameId = "908172", GameProcesses = [selfGame.Name] };
            var candidates = confirm.CandidateProcesses(candidateSession);
            Check(candidates.Complete && candidates.Candidates.Count == 1 && candidates.Candidates[0].Pid == selfGame.Pid, "current session exposes only live mapped candidates for confirmation");
            Check(!confirm.CandidateProcesses(candidateSession with { GameProcesses = [] }).Complete, "mapping confirmation rejects a session without process names");
            confirm.ConfirmProcess("908172", selfGame);
            var reload = new GameDetector(_ => new GameProcessInventory(true, [selfGame], "offline fixture"), aliasPath);
            var newlyMapped = reload.Find(session.Session! with { GameId = "908172" });
            Check(newlyMapped.Known && newlyMapped.Processes.Count == 1 && newlyMapped.Processes[0].Path == selfGame.Path, "new game ID loads user-confirmed path mapping");
        }
        Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocal);
        isolatedLocal.Delete(true);
        await TestAutomaticWorkflow(Check, t, session);
        await TestUiAutomationStop(Check);
        TestAcceptanceGate(Check);
        TestLegacyTaskGuard(Check);
        TestClientLogPauseAdapter(Check);
        Console.WriteLine("All offline scenarios passed.");
    }

    private static void TestClientLogPauseAdapter(Action<bool, string> check)
    {
        var dir = Directory.CreateTempSubdirectory("leigod-pause-shape-");
        try
        {
            const int pid = 4242;
            const string token = "fixture-token-0123456789";
            const string secret = "tQFyh13f7AYsHg==";
            var start = DateTime.Now;
            var file = Path.Combine(dir.FullName, "http-fixture.log");
            var adapter = new ClientLogPauseAdapter(dir.FullName, pid, start);
            string Stamp(DateTime when) => when.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string Url(string host) => $"https://{host}/client/pause/status?region_code=1&client_version=11.3.2.9&hardware_id=fixture-hardware&locale=zh_CN&account_token={token}";

            File.WriteAllText(file, $"[{pid}][{Stamp(start.AddSeconds(1))}] [info] client started, IPC handshake pending\n");
            var silent = adapter.Inspect();
            check(!adapter.CanControl("") && silent.ClientLines == 1 && silent.RequestSource == "none" && silent.BodySource == "none", "a client log with no request line yet is not controllable");

            File.WriteAllText(file, $"[{pid}][{Stamp(start.AddSeconds(1))}] [info] [post] \"https://api2.leigod.com/client/user/time/status?account_token={token}\" ~ {secret} ~\n");
            var unrelatedRequest = adapter.Inspect();
            check(adapter.CanControl("") && unrelatedRequest.RequestSource == "client-http-line" && unrelatedRequest.BodySource == "client-signature" && unrelatedRequest.StatusRequestLines == 0 && unrelatedRequest.PausePathLines == 0, "a machine whose logs never show the pause endpoint still becomes controllable from another client request line");
            check(!unrelatedRequest.Fingerprint.Contains(token, StringComparison.Ordinal) && !unrelatedRequest.Fingerprint.Contains(secret, StringComparison.Ordinal), "pause evidence fingerprint excludes token and body text");

            var observed = $"[{pid}][{Stamp(start.AddSeconds(1))}] [info]  [1000079] [post] \"{Url("api2.leigod.com")}\" ~ {secret} ~\n";
            File.WriteAllText(file, observed);
            check(adapter.CanControl(""), "pause adapter accepts the observed tilde-delimited request shape");
            var observedEvidence = adapter.Inspect();
            check(observedEvidence.Fingerprint.Contains("api2.leigod.com", StringComparison.OrdinalIgnoreCase) && observedEvidence.Fingerprint.Contains("body=len=16", StringComparison.Ordinal), "pause evidence fingerprint describes host and body length");
            check(observedEvidence.Fingerprint.Contains("<secret>", StringComparison.Ordinal) && !observedEvidence.Fingerprint.Contains("fixture-hardware", StringComparison.Ordinal), "pause evidence fingerprint names secret query fields without their values");

            File.WriteAllText(file, observed.Replace($" ~ {secret} ~", $" ~ {secret}", StringComparison.Ordinal));
            check(adapter.CanControl(""), "pause adapter accepts a request line without the trailing delimiter");

            File.WriteAllText(file, observed.Replace($" ~ {secret} ~", $" | {secret} |", StringComparison.Ordinal));
            check(adapter.CanControl(""), "pause adapter accepts an alternate body delimiter");

            File.WriteAllText(file, observed.Replace("api2.leigod.com", "api3.leigod.com", StringComparison.Ordinal));
            check(adapter.CanControl(""), "pause adapter accepts the api3 endpoint seen in real client logs");

            File.WriteAllText(file, observed.Replace("api2.leigod.com", "api9.leigod.com", StringComparison.Ordinal));
            check(!adapter.CanControl(""), "pause adapter rejects an unlisted api host");

            File.WriteAllText(file, $"{{\"pid\":{pid},\"time\":\"{Stamp(start.AddSeconds(1))}\",\"url\":\"{Url("api1.leigod.com")}\",\"body\":\"{secret}\"}}\n");
            check(adapter.CanControl(""), "pause adapter accepts a JSON record with url and body fields");

            var axiosShape = $"[{pid}][{Stamp(start.AddSeconds(1))}] [error] pauseUserTime error {{\"message\":\"timeout of 3000ms exceeded\",\"name\":\"AxiosError\",\"config\":{{\"timeout\":3000,\"method\":\"post\",\"url\":\"https://api3.leigod.com/client/pause\",\"data\":\"{secret}\",\"params\":{{\"region_code\":1,\"area_code\":\"HN\",\"os_type\":\"0\",\"client_version\":\"11.3.2.9\",\"hardware_id\":\"fixture-hardware\",\"locale\":\"zh_CN\",\"account_token\":\"{token}\"}}}}}}\n";
            File.WriteAllText(file, axiosShape);
            var axiosEvidence = adapter.Inspect();
            check(adapter.CanControl(""), "pause adapter accepts a client axios record whose url has no query and carries params");
            check(axiosEvidence.Fingerprint.Contains("api3.leigod.com", StringComparison.OrdinalIgnoreCase) && axiosEvidence.Fingerprint.Contains("<secret>", StringComparison.Ordinal) && !axiosEvidence.Fingerprint.Contains(token, StringComparison.Ordinal), "axios shape fingerprint keeps host and field names but no token");

            var headerFixture = axiosShape.Replace("\"params\":{", "\"headers\":{\"X-User-Id\":\"fixture-user\",\"X-Device-Id\":\"fixture-device\"},\"params\":{", StringComparison.Ordinal);
            var headers = ClientLogPauseAdapter.ExtractRequestHeaders(headerFixture);
            check(headers.UserId == "fixture-user" && headers.DeviceId == "fixture-device", "pause adapter extracts the client request identity headers");

            File.WriteAllText(file, axiosShape.Replace($"\"account_token\":\"{token}\"", "\"other_field\":\"value\"", StringComparison.Ordinal));
            check(!adapter.CanControl(""), "axios record without an account token parameter is rejected");

            File.WriteAllText(file, observed.Replace($" ~ {secret} ~", " ~ ~", StringComparison.Ordinal));
            var emptyBody = adapter.Inspect();
            check(adapter.CanControl("") && emptyBody.StatusRequestLines == 1 && emptyBody.UsableBodyLines == 0 && emptyBody.BodySource == "client-signature", "pause line without an observed body falls back to the client signature");
            check(emptyBody.Fingerprint.Contains("body=missing", StringComparison.Ordinal), "missing local body is still recorded in the structure fingerprint");

            File.WriteAllText(file, $"[{pid}][{Stamp(start.AddSeconds(1))}] [info]  [1000088] [post] \"https://secapiv12.leigod.com/client/user/recent/query_log/v2?region_code=1&area_code=HN&client_version=11.3.2.9&hardware_id=fixture-hardware&locale=zh_CN&account_token={token}\" ~ {secret} ~\n");
            var genericOnly = adapter.Inspect();
            check(adapter.CanControl("") && genericOnly.RequestSource == "client-http-line" && genericOnly.BodySource == "client-signature", "pause capability is built from any client request line when the pause endpoint never appears");
            check(genericOnly.Fingerprint.Contains("secapiv12.leigod.com", StringComparison.OrdinalIgnoreCase) && !genericOnly.Fingerprint.Contains(token, StringComparison.Ordinal), "generic source fingerprint keeps the host and drops the token");

            var axiosOther = $"[{pid}][{Stamp(start.AddSeconds(1))}] [error] fetchUserInfo error {{\"config\":{{\"method\":\"post\",\"url\":\"https://secapiv12.leigod.com/client/user/time/status\",\"params\":{{\"region_code\":1,\"client_version\":\"11.3.2.9\",\"hardware_id\":\"fixture-hardware\",\"account_token\":\"{token}\"}}}}}}\n";
            File.WriteAllText(file, axiosOther);
            check(adapter.CanControl("") && adapter.Inspect().RequestSource == "client-http-line", "axios params from an unrelated endpoint are enough to build the pause request");

            var observedCandidates = ClientLogPauseAdapter.PauseCandidates(new Uri($"https://api3.leigod.com/client/pause/status?region_code=1&account_token={token}")).ToArray();
            check(observedCandidates.Length == 3 && observedCandidates[0].Host == "api3.leigod.com" && observedCandidates.All(c => c.AbsolutePath == "/client/pause" && c.Query.Contains("account_token=", StringComparison.Ordinal)), "pause targets keep the observed host first on the allowed hosts");
            var genericCandidates = ClientLogPauseAdapter.PauseCandidates(new Uri($"https://secapiv12.leigod.com/client/user/time/status?account_token={token}")).ToArray();
            check(genericCandidates.Length == 3 && genericCandidates[0].Host == "api2.leigod.com" && genericCandidates.All(c => c.Host.EndsWith(".leigod.com", StringComparison.OrdinalIgnoreCase)), "an unrelated source host never becomes the pause target");

            File.WriteAllText(file, observed.Replace("api2.leigod.com", "evil.example.com", StringComparison.Ordinal));
            check(!adapter.CanControl(""), "pause request from a non-whitelisted host is rejected");

            File.WriteAllText(file, observed.Replace("account_token=", "account_token_other=", StringComparison.Ordinal));
            check(!adapter.CanControl(""), "pause request without an account token query is rejected");

            File.WriteAllText(file, observed.Replace($"[{pid}]", "[999999]", StringComparison.Ordinal));
            check(!adapter.CanControl(""), "pause request from another process id is ignored");

            File.WriteAllText(file, observed.Replace(Stamp(start.AddSeconds(1)), Stamp(start.AddSeconds(-600)), StringComparison.Ordinal));
            check(!adapter.CanControl(""), "pause request from before the current client start is ignored");
        }
        finally
        {
            try { dir.Delete(true); } catch { }
        }
    }

    private static void TestPauseOnlyMonitor(Action<bool, string> check)
    {
        var monitor = new PauseOnlyMonitor(TimeSpan.FromSeconds(120));
        var start = Stopwatch.GetTimestamp();
        check(!monitor.Tick(null, "会话未知", start), "pause-only monitor fails closed when acceleration state is unknown");
        check(!monitor.Tick("client/session-1", "无加速游戏进程", start), "pause-only monitor starts a fresh idle interval after confirmed inactivity");
        check(!monitor.Tick("client/session-1", "无加速游戏进程", start + 119L * Stopwatch.Frequency), "pause-only monitor does not request pause before 120 seconds");
        check(monitor.Tick("client/session-1", "无加速游戏进程", start + 120L * Stopwatch.Frequency), "pause-only monitor requests pause at 120 seconds");
        monitor.MarkPaused("client/session-1");
        check(!monitor.Tick("client/session-1", "无加速游戏进程", start + 180L * Stopwatch.Frequency) && monitor.State == "本轮空闲已暂停", "pause-only monitor issues at most one pause per stable idle epoch");
        check(!monitor.Tick(null, "加速游戏运行", start + 181L * Stopwatch.Frequency), "running or unknown game state resets pause-only idle epoch");
        check(!monitor.Tick("client/session-1", "无加速游戏进程", start + 182L * Stopwatch.Frequency), "return to idle begins a new full 120-second interval");
    }

    private static async Task TestAutomaticWorkflow(Action<bool, string> check, long t, SessionSnapshot active)
    {
        var expected = active.Session! with { ClientStart = DateTime.UtcNow };
        var activeSession = active with { Session = expected, ActiveSessions = [expected] };
        var empty = new GameSnapshot(true, [], "fixture no target process");
        Func<(SessionSnapshot Session, GameSnapshot Game)> idle = () => (activeSession, empty);

        var pauseRunning = new FakePauseAdapter([Proof(false, 100, t)]);
        var stopRunning = new FakeStopAdapter();
        var gameRunning = new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, () => (activeSession, new(true, [new(1, "Target.exe", @"C:\Target.exe", DateTime.UtcNow, "fixture")], "running")), pauseRunning, stopRunning, CancellationToken.None);
        var gameRunningResult = await gameRunning;
        check(!gameRunningResult.Completed && pauseRunning.Requests == 0 && stopRunning.StopRequests == 0, "workflow game-running precheck sends no pause or stop");

        var pauseUnknown = new FakePauseAdapter([Proof(false, 100, t)]);
        var stopUnknown = new FakeStopAdapter();
        var unknownResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, () => (new(SessionKnowledge.Unknown, null, "fixture unknown"), new(false, [], "unknown")), pauseUnknown, stopUnknown, CancellationToken.None);
        check(!unknownResult.Completed && pauseUnknown.Requests == 0 && stopUnknown.StopRequests == 0, "workflow unknown evidence sends no pause or stop");

        var disabledPause = new FakePauseAdapter([Proof(false, 100, t)]);
        var disabledStop = new FakeStopAdapter(canStop: false);
        var disabledResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, idle, disabledPause, disabledStop, CancellationToken.None);
        check(disabledResult.Stage == "stop" && disabledPause.Requests == 0 && disabledStop.StopRequests == 0, "unverified stop protocol prevents pause request and shutdown workflow");

        var failedProofPause = new FakePauseAdapter([Proof(false, 100, t), Proof(false, 100, t), Proof(false, 100, t), Proof(false, 100, t), Proof(false, 100, t), Proof(false, 100, t)]);
        var failedProofStop = new FakeStopAdapter();
        var failedProofResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, idle, failedProofPause, failedProofStop, CancellationToken.None);
        check(failedProofResult.Stage == "pause-proof" && failedProofStop.StopRequests == 0 && failedProofStop.ExitRequests == 0, "pause proof failure never calls stop or exit");

        var successPause = new FakePauseAdapter([Proof(false, 100, t), Proof(true, 99, t), Proof(true, 99, t + (long)(13 * (double)Stopwatch.Frequency)), Proof(true, 99, t + (long)(14 * (double)Stopwatch.Frequency))]);
        var successStop = new FakeStopAdapter();
        var successResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, idle, successPause, successStop, CancellationToken.None, [expected]);
        check(successResult.Completed && successPause.Requests == 1 && successStop.StopRequests == 1 && successStop.ExitRequests == 1 && successStop.ExitVerified, "workflow requires stable pause readings before stop and exit");
        check(successResult.AutomatedPauseRequest, "live acceptance can distinguish an application-issued pause request");
        var alreadyPaused = new FakePauseAdapter([Proof(true, 99, t), Proof(true, 99, t), Proof(true, 99, t + (long)(13 * (double)Stopwatch.Frequency)), Proof(true, 99, t + (long)(14 * (double)Stopwatch.Frequency))]);
        var alreadyPausedResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, idle, alreadyPaused, new FakeStopAdapter(), CancellationToken.None, [expected]);
        check(alreadyPausedResult.Completed && !alreadyPausedResult.AutomatedPauseRequest && alreadyPaused.Requests == 0, "an already paused account cannot claim this app submitted the pause");
        check(!PauseProofRules.Confirmed(Proof(true, 100, t), Proof(true, 100, t + (long)(11.9 * (double)Stopwatch.Frequency))), "pause proof rejects readings less than 12 seconds apart");
        // Use one extra stopwatch tick so the fixture is not sensitive to the
        // tiny DateTime.UtcNow conversion error in Proof().
        const double proofSeconds = 46;
        check(PauseProofRules.ConfirmedStable(Proof(true, 100, t), Proof(true, 100, t + (long)(proofSeconds * (double)Stopwatch.Frequency)), TimeSpan.FromSeconds(45)), "stable pause proof accepts unchanged balance over 45 seconds");
        check(PauseProofRules.ConfirmedStable(Proof(true, 100, t), Proof(true, 98.5m, t + (long)(proofSeconds * (double)Stopwatch.Frequency)), TimeSpan.FromSeconds(45)), "stable pause proof tolerates sub-two-second rounding drift");
        check(!PauseProofRules.ConfirmedStable(Proof(true, 100, t), Proof(true, 97, t + (long)(proofSeconds * (double)Stopwatch.Frequency)), TimeSpan.FromSeconds(45)), "stable pause proof rejects a real countdown");

        var fallbackPause = new FakePauseAdapter([Proof(false, 100, t), Proof(true, 99, t), Proof(true, 99, t + (long)(13 * (double)Stopwatch.Frequency)), Proof(true, 99, t + (long)(14 * (double)Stopwatch.Frequency))]);
        var fallbackStop = new FakeStopAdapter(throwStop: true);
        var fallbackResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, idle, fallbackPause, fallbackStop, CancellationToken.None, [expected]);
        check(fallbackResult.Completed && fallbackStop.FallbackRequests == 1 && fallbackStop.ExitRequests == 0, "SDK stop failure uses paused-session process-tree fallback");
        var rejectedFallback = new FakeStopAdapter(throwStop: true, fallbackSuccess: false);
        var rejectedFallbackPause = new FakePauseAdapter([Proof(false, 100, t), Proof(true, 99, t), Proof(true, 99, t + (long)(13 * (double)Stopwatch.Frequency)), Proof(true, 99, t + (long)(14 * (double)Stopwatch.Frequency))]);
        var rejectedFallbackResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, idle, rejectedFallbackPause, rejectedFallback, CancellationToken.None, [expected]);
        check(!rejectedFallbackResult.Completed && rejectedFallback.FallbackRequests == 1 && !rejectedFallback.ExitVerified, "paused-session fallback rejection prevents exit completion");

        var pauseInducedStopped = new SessionSnapshot(SessionKnowledge.NotAccelerating, expected with { Active = false }, "pause stopped acceleration")
        {
            StopStateConfirmed = true,
            StoppedGameIds = new HashSet<string>([expected.GameId], StringComparer.OrdinalIgnoreCase)
        };
        var pauseStopPause = new FakePauseAdapter([Proof(false, 100, t), Proof(true, 99, t), Proof(true, 99, t + (long)(13 * (double)Stopwatch.Frequency)), Proof(true, 99, t + (long)(14 * (double)Stopwatch.Frequency))]);
        var pauseStopAdapter = new FakeStopAdapter(initiallyStopped: true);
        var pauseStopReads = 0;
        var pauseStopResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, () =>
        {
            pauseStopReads++;
            return pauseStopReads <= 2 ? (activeSession, empty) : (pauseInducedStopped, empty);
        }, pauseStopPause, pauseStopAdapter, CancellationToken.None, [expected]);
        check(pauseStopResult.Completed && pauseStopAdapter.StopRequests == 0 && pauseStopAdapter.ExitRequests == 1,
            "pause-induced stop with complete same-ID status proof skips stop click and safely exits after pause proof");

        var subsetStopped = pauseInducedStopped with { StoppedGameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
        var subsetPause = new FakePauseAdapter([Proof(false, 100, t), Proof(true, 99, t)]);
        var subsetStop = new FakeStopAdapter(initiallyStopped: true);
        var subsetReads = 0;
        var subsetResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, () =>
        {
            subsetReads++;
            return subsetReads <= 2 ? (activeSession, empty) : (subsetStopped, empty);
        }, subsetPause, subsetStop, CancellationToken.None, [expected]);
        check(subsetResult.Stage == "pause-proof" && subsetStop.StopRequests == 0 && subsetStop.ExitRequests == 0,
            "pause-induced stop with incomplete original ID proof blocks exit");

        var restartedDuringPauseSession = expected with { EventTime = expected.EventTime.AddSeconds(1) };
        var restartedDuringPauseSnapshot = new SessionSnapshot(SessionKnowledge.Accelerating, restartedDuringPauseSession, "same game ID restarted") { ActiveSessions = [restartedDuringPauseSession] };
        var eventRestartPause = new FakePauseAdapter([Proof(false, 100, t), Proof(true, 99, t)]);
        var eventRestartStop = new FakeStopAdapter();
        var eventRestartReads = 0;
        var eventRestartResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, () =>
        {
            eventRestartReads++;
            return eventRestartReads <= 2 ? (activeSession, empty) : (restartedDuringPauseSnapshot, empty);
        }, eventRestartPause, eventRestartStop, CancellationToken.None, [expected]);
        check(eventRestartResult.Stage == "pause-proof" && eventRestartStop.StopRequests == 0 && eventRestartStop.ExitRequests == 0,
            "same game ID restarted with a new event time after pause is blocked");

        var stoppedPause = new FakePauseAdapter([Proof(false, 100, t), Proof(true, 99, t), Proof(true, 99, t + (long)(13 * (double)Stopwatch.Frequency))]);
        var stoppedStop = new FakeStopAdapter();
        var stopAccObservations = 0;
        var stoppedResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, () =>
        {
            stopAccObservations++;
            return stopAccObservations <= 2 ? (activeSession, empty) :
                (new SessionSnapshot(SessionKnowledge.NotAccelerating, expected with { Active = false }, "fixture manual stop-acc"), empty);
        }, stoppedPause, stoppedStop, CancellationToken.None);
        check(!stoppedResult.Completed && stoppedResult.Stage == "pause-proof" && stoppedStop.StopRequests == 0 && stoppedStop.ExitRequests == 0,
            "manual stop-acc after pause blocks further client shutdown");

        var observationCount = 0;
        var restartedDuringPause = () =>
        {
            observationCount++;
            return observationCount <= 2
                ? (activeSession, empty)
                : (activeSession, new GameSnapshot(true, [new(9, "Target.exe", @"C:\Target.exe", DateTime.UtcNow, "restarted during pause")], "target restarted"));
        };
        var racePause = new FakePauseAdapter([Proof(false, 100, t)]);
        var raceStop = new FakeStopAdapter();
        var raceResult = await new AutoStopWorkflow(TimeSpan.Zero, TimeSpan.Zero).RunAsync(expected, restartedDuringPause, racePause, raceStop, CancellationToken.None);
        check(raceResult.Stage == "pause-proof" && racePause.Requests == 1 && raceStop.StopRequests == 0 && raceStop.ExitRequests == 0, "target game restart after pause request blocks client stop and exit");
    }

    private static PauseProof Proof(bool paused, decimal seconds, long stamp) => new(paused, seconds, DateTime.UtcNow + Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), stamp), "fixture");

    private static async Task TestUiAutomationStop(Action<bool, string> check)
    {
        var id = "231"; var fixture = new FakeUiAutomationProvider(); var exit = new FakeVerifiedExit(); var paused = true;
        var stopped = new SessionSnapshot(SessionKnowledge.NotAccelerating, new("", "", 7788, DateTime.UtcNow, DateTime.UtcNow, false), "fixture stop proof") { StopStateConfirmed = true, StoppedGameIds = new HashSet<string>([id]) };
        var adapter = new LeiGodUiAutomationStopAdapter(7788, DateTime.UtcNow, @"C:\LeiGod\leigod.exe", "DBD", [id], fixture, () => fixture.Confirmed ? stopped : new(SessionKnowledge.Accelerating, new(id, "DBD", 7788, DateTime.UtcNow, DateTime.UtcNow, true), "running"), () => true, () => true, () => paused ? Task.FromResult(true) : Task.FromResult(false), exit, registered: false, liveReadbackValidated: true);
        check(!adapter.CanStopAcceleration, "UIA adapter remains disabled unless explicitly registered");
        try { await adapter.RequestStopAccelerationAsync(CancellationToken.None); check(false, "unregistered UIA adapter rejects stop request"); }
        catch (NotSupportedException) { check(fixture.Invocations.Count == 0, "disabled UIA adapter sends no click"); }

        var enabled = new LeiGodUiAutomationStopAdapter(7788, DateTime.UtcNow, @"C:\LeiGod\leigod.exe", "DBD", [id], fixture, () => fixture.Confirmed ? stopped : new(SessionKnowledge.Accelerating, new(id, "DBD", 7788, DateTime.UtcNow, DateTime.UtcNow, true), "running"), () => true, () => true, () => paused ? Task.FromResult(true) : Task.FromResult(false), exit, registered: true, liveReadbackValidated: true);
        check(enabled.CanStopAcceleration, "registered UIA adapter requires provider readback contract");
        await enabled.RequestStopAccelerationAsync(CancellationToken.None);
        check(fixture.Invocations.SequenceEqual(["停止加速", "确定"]) && await enabled.IsAccelerationStoppedAsync(CancellationToken.None), "UIA stop clicks unique exact control and waits for stop-acc plus normal status log proof");
        await enabled.RequestNormalExitAsync(CancellationToken.None);
        check(exit.Requests == 1, "UIA adapter delegates exit only after independent stop proof and pause checks");

        var pausedFalseExit = new FakeVerifiedExit();
        paused = false;
        var noExit = new LeiGodUiAutomationStopAdapter(7788, DateTime.UtcNow, @"C:\LeiGod\leigod.exe", "DBD", [id], fixture, () => stopped, () => true, () => true, () => Task.FromResult(false), pausedFalseExit, registered: true, liveReadbackValidated: true);
        try { await noExit.RequestNormalExitAsync(CancellationToken.None); check(false, "UIA exit requires still-paused status"); }
        catch (InvalidOperationException) { check(pausedFalseExit.Requests == 0, "pause loss prevents process-exit delegation"); }

        var contractDisabledUi = new FakeUiAutomationProvider(contractEnabled: false);
        var noContract = new LeiGodUiAutomationStopAdapter(7788, DateTime.UtcNow, @"C:\LeiGod\leigod.exe", "DBD", [id], contractDisabledUi, () => stopped, () => true, () => true, () => Task.FromResult(true), new FakeVerifiedExit(), registered: true, liveReadbackValidated: true);
        check(!noContract.CanStopAcceleration, "UIA provider readback contract is a required independent enable gate");

        var ambiguousUi = new FakeUiAutomationProvider(duplicateStop: true);
        var ambiguous = new LeiGodUiAutomationStopAdapter(7788, DateTime.UtcNow, @"C:\LeiGod\leigod.exe", "DBD", [id], ambiguousUi, () => stopped, () => true, () => true, () => Task.FromResult(true), new FakeVerifiedExit(), registered: true, liveReadbackValidated: true);
        try { await ambiguous.RequestStopAccelerationAsync(CancellationToken.None); check(false, "duplicate stop buttons are rejected"); }
        catch (InvalidOperationException) { check(ambiguousUi.Invocations.Count == 0, "ambiguous UIA stop controls fail closed before Invoke"); }
    }

    private static void TestAcceptanceGate(Action<bool, string> check)
    {
        var dir = Directory.CreateTempSubdirectory("leigod-accept-"); var path = Path.Combine(dir.FullName, "acceptance.json");
        try
        {
            check(!AutomaticAcceptanceGate.Validate(path, "11.3.2.9", "abc", "app-hash", "machine-A", out _), "automatic mode remains gated without live acceptance");
            var valid = new AutomaticAcceptanceRecord(AutomaticAcceptanceGate.RequiredProtocol, "11.3.2.9", "fixture-evidence", DateTime.UtcNow, "abc", "app-hash", "machine-A", true, true, true, true);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(valid));
            check(AutomaticAcceptanceGate.Validate(path, "11.3.2.9", "abc", "app-hash", "machine-A", out _), "complete acceptance record enables matching client/app/machine fingerprints");
            check(!AutomaticAcceptanceGate.Validate(path, "11.3.2.9", "abc", "app-hash", "machine-B", out _), "acceptance copied from another computer is rejected");
            check(!AutomaticAcceptanceGate.Validate(path, "11.3.2.9", "different-hash", "app-hash", "machine-A", out _), "client binary hash change invalidates automatic acceptance");
            check(!AutomaticAcceptanceGate.Validate(path, "11.3.2.9", "abc", "new-app-hash", "machine-A", out _), "application build change invalidates automatic acceptance");
            var partial = valid with { AutomatedPauseRequestVerified = false };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(partial));
            check(!AutomaticAcceptanceGate.Validate(path, "11.3.2.9", "abc", "app-hash", "machine-A", out _), "incomplete live acceptance cannot enable automatic mode");
        }
        finally { dir.Delete(true); }
    }

    private static void TestLegacyTaskGuard(Action<bool, string> check)
    {
        var dir = Directory.CreateTempSubdirectory("leigod-legacy-guard-");
        var script = Path.Combine(dir.FullName, "legacy-wrapper.ps1");
        var marker = Path.Combine(dir.FullName, "LeiGodAutoStop.legacy-disabled");
        const string prefix = "#requires -Version 5.1\n[CmdletBinding()]\nparam()\n\n";
        const string guard = "if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'LeiGodAutoStop.legacy-disabled')) { return }\n";
        var action = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        try
        {
            File.WriteAllText(script, prefix + guard + "Start-Sleep -Seconds 30\n"); File.WriteAllText(marker, "disabled");
            check(LegacyTaskGuard.IsSafelyDisabledWrapper(action, "-NoProfile -File \"" + script + "\""), "legacy task safe guard accepts immediate-exit wrapper with existing marker");
            File.Delete(marker);
            check(!LegacyTaskGuard.IsSafelyDisabledWrapper(action, "-NoProfile -File \"" + script + "\""), "legacy task safe guard rejects missing kill marker");
            File.WriteAllText(marker, "disabled"); File.WriteAllText(script, prefix + "Start-Sleep -Seconds 1\n" + guard);
            check(!LegacyTaskGuard.IsSafelyDisabledWrapper(action, "-NoProfile -File \"" + script + "\""), "legacy task safe guard rejects marker check after executable code");
            check(!LegacyTaskGuard.IsSafelyDisabledWrapper(@"C:\Windows\System32\cmd.exe", "-File \"" + script + "\""), "legacy task safe guard rejects non-PowerShell action");
        }
        finally { dir.Delete(true); }
    }

    private sealed class FakePauseAdapter(IReadOnlyList<PauseProof> proofs) : IPauseAdapter
    {
        private int _reads;
        public int Requests { get; private set; }
        public string Id => "offline-fake-pause";
        public int Version => 1;
        public bool CanControl(string clientVersion) => true;
        public Task RequestPauseAsync(CancellationToken ct) { Requests++; return Task.CompletedTask; }
        public Task<PauseProof> ReadPauseStateAsync(CancellationToken ct) { var index = Math.Min(_reads++, proofs.Count - 1); return Task.FromResult(proofs[index]); }
    }

    private sealed class FakeStopAdapter(bool canStop = true, bool initiallyStopped = false, bool throwStop = false, bool fallbackSuccess = true) : IStopAdapter
    {
        public int StopRequests { get; private set; }
        public int ExitRequests { get; private set; }
        public int FallbackRequests { get; private set; }
        public bool ExitVerified { get; private set; }
        public string Id => "offline-fake-stop";
        public int Version => 1;
        public bool CanStopAcceleration => canStop;
        public Task RequestStopAccelerationAsync(CancellationToken ct) { StopRequests++; if (throwStop) throw new InvalidOperationException("fixture stop timeout"); return Task.CompletedTask; }
        public Task<bool> IsAccelerationStoppedAsync(CancellationToken ct) => Task.FromResult(initiallyStopped || StopRequests > 0 && !throwStop);
        public Task RequestNormalExitAsync(CancellationToken ct) { ExitRequests++; ExitVerified = true; return Task.CompletedTask; }
        public Task RequestPausedSessionExitFallbackAsync(CancellationToken ct) { FallbackRequests++; if (!fallbackSuccess) throw new InvalidOperationException("fixture fallback gate rejected"); ExitVerified = true; return Task.CompletedTask; }
        public Task<bool> AreOwnedClientProcessesGoneAsync(CancellationToken ct) => Task.FromResult(ExitVerified);
    }

    private sealed class FakeVerifiedExit : ILeiGodVerifiedExitController
    {
        public int Requests { get; private set; }
        public Task RequestVerifiedExitAsync(CancellationToken ct) { Requests++; return Task.CompletedTask; }
        public Task<bool> AreOwnedProcessesGoneAsync(CancellationToken ct) => Task.FromResult(Requests > 0);
    }

    private sealed class FakeUiAutomationProvider(bool contractEnabled = true, bool duplicateStop = false) : ILeiGodUiAutomationProvider
    {
        public bool ReadbackContractEnabled => contractEnabled;
        public bool DuplicateStop => duplicateStop;
        public bool Confirmed { get; private set; }
        public List<string> Invocations { get; } = [];
        private bool _dialog;
        public bool ValidateClientIdentity(int processId, DateTime startTime, string executablePath) => processId == 7788 && executablePath == @"C:\LeiGod\leigod.exe";
        public int GetMainWindowHandle(int processId) => 55;
        public IReadOnlyList<UiAutomationWindowSnapshot> ReadProcessWindows(int processId)
        {
            var stop = new UiAutomationElementSnapshot(processId, 55, "LeiGod", "停止加速", ControlType.Button.ProgrammaticName, "stop", true, true);
            var elements = DuplicateStop ? new[] { stop, stop with { AutomationId = "stop2" } } : new[] { stop };
            var result = new List<UiAutomationWindowSnapshot> { new(processId, 55, "LeiGod", false, elements) };
            if (_dialog) result.Add(new(processId, 66, "Confirm", true, [new(processId, 66, "Confirm", "当前正在加速 DBD，是否确定要停止加速?", ControlType.Text.ProgrammaticName, "message", true, false), new(processId, 66, "Confirm", "确定", ControlType.Button.ProgrammaticName, "confirm", true, true)]));
            return result;
        }
        public void InvokeExact(UiAutomationElementSnapshot target)
        {
            Invocations.Add(target.Name);
            if (target.Name == "停止加速") _dialog = true;
            else if (target.Name == "确定") { Confirmed = true; _dialog = false; }
        }
    }
}

