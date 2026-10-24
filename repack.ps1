param(
    [string]$PublishedExePath = (Join-Path $PSScriptRoot 'publish\LeiGodAutoStop.exe'),
    [string]$ZipPath = (Join-Path $PSScriptRoot ('dist\LeiGodAutoStop_' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip'))
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $PublishedExePath).Path
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$zip = [IO.Path]::GetFullPath($ZipPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $zip) -Force | Out-Null
$pkg = Join-Path (Split-Path -Parent $zip) ([IO.Path]::GetFileNameWithoutExtension($zip))
if ((Test-Path -LiteralPath $pkg) -or (Test-Path -LiteralPath $zip)) { throw '发布目录或 ZIP 已存在；请使用新的时间戳名称。' }
New-Item -ItemType Directory -Path $pkg | Out-Null
Copy-Item $exe (Join-Path $pkg 'LeiGodAutoStop.exe') -Force
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') (Join-Path $pkg 'LICENSE') -Force
Copy-Item (Join-Path $PSScriptRoot 'DOTNET-LICENSE.txt') (Join-Path $pkg 'DOTNET-LICENSE.txt') -Force
Copy-Item (Join-Path $PSScriptRoot 'ThirdPartyNotices.txt') (Join-Path $pkg 'ThirdPartyNotices.txt') -Force
Set-Content (Join-Path $pkg 'SHA256.txt') ("$hash  LeiGodAutoStop.exe") -Encoding UTF8
$toolPackage = Join-Path $pkg 'tools'
New-Item -ItemType Directory -Path $toolPackage -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'tools\verify-package.ps1') $toolPackage -Force
Copy-Item (Join-Path $PSScriptRoot 'tools\game-mappings.ps1') $toolPackage -Force
Copy-Item (Join-Path $PSScriptRoot 'tools\cross-computer-acceptance.ps1') $toolPackage -Force
Copy-Item (Join-Path $PSScriptRoot 'tools\cross-computer-concurrent-acceptance.ps1') $toolPackage -Force
Copy-Item (Join-Path $PSScriptRoot 'tools\compare-transfer-acceptance.ps1') $toolPackage -Force
Copy-Item (Join-Path $PSScriptRoot 'tools\start-acceptance.ps1') $toolPackage -Force
Copy-Item (Join-Path $PSScriptRoot '开始跨电脑验收.cmd') (Join-Path $pkg '开始跨电脑验收.cmd') -Force
Copy-Item (Join-Path $PSScriptRoot 'QUICK_START.md') (Join-Path $pkg '快速开始_中文.md')
Copy-Item (Join-Path $PSScriptRoot 'CROSS_COMPUTER_SETUP.md') (Join-Path $pkg '跨电脑迁移.md') -Force
Copy-Item (Join-Path $PSScriptRoot 'FOLLOW_STARTUP.md') (Join-Path $pkg '随雷神启动和退出.md')
Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Output "EXE=$hash"
Write-Output "ZIP=$zipHash"
Write-Output "ZIP_PATH=$zip"
Write-Output "PACKAGE_DIR=$pkg"
& (Join-Path $PSScriptRoot 'tools\verify-package.ps1') -PackagePath $pkg
& (Join-Path $PSScriptRoot 'tools\verify-package.ps1') -PackagePath $zip -ExecutablePath $exe
