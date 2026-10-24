param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(2, 10)]
    [string[]]$SourceSingleGameReports,
    [Parameter(Mandatory = $true)]
    [string]$SourceConcurrentReport,
    [Parameter(Mandatory = $true)]
    [ValidateCount(2, 10)]
    [string[]]$TargetSingleGameReports,
    [Parameter(Mandatory = $true)]
    [string]$TargetConcurrentReport,
    [string]$OutputPath = (Join-Path (Get-Location) ('LeiGodAutoStop-transfer-comparison-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))
)

$ErrorActionPreference = 'Stop'

function Read-Report([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $report = Get-Content -LiteralPath $resolved -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if (-not $report.Computer -or -not $report.PackageIdentity -or -not $report.Criteria) {
        throw "验收记录缺少电脑名、包身份或 criteria：$resolved"
    }
    [pscustomobject]@{ Path = $resolved; Report = $report }
}

function Require-Criteria([object]$Report, [string[]]$Names, [string]$Label) {
    $criteria = @($Report.Criteria)
    $missing = @($Names | Where-Object { $name = $_; @($criteria | Where-Object { $_.Name -eq $name }).Count -eq 0 })
    $failed = @($criteria | Where-Object { $_.Result -ne 'PASS' })
    if ($missing.Count -gt 0 -or $failed.Count -gt 0 -or $Report.Verdict -ne 'PASS') {
        $detail = @()
        if ($missing.Count -gt 0) { $detail += "缺少项目：$($missing -join '、')" }
        if ($failed.Count -gt 0) { $detail += "未通过项目：$(($failed | ForEach-Object { "$($_.Name)=$($_.Result)" }) -join '；')" }
        if ($Report.Verdict -ne 'PASS') { $detail += "报告结论=$($Report.Verdict)" }
        throw "$Label 未通过：$($detail -join '；')"
    }
}

function Check-Machine([string]$Label, [string[]]$SinglePaths, [string]$ConcurrentPath) {
    $single = @($SinglePaths | ForEach-Object { Read-Report $_ })
    $concurrent = Read-Report $ConcurrentPath
    $reports = @($single) + @($concurrent)
    $computerNames = @($reports | ForEach-Object { $_.Report.Computer } | Select-Object -Unique)
    if ($computerNames.Count -ne 1) { throw "$Label 的多份验收记录来自不同电脑：$($computerNames -join ', ')" }
    $gameNames = @($single | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.Report.GameProcessName) } | Where-Object { $_ } | Select-Object -Unique)
    if ($gameNames.Count -lt 2) { throw "$Label 至少需要两款不同游戏的单游戏验收记录；当前=$($gameNames -join ', ')" }
    foreach ($item in $single) {
        Require-Criteria $item.Report @('首次检查','游戏运行期间不暂停','重开游戏取消倒计时','退出游戏后自动暂停','未加速程序不影响','雷神关闭时监控退出','雷神重开时监控启动') "$Label / $($item.Report.GameProcessName)"
    }
    Require-Criteria $concurrent.Report @('两个加速游戏均运行且被识别','退出一个后另一个阻止倒计时','全部退出后完整 120 秒自动暂停','未加速程序运行时不阻止暂停') "$Label / 双游戏并行"
    [pscustomobject]@{
        Computer = $computerNames[0]
        Games = $gameNames
        SingleGameReports = @($single | ForEach-Object { $_.Path })
        ConcurrentReport = $concurrent.Path
        PackageIdentities = @($reports | ForEach-Object { $_.Report.PackageIdentity })
    }
}

try {
    $source = Check-Machine '源电脑' $SourceSingleGameReports $SourceConcurrentReport
    $target = Check-Machine '目标电脑' $TargetSingleGameReports $TargetConcurrentReport
    if ($source.Computer -ieq $target.Computer) { throw '源电脑和目标电脑的 Computer 名相同，无法证明是两台不同电脑。' }

    $allIdentities = @($source.PackageIdentities) + @($target.PackageIdentities)
    $exeHashes = @($allIdentities | ForEach-Object { [string]$_.ExecutableSha256.ToUpperInvariant() } | Select-Object -Unique)
    $treeHashes = @($allIdentities | ForEach-Object { [string]$_.PackageTreeSha256.ToUpperInvariant() } | Select-Object -Unique)
    if ($exeHashes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($exeHashes[0])) { throw '两台电脑的 EXE SHA-256 不一致或缺失。' }
    if ($treeHashes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($treeHashes[0])) { throw '两台电脑的 PackageTreeSha256 不一致或缺失。' }

    $result = [pscustomobject]@{
        ComparedAt = Get-Date
        Verdict = 'PASS'
        SourceComputer = $source.Computer
        TargetComputer = $target.Computer
        GameNamesMayDiffer = $true
        ExecutableSha256 = $exeHashes[0]
        PackageTreeSha256 = $treeHashes[0]
        SourceEvidence = $source
        TargetEvidence = $target
        Interpretation = 'PASS 表示两台不同电脑都各自完成两款游戏单测与双游戏并行验收，且使用完全相同的软件包。两台电脑仍各自使用本机雷神、账号、客户端路径和游戏路径；本报告汇总既有现场验收 JSON，不替代原始记录。'
    }
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Host "跨电脑对比通过：$($source.Computer) ↔ $($target.Computer)"
    Write-Host "EXE SHA-256：$($exeHashes[0])"
    Write-Host "包内容指纹：$($treeHashes[0])"
    Write-Host "汇总记录：$OutputPath"
} catch {
    [Console]::Error.WriteLine("跨电脑对比未通过：$($_.Exception.Message)")
    exit 1
}
