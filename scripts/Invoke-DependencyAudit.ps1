[CmdletBinding()]
param(
  [string] $SolutionPath = 'AuditionModStudio.sln'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solution = (Resolve-Path -LiteralPath (Join-Path $repositoryRoot $SolutionPath)).Path
$output = @(& dotnet list $solution package --vulnerable --include-transitive --format json 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'DEPENDENCY_AUDIT_COMMAND_FAILED' }
$text = $output -join [Environment]::NewLine
$jsonStart = $text.IndexOf('{')
if ($jsonStart -lt 0) { throw 'DEPENDENCY_AUDIT_OUTPUT_INVALID' }
$report = $text[$jsonStart..($text.Length - 1)] -join '' | ConvertFrom-Json
$findings = @()
foreach ($project in @($report.projects)) {
  foreach ($framework in @($project.frameworks)) {
    foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
      if ($null -eq $package) { continue }
      if (@($package.vulnerabilities).Count -gt 0) {
        $findings += '{0}@{1}' -f $package.id, $package.resolvedVersion
      }
    }
  }
}
if ($findings.Count -gt 0) {
  Write-Error ('DEPENDENCY_VULNERABILITY_FOUND: ' + (($findings | Sort-Object -Unique) -join ', '))
  exit 1
}
Write-Output 'Dependency vulnerability audit PASS: không có finding theo NuGet sources hiện tại.'
