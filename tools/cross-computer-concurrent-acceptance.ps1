param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(2, 2)]
    [string[]]$AcceleratedGameProcessNames,
    [Parameter(Mandatory = $true)]
    [ValidateCount(1, 10)]
    [string[]]$UnacceleratedProcessNames,
    [ValidateRange(120, 3600)]
    [int]$IdleSeconds = 120,
    [string]$OutputPath = (Join-Path (Get-Location) ('LeiGodAutoStop-concurrent-acceptance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))
)

$ErrorActionPreference = 'Stop'
$accelerated = @($AcceleratedGameProcessNames | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
$unaccelerated = @($UnacceleratedProcessNames | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
if (@($accelerated | Select-Object -Unique).Count -ne 2) { throw 'Provide two different accelerated game process names.' }
if (@($unaccelerated | Select-Object -Unique).Count -ne $unaccelerated.Count) { throw 'Unaccelerated process names must be unique.' }
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
    $names = @('LeiGodAutoStop', 'leigod', 'leishenSdk') + $accelerated + $unaccelerated
    $processes = foreach ($name in $names | Select-Object -Unique) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            $process = $_
            $commandLine = $null
            try { $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction Stop).CommandLine } catch { }
            [pscustomobject]@{ Name = $process.ProcessName; PID = $process.Id; StartTime = $process.StartTime; Responding = $process.Responding; Window = $process.MainWindowTitle; CommandLine = $commandLine }
        }
    }
    $logDirectory = Join-Path $env:LOCALAPPDATA 'LeiGodAutoStop\logs'
    $latestLog = Get-ChildItem -LiteralPath $logDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $tail = if ($latestLog) { @(Get-Content -LiteralPath $latestLog.FullName -Tail 20 -ErrorAction SilentlyContinue) } else { @() }
    $pauseEvents = if ($latestLog) { @(Get-Content -LiteralPath $latestLog.FullName -Tail 500 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'pause-only workflow (completed|failed)|pause-only pause request submitted' } | Select-Object -Last 12) } else { @() }
    $records.Add([pscustomobject]@{ Stage = $Stage; Time = Get-Date; Processes = @($processes); LatestLog = if ($latestLog) { $latestLog.FullName } else { $null }; LatestLogWrite = if ($latestLog) { $latestLog.LastWriteTime } else { $null }; PauseEventsInTail = $pauseEvents; LogTail = $tail })
}

function Sample([string]$Stage, [int]$Seconds) {
    for ($elapsed = 0; $elapsed -lt $Seconds; $elapsed += 5) { Start-Sleep -Seconds 5; Get-Snapshot $Stage }
}

Write-Host '先确认首次环境检查通过、监控已启用，账户处于计时状态。保持指定的未加速程序在整个验收中运行。'
Write-Host "未加速进程：$($UnacceleratedProcessNames -join ', ')"
Read-Host '准备好后按回车' | Out-Null
Get-Snapshot 'preflight'

Write-Host "阶段 1：在雷神中同时加速并启动这两个游戏：$($AcceleratedGameProcessNames -join ', ')；进入可运行界面后按回车。"
Read-Host | Out-Null
Get-Snapshot 'both-accelerated-running'
Write-Host '保持两个游戏运行 130 秒。'
Sample 'both-accelerated-running-sample' 130

Write-Host "阶段 2：只退出第一个游戏 $($AcceleratedGameProcessNames[0])，保持第二个游戏和未加速程序运行，然后按回车。"
Read-Host | Out-Null
Get-Snapshot 'first-game-exited'
Write-Host '保持第二个游戏运行 60 秒。'
Sample 'second-game-still-running-sample' 60

Write-Host "阶段 3：现在只退出第二个游戏 $($AcceleratedGameProcessNames[1])，未加速程序继续运行；按回车开始完整 $IdleSeconds 秒倒计时验收。"
Read-Host | Out-Null
$allExitedAt = Get-Date
Get-Snapshot 'all-accelerated-games-exited'
Sample 'all-exited-idle-sample' $IdleSeconds
Get-Snapshot 'after-idle-threshold'

$bothStages = @($records | Where-Object Stage -in @('both-accelerated-running', 'both-accelerated-running-sample'))
$bothProcessesSeen = $bothStages.Count -gt 0 -and @($bothStages | Where-Object {
    $seen = @($_.Processes | ForEach-Object Name)
    @($accelerated | Where-Object { $_ -notin $seen }).Count -gt 0
}).Count -eq 0
$bothRecognized = @($bothStages | ForEach-Object LogTail | Where-Object { $_ -match 'pause-only observation state=加速游戏仍运行' }).Count -gt 0
$firstExitStages = @($records | Where-Object Stage -in @('first-game-exited', 'second-game-still-running-sample'))
$remainingSeen = $firstExitStages.Count -gt 0 -and @($firstExitStages | Where-Object { @($_.Processes | Where-Object Name -ieq $accelerated[1]).Count -eq 0 }).Count -eq 0
$firstGoneThroughout = $firstExitStages.Count -gt 0 -and @($firstExitStages | Where-Object { @($_.Processes | Where-Object Name -ieq $accelerated[0]).Count -gt 0 }).Count -eq 0
$remainingRecognized = @($firstExitStages | ForEach-Object LogTail | Where-Object { $_ -match 'pause-only observation state=加速游戏仍运行' }).Count -gt 0
$earlyPause = @($bothStages + $firstExitStages | ForEach-Object PauseEventsInTail | Where-Object { $_ -match 'pause-only workflow completed' -and $_ -match 'pause_request_sent=True' }).Count -gt 0
$idleStages = @($records | Where-Object Stage -in @('all-exited-idle-sample', 'after-idle-threshold'))
$allGone = $idleStages.Count -gt 0 -and @($idleStages | Where-Object { @($_.Processes | Where-Object { $_.Name -in $accelerated }).Count -gt 0 }).Count -eq 0
$unacceleratedPresent = $idleStages.Count -gt 0 -and @($idleStages | Where-Object {
    $seen = @($_.Processes | ForEach-Object Name)
    @($unaccelerated | Where-Object { $_ -notin $seen }).Count -gt 0
}).Count -eq 0
$idleCompletions = @($idleStages | ForEach-Object PauseEventsInTail | Where-Object {
    $_ -match 'pause-only workflow completed' -and $_ -match 'pause_request_sent=True' -and $_ -match '^(?<time>\S+)' -and ([datetimeoffset]::Parse($Matches.time).LocalDateTime -ge $allExitedAt.AddSeconds($IdleSeconds - 5))
})
$criteria = @(
    [pscustomobject]@{ Name = '两个加速游戏均运行且被识别'; Result = if (-not $bothStages.Count) { 'NOT_VERIFIED' } elseif (-not $bothProcessesSeen -or -not $bothRecognized -or $earlyPause) { 'FAIL' } else { 'PASS' }; Evidence = "进程均观察到=$bothProcessesSeen；应用识别加速会话=$bothRecognized；提前暂停=$earlyPause" },
    [pscustomobject]@{ Name = '退出一个后另一个阻止倒计时'; Result = if (-not $firstExitStages.Count) { 'NOT_VERIFIED' } elseif (-not $firstGoneThroughout -or -not $remainingSeen -or -not $remainingRecognized -or $earlyPause) { 'FAIL' } else { 'PASS' }; Evidence = "第一个进程全程已退出=$firstGoneThroughout；第二个持续运行=$remainingSeen；应用继续识别加速=$remainingRecognized；提前暂停=$earlyPause" },
    [pscustomobject]@{ Name = '全部退出后完整 120 秒自动暂停'; Result = if (-not $allGone) { 'FAIL' } elseif ($idleCompletions.Count -gt 0) { 'PASS' } else { 'FAIL' }; Evidence = "空闲阶段加速游戏均退出=$allGone；完成事件=$($idleCompletions.Count)；阈值=$IdleSeconds 秒" },
    [pscustomobject]@{ Name = '未加速程序运行时不阻止暂停'; Result = if (-not $unacceleratedPresent) { 'FAIL' } elseif ($idleCompletions.Count -gt 0) { 'PASS' } else { 'FAIL' }; Evidence = "未加速进程=$($UnacceleratedProcessNames -join ', ')；完整空闲阶段均存在=$unacceleratedPresent；暂停完成=$($idleCompletions.Count)" }
)
$verdict = if (@($criteria | Where-Object Result -eq 'FAIL').Count -gt 0) { 'FAIL' } elseif (@($criteria | Where-Object Result -eq 'NOT_VERIFIED').Count -gt 0) { 'NOT_VERIFIED' } else { 'PASS' }
$result = [pscustomobject]@{ StartedAt = $records[0].Time; FinishedAt = Get-Date; Computer = $env:COMPUTERNAME; User = $env:USERNAME; PackageIdentity = $packageIdentity; AcceleratedGameProcessNames = $AcceleratedGameProcessNames; UnacceleratedProcessNames = $UnacceleratedProcessNames; IdleSeconds = $IdleSeconds; Records = $records.ToArray(); Criteria = $criteria; Verdict = $verdict; Interpretation = @('必须在两台电脑分别运行；本机结果不能替代目标机。', '比较 PackageIdentity.PackageTreeSha256 和 ExecutableSha256，可确认两台电脑使用相同解压包内容。', '脚本只读取本机进程和日志，不修改账户、不关闭进程。', '只有雷神账户暂停状态及稳定余额读回与脚本完成事件一致，才可人工确认真实暂停。') }
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "验收记录已写入：$OutputPath"
Write-Host "验收结论：$verdict"
$criteria | Format-Table -AutoSize
