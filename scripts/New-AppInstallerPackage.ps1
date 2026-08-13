[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [Parameter(Mandatory = $true)]
    [string] $ArtifactRoot,
    [ValidateSet('Development', 'Production')]
    [string] $Mode = 'Development',
    [string] $PublisherSubject = 'CN=Unsigned',
    [string] $PublisherDisplayName = 'Audition AI Mod Studio Unsigned Development',
    [string] $SignToolPath,
    [string] $CertificateThumbprint,
    [uri] $TimestampUrl
)

$ErrorActionPreference = 'Stop'
$parsedVersion = $null
$invalidVersionComponents = @()
if ([Version]::TryParse($Version, [ref] $parsedVersion)) {
    $versionComponents = @(
        $parsedVersion.Major,
        $parsedVersion.Minor,
        $parsedVersion.Build,
        $parsedVersion.Revision)
    $invalidVersionComponents = @($versionComponents | Where-Object { $_ -lt 0 -or $_ -gt 65535 })
}
if (-not [Version]::TryParse($Version, [ref] $parsedVersion) -or
    $Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
    $invalidVersionComponents.Count -ne 0) {
    throw 'INSTALLER_VERSION_INVALID'
}

$root = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\', '/')
if ($root -eq [IO.Path]::GetPathRoot($root)) {
    throw 'INSTALLER_ARTIFACT_ROOT_INVALID'
}
if (Test-Path -LiteralPath $root) {
    if (-not (Test-Path -LiteralPath $root -PathType Container) -or
        (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1)) {
        throw 'INSTALLER_ARTIFACT_ROOT_NOT_EMPTY'
    }
}
else {
    New-Item -ItemType Directory -Path $root | Out-Null
}

if ($Mode -eq 'Production') {
    if ([string]::IsNullOrWhiteSpace($PublisherSubject) -or
        $PublisherSubject -in @('CN=Unsigned', 'CN=Audition AI Mod Studio Development', 'CN=AppPublisher') -or
        $PublisherSubject.Length -gt 512 -or $PublisherSubject.IndexOfAny(@([char]34, [char]13, [char]10)) -ge 0 -or
        [string]::IsNullOrWhiteSpace($PublisherDisplayName) -or $PublisherDisplayName.Length -gt 128 -or
        $PublisherDisplayName.IndexOfAny(@([char]13, [char]10)) -ge 0) {
        throw 'PRODUCTION_INSTALLER_PUBLISHER_REQUIRED'
    }
    if (-not [IO.Path]::IsPathRooted($SignToolPath) -or
        -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
        throw 'SIGNTOOL_ABSOLUTE_PATH_REQUIRED'
    }
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'CERTIFICATE_THUMBPRINT_INVALID'
    }
    if ($null -eq $TimestampUrl -or $TimestampUrl.Scheme -ne 'https') {
        throw 'HTTPS_TIMESTAMP_URL_REQUIRED'
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\AuditionModStudio.App\AuditionModStudio.App.csproj'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$powerShell = (Get-Process -Id $PID).Path
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$stagingRoot = Join-Path $tempBase ('AuditionModStudio-Installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

try {
    $packageName = "AuditionAI-Mod-Studio-$Version-win-x64"
    $arguments = @(
        'msbuild', $projectPath,
        '-p:Configuration=Release',
        '-p:Platform=x64',
        '-p:RuntimeIdentifier=win-x64',
        '-p:GenerateAppxPackageOnBuild=true',
        "-p:AppxPackageDir=$stagingRoot\",
        '-p:UapAppxPackageBuildMode=SideloadOnly',
        '-p:AppxBundle=Never',
        '-p:AppxSymbolPackageEnabled=false',
        "-p:AppxPackageName=$packageName",
        '-p:InstallerPackageIdentityName=AuditionAIModStudio',
        "-p:InstallerPackageVersion=$Version",
        "-p:InstallerPublisherSubject=$PublisherSubject",
        "-p:InstallerPublisherDisplayName=$PublisherDisplayName",
        "-p:Version=$Version",
        "-p:FileVersion=$Version",
        "-p:AssemblyVersion=$Version",
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:SelfContained=true',
        '-p:RestoreLockedMode=true',
        '-nologo'
    )

    if ($Mode -eq 'Production') {
        $payloadReport = Join-Path $root 'application-signing-report.json'
        $arguments += @(
            '-p:InstallerSignPayload=true',
            "-p:InstallerPowerShellPath=$powerShell",
            "-p:InstallerSignToolPath=$SignToolPath",
            "-p:InstallerPayloadSigningReportPath=$payloadReport",
            '-p:AppxPackageSigningEnabled=true',
            "-p:PackageCertificateThumbprint=$CertificateThumbprint",
            "-p:AppxPackageSigningTimestampServerUrl=$($TimestampUrl.AbsoluteUri)",
            '-p:AppxPackageSigningTimestampDigestAlgorithm=SHA256',
            '-p:AppxPackageSigningDigestAlgorithm=SHA256'
        )
    }
    else {
        $arguments += '-p:AppxPackageSigningEnabled=false'
    }

    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "INSTALLER_BUILD_FAILED_$LASTEXITCODE"
    }

    $packages = @(Get-ChildItem -LiteralPath $stagingRoot -Recurse -File -Filter *.msix |
        Where-Object { $_.BaseName -eq $packageName })
    if ($packages.Count -ne 1) {
        throw 'INSTALLER_BUILD_OUTPUT_INVALID'
    }
    $destination = Join-Path $root $packages[0].Name
    Copy-Item -LiteralPath $packages[0].FullName -Destination $destination

    if ($Mode -eq 'Production') {
        & (Join-Path $PSScriptRoot 'Invoke-AppCodeSigning.ps1') -Mode Verify `
            -ArtifactRoot $root -Artifacts @($destination) -SignToolPath $SignToolPath `
            -CertificateThumbprint $CertificateThumbprint -ExpectedPublisherSubject $PublisherSubject -RequireSignTool |
            Set-Content -LiteralPath (Join-Path $root 'installer-signing-report.json') -Encoding utf8
    }

    & (Join-Path $PSScriptRoot 'Test-AppInstallerPackage.ps1') -PackagePath $destination `
        -ExpectedVersion $Version -ExpectedPublisherSubject $PublisherSubject `
        -RequireSignature:($Mode -eq 'Production') -ExpectedPublisherThumbprint $CertificateThumbprint
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
    if ($resolvedStaging.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStaging -PathType Container)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
