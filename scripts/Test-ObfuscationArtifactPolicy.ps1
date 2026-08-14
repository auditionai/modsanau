[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublicArtifactRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $ObfuscationEnabled,
    [string] $PrivateMappingRoot
)

$ErrorActionPreference = 'Stop'
$publicRoot = [IO.Path]::GetFullPath($PublicArtifactRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not (Test-Path -LiteralPath $publicRoot -PathType Container)) {
    throw 'OBFUSCATION_PUBLIC_ARTIFACT_ROOT_MISSING'
}
if ($ObfuscationEnabled -and $Configuration -ne 'Release') {
    throw 'OBFUSCATION_DEBUG_FORBIDDEN'
}

$leaks = @(Get-ChildItem -LiteralPath $publicRoot -Recurse -Force -ErrorAction Stop | Where-Object {
    $_.Name -in @('Map.xml', 'Renaming.xml') -or
    $_.Name -like '*.obfuscation-map.xml' -or
    $_.Name -in @('DotfuscatorReports', 'Dotfuscated')
})
if ($leaks.Count -gt 0) {
    throw 'OBFUSCATION_MAPPING_PUBLIC_LEAK'
}

if (-not [string]::IsNullOrWhiteSpace($PrivateMappingRoot)) {
    if (-not [IO.Path]::IsPathRooted($PrivateMappingRoot)) {
        throw 'OBFUSCATION_PRIVATE_MAPPING_ROOT_NOT_ABSOLUTE'
    }
    $privateRoot = [IO.Path]::GetFullPath($PrivateMappingRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($privateRoot.StartsWith($publicRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $publicRoot.StartsWith($privateRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OBFUSCATION_MAPPING_NOT_SEPARATED'
    }
}

[pscustomobject]@{
    Configuration = $Configuration
    ObfuscationEnabled = [bool] $ObfuscationEnabled
    PublicMappingLeak = $false
    PolicyStatus = if ($ObfuscationEnabled) { 'PILOT_REQUIRES_EXTERNAL_EVIDENCE' } else { 'NOT_ADOPTED' }
} | ConvertTo-Json
