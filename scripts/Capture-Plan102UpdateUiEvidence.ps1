[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [switch] $SuccessOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Plan102CaptureNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) }
else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory)) }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
if (-not $outputRoot.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SCREENSHOT_OUTPUT_MUST_BE_UNDER_ARTIFACTS'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$process = Get-Process -Id $ProcessId -ErrorAction Stop
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw 'APP_WINDOW_NOT_FOUND' }
[Plan102CaptureNative]::ShowWindow($handle, 3) | Out-Null
[Plan102CaptureNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 800

function Root { [Windows.Automation.AutomationElement]::FromHandle($handle) }
function Find-Name([string] $name) {
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $name)
    (Root).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Select-Name([string] $name) {
    $item = Find-Name $name
    if ($null -eq $item) { throw "ITEM_NOT_FOUND:$name" }
    $pattern = $null
    if (-not $item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        throw "ITEM_NOT_SELECTABLE:$name"
    }
    ([Windows.Automation.SelectionItemPattern]$pattern).Select()
    Start-Sleep -Milliseconds 900
}
function Invoke-Name([string] $name) {
    $item = Find-Name $name
    if ($null -eq $item) { throw "BUTTON_NOT_FOUND:$name" }
    $pattern = $null
    if (-not $item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw "BUTTON_NOT_INVOKABLE:$name"
    }
    ([Windows.Automation.InvokePattern]$pattern).Invoke()
}
function Save-Window([string] $name) {
    $rect = New-Object Plan102CaptureNative+RECT
    if (-not [Plan102CaptureNative]::GetWindowRect($handle, [ref]$rect)) { throw 'WINDOW_RECT_FAILED' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try { $printed = [Plan102CaptureNative]::PrintWindow($handle, $hdc, 2) }
        finally { $graphics.ReleaseHdc($hdc) }
        if (-not $printed) { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        $path = Join-Path $outputRoot $name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        [pscustomobject]@{ Name=$name; Width=$width; Height=$height; Bytes=(Get-Item $path).Length }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

$utf8 = [Text.Encoding]::UTF8
$settingsName = $utf8.GetString([Convert]::FromBase64String('Q8OgaSDEkeG6t3Q='))
$updateNowName = $utf8.GetString([Convert]::FromBase64String('Q+G6rXAgbmjhuq10IG5nYXk='))
$activityName = $utf8.GetString([Convert]::FromBase64String('Tmjhuq10IGvDvSB0w6FjIHbhu6U='))
Select-Name $settingsName
Start-Sleep -Milliseconds 1200
if ($SuccessOnly) {
    Save-Window '01-settings-no-update.png'
    Save-Window '06-update-success-after-restart.png'
    exit 0
}

$results = @()
$results += Save-Window '02-update-available.png'
Invoke-Name $updateNowName
Start-Sleep -Milliseconds 500
$results += Save-Window '03-download-progress.png'
Start-Sleep -Milliseconds 2600
$results += Save-Window '05-update-failure.png'
Invoke-Name $updateNowName
Start-Sleep -Milliseconds 3100
$results += Save-Window '04-ready-to-restart.png'
Invoke-Name $activityName
Start-Sleep -Milliseconds 500
$results += Save-Window '07-activity-log-update-events.png'
$results | Format-Table -AutoSize
