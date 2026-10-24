param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Export', 'Import', 'Check')]
    [string]$Action,
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [string]$ConfigRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$configRoot = if ([string]::IsNullOrWhiteSpace($ConfigRoot)) {
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LeiGodAutoStop'
} else {
    [IO.Path]::GetFullPath($ConfigRoot)
}
$localFile = Join-Path $configRoot 'aliases.json'

function Read-Mappings([string]$File) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { return [ordered]@{} }
    $parsed = Get-Content -LiteralPath $File -Raw | ConvertFrom-Json
    $result = [ordered]@{}
    foreach ($property in $parsed.PSObject.Properties) {
        $gameId = [string]$property.Name
        if ($gameId -notmatch '^[1-9][0-9]*$') { throw "Invalid game ID in mapping file: $gameId" }
        $items = @($property.Value)
        $normalized = @()
        foreach ($item in $items) {
            if ($item -is [string]) { $name = $item; $exePath = $null }
            else { $name = [string]$item.Name; $exePath = $item.Path }
            if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::GetFileName($name) -ne $name -or -not $name.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Invalid executable name for game ID ${gameId}: $name"
            }
            if ($null -ne $exePath -and [string]$exePath -ne '' -and -not [IO.Path]::IsPathRooted([string]$exePath)) {
                throw "Executable path must be absolute for game ID $gameId / $name"
            }
            $normalized += [pscustomobject]@{ Name = $name; Path = $(if ($null -eq $exePath -or [string]$exePath -eq '') { $null } else { [string]$exePath }) }
        }
        $result[$gameId] = $normalized
    }
    return $result
}

function Write-AtomicJson([string]$File, $Object) {
    $directory = Split-Path -Parent $File
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $tempFile = Join-Path $directory ([IO.Path]::GetRandomFileName())
    try {
        $json = ConvertTo-Json -InputObject $Object -Depth 12
        [IO.File]::WriteAllText($tempFile, $json, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $File -PathType Leaf) { [IO.File]::Replace($tempFile, $File, $null) }
        else { [IO.File]::Move($tempFile, $File) }
    }
    finally { if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile -Force } }
}

switch ($Action) {
    'Export' {
        $outFile = [IO.Path]::GetFullPath($Path)
        if ((Test-Path -LiteralPath $outFile) -and -not $Force) { throw "Output already exists; use -Force to replace it: $outFile" }
        $mappings = Read-Mappings $localFile
        Write-AtomicJson $outFile $mappings
        [pscustomobject]@{ Result = 'EXPORTED'; GameIds = $mappings.Count; File = $outFile; Includes = 'confirmed game IDs, executable names, and paths only' }
    }
    'Import' {
        $inFile = (Resolve-Path -LiteralPath $Path).Path
        if ($inFile -eq [IO.Path]::GetFullPath($localFile)) { throw 'Import source must not be the active aliases.json file.' }
        $incoming = Read-Mappings $inFile
        $current = Read-Mappings $localFile
        foreach ($gameId in $incoming.Keys) {
            if (-not $current.Contains($gameId)) { $current[$gameId] = @() }
            $combined = @($current[$gameId])
            foreach ($entry in $incoming[$gameId]) {
                $duplicate = @($combined | Where-Object { $_.Name -eq $entry.Name -and $_.Path -eq $entry.Path }).Count -gt 0
                if (-not $duplicate) { $combined += $entry }
            }
            $current[$gameId] = $combined
        }
        Write-AtomicJson $localFile $current
        $missingPaths = @($incoming.Values | ForEach-Object { $_ } | Where-Object { $_.Path -and -not (Test-Path -LiteralPath $_.Path -PathType Leaf) })
        [pscustomobject]@{ Result = 'IMPORTED'; GameIds = $incoming.Count; File = $localFile; PathsMissingOnThisComputer = $missingPaths.Count; NextStep = 'Run Check, then confirm each target game process on this computer before relying on its mapping.' }
    }
    'Check' {
        $file = if ($Path -eq 'local') { $localFile } else { (Resolve-Path -LiteralPath $Path).Path }
        $mappings = Read-Mappings $file
        $rows = foreach ($gameId in $mappings.Keys) {
            foreach ($entry in $mappings[$gameId]) {
                [pscustomobject]@{ GameId = $gameId; Name = $entry.Name; Path = $entry.Path; ExistsHere = (-not $entry.Path -or (Test-Path -LiteralPath $entry.Path -PathType Leaf)) }
            }
        }
        if ($rows) { $rows }
        else { [pscustomobject]@{ Result = 'EMPTY'; File = $file; Note = 'No confirmed game mappings. This does not permit automatic control.' } }
    }
}
