[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactRoot,
    [switch] $Prepare
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\', '/')
if ($root -eq [IO.Path]::GetPathRoot($root) -or -not (Test-Path -LiteralPath $root -PathType Container)) {
    throw 'RELEASE_EXPOSURE_ROOT_INVALID'
}
$executable = Join-Path $root 'AuditionModStudio.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'RELEASE_EXPOSURE_APP_SENTINEL_MISSING'
}

if ($Prepare) {
    Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.pdb -Force |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}

$forbiddenExtensions = @('.pdb', '.cs', '.csproj', '.sln', '.user', '.pfx', '.p12', '.key')
$forbiddenNames = @(
    'acv.exe', '015.ab', '015.keydat', 'Map.xml', 'Renaming.xml',
    'AuditionModStudio.obfuscation-map.xml'
)
$leaks = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object {
    $_.Extension -in $forbiddenExtensions -or $_.Name -in $forbiddenNames -or
    $_.Name -like '*.obfuscation-map.xml'
})
if ($leaks.Count -gt 0) {
    $categories = @($leaks | ForEach-Object {
        if ($_.Extension -eq '.pdb') { 'debug_symbols' }
        elseif ($_.Extension -in @('.cs', '.csproj', '.sln', '.user')) { 'source' }
        elseif ($_.Extension -in @('.pfx', '.p12', '.key')) { 'private_key_material' }
        elseif ($_.Name -in @('acv.exe', '015.ab', '015.keydat')) { 'proprietary_fixture' }
        else { 'private_mapping' }
    } | Sort-Object -Unique)
    throw ('RELEASE_EXPOSURE_POLICY_FAILED: ' + ($categories -join ','))
}

[pscustomobject]@{
    PolicyStatus = 'PASS'
    DebugSymbols = 0
    SourceFiles = 0
    PrivateMappings = 0
    ProprietaryFixtures = 0
} | ConvertTo-Json -Compress
