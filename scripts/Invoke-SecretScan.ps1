[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Paths,
    [long] $MaximumFileBytes = 268435456
)

$ErrorActionPreference = 'Stop'
$allowedExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@('.cs', '.xaml', '.json', '.jsonc', '.xml', '.props', '.targets', '.config', '.md', '.sql', '.ps1',
  '.yml', '.yaml', '.log', '.txt', '.crash', '.dmp', '.dll', '.exe', '.pdb') |
    ForEach-Object { [void] $allowedExtensions.Add($_) }

$privateKeyMarker = '-----BEGIN ' + '(?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
$rules = @(
    @{ Name = 'PRIVATE_KEY'; Pattern = $privateKeyMarker },
    @{ Name = 'PKCS12_PRIVATE_MATERIAL'; Pattern = '(?i)\b(?:pfx|p12)(?:Base64|Bytes|Content)?\b\s*[:=]\s*["''][A-Za-z0-9+/=]{40,}["'']' },
    @{ Name = 'SIGNING_PASSWORD'; Pattern = '(?i)\b(?:signing|certificate|pfx)(?:Password|Passphrase)\b\s*[:=]\s*["''](?!\s*(?:redacted|placeholder|example|fake|test|string\.empty)\b)[^"''\r\n]{8,}["'']' },
    @{ Name = 'CLOUD_SIGNING_TOKEN'; Pattern = '(?i)\b(?:codeSigning|trustedSigning|signingService)(?:Token|Secret|Credential)\b\s*[:=]\s*["''](?!\s*(?:redacted|placeholder|example|fake|test|string\.empty)\b)[^"''\r\n]{12,}["'']' },
    @{ Name = 'OPENAI_STYLE_KEY'; Pattern = '\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}\b' },
    @{ Name = 'GITHUB_TOKEN'; Pattern = '\bgh[pousr]_[A-Za-z0-9]{30,}\b' },
    @{ Name = 'GOOGLE_API_KEY'; Pattern = '\bAIza[A-Za-z0-9_-]{30,}\b' },
    @{ Name = 'JWT_SERVICE_CREDENTIAL'; Pattern = '\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b' },
    @{ Name = 'PRIVILEGED_ASSIGNMENT'; Pattern = '(?i)\b(?:providerApiKey|serviceRoleKey|paymentWebhookSecret|masterEncryptionKey|privateSigningKey)\b\s*[:=]\s*["''](?!\s*(?:redacted|placeholder|example|fake|test|provider-secret|string\.empty)\b)[^"''\r\n]{12,}["'']' },
    @{ Name = 'CONNECTION_PASSWORD'; Pattern = '(?i)(?:^|[;"''])\s*(?:Password|Pwd)\s*=\s*(?!\s*(?:placeholder|example|fake|test|unused|server-secret)(?:;|["'']))[^;\r\n"'']{8,}' }
)

$files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($inputPath in $Paths) {
    $resolved = Resolve-Path -LiteralPath $inputPath -ErrorAction Stop
    $item = Get-Item -LiteralPath $resolved.Path -Force
    if ($item -is [System.IO.FileInfo]) {
        if ($allowedExtensions.Contains($item.Extension)) { $files.Add($item) }
        continue
    }
    $isExplicitBuildRoot = $item.FullName -match '[\\/](?:bin|artifacts)(?:[\\/]|$)'
    Get-ChildItem -LiteralPath $item.FullName -File -Recurse -Force -ErrorAction Stop |
        Where-Object {
            $allowedExtensions.Contains($_.Extension) -and
            $_.FullName -notmatch '[\\/]\.git[\\/]' -and
            ($isExplicitBuildRoot -or $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]')
        } |
        ForEach-Object { $files.Add($_) }
}

$findings = [System.Collections.Generic.List[object]]::new()
foreach ($file in $files | Sort-Object FullName -Unique) {
    if ($file.Length -gt $MaximumFileBytes) {
        Write-Error "Secret scan không thể kiểm tra file vượt giới hạn: $($file.FullName)"
        exit 2
    }
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $representations = @(
        [System.Text.Encoding]::UTF8.GetString($bytes),
        [System.Text.Encoding]::Unicode.GetString($bytes)
    )
    foreach ($rule in $rules) {
        $matched = $false
        foreach ($content in $representations) {
            if ([System.Text.RegularExpressions.Regex]::IsMatch($content, $rule.Pattern,
                    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
                $matched = $true
                break
            }
        }
        if ($matched) {
            $findings.Add([pscustomobject]@{ Rule = $rule.Name; Path = $file.FullName })
        }
    }
    [System.Array]::Clear($bytes, 0, $bytes.Length)
}

if ($findings.Count -gt 0) {
    Write-Error ("Secret scan thất bại. Không in giá trị nhạy cảm. Findings:`n" +
        (($findings | Sort-Object Rule, Path | ForEach-Object { "[$($_.Rule)] $($_.Path)" }) -join "`n"))
    exit 1
}

Write-Output "Secret scan PASS: $($files.Count) file source/build/log/crash đã được kiểm tra."
exit 0
