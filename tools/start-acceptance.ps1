param(
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$UnacceleratedProcessName = 'notepad.exe'
)

$ErrorActionPreference = 'Stop'
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$verify = Join-Path $PackageRoot 'tools\verify-package.ps1'
$single = Join-Path $PackageRoot 'tools\cross-computer-acceptance.ps1'
$concurrent = Join-Path $PackageRoot 'tools\cross-computer-concurrent-acceptance.ps1'
$exe = Join-Path $PackageRoot 'LeiGodAutoStop.exe'

foreach ($path in @($verify, $single, $concurrent, $exe)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "包文件缺失：$path" }
}

Write-Host '正在校验当前解压目录……' -ForegroundColor Cyan
& $verify $PackageRoot | Format-Table -AutoSize

if (-not (Get-Process -Name 'LeiGodAutoStop' -ErrorAction SilentlyContinue)) {
    Write-Host '正在启动雷神自动暂停时长；若出现 Windows 权限提示，请选择“是”。' -ForegroundColor Cyan
    Start-Process -FilePath $exe -WorkingDirectory $PackageRoot
    Start-Sleep -Seconds 3
}

function Read-Required([string]$Prompt) {
    do { $value = (Read-Host $Prompt).Trim() } while ([string]::IsNullOrWhiteSpace($value))
    return $value
}

$game1 = Read-Required '输入第一款加速游戏的进程名（例如 r5apex_dx12.exe）'
$game2 = Read-Required '输入第二款加速游戏的进程名（例如 Terraria.exe）'
if ($game1 -ieq $game2) { throw '两款游戏的进程名必须不同。' }
$background = (Read-Host "输入要保持运行的未加速后台进程名（直接回车使用 $UnacceleratedProcessName）").Trim()
if ([string]::IsNullOrWhiteSpace($background)) { $background = $UnacceleratedProcessName }

Write-Host "`n开始第一款游戏的完整验收……" -ForegroundColor Yellow
& $single -GameProcessName $game1 -UnacceleratedProcessNames $background
if ($LASTEXITCODE -ne 0) { throw '第一款游戏验收脚本失败。' }

Write-Host "`n开始第二款游戏的完整验收……" -ForegroundColor Yellow
& $single -GameProcessName $game2 -UnacceleratedProcessNames $background
if ($LASTEXITCODE -ne 0) { throw '第二款游戏验收脚本失败。' }

Write-Host "`n开始两款游戏同时加速的验收……" -ForegroundColor Yellow
& $concurrent -AcceleratedGameProcessNames @($game1, $game2) -UnacceleratedProcessNames @($background)
if ($LASTEXITCODE -ne 0) { throw '双游戏并行验收脚本失败。' }

Write-Host "`n验收采集完成。请把本目录生成的两个单游戏 JSON 和一个 concurrent JSON 一起保存；不要手工修改 Verdict。" -ForegroundColor Green
