[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Execute,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ($Configuration -ne 'Release') {
    throw 'NATIVE_AOT_RELEASE_ONLY'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\AuditionModStudio.App\AuditionModStudio.App.csproj'
$projectXml = Get-Content -LiteralPath $projectPath -Raw -ErrorAction Stop
$adoptedInProject = $projectXml -match '<PublishAot>\s*true\s*</PublishAot>'

if (-not $Execute) {
    [pscustomobject]@{
        Configuration = $Configuration
        Project = 'src/AuditionModStudio.App/AuditionModStudio.App.csproj'
        PublishAotAdopted = $adoptedInProject
        EvaluationStatus = if ($adoptedInProject) { 'UNAPPROVED_ADOPTION_DETECTED' } else { 'EVALUATED_NOT_ADOPTED' }
        RequiredWarningCodes = @('IL2026', 'IL3050')
    } | ConvertTo-Json
    if ($adoptedInProject) { exit 1 }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory) -or -not [IO.Path]::IsPathRooted($OutputDirectory)) {
    throw 'NATIVE_AOT_OUTPUT_MUST_BE_ABSOLUTE'
}
if ($adoptedInProject) {
    throw 'NATIVE_AOT_PROJECT_ALREADY_ADOPTED'
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if ($outputPath.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'NATIVE_AOT_OUTPUT_MUST_BE_OUTSIDE_REPOSITORY'
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$arguments = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishAot=true',
    '-p:WindowsPackageType=None',
    '-p:EnableMsixTooling=false',
    '-o', $outputPath,
    '--nologo'
)
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "NATIVE_AOT_PUBLISH_FAILED_EXIT_$LASTEXITCODE"
}

[pscustomobject]@{
    Configuration = 'Release'
    Project = 'src/AuditionModStudio.App/AuditionModStudio.App.csproj'
    OutputDirectory = $outputPath
    EvaluationStatus = 'PUBLISH_SUCCEEDED_RUNTIME_MATRIX_REQUIRED'
} | ConvertTo-Json
