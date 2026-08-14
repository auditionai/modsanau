[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadRoot,
    [Parameter(Mandatory = $true)]
    [string] $SignToolPath,
    [Parameter(Mandatory = $true)]
    [string] $CertificateThumbprint,
    [Parameter(Mandatory = $true)]
    [string] $ExpectedPublisherSubject,
    [Parameter(Mandatory = $true)]
    [uri] $TimestampUrl,
    [Parameter(Mandatory = $true)]
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw 'INSTALLER_PAYLOAD_ROOT_MISSING'
}
$artifacts = @(Get-ChildItem -LiteralPath $root -File | Where-Object {
    $_.Name -eq 'AuditionModStudio.App.exe' -or $_.Name -like 'AuditionModStudio.*.dll'
} | Sort-Object Name | ForEach-Object FullName)
if ($artifacts.Count -lt 2) {
    throw 'INSTALLER_PAYLOAD_APPLICATION_ARTIFACTS_MISSING'
}

$signingScript = Join-Path $PSScriptRoot 'Invoke-AppCodeSigning.ps1'
$report = & $signingScript -Mode Sign -ArtifactRoot $root -Artifacts $artifacts `
    -SignToolPath $SignToolPath -CertificateThumbprint $CertificateThumbprint `
    -ExpectedPublisherSubject $ExpectedPublisherSubject -TimestampUrl $TimestampUrl -RequireSignTool
$resolvedReport = [IO.Path]::GetFullPath($ReportPath)
$reportParent = Split-Path -Parent $resolvedReport
if (-not (Test-Path -LiteralPath $reportParent -PathType Container)) {
    throw 'INSTALLER_PAYLOAD_SIGNING_REPORT_DIRECTORY_MISSING'
}
$report | Set-Content -LiteralPath $resolvedReport -Encoding utf8
