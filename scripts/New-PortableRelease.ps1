[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version = '1.0.0-internal.101',
    [ValidateSet('Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $OutputRoot = 'artifacts/portable'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'src/AuditionModStudio.App/AuditionModStudio.App.csproj'
$updaterProjectPath = Join-Path $repositoryRoot 'src/AuditionModStudio.PortableUpdater/AuditionModStudio.PortableUpdater.csproj'
$requestedOutput = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$artifactBoundary = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\', '/')
if (-not $requestedOutput.StartsWith($artifactBoundary + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PORTABLE_OUTPUT_MUST_BE_UNDER_ARTIFACTS'
}

$releaseRoot = Join-Path $requestedOutput $Version
if (Test-Path -LiteralPath $releaseRoot) {
    $resolvedRelease = [IO.Path]::GetFullPath($releaseRoot)
    if (-not $resolvedRelease.StartsWith($artifactBoundary + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PORTABLE_CLEAN_TARGET_INVALID'
    }
    Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
}

$stagingRoot = Join-Path $releaseRoot 'staging'
$packageRoot = Join-Path $stagingRoot 'AuditionAI-ModStudio'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

& dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $packageRoot `
    -p:Platform=x64 `
    -p:PortableRelease=true `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'PORTABLE_PUBLISH_FAILED' }

$updaterPublishRoot = Join-Path $stagingRoot 'updater-publish'
& dotnet publish $updaterProjectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $updaterPublishRoot `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'PORTABLE_UPDATER_PUBLISH_FAILED' }
$updaterExecutable = Join-Path $updaterPublishRoot 'AuditionAI.Updater.exe'
if (-not (Test-Path -LiteralPath $updaterExecutable -PathType Leaf)) {
    throw 'PORTABLE_UPDATER_ENTRY_POINT_MISSING'
}
Copy-Item -LiteralPath $updaterExecutable -Destination (Join-Path $packageRoot 'AuditionAI.Updater.exe')
Remove-Item -LiteralPath $updaterPublishRoot -Recurse -Force

$portableEntryPointName = 'AuditionModStudio.App.exe'
$publishedEntryPoint = Join-Path $packageRoot $portableEntryPointName
if (-not (Test-Path -LiteralPath $publishedEntryPoint -PathType Leaf)) {
    throw 'PORTABLE_PUBLISHED_ENTRY_POINT_MISSING'
}
$exposureScript = Join-Path $repositoryRoot 'scripts/Protect-ReleaseArtifactExposure.ps1'
& $exposureScript -ArtifactRoot $packageRoot -EntryPoint $portableEntryPointName -Prepare | Out-Host

$secretScan = Join-Path $repositoryRoot 'scripts/Invoke-SecretScan.ps1'
& $secretScan -Paths $packageRoot | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'PORTABLE_SECRET_SCAN_FAILED' }

$forbiddenDirectories = @('.git', 'tests', 'fixtures', 'workspace', 'workspaces', 'logs', 'userdata')
$directoryLeaks = @(Get-ChildItem -LiteralPath $packageRoot -Directory -Recurse -Force | Where-Object {
    $_.Name.ToLowerInvariant() -in $forbiddenDirectories
})
if ($directoryLeaks.Count -gt 0) { throw 'PORTABLE_DIRECTORY_POLICY_FAILED' }

$files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force | Sort-Object FullName)
if ($files.Count -eq 0) { throw 'PORTABLE_LAYOUT_EMPTY' }
$relativeEntries = @($files | ForEach-Object {
    if (-not $_.FullName.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'PORTABLE_ENTRY_OUTSIDE_ROOT' }
    $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
})
if ($relativeEntries.Count -ne @($relativeEntries | Sort-Object -Unique).Count) {
    throw 'PORTABLE_DUPLICATE_ENTRY'
}
if (@($relativeEntries | Where-Object { $_ -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($_) }).Count -gt 0) {
    throw 'PORTABLE_ENTRY_TRAVERSAL'
}

$zipName = "AuditionAI-Mod-Studio-$Version-$RuntimeIdentifier.zip"
$zipPath = Join-Path $releaseRoot $zipName
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw 'PORTABLE_ZIP_MISSING' }

$zipInfo = Get-Item -LiteralPath $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$payloadBytes = ($files | Measure-Object -Property Length -Sum).Sum
$report = [ordered]@{
    schemaVersion = 1
    status = 'PASS'
    classification = 'Development/Internal QA'
    product = 'Audition AI Mod Studio'
    version = $Version
    architecture = 'x64'
    runtimeIdentifier = $RuntimeIdentifier
    configuration = $Configuration
    publishModel = 'unpackaged; Windows App SDK self-contained; .NET self-contained; framework-dependent=false'
    packageDirectoryName = 'AuditionAI-ModStudio'
    entryPoint = $portableEntryPointName
    updaterEntryPoint = 'AuditionAI.Updater.exe'
    resourceEntryAssembly = 'AuditionModStudio.App.dll'
    fileCount = $files.Count
    payloadBytes = [long]$payloadBytes
    zipFileName = $zipName
    zipBytes = $zipInfo.Length
    zipSha256 = $zipHash
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$reportPath = Join-Path $releaseRoot 'portable-artifact-report.json'
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
$report | ConvertTo-Json -Depth 4
