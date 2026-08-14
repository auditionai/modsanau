[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageRoot,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')] [string] $MinimumSupportedVersion,
    [Parameter(Mandatory)] [ValidatePattern('^https://')] [string] $PackageUri,
    [Parameter(Mandatory)] [string] $PrivateSigningKeyPath,
    [Parameter(Mandatory)] [string] $ReleaseNotesPath,
    [ValidateSet('optional','required')] [string] $UpdatePolicy = 'optional',
    [string] $OutputRoot = 'artifacts/portable-update-release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$privateKey = (Resolve-Path -LiteralPath $PrivateSigningKeyPath).Path
$notes = (Resolve-Path -LiteralPath $ReleaseNotesPath).Path
$output = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) }
else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot)) }
$artifactBoundary = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
if (-not $output.StartsWith($artifactBoundary + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'UPDATE_RELEASE_OUTPUT_MUST_BE_UNDER_ARTIFACTS'
}
if ($privateKey.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PRODUCTION_PRIVATE_KEY_MUST_NOT_BE_IN_REPOSITORY'
}
if (Test-Path -LiteralPath $output) { throw 'UPDATE_RELEASE_OUTPUT_ALREADY_EXISTS' }
New-Item -ItemType Directory -Path $output | Out-Null

& (Join-Path $repositoryRoot 'scripts/Protect-ReleaseArtifactExposure.ps1') `
    -ArtifactRoot $package -EntryPoint 'AuditionModStudio.App.exe' | Out-Host
& (Join-Path $repositoryRoot 'scripts/Invoke-SecretScan.ps1') -Paths $package | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'UPDATE_RELEASE_ARTIFACT_SCAN_FAILED' }

$tool = Join-Path $repositoryRoot 'tools/PortableUpdate.ReleaseTool/PortableUpdate.ReleaseTool.csproj'
& dotnet run --project $tool --configuration Release -- `
    --package-root $package --version $Version --minimum-version $MinimumSupportedVersion `
    --package-uri $PackageUri --private-key $privateKey --release-notes $notes `
    --policy $UpdatePolicy --output $output
if ($LASTEXITCODE -ne 0) { throw 'UPDATE_RELEASE_CREATION_FAILED' }
