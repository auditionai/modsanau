[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [Parameter(Mandatory = $true)]
    [string] $ExpectedVersion,
    [string] $ExpectedIdentityName = 'AuditionAIModStudio',
    [Parameter(Mandatory = $true)]
    [string] $ExpectedPublisherSubject,
    [switch] $RequireSignature,
    [switch] $AllowUntrustedDevelopmentSignature,
    [string] $ExpectedPublisherThumbprint
)

$ErrorActionPreference = 'Stop'
$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) -or
    [IO.Path]::GetExtension($resolvedPackage) -ne '.msix') {
    throw 'INSTALLER_PACKAGE_INVALID'
}
$packageItem = Get-Item -LiteralPath $resolvedPackage -Force
if (($packageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'INSTALLER_PACKAGE_REPARSE_POINT'
}

Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($resolvedPackage, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -eq 0 -or $entries.Count -gt 4096) {
            throw 'INSTALLER_CONTENT_COUNT_INVALID'
        }

        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $forbiddenExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        @('.pdb', '.cs', '.csproj', '.sln', '.user', '.map', '.pfx', '.p12', '.pem', '.key',
          '.log', '.audproj', '.ab', '.acv', '.keydat', '.ps1', '.cmd', '.bat', '.vbs') |
            ForEach-Object { [void] $forbiddenExtensions.Add($_) }
        $forbiddenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        @('acv.exe', 'texconv.exe', '015.ab', '015.keydat') |
            ForEach-Object { [void] $forbiddenNames.Add($_) }

        foreach ($entry in $entries) {
            $name = $entry.FullName.Replace('\', '/')
            $segments = @($name.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or
                $segments -contains '..' -or $segments -contains '.' -or
                -not $seen.Add($name)) {
                throw 'INSTALLER_CONTENT_PATH_INVALID'
            }
            if ($segments | Where-Object { $_ -in @('.git', 'tests', 'fixtures', 'workspaces') }) {
                throw 'INSTALLER_PRIVATE_DIRECTORY_EXPOSED'
            }
            $leaf = if ($segments.Count -gt 0) { $segments[-1] } else { '' }
            $extension = [IO.Path]::GetExtension($leaf)
            if ($forbiddenExtensions.Contains($extension) -or $forbiddenNames.Contains($leaf)) {
                throw 'INSTALLER_FORBIDDEN_ARTIFACT_EXPOSED'
            }
        }

        $required = @(
            'AppxManifest.xml',
            'AppxBlockMap.xml',
            '[Content_Types].xml',
            'AuditionModStudio.App.exe',
            'AuditionModStudio.App.dll'
        )
        foreach ($requiredEntry in $required) {
            if (-not $seen.Contains($requiredEntry)) {
                throw 'INSTALLER_REQUIRED_ARTIFACT_MISSING'
            }
        }

        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        $reader = [IO.StreamReader]::new($manifestEntry.Open(), [Text.Encoding]::UTF8, $true)
        try {
            [xml] $manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespace.AddNamespace('pkg', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $namespace.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
        $identity = $manifest.SelectSingleNode('/pkg:Package/pkg:Identity', $namespace)
        $application = $manifest.SelectSingleNode('/pkg:Package/pkg:Applications/pkg:Application', $namespace)
        $displayName = $manifest.SelectSingleNode('/pkg:Package/pkg:Properties/pkg:DisplayName', $namespace)
        if ($null -eq $identity -or $identity.Name -ne $ExpectedIdentityName -or
            $identity.Version -ne $ExpectedVersion -or
            $identity.Publisher -ne $ExpectedPublisherSubject -or
            $identity.ProcessorArchitecture -ne 'x64') {
            throw 'INSTALLER_IDENTITY_MISMATCH'
        }
        if ($null -eq $displayName -or $displayName.InnerText -ne 'Audition AI Mod Studio' -or
            $null -eq $application -or $application.Id -ne 'App' -or
            $application.Executable -ne 'AuditionModStudio.App.exe' -or
            $application.EntryPoint -ne 'Windows.FullTrustApplication') {
            throw 'INSTALLER_APPLICATION_IDENTITY_MISMATCH'
        }
        $desktopDependency = $manifest.SelectSingleNode(
            "/pkg:Package/pkg:Dependencies/pkg:TargetDeviceFamily[@Name='Windows.Desktop']", $namespace)
        $runtimeDependency = $manifest.SelectSingleNode(
            "/pkg:Package/pkg:Dependencies/pkg:PackageDependency[@Name='Microsoft.WindowsAppRuntime.2']", $namespace)
        if ($null -eq $desktopDependency -or $desktopDependency.MinVersion -ne '10.0.17763.0' -or
            $null -eq $runtimeDependency -or $runtimeDependency.MinVersion -ne '2.3.1.0' -or
            $runtimeDependency.Publisher -ne
                'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US') {
            throw 'INSTALLER_PREREQUISITE_MISMATCH'
        }
        $dangerousExtensions = @($manifest.SelectNodes(
            "//uap:Extension[@Category='windows.protocol' or @Category='windows.fileTypeAssociation']",
            $namespace))
        if ($dangerousExtensions.Count -ne 0) {
            throw 'INSTALLER_UNAPPROVED_SHELL_EXTENSION'
        }

        $hasEmbeddedSignature = $seen.Contains('AppxSignature.p7x')
        $signature = Get-AuthenticodeSignature -LiteralPath $resolvedPackage
        if ($RequireSignature) {
            $normalizedExpected = ($ExpectedPublisherThumbprint -replace '[^A-Fa-f0-9]', '').ToUpperInvariant()
            $normalizedActual = if ($null -eq $signature.SignerCertificate) { '' } else {
                ($signature.SignerCertificate.Thumbprint -replace '[^A-Fa-f0-9]', '').ToUpperInvariant()
            }
            if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                $null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.Subject -ne $ExpectedPublisherSubject -or
                $normalizedExpected.Length -ne 40 -or $normalizedActual -ne $normalizedExpected -or
                $null -eq $signature.TimeStamperCertificate) {
                throw 'INSTALLER_SIGNATURE_POLICY_FAILED'
            }
            $signingStatus = 'SignedAndTimestamped'
        }
        elseif (-not $hasEmbeddedSignature) {
            $signingStatus = 'UnsignedDevelopment'
        }
        elseif ($null -ne $signature.SignerCertificate -and
            $signature.SignerCertificate.Subject -eq $ExpectedPublisherSubject -and
            ($signature.Status -eq [Management.Automation.SignatureStatus]::Valid -or
             ($AllowUntrustedDevelopmentSignature -and
              $signature.Status -eq [Management.Automation.SignatureStatus]::UnknownError -and
              $signature.StatusMessage -like '*root certificate which is not trusted*'))) {
            $signingStatus = if ($signature.Status -eq [Management.Automation.SignatureStatus]::Valid) {
                'SignedDevelopmentTrusted'
            } else {
                'SignedDevelopmentUntrustedTest'
            }
        }
        else {
            throw 'INSTALLER_UNEXPECTED_SIGNATURE_STATE'
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $stream.Dispose()
}

[pscustomobject]@{
    PolicyStatus = 'PASS'
    FileName = $packageItem.Name
    Size = $packageItem.Length
    Sha256 = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash
    AppVersion = $ExpectedVersion
    Architecture = 'x64'
    SigningStatus = $signingStatus
    EntryCount = $entries.Count
} | ConvertTo-Json -Compress
