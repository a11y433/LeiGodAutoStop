param(
    [string]$GameProcessName,
    [string[]]$UnacceleratedProcessNames = @(),
    [ValidateRange(120, 3600)][int]$IdleSeconds = 120,
    [string]$OutputPath = (Join-Path (Get-Location) ('LeiGodAutoStop-acceptance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))
)

$ErrorActionPreference = 'Stop'
$records = [System.Collections.Generic.List[object]]::new()

function Get-PackageIdentity([string]$packageRoot = (Split-Path -Parent $PSScriptRoot)) {
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object { $_.Name -notmatch '^LeiGodAutoStop(?:-concurrent)?-acceptance-.*\.json$' }) {
        $relative = $file.FullName.Substring($packageRoot.Length).TrimStart('\').Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        $entries.Add("$hash  $relative")
    }
    $lines = $entries.ToArray()
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $treeHash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') } finally { $sha.Dispose() }
    $manifest = Join-Path $packageRoot 'SHA256.txt'
    $exeHash = if (Test-Path -LiteralPath $manifest) { ((Get-Content -LiteralPath $manifest -Raw) -split '\s+')[0].Trim() } else { $null }
    [pscustomobject]@{ PackageRoot = $packageRoot; ExecutableSha256 = $exeHash; PackageTreeSha256 = $treeHash }
}

$packageIdentity = Get-PackageIdentity

function Get-Snapshot([string]$Stage) {
    $names = @('LeiGodAutoStop', 'leigod', 'leishenSdk')
    if ($GameProcessName) { $names += $GameProcessName.Replace('.exe', '') }
    if ($UnacceleratedProcessNames) { $names += @($UnacceleratedProcessNames | ForEach-Object { $_.Replace('.exe', '') }) }
    $processes = foreach ($name in $names | Select-Object -Unique) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            $process = $_
            $commandLine = $null
            try { $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction Stop).CommandLine } catch { }
            [pscustomobject]@{ Name = $process.ProcessName; PID = $process.Id; ParentPID = $null; StartTime = $process.StartTime; Responding = $process.Responding; Window = $process.MainWindowTitle; CommandLine = $commandLine }
        }
    }
    $taskName = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'LeiGodAutoStopFollow-*' } | Select-Object -First 1 -ExpandProperty TaskName
    $task = $null
    if ($taskName) {
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
        $taskDefinition = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        $taskAction = if ($taskDefinition) { @($taskDefinition.Actions | ForEach-Object { @{ Execute = $_.Execute; Arguments = $_.Arguments; WorkingDirectory = $_.WorkingDirectory } }) } else { @() }
        $task = [pscustomobject]@{ LastRunTime = $taskInfo.LastRunTime; LastTaskResult = $taskInfo.LastTaskResult; Action = $taskAction }
    }
    $log = Join-Path $env:LOCALAPPDATA 'LeiGodAutoStop\logs'
    $latestLog = Get-ChildItem -LiteralPath $log -Filter '*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $tail = if ($latestLog) { @(Get-Content -LiteralPath $latestLog.FullName -Tail 20 -ErrorAction SilentlyContinue) } else { @() }
    $pauseEvents = if ($latestLog) { @(Get-Content -LiteralPath $latestLog.FullName -Tail 500 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'pause-only workflow (completed|failed)|pause-only pause request submitted' } | Select-Object -Last 12) } else { @() }
    $latestLogPath = if ($latestLog) { $latestLog.FullName } else { $null }
    $latestLogWrite = if ($latestLog) { $latestLog.LastWriteTime } else { $null }
    $diagnosticsPath = Join-Path $env:LOCALAPPDATA 'LeiGodAutoStop\first-use-check.json'
    $diagnostics = $null
    $diagnosticsWriteTime = $null
    if (Test-Path -LiteralPath $diagnosticsPath) {
        $diagnosticsWriteTime = (Get-Item -LiteralPath $diagnosticsPath).LastWriteTime
        try { $diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw | ConvertFrom-Json } catch { }
    }
    $records.Add([pscustomobject]@{ Stage = $Stage; Time = (Get-Date); Processes = @($processes); FollowTask = $task; Diagnostics = $diagnostics; DiagnosticsWriteTime = $diagnosticsWriteTime; LatestLog = $latestLogPath; LatestLogWrite = $latestLogWrite; PauseEventsInTail = $pauseEvents; LogTail = $tail })
}

Write-Host '阶段 0：请在目标电脑上启动雷神和本应用，点击“首次检查环境”，确认检查结果后按回车。'
Read-Host | Out-Null
Get-Snapshot 'preflight'

Write-Host '阶段 1：请在雷神中加速并启动目标游戏，进入主界面后保持运行，按回车。'
Read-Host | Out-Null
Get-Snapshot 'accelerated-game-running'
Write-Host '保持游戏运行，连续观察 130 秒。'
1..26 | ForEach-Object { Start-Sleep -Seconds 5; Get-Snapshot 'accelerated-game-running-sample' }

Write-Host '阶段 2：请只退出游戏，不点击雷神“停止加速”，按回车开始观察。'
Read-Host | Out-Null
$firstExit = Get-Date
Get-Snapshot 'first-game-exit'
Write-Host '等待 30 秒，确认尚未到暂停阈值……'
1..6 | ForEach-Object { Start-Sleep -Seconds 5; Get-Snapshot 'first-idle-sample' }

Write-Host '阶段 2a：请重新通过雷神启动同一款加速游戏，进入主界面并保持运行，按回车。'
Read-Host | Out-Null
Get-Snapshot 'countdown-cancel-restarted'
Write-Host '保持游戏运行 30 秒，确认重开后暂停倒计时已取消……'
1..6 | ForEach-Object { Start-Sleep -Seconds 5; Get-Snapshot 'countdown-cancel-running-sample' }

if ($UnacceleratedProcessNames.Count -gt 0) {
    Write-Host "保持这些未加速程序运行至暂停确认：$($UnacceleratedProcessNames -join ', ')"
}
Write-Host '阶段 2b：再次只退出游戏，不停止雷神加速；按回车后开始完整 120 秒验收。'
Read-Host | Out-Null
$idleStart = Get-Date
Get-Snapshot 'game-exited-start'
Write-Host "等待 $IdleSeconds 秒，只读观察应用日志和进程状态……"
for ($elapsed = 0; $elapsed -lt $IdleSeconds; $elapsed += 5) { Start-Sleep -Seconds 5; Get-Snapshot 'idle-sample' }
Get-Snapshot 'after-idle-threshold'

Write-Host '阶段 3：请关闭雷神客户端，等待约 10 秒后按回车；不要结束本应用以外的进程。'
Read-Host | Out-Null
Start-Sleep -Seconds 10
Get-Snapshot 'leigod-closed'

Write-Host '阶段 4：重新打开雷神，等待自动监控进程和托盘图标重新出现后按回车。后台跟随启动会缩到托盘，不一定显示主窗口。'
Read-Host | Out-Null
Get-Snapshot 'leigod-reopened'

$appName = 'LeiGodAutoStop'
$gameName = if ($GameProcessName) { [IO.Path]::GetFileNameWithoutExtension($GameProcessName) } else { $null }
$gameSamples = @($records | Where-Object { $_.Stage -like 'accelerated-game-running*' })
$gameSeen = $gameName -and @($gameSamples | Where-Object { @($_.Processes | Where-Object { $_.Name -ieq $gameName }).Count -gt 0 }).Count -gt 0
$gameRecognized = @($gameSamples | ForEach-Object { $_.LogTail } | Where-Object { $_ -match 'pause-only observation state=加速游戏仍运行' }).Count -gt 0
$restartSamples = @($records | Where-Object { $_.Stage -like 'countdown-cancel-*' })
$restartSeen = $gameName -and @($restartSamples | Where-Object { @($_.Processes | Where-Object { $_.Name -ieq $gameName }).Count -gt 0 }).Count -gt 0
$restartRecognized = @($restartSamples | ForEach-Object { $_.LogTail } | Where-Object { $_ -match 'pause-only observation state=加速游戏仍运行' }).Count -gt 0
$startRunning = @($records | Where-Object { $_.Stage -eq 'accelerated-game-running' } | Select-Object -First 1)
$endRunning = @($records | Where-Object { $_.Stage -eq 'accelerated-game-running-sample' } | Select-Object -Last 1)
$runningFrom = if ($startRunning.Count) { [datetime]$startRunning[0].Time } else { [datetime]::MaxValue }
$runningTo = if ($endRunning.Count) { [datetime]$endRunning[0].Time } else { [datetime]::MinValue }
$runningCompletions = @($gameSamples | ForEach-Object { $_.PauseEventsInTail } | Where-Object {
    $_ -match 'pause-only workflow completed' -and $_ -match '^(?<time>\S+)' -and ([datetimeoffset]::Parse($Matches.time).LocalDateTime -ge $runningFrom) -and ([datetimeoffset]::Parse($Matches.time).LocalDateTime -le $runningTo)
})
$idleStartRecord = @($records | Where-Object { $_.Stage -eq 'game-exited-start' } | Select-Object -First 1)
$idleStarted = if ($idleStartRecord.Count) { [datetime]$idleStartRecord[0].Time } else { [datetime]::MaxValue }
$idleCompletions = @($records | Where-Object { $_.Stage -in @('idle-sample', 'after-idle-threshold') } | ForEach-Object { $_.PauseEventsInTail } | Where-Object {
    $_ -match 'pause-only workflow completed' -and $_ -match 'pause_request_sent=True' -and $_ -match '^(?<time>\S+)' -and ([datetimeoffset]::Parse($Matches.time).LocalDateTime -ge $idleStarted.AddSeconds($IdleSeconds - 5))
})
$earlyRestartCompletions = @($records | Where-Object { $_.Stage -in @('first-idle-sample', 'countdown-cancel-restarted', 'countdown-cancel-running-sample') } | ForEach-Object { $_.PauseEventsInTail } | Where-Object {
    $_ -match 'pause-only workflow completed' -and $_ -match 'pause_request_sent=True'
})
$idleEvidenceSamples = @($records | Where-Object { $_.Stage -in @('idle-sample', 'after-idle-threshold') })
$unacceleratedNames = @($UnacceleratedProcessNames | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
$unacceleratedPresentThroughout = $unacceleratedNames.Count -gt 0 -and $idleEvidenceSamples.Count -gt 0 -and @($idleEvidenceSamples | Where-Object {
    $seenNames = @($_.Processes | ForEach-Object { $_.Name })
    @($unacceleratedNames | Where-Object { $_ -notin $seenNames }).Count -gt 0
}).Count -eq 0
$closed = @($records | Where-Object { $_.Stage -eq 'leigod-closed' } | Select-Object -First 1)
$reopened = @($records | Where-Object { $_.Stage -eq 'leigod-reopened' } | Select-Object -First 1)
$closedApps = if ($closed.Count) { @($closed[0].Processes | Where-Object { $_.Name -ieq $appName }) } else { @() }
$closedMonitor = @($closedApps | Where-Object { $_.CommandLine -match '--follow-leigod' -and $_.CommandLine -notmatch '--watch-leigod' })
$closedWatcher = @($closedApps | Where-Object { $_.CommandLine -match '--watch-leigod' })
$reopenedApps = if ($reopened.Count) { @($reopened[0].Processes | Where-Object { $_.Name -ieq $appName }) } else { @() }
$reopenedMonitor = @($reopenedApps | Where-Object { $_.CommandLine -match '--follow-leigod' -and $_.CommandLine -notmatch '--watch-leigod' })
$diag = if ($records[0].Diagnostics) { $records[0].Diagnostics } else { $null }
$blockingDiagnostics = if ($diag -and $diag.Checks) { @($diag.Checks | Where-Object { $_.Key -in @('logs','format','account','pause-control') -and $_.Severity -ne 0 }) } else { @() }
$diagnosticsFresh = $records[0].DiagnosticsWriteTime -and ([datetime]$records[0].Time - [datetime]$records[0].DiagnosticsWriteTime).TotalMinutes -le 10
$criteria = @(
    [pscustomobject]@{ Name = '首次检查'; Result = if (-not $diag -or -not $diagnosticsFresh) { 'NOT_VERIFIED' } elseif ($blockingDiagnostics.Count -gt 0) { 'FAIL' } else { 'PASS' }; Evidence = if ($diag) { "$($diag.Summary)；报告新鲜度通过=$diagnosticsFresh" } else { '未找到首次检查报告' } },
    [pscustomobject]@{ Name = '游戏运行期间不暂停'; Result = if (-not $GameProcessName -or -not $gameSamples.Count) { 'NOT_VERIFIED' } elseif (-not $gameSeen -or -not $gameRecognized -or $runningCompletions.Count -gt 0) { 'FAIL' } else { 'PASS' }; Evidence = "游戏进程采样=$($gameSamples.Count)，发现运行进程=$([bool]$gameSeen)，应用识别运行=$gameRecognized，运行期间暂停完成事件=$($runningCompletions.Count)" },
    [pscustomobject]@{ Name = '重开游戏取消倒计时'; Result = if (-not $restartSamples.Count) { 'NOT_VERIFIED' } elseif (-not $restartSeen -or -not $restartRecognized -or $earlyRestartCompletions.Count -gt 0) { 'FAIL' } else { 'PASS' }; Evidence = "重开阶段进程采样=$($restartSamples.Count)，发现游戏进程=$([bool]$restartSeen)，应用识别运行=$restartRecognized，暂停完成事件=$($earlyRestartCompletions.Count)；第二次退出后仅在完整等待阈值后计为暂停通过" },
    [pscustomobject]@{ Name = '退出游戏后自动暂停'; Result = if ($idleCompletions.Count -gt 0) { 'PASS' } elseif ($idleStartRecord.Count -eq 0) { 'NOT_VERIFIED' } else { 'FAIL' }; Evidence = "游戏退出后已确认请求并稳定读回的完成事件=$($idleCompletions.Count)" },
    [pscustomobject]@{ Name = '未加速程序不影响'; Result = if ($unacceleratedNames.Count -eq 0) { 'NOT_VERIFIED' } elseif (-not $unacceleratedPresentThroughout) { 'FAIL' } elseif ($idleCompletions.Count -gt 0) { 'PASS' } else { 'FAIL' }; Evidence = "要求持续运行=$($unacceleratedNames -join ', ')；覆盖完整空闲采样=$unacceleratedPresentThroughout；完成暂停事件=$($idleCompletions.Count)" },
    [pscustomobject]@{ Name = '雷神关闭时监控退出'; Result = if (-not $closed.Count -or -not $closedApps.Count) { 'NOT_VERIFIED' } elseif ($closedMonitor.Count -eq 0 -and $closedWatcher.Count -gt 0) { 'PASS' } else { 'FAIL' }; Evidence = "关闭阶段监控进程=$($closedMonitor.Count)，跟随助手=$($closedWatcher.Count)" },
    [pscustomobject]@{ Name = '雷神重开时监控启动'; Result = if (-not $reopened.Count) { 'NOT_VERIFIED' } elseif ($reopenedMonitor.Count -gt 0) { 'PASS' } else { 'FAIL' }; Evidence = "重开阶段监控进程=$($reopenedMonitor.Count)" }
)
$verdict = if (@($criteria | Where-Object Result -eq 'FAIL').Count -gt 0) { 'FAIL' } elseif (@($criteria | Where-Object Result -eq 'NOT_VERIFIED').Count -gt 0) { 'NOT_VERIFIED' } else { 'PASS' }
$result = [pscustomobject]@{
    StartedAt = $records[0].Time
    FinishedAt = Get-Date
    Computer = $env:COMPUTERNAME
    User = $env:USERNAME
    PackageIdentity = $packageIdentity
    GameProcessName = $GameProcessName
    UnacceleratedProcessNames = $UnacceleratedProcessNames
    IdleSeconds = $IdleSeconds
    Records = $records.ToArray()
    Criteria = $criteria
    Verdict = $verdict
    Interpretation = @(
        'PASS 只表示脚本在目标电脑采集到各阶段所需的本地进程、诊断与应用日志证据。',
        '若阶段日志缺失或 CIM 进程命令行不可读，对应项会是 NOT_VERIFIED；请连同应用窗口截图人工复核。',
        '脚本只读取状态，不暂停、恢复、关闭或结束任何应用进程。'
    )
}
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "验收记录已写入：$OutputPath"
Write-Host "验收结论：$verdict"
$criteria | Format-Table -AutoSize
