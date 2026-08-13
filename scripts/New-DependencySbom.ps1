[CmdletBinding()]
param(
  [string] $SolutionPath = 'AuditionModStudio.sln',
  [string] $OutputPath = 'docs/supply-chain/AuditionModStudio.spdx.json',
  [switch] $Verify
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solution = (Resolve-Path -LiteralPath (Join-Path $repositoryRoot $SolutionPath)).Path
$destination = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
if (-not $destination.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) { throw 'SBOM_OUTPUT_OUTSIDE_REPOSITORY' }

$raw = @(& dotnet list $solution package --include-transitive --format json 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'SBOM_PACKAGE_LIST_FAILED' }
$text = $raw -join [Environment]::NewLine
$jsonStart = $text.IndexOf('{')
if ($jsonStart -lt 0) { throw 'SBOM_PACKAGE_LIST_INVALID' }
$graph = $text[$jsonStart..($text.Length - 1)] -join '' | ConvertFrom-Json

$packages = @{}
foreach ($project in @($graph.projects)) {
  foreach ($framework in @($project.frameworks)) {
    foreach ($item in @($framework.topLevelPackages)) {
      if ($null -eq $item) { continue }
      $packages[('{0}/{1}' -f $item.id.ToLowerInvariant(), $item.resolvedVersion)] =
        [pscustomobject]@{ Id = $item.id; Version = $item.resolvedVersion; Direct = $true }
    }
    foreach ($item in @($framework.transitivePackages)) {
      if ($null -eq $item) { continue }
      $key = '{0}/{1}' -f $item.id.ToLowerInvariant(), $item.resolvedVersion
      if (-not $packages.ContainsKey($key)) {
        $packages[$key] = [pscustomobject]@{ Id = $item.id; Version = $item.resolvedVersion; Direct = $false }
      }
    }
  }
}

$locals = (& dotnet nuget locals global-packages --list 2>&1) -join ''
if ($LASTEXITCODE -ne 0 -or $locals -notmatch '^[^:]+:\s*(.+)$') { throw 'NUGET_GLOBAL_PACKAGES_UNAVAILABLE' }
$globalPackages = $Matches[1].Trim()
function Get-LicenseExpression([string] $id, [string] $version) {
  $packageDirectory = Join-Path $globalPackages $id.ToLowerInvariant()
  $versionDirectory = Join-Path $packageDirectory $version.ToLowerInvariant()
  $nuspec = Join-Path $versionDirectory ($id.ToLowerInvariant() + '.nuspec')
  if (-not (Test-Path -LiteralPath $nuspec)) { return 'NOASSERTION' }
  [xml] $metadata = Get-Content -LiteralPath $nuspec -Raw
  $license = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
  if ($license -and $license.GetAttribute('type') -eq 'expression' -and -not [string]::IsNullOrWhiteSpace($license.InnerText)) {
    return $license.InnerText.Trim()
  }
  return 'NOASSERTION'
}

$ordered = @($packages.Values | Sort-Object @{ Expression = { $_.Id.ToLowerInvariant() } }, Version)
$identity = ($ordered | ForEach-Object { '{0}@{1}' -f $_.Id.ToLowerInvariant(), $_.Version }) -join "`n"
$hasher = [Security.Cryptography.SHA256]::Create()
try { $sha = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity)) }
finally { $hasher.Dispose() }
$digest = ($sha | ForEach-Object { $_.ToString('x2') }) -join ''
$created = '2026-08-13T00:00:00Z'

$spdxPackages = foreach ($package in $ordered) {
  $safeId = ($package.Id + '-' + $package.Version) -replace '[^A-Za-z0-9.-]', '-'
  $lowerId = $package.Id.ToLowerInvariant()
  [ordered]@{
    name = $package.Id
    SPDXID = 'SPDXRef-Package-' + $safeId
    versionInfo = $package.Version
    downloadLocation = "https://api.nuget.org/v3-flatcontainer/$lowerId/$($package.Version)/$lowerId.$($package.Version).nupkg"
    filesAnalyzed = $false
    licenseConcluded = 'NOASSERTION'
    licenseDeclared = Get-LicenseExpression $package.Id $package.Version
    copyrightText = 'NOASSERTION'
    supplier = 'NOASSERTION'
    externalRefs = @([ordered]@{
      referenceCategory = 'PACKAGE-MANAGER'
      referenceType = 'purl'
      referenceLocator = "pkg:nuget/$($package.Id)@$($package.Version)"
    })
    comment = if ($package.Direct) { 'Direct dependency in at least one project.' }
      else { 'Transitive dependency.' }
  }
}
$relationships = foreach ($package in $spdxPackages) {
  [ordered]@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = $package.SPDXID }
}
$document = [ordered]@{
  spdxVersion = 'SPDX-2.3'
  dataLicense = 'CC0-1.0'
  SPDXID = 'SPDXRef-DOCUMENT'
  name = 'Audition-AI-Mod-Studio-NuGet-Dependencies'
  documentNamespace = "https://auditionmodstudio.invalid/spdx/nuget/$digest"
  creationInfo = [ordered]@{ created = $created; creators = @('Tool: scripts/New-DependencySbom.ps1') }
  packages = @($spdxPackages)
  relationships = @($relationships)
}
$json = ($document | ConvertTo-Json -Depth 12) + "`n"
$encoding = [Text.UTF8Encoding]::new($false)
if ($Verify) {
  if (-not (Test-Path -LiteralPath $destination)) { throw 'SBOM_MISSING' }
  $existing = [IO.File]::ReadAllText($destination, $encoding).Replace("`r`n", "`n")
  if ($existing -cne $json.Replace("`r`n", "`n")) { throw 'SBOM_OUT_OF_DATE' }
  Write-Output "SBOM verify PASS: $($ordered.Count) NuGet package version duy nhất."
  return
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
$temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
  [IO.File]::WriteAllText($temporary, $json, $encoding)
  if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($temporary, $destination, $null) }
  else { [IO.File]::Move($temporary, $destination) }
} finally {
  if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
Write-Output "SBOM generated: $($ordered.Count) NuGet package version duy nhất."
