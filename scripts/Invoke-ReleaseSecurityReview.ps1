[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $RequireStandardUser
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifactPath = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$review = Get-Content -LiteralPath (Join-Path $repositoryRoot 'docs/release-security-review.json') -Raw |
    ConvertFrom-Json
$expectedIds = 1..8 | ForEach-Object { 'RPR-{0:D2}' -f $_ }
if ($review.schemaVersion -ne 1 -or $review.plan -ne 90 -or
    (Compare-Object $expectedIds @($review.attackPaths | ForEach-Object id))) {
    throw 'RELEASE_REVIEW_CONTROL_SET_INVALID'
}

if ($RequireStandardUser) {
    if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
        throw 'STANDARD_USER_GATE_REQUIRES_WINDOWS'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'RELEASE_REVIEW_MUST_RUN_UNELEVATED'
    }
}

$connection = [Environment]::GetEnvironmentVariable('AUDITION_POSTGRES_ADMIN_CONNECTION')
if ([string]::IsNullOrWhiteSpace($connection)) { throw 'AUDITION_POSTGRES_ADMIN_CONNECTION_REQUIRED' }
$connectionBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
$connectionBuilder.set_ConnectionString($connection)
$hostName = if ($connectionBuilder.ContainsKey('Host')) { [string] $connectionBuilder['Host'] }
    elseif ($connectionBuilder.ContainsKey('Server')) { [string] $connectionBuilder['Server'] }
    else { '' }
if ($hostName -notin @('127.0.0.1', 'localhost', '::1')) {
    throw 'RELEASE_REVIEW_POSTGRES_MUST_BE_LOOPBACK'
}

$testGroups = @(
    @('tests/Core.Tests/Core.Tests.csproj', 'FullyQualifiedName~PrivilegeModelAdrTests'),
    @('tests/Gateway.Tests/Gateway.Tests.csproj', 'FullyQualifiedName~TrustedGatewayEndpointTests|FullyQualifiedName~SupabaseAccessTokenValidatorTests|FullyQualifiedName~SupabaseRlsHardeningTests|FullyQualifiedName~SecurityMatrixPostgresTests|FullyQualifiedName~PaymentSecurityTests'),
    @('tests/Security.Tests/Security.Tests.csproj', 'FullyQualifiedName~ReleasePenetrationReviewTests|FullyQualifiedName~ReleaseArtifactExposurePolicyTests|FullyQualifiedName~AppUpdateVerificationTests|FullyQualifiedName~EncryptedPremiumTemplateCacheTests|FullyQualifiedName~ClientIntegrityServiceTests'),
    @('tests/IntegrationTests/IntegrationTests.csproj', 'FullyQualifiedName~UnelevatedManifestTests|FullyQualifiedName~ProcessLaunchHardeningContractTests|FullyQualifiedName~PathSecurityTests|FullyQualifiedName~Plan89MalformedArchiveSecurityTests'),
    @('tests/Archives.Tests/Archives.Tests.csproj', 'FullyQualifiedName~ArchiveToolIntegrityPolicyTests|FullyQualifiedName~AcvTool5CommandBuilderTests'),
    @('tests/Dds.Tests/Dds.Tests.csproj', 'FullyQualifiedName~DirectXTexEvaluationHarnessTests')
)
foreach ($group in $testGroups) {
    & dotnet test (Join-Path $repositoryRoot $group[0]) --no-restore --configuration $Configuration `
        --filter $group[1] --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw "RELEASE_REVIEW_TEST_FAILED: $($group[0])" }
}

& powershell -NoProfile -File (Join-Path $PSScriptRoot 'Protect-ReleaseArtifactExposure.ps1') `
    -ArtifactRoot $artifactPath
if ($LASTEXITCODE -ne 0) { throw 'RELEASE_REVIEW_EXPOSURE_POLICY_FAILED' }
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-SecretScan.ps1') -Paths $artifactPath
if ($LASTEXITCODE -ne 0) { throw 'RELEASE_REVIEW_SECRET_SCAN_FAILED' }

Write-Output "PLAN 90 internal release security review PASS: 8/8 attack paths ($Configuration)."
