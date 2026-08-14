[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$matrixPath = Join-Path $repositoryRoot 'docs/security-test-matrix.json'
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
$expectedIds = 1..12 | ForEach-Object { 'STM-{0:D2}' -f $_ }
$actualIds = @($matrix.controls | ForEach-Object id)
if ($matrix.schemaVersion -ne 1 -or $matrix.plan -ne 89 -or
    (Compare-Object $expectedIds $actualIds)) {
    throw 'SECURITY_MATRIX_SCHEMA_OR_CONTROL_SET_INVALID'
}

$connection = [Environment]::GetEnvironmentVariable('AUDITION_POSTGRES_ADMIN_CONNECTION')
if ([string]::IsNullOrWhiteSpace($connection)) {
    throw 'AUDITION_POSTGRES_ADMIN_CONNECTION_REQUIRED'
}
$connectionBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
$connectionBuilder.set_ConnectionString($connection)
$hostName = if ($connectionBuilder.ContainsKey('Host')) { [string] $connectionBuilder['Host'] }
    elseif ($connectionBuilder.ContainsKey('Server')) { [string] $connectionBuilder['Server'] }
    else { '' }
if ($hostName -notin @('127.0.0.1', 'localhost', '::1')) {
    throw 'SECURITY_MATRIX_POSTGRES_MUST_BE_LOOPBACK'
}

$acvPath = Join-Path $repositoryRoot 'acv.exe'
if (-not (Test-Path -LiteralPath $acvPath -PathType Leaf)) { throw 'PRIVATE_ACV_FIXTURE_REQUIRED' }
$acvHash = (Get-FileHash -LiteralPath $acvPath -Algorithm SHA256).Hash
if ($acvHash -cne '6A52C808D7E5A59EB41E43D86A32E78067F424C8531E34981887F093E81547D3') {
    throw 'PRIVATE_ACV_FIXTURE_HASH_MISMATCH'
}

$testGroups = @(
    @('tests/Archives.Tests/Archives.Tests.csproj', 'FullyQualifiedName~ArchiveToolIntegrityPolicyTests.Modified_byte_is_rejected_with_hash_mismatch'),
    @('tests/Projects.Tests/Projects.Tests.csproj', 'FullyQualifiedName~ProjectArchiveWorkspaceServiceTests.Expected_source_hash_mismatch_returns_structured_failure'),
    @('tests/IntegrationTests/IntegrationTests.csproj', 'FullyQualifiedName~PathSecurityTests|FullyQualifiedName~Plan89MalformedArchiveSecurityTests'),
    @('tests/Gateway.Tests/Gateway.Tests.csproj', 'FullyQualifiedName~TrustedGatewayEndpointTests.Client_cannot_mutate_credit_or_send_client_authority_fields|FullyQualifiedName~TrustedGatewayEndpointTests.Job_enqueue_rejects_client_cost|FullyQualifiedName~SupabaseAccessTokenValidatorTests.Expired_overlong_or_cross_subject_token_is_rejected_fail_closed|FullyQualifiedName~SecurityMatrixPostgresTests|FullyQualifiedName~CreditConcurrencyIntegrationTests.Parallel_reservations_cannot_overspend_one_wallet|FullyQualifiedName~PremiumTemplateDistributionTests.Package_bytes_require_exact_length_hash_and_manifest_signature'),
    @('tests/Security.Tests/Security.Tests.csproj', 'FullyQualifiedName~AppUpdateVerificationTests.Replaced_manifest_metadata_is_rejected_before_network_or_install|FullyQualifiedName~AppUpdateVerificationTests.Unsigned_wrong_publisher_or_altered_artifact_is_rejected_before_install|FullyQualifiedName~SecretScanTests'),
    @('tests/Dds.Tests/Dds.Tests.csproj', 'FullyQualifiedName~DdsPreviewServiceTests.Invalid_or_malformed_dds_is_rejected_before_decoder|FullyQualifiedName~DirectXTexEvaluationHarnessTests.Malformed_dds_is_rejected_before_native_process')
)

foreach ($group in $testGroups) {
    $project = Join-Path $repositoryRoot $group[0]
    & dotnet test $project --no-restore --configuration $Configuration --filter $group[1] --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw "SECURITY_MATRIX_TEST_FAILED: $($group[0])" }
}

$scanPaths = @('src', 'tests', 'docs', 'supabase', 'scripts', '.github') |
    ForEach-Object { Join-Path $repositoryRoot $_ }
$buildOutputs = @(
    "src/AuditionModStudio.App/bin/$Configuration/net10.0-windows10.0.26100.0",
    "src/AuditionModStudio.Gateway/bin/$Configuration/net10.0"
)
foreach ($relativeOutput in $buildOutputs) {
    $output = Join-Path $repositoryRoot $relativeOutput
    if (-not (Test-Path -LiteralPath $output -PathType Container)) {
        throw "SECURITY_MATRIX_BUILD_OUTPUT_REQUIRED: $output"
    }
    $scanPaths += $output
}
$secretScanPath = Join-Path $PSScriptRoot 'Invoke-SecretScan.ps1'
$quotedScanPaths = $scanPaths | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }
$childCommand = "`$ProgressPreference='SilentlyContinue'; & '" + $secretScanPath.Replace("'", "''") + "' -Paths @(" +
    ($quotedScanPaths -join ',') + ")"
$encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
& powershell -NoProfile -NonInteractive -EncodedCommand $encodedCommand
if ($LASTEXITCODE -ne 0) { throw 'SECURITY_MATRIX_SECRET_SCAN_FAILED' }

Write-Output "PLAN 89 security matrix PASS: 12/12 controls ($Configuration)."
