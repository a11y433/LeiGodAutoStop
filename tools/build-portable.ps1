param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stageRoot = Join-Path $projectRoot (".artifacts\release-" + $stamp)
$buildOutput = Join-Path $stageRoot 'build'
$publishOutput = Join-Path $stageRoot 'publish'
New-Item -ItemType Directory -Path $buildOutput,$publishOutput -Force | Out-Null

$testOutput = & dotnet run --project (Join-Path $projectRoot 'tests\LeiGodAutoStop.OfflineTests.csproj') -c Release
if ($LASTEXITCODE -ne 0 -or $testOutput -notcontains 'All offline scenarios passed.') { throw 'Offline scenario run failed; release packaging stopped.' }
$passed = @($testOutput | Where-Object { $_ -like 'PASS *' }).Count

$publishArgs = @(
    'publish', (Join-Path $projectRoot 'app\LeiGodAutoStop.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '/p:PublishSingleFile=true', ("/p:OutputPath=" + $buildOutput + '\'),
    '-o', $publishOutput
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed; package was not refreshed.' }

$exe = Join-Path $publishOutput 'LeiGodAutoStop.exe'
$packageZip = Join-Path $projectRoot ("dist\LeiGodAutoStop_" + $stamp + '.zip')
& (Join-Path $projectRoot 'repack.ps1') -PublishedExePath $exe -ZipPath $packageZip
if ($LASTEXITCODE -ne 0) { throw 'Package rebuild failed.' }

[pscustomobject]@{
    Result = 'PASS'
    OfflineScenarios = $passed
    StagedExecutable = $exe
    ExecutableBytes = (Get-Item -LiteralPath $exe).Length
    ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    PackageZip = $packageZip
    StageDirectory = $stageRoot
}
