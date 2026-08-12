[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactRoot,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    [string[]] $ResourceRelativePaths = @(),
    [string[]] $CompanionRelativePaths = @()
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'CLIENT_INTEGRITY_ROOT_MISSING' }
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CLIENT_INTEGRITY_MANIFEST_OUTSIDE_ROOT'
}

$items = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($specification in @(
    @{ Kind = 'resource_bundle'; Paths = $ResourceRelativePaths },
    @{ Kind = 'companion_tool'; Paths = $CompanionRelativePaths }
)) {
    foreach ($relativePath in $specification.Paths) {
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains(':') -or $relativePath -match '(^|[\\/])\.\.([\\/]|$)') {
            throw 'CLIENT_INTEGRITY_RELATIVE_PATH_INVALID'
        }
        $normalized = $relativePath.Replace('\', '/')
        if (-not $seen.Add($normalized)) { throw 'CLIENT_INTEGRITY_DUPLICATE_PATH' }
        $candidate = [IO.Path]::GetFullPath((Join-Path $root $relativePath))
        if (-not $candidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw 'CLIENT_INTEGRITY_ARTIFACT_MISSING'
        }
        $current = $root
        foreach ($segment in $normalized.Split('/')) {
            $current = Join-Path $current $segment
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'CLIENT_INTEGRITY_REPARSE_REJECTED'
            }
        }
        $file = Get-Item -LiteralPath $candidate
        $items.Add([ordered]@{
            relativePath = $normalized
            kind = $specification.Kind
            contentLength = $file.Length
            sha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        })
    }
}
if ($items.Count -eq 0 -or $items.Count -gt 512) { throw 'CLIENT_INTEGRITY_ARTIFACT_COUNT_INVALID' }

$manifest = [ordered]@{
    schemaVersion = 1
    artifacts = @($items | Sort-Object { $_.relativePath })
} | ConvertTo-Json -Depth 4 -Compress
$parent = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
[IO.File]::WriteAllText($output, $manifest, [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    ManifestPath = $output
    ManifestSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    ArtifactCount = $items.Count
    ProductionBindingStatus = 'EXPECTED_HASH_MUST_BE_BOUND_BY_SIGNED_RELEASE'
} | ConvertTo-Json
