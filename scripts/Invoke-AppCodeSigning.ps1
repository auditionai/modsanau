[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Sign', 'Verify')]
    [string] $Mode,
    [Parameter(Mandatory = $true)]
    [string] $ArtifactRoot,
    [Parameter(Mandatory = $true)]
    [string[]] $Artifacts,
    [string] $SignToolPath,
    [string] $CertificateThumbprint,
    [string] $ExpectedPublisherSubject,
    [uri] $TimestampUrl,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string] $CertificateStoreLocation = 'CurrentUser',
    [switch] $RequireSignTool
)

$ErrorActionPreference = 'Stop'
$allowedExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
@('.exe', '.dll', '.msix', '.msixbundle', '.msi') | ForEach-Object {
    [void] $allowedExtensions.Add($_)
}

function Normalize-Thumbprint([string] $Value) {
    return ($Value -replace '[^A-Fa-f0-9]', '').ToUpperInvariant()
}

function Resolve-ControlledArtifact([string] $Root, [string] $Candidate) {
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $candidatePath = if ([System.IO.Path]::IsPathRooted($Candidate)) {
        [System.IO.Path]::GetFullPath($Candidate)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $rootPath $Candidate))
    }
    if (-not $candidatePath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'SIGNING_ARTIFACT_OUTSIDE_ROOT'
    }
    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
        throw 'SIGNING_ARTIFACT_MISSING'
    }
    $item = Get-Item -LiteralPath $candidatePath -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'SIGNING_ARTIFACT_REPARSE_POINT'
    }
    if (-not $allowedExtensions.Contains($item.Extension)) {
        throw 'SIGNING_ARTIFACT_TYPE_NOT_ALLOWED'
    }
    return $item.FullName
}

function Invoke-SignTool([string[]] $Arguments) {
    if ([string]::IsNullOrWhiteSpace($SignToolPath) -or
        -not [System.IO.Path]::IsPathRooted($SignToolPath) -or
        -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
        throw 'SIGNTOOL_ABSOLUTE_PATH_REQUIRED'
    }
    & $SignToolPath @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "SIGNTOOL_FAILED_$LASTEXITCODE"
    }
}

function Assert-Authenticode([string] $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "AUTHENTICODE_INVALID_$($signature.Status)"
    }
    if ($null -eq $signature.SignerCertificate) {
        throw 'AUTHENTICODE_SIGNER_MISSING'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisherSubject) -or
        -not [string]::Equals($signature.SignerCertificate.Subject, $ExpectedPublisherSubject,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'AUTHENTICODE_PUBLISHER_MISMATCH'
    }
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
        (Normalize-Thumbprint $signature.SignerCertificate.Thumbprint) -ne
        (Normalize-Thumbprint $CertificateThumbprint)) {
        throw 'AUTHENTICODE_THUMBPRINT_MISMATCH'
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw 'AUTHENTICODE_TIMESTAMP_MISSING'
    }
}

$root = [System.IO.Path]::GetFullPath($ArtifactRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw 'SIGNING_ARTIFACT_ROOT_MISSING'
}
if ($Artifacts.Count -eq 0) {
    throw 'SIGNING_ARTIFACT_LIST_EMPTY'
}
$resolvedArtifacts = @($Artifacts | ForEach-Object { Resolve-ControlledArtifact $root $_ } |
    Sort-Object -Unique)
if ($resolvedArtifacts.Count -ne $Artifacts.Count) {
    throw 'SIGNING_ARTIFACT_LIST_DUPLICATE'
}

if ($Mode -eq 'Sign') {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
        [string]::IsNullOrWhiteSpace($ExpectedPublisherSubject)) {
        throw 'PRODUCTION_SIGNING_IDENTITY_REQUIRED'
    }
    if ($null -eq $TimestampUrl -or $TimestampUrl.Scheme -ne 'https') {
        throw 'HTTPS_TIMESTAMP_URL_REQUIRED'
    }
    $storeArguments = if ($CertificateStoreLocation -eq 'LocalMachine') { @('/sm') } else { @() }
    foreach ($artifact in $resolvedArtifacts) {
        Invoke-SignTool (@('sign', '/sha1', (Normalize-Thumbprint $CertificateThumbprint), '/s', 'My') +
            $storeArguments + @('/fd', 'SHA256', '/tr', $TimestampUrl.AbsoluteUri, '/td', 'SHA256', $artifact))
    }
}

foreach ($artifact in $resolvedArtifacts) {
    if ($RequireSignTool -or -not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        Invoke-SignTool @('verify', '/pa', '/all', '/tw', '/v', $artifact)
    }
    Assert-Authenticode $artifact
}

$report = foreach ($artifact in $resolvedArtifacts) {
    $signature = Get-AuthenticodeSignature -LiteralPath $artifact
    [pscustomobject]@{
        FileName = [System.IO.Path]::GetFileName($artifact)
        Sha256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
        PublisherSubject = $signature.SignerCertificate.Subject
        SignerThumbprint = (Normalize-Thumbprint $signature.SignerCertificate.Thumbprint)
        Timestamped = $null -ne $signature.TimeStamperCertificate
        Verified = $true
    }
}
$report | ConvertTo-Json -Depth 3
