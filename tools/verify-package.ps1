param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$PackagePath,
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-HashFromStream([System.IO.Stream]$Stream) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-ZipEntry([System.IO.Compression.ZipArchive]$Archive, [string]$Name) {
    $wanted = $Name.Replace('/', '\').TrimStart('\')
    return $Archive.Entries | Where-Object { $_.FullName.Replace('/', '\').TrimStart('\') -eq $wanted } | Select-Object -First 1
}

$packageItem = Get-Item -LiteralPath $PackagePath
$package = $packageItem.FullName
if (-not $packageItem.PSIsContainer -and [IO.Path]::GetExtension($package) -eq '.zip') {
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $exeEntry = Get-ZipEntry $archive 'LeiGodAutoStop.exe'
        $manifestEntry = Get-ZipEntry $archive 'SHA256.txt'
        $guideEntry = Get-ZipEntry $archive '快速开始_中文.md'
        $transferGuide = Get-ZipEntry $archive '跨电脑迁移.md'
        $mappingTool = Get-ZipEntry $archive 'tools/game-mappings.ps1'
        $acceptanceTool = Get-ZipEntry $archive 'tools/cross-computer-acceptance.ps1'
        $concurrentAcceptanceTool = Get-ZipEntry $archive 'tools/cross-computer-concurrent-acceptance.ps1'
        $comparisonTool = Get-ZipEntry $archive 'tools/compare-transfer-acceptance.ps1'
        $guidedAcceptanceTool = Get-ZipEntry $archive 'tools/start-acceptance.ps1'
        $selfTestTool = Get-ZipEntry $archive 'tools/verify-package.ps1'
        $guidedCmd = Get-ZipEntry $archive '开始跨电脑验收.cmd'
        if ($null -eq $exeEntry -or $null -eq $manifestEntry -or $null -eq $guideEntry -or $null -eq $transferGuide -or $null -eq $mappingTool -or $null -eq $acceptanceTool -or $null -eq $concurrentAcceptanceTool -or $null -eq $comparisonTool -or $null -eq $guidedAcceptanceTool -or $null -eq $guidedCmd -or $null -eq $selfTestTool) {
            throw 'ZIP is missing the EXE, SHA256 manifest, Chinese guide, migration guide, or bundled verification/acceptance/comparison tools.'
        }
        $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $manifestReader.ReadToEnd().Trim() } finally { $manifestReader.Dispose() }
        if ($manifest -notmatch '^([A-Fa-f0-9]{64})\s+LeiGodAutoStop\.exe$') { throw 'SHA256.txt has an invalid manifest line.' }
        $expectedHash = $Matches[1].ToUpperInvariant()
        $exeStream = $exeEntry.Open()
        try { $actualHash = Get-HashFromStream $exeStream } finally { $exeStream.Dispose() }
        if ($actualHash -ne $expectedHash) { throw "Archive EXE hash mismatch. Manifest=$expectedHash Actual=$actualHash" }
        if ($ExecutablePath) {
            $published = (Resolve-Path -LiteralPath $ExecutablePath).Path
            $publishedHash = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash
            if ($publishedHash -ne $actualHash) { throw "Published EXE differs from archive. Archive=$actualHash Published=$publishedHash" }
        }
        [pscustomobject]@{ Result = 'PASS'; Package = $package; ExecutableBytes = $exeEntry.Length; ExecutableSha256 = $actualHash; GuideBytes = $guideEntry.Length; TransferGuide = $transferGuide.FullName; BundledTools = 6; GuidedAcceptance = $guidedCmd.FullName }
    }
    finally { $archive.Dispose() }
}
else {
    $exe = $package
    if ((Get-Item -LiteralPath $exe).PSIsContainer) { $exe = Join-Path $exe 'LeiGodAutoStop.exe' }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable not found: $exe" }
    $manifestPath = Join-Path (Split-Path -Parent $exe) 'SHA256.txt'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Manifest not found: $manifestPath" }
    $manifest = (Get-Content -LiteralPath $manifestPath -Raw).Trim()
    if ($manifest -notmatch '^([A-Fa-f0-9]{64})\s+LeiGodAutoStop\.exe$') { throw 'SHA256.txt has an invalid manifest line.' }
    $expectedHash = $Matches[1].ToUpperInvariant()
    $actualHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw "EXE hash mismatch. Manifest=$expectedHash Actual=$actualHash" }
    [pscustomobject]@{ Result = 'PASS'; Package = (Split-Path -Parent $exe); ExecutableBytes = (Get-Item -LiteralPath $exe).Length; ExecutableSha256 = $actualHash }
}
