[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src'),(Join-Path $repositoryRoot 'tests') `
  -Recurse -Filter *.csproj -File)
$locks = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src'),(Join-Path $repositoryRoot 'tests') `
  -Recurse -Filter packages.lock.json -File)
if ($projects.Count -ne $locks.Count) { throw "LOCK_FILE_COUNT_MISMATCH: $($projects.Count) projects / $($locks.Count) locks" }
foreach ($project in $projects) {
  if (-not (Test-Path -LiteralPath (Join-Path $project.DirectoryName 'packages.lock.json'))) {
    throw "LOCK_FILE_MISSING: $($project.Name)"
  }
}

$provenancePath = Join-Path $repositoryRoot 'docs/supply-chain/THIRD_PARTY_PROVENANCE.json'
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
if ($provenance.schemaVersion -ne 1) { throw 'PROVENANCE_SCHEMA_UNSUPPORTED' }
$blocked = @($provenance.artifacts | Where-Object { $_.id -in @('acv_tool_5','audition_template_015','audition_game_assets') })
if ($blocked.Count -ne 3 -or @($blocked | Where-Object { $_.commercialDistributionAllowed -ne $false }).Count -gt 0) {
  throw 'PROPRIETARY_REDISTRIBUTION_MUST_REMAIN_BLOCKED'
}
$directXTex = @($provenance.artifacts | Where-Object id -eq 'microsoft_directxtex_texconv_may2026_x64')
if ($directXTex.Count -ne 1 -or $directXTex[0].sha256 -cne 'DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06') {
  throw 'DIRECTXTEX_PROVENANCE_MISMATCH'
}

$trackedProprietary = @(& git -C $repositoryRoot ls-files -- acv.exe 015.ab 015.keydat 015)
if ($LASTEXITCODE -ne 0) { throw 'GIT_INVENTORY_FAILED' }
if ($trackedProprietary.Count -gt 0) { throw 'PROPRIETARY_ARTIFACT_TRACKED' }
$workflowFiles = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github/workflows') -Filter *.yml -File)
foreach ($workflow in $workflowFiles) {
  $floating = Select-String -LiteralPath $workflow.FullName -Pattern 'uses:\s+[^\s]+@(?![0-9a-f]{40}(?:\s|$))'
  if ($floating) { throw "GITHUB_ACTION_NOT_SHA_PINNED: $($workflow.Name)" }
}
Write-Output "Supply-chain policy PASS: $($locks.Count) lock files, proprietary redistribution blocked, Actions SHA-pinned."
