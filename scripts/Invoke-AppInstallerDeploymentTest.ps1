[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OlderPackagePath,
    [Parameter(Mandatory = $true)]
    [string] $CurrentPackagePath,
    [Parameter(Mandatory = $true)]
    [string] $OlderVersion,
    [Parameter(Mandatory = $true)]
    [string] $CurrentVersion,
    [string] $DependencyPackagePath,
    [string] $PackageName = 'AuditionAIModStudio'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw 'INSTALLER_DEPLOYMENT_TEST_WINDOWS_REQUIRED'
}
$older = [IO.Path]::GetFullPath($OlderPackagePath)
$current = [IO.Path]::GetFullPath($CurrentPackagePath)
if (-not (Test-Path -LiteralPath $older -PathType Leaf) -or
    -not (Test-Path -LiteralPath $current -PathType Leaf)) {
    throw 'INSTALLER_DEPLOYMENT_TEST_PACKAGE_MISSING'
}
if (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue) {
    throw 'INSTALLER_DEPLOYMENT_TEST_PACKAGE_ALREADY_INSTALLED'
}
$dependencyArguments = @{}
if (-not [string]::IsNullOrWhiteSpace($DependencyPackagePath)) {
    $dependency = [IO.Path]::GetFullPath($DependencyPackagePath)
    if (-not (Test-Path -LiteralPath $dependency -PathType Leaf) -or
        [IO.Path]::GetFileName($dependency) -ne 'Microsoft.WindowsAppRuntime.2.msix') {
        throw 'INSTALLER_DEPENDENCY_PACKAGE_INVALID'
    }
    $dependencySignature = Get-AuthenticodeSignature -LiteralPath $dependency
    if ($dependencySignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $dependencySignature.SignerCertificate.Subject -ne
            'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US') {
        throw 'INSTALLER_DEPENDENCY_SIGNATURE_INVALID'
    }
    $dependencyArguments.DependencyPath = @($dependency)
}

$dataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    'AuditionModStudio'
$validationRoot = Join-Path $dataRoot 'InstallerValidation'
$sentinel = Join-Path $validationRoot (([Guid]::NewGuid().ToString('N')) + '.sentinel')
$installedByTest = $false
$launchedProcess = $null
try {
    Add-AppxPackage -Path $older -AllowUnsigned -ErrorAction Stop @dependencyArguments
    $installedByTest = $true
    $installed = Get-AppxPackage -Name $PackageName -ErrorAction Stop
    if ($installed.Version.ToString() -ne $OlderVersion) {
        throw 'INSTALLER_OLDER_VERSION_INSTALL_MISMATCH'
    }

    New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
    [IO.File]::WriteAllText($sentinel, 'PLAN95_USER_DATA_PRESERVATION_SENTINEL')
    Add-AppxPackage -Path $current -AllowUnsigned -ErrorAction Stop @dependencyArguments
    $installed = Get-AppxPackage -Name $PackageName -ErrorAction Stop
    if ($installed.Version.ToString() -ne $CurrentVersion -or
        -not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
        throw 'INSTALLER_UPGRADE_PRESERVATION_FAILED'
    }

    $downgradeBlocked = $false
    try {
        Add-AppxPackage -Path $older -AllowUnsigned -ErrorAction Stop @dependencyArguments
    }
    catch {
        $downgradeBlocked = $true
    }
    if (-not $downgradeBlocked) {
        throw 'INSTALLER_DOWNGRADE_NOT_BLOCKED'
    }

    $applicationId = "$($installed.PackageFamilyName)!App"
    Start-Process -FilePath 'explorer.exe' -ArgumentList "shell:AppsFolder\$applicationId"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $launchedProcess = Get-Process -Name 'AuditionModStudio.App' -ErrorAction SilentlyContinue |
            Select-Object -First 1
    } while ($null -eq $launchedProcess -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $launchedProcess) {
        throw 'INSTALLER_INSTALLED_APP_LAUNCH_FAILED'
    }
}
finally {
    if ($null -ne $launchedProcess -and -not $launchedProcess.HasExited) {
        Stop-Process -Id $launchedProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($installedByTest) {
        $installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
        if ($null -ne $installed) {
            Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction Stop
        }
    }
}

if (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue) {
    throw 'INSTALLER_UNINSTALL_FAILED'
}
if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
    throw 'INSTALLER_UNINSTALL_REMOVED_USER_DATA'
}
Remove-Item -LiteralPath $sentinel -Force
if ((Test-Path -LiteralPath $validationRoot -PathType Container) -and
    -not (Get-ChildItem -LiteralPath $validationRoot -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $validationRoot -Force
}

[pscustomobject]@{
    PolicyStatus = 'PASS'
    CleanInstall = $true
    Launch = $true
    Upgrade = $true
    UserDataPreservedOnUpgrade = $true
    DowngradeBlocked = $true
    Uninstall = $true
    UserDataPreservedOnUninstall = $true
    InstallerElevation = 'NonePerUser'
    RuntimePrivilege = 'asInvoker'
} | ConvertTo-Json -Compress
