[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [string] $OutputDirectory = 'artifacts/plan-101ux-acceptance-vi'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Plan101UxNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
if (-not $outputRoot.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SCREENSHOT_OUTPUT_MUST_BE_UNDER_ARTIFACTS'
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$process = Get-Process -Id $ProcessId -ErrorAction Stop
$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { throw 'APP_WINDOW_NOT_FOUND' }
[Plan101UxNative]::ShowWindow($handle, 3) | Out-Null
[Plan101UxNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 800

function Save-Window([string] $name) {
    $rect = New-Object Plan101UxNative+RECT
    if (-not [Plan101UxNative]::GetWindowRect($handle, [ref]$rect)) { throw 'WINDOW_RECT_FAILED' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try { $printed = [Plan101UxNative]::PrintWindow($handle, $hdc, 2) }
        finally { $graphics.ReleaseHdc($hdc) }
        if (-not $printed) { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        $path = Join-Path $outputRoot $name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        [pscustomobject]@{ Name = $name; Width = $width; Height = $height; Bytes = (Get-Item $path).Length }
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Select-Navigation([string] $name) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $name)
    $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($item in $items) {
        $pattern = $null
        if ($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            ([Windows.Automation.SelectionItemPattern]$pattern).Select()
            Start-Sleep -Milliseconds 900
            return
        }
        if ($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            ([Windows.Automation.InvokePattern]$pattern).Invoke()
            Start-Sleep -Milliseconds 900
            return
        }
    }
    throw "NAVIGATION_ITEM_NOT_FOUND:$name"
}

function Set-PageScroll([double] $verticalPercent) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $candidate = $null
    $candidateArea = 0
    foreach ($item in $all) {
        $pattern = $null
        if (-not $item.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { continue }
        $scroll = [Windows.Automation.ScrollPattern]$pattern
        if (-not $scroll.Current.VerticallyScrollable) { continue }
        $bounds = $item.Current.BoundingRectangle
        $area = $bounds.Width * $bounds.Height
        if ($area -gt $candidateArea) { $candidate = $scroll; $candidateArea = $area }
    }
    if ($null -eq $candidate) { return $false }
    $candidate.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, $verticalPercent)
    Start-Sleep -Milliseconds 900
    return $true
}

function Bring-NamedElementIntoView([string] $namePrefix) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $all) {
        if (-not $item.Current.Name.StartsWith($namePrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $pattern = $null
        if ($item.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
            ([Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
            $parent = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
            while ($null -ne $parent) {
                $scrollPattern = $null
                if ($parent.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scrollPattern)) {
                    $scroll = [Windows.Automation.ScrollPattern]$scrollPattern
                    if ($scroll.Current.VerticallyScrollable) {
                        $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, 100)
                        break
                    }
                }
                $parent = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
            }
            Start-Sleep -Milliseconds 900
            return $true
        }
    }
    return $false
}

function Clear-WorkspaceSearch {
    $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $searchName = ([Text.Encoding]::UTF8).GetString([Convert]::FromBase64String('VMOsbSBraeG6v20gVGV4dHVyZQ=='))
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $searchName)
    $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($item in $items) {
        $pattern = $null
        if ($item.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            ([Windows.Automation.ValuePattern]$pattern).SetValue('')
        }
    }
}

$results = @()
$utf8 = [Text.Encoding]::UTF8
Select-Navigation $utf8.GetString([Convert]::FromBase64String('VHJhbmcgY2jhu6c='))
$results += Save-Window '01-home-pass2-vi.png'
Select-Navigation $utf8.GetString([Convert]::FromBase64String('ROG7sSDDoW4='))
Clear-WorkspaceSearch
$results += Save-Window '02-projects-pass2-vi.png'
$results += Save-Window '03-project-workspace-pass2-vi.png'
if (Bring-NamedElementIntoView 'Build &') { $results += Save-Window '04-build-export-pass2-vi.png' }
Select-Navigation $utf8.GetString([Convert]::FromBase64String('VHLDrG5oIGNo4buJbmggc+G7rWEg4bqjbmg='))
$results += Save-Window '05-editor-pass2-vi.png'
if (Set-PageScroll 48) { $results += Save-Window '06-crop-resize-pass2-vi.png' }
if (Set-PageScroll 100) { $results += Save-Window '07-before-after-pass2-vi.png' }
Select-Navigation 'AI Studio'
$results += Save-Window '08-ai-pass2-vi.png'
Select-Navigation $utf8.GetString([Convert]::FromBase64String('VMOgaSBraG/huqNu'))
$results += Save-Window '09-account-pass2-vi.png'
Select-Navigation $utf8.GetString([Convert]::FromBase64String('Q8OgaSDEkeG6t3Q='))
$results += Save-Window '10-settings-pass2-vi.png'
Select-Navigation $utf8.GetString([Convert]::FromBase64String('VHJhbmcgY2jhu6c='))
$results += Save-Window '11-full-1920-pass2-vi.png'

[Plan101UxNative]::ShowWindow($handle, 9) | Out-Null
[Plan101UxNative]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, 1366, 768, 0x0040) | Out-Null
Start-Sleep -Milliseconds 900
$results += Save-Window '12-1366-pass2-vi.png'
$results | Format-Table -AutoSize
