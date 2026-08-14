[CmdletBinding()]
param([string] $OutputRoot = 'artifacts/plan-102-e2e')

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$root = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) }
else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot)) }
$artifactBoundary = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
if (-not $root.StartsWith($artifactBoundary + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'E2E_OUTPUT_MUST_BE_UNDER_ARTIFACTS'
}
if (Test-Path -LiteralPath $root) { throw 'E2E_OUTPUT_ALREADY_EXISTS' }

$privateKey = Join-Path $root 'TEST_SIGNING_KEY_NOT_PRODUCTION.pem'
$publicKey = Join-Path $root 'TEST_SIGNING_PUBLIC_KEY_NOT_PRODUCTION.pem'
$harness = Join-Path $repositoryRoot 'tests/PortableUpdate.E2EHarness/PortableUpdate.E2EHarness.csproj'
$health = Join-Path $repositoryRoot 'tests/PortableUpdate.HealthApp/PortableUpdate.HealthApp.csproj'
$updater = Join-Path $repositoryRoot 'src/AuditionModStudio.PortableUpdater/AuditionModStudio.PortableUpdater.csproj'
$installName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('Q8OgaSDEkeG6t3QgY8WpIFVuaWNvZGU='))
$newRootName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('QuG6o24gbeG7m2k='))
$install = Join-Path $root $installName
$newRoot = Join-Path $root $newRootName
$updates = Join-Path $root 'LocalAppData/Updates'
$marker = Join-Path $root 'health-version.txt'
New-Item -ItemType Directory -Path $root,$install,$newRoot,$updates | Out-Null

& dotnet run --project $harness --configuration Release -- generate-key $privateKey $publicKey
if ($LASTEXITCODE -ne 0) { throw 'E2E_KEYGEN_FAILED' }
& dotnet publish $health -c Release -r win-x64 --self-contained true -o $install -p:Version=1.0.0.0
if ($LASTEXITCODE -ne 0) { throw 'E2E_OLD_PUBLISH_FAILED' }
& dotnet publish $health -c Release -r win-x64 --self-contained true -o $newRoot -p:Version=1.0.1.0
if ($LASTEXITCODE -ne 0) { throw 'E2E_NEW_PUBLISH_FAILED' }
& dotnet publish $updater -c Release -r win-x64 --self-contained true -o (Join-Path $root 'updater') `
    -p:PortableUpdatePublicKeyFile=$publicKey
if ($LASTEXITCODE -ne 0) { throw 'E2E_UPDATER_PUBLISH_FAILED' }
Copy-Item -LiteralPath (Join-Path $root 'updater/AuditionAI.Updater.exe') -Destination (Join-Path $install 'AuditionAI.Updater.exe')
Copy-Item -LiteralPath (Join-Path $root 'updater/AuditionAI.Updater.exe') -Destination (Join-Path $newRoot 'AuditionAI.Updater.exe')
Set-Content -LiteralPath (Join-Path $install 'e2e-marker-path.txt') -Value $marker -Encoding UTF8
Set-Content -LiteralPath (Join-Path $install 'e2e-delay-ms.txt') -Value '4000' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $newRoot 'e2e-marker-path.txt') -Value $marker -Encoding UTF8
Set-Content -LiteralPath (Join-Path $newRoot 'e2e-delay-ms.txt') -Value '0' -Encoding ASCII
Set-Content -LiteralPath (Join-Path $install 'user-project.audproj') -Value 'PRESERVE_USER_PROJECT' -Encoding UTF8

$old = Start-Process -FilePath (Join-Path $install 'AuditionModStudio.App.exe') -WorkingDirectory $env:TEMP -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while (-not (Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
if ((Get-Content -Raw -LiteralPath $marker).Trim() -ne '1.0.0.0') { throw 'E2E_OLD_VERSION_NOT_STARTED' }
$handoffStarted = [Diagnostics.Stopwatch]::StartNew()
& dotnet run --project $harness --configuration Release -- run $newRoot $updates $privateKey $publicKey $install $old.Id
if ($LASTEXITCODE -ne 0) { throw 'E2E_HANDOFF_FAILED' }

$deadline = [DateTime]::UtcNow.AddSeconds(40)
do {
    Start-Sleep -Milliseconds 250
    $version = if (Test-Path -LiteralPath $marker) { (Get-Content -Raw -LiteralPath $marker).Trim() } else { '' }
} while ($version -ne '1.0.1.0' -and [DateTime]::UtcNow -lt $deadline)
if ($version -ne '1.0.1.0') { throw 'E2E_NEW_VERSION_NOT_STARTED' }
if ((Get-Content -Raw -LiteralPath (Join-Path $install 'user-project.audproj')).Trim() -ne 'PRESERVE_USER_PROJECT') {
    throw 'E2E_USER_DATA_NOT_PRESERVED'
}
[pscustomobject]@{ Status='PASS'; Old='1.0.0.0'; New='1.0.1.0'; UserData='PRESERVED'; Path='UNICODE_SPACES'; HandoffToHealthMilliseconds=$handoffStarted.ElapsedMilliseconds } |
    ConvertTo-Json
