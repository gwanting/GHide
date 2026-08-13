param(
    [string]$ExecutablePath = "$PSScriptRoot\dist\GHide.exe"
)

$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "TEST FAILED: $Message"
    }
    Write-Host "PASS: $Message"
}

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Executable not found: $ExecutablePath"
}

Add-Type -ReferencedAssemblies Accessibility.dll -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class GHideTestNative
{
    private const uint OBJID_CLIENT = 0xFFFFFFFC;

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr window, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible);

    [DllImport("shcore.dll")]
    public static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);

    public static Accessibility.IAccessible GetAccessibleClient(IntPtr window)
    {
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        Accessibility.IAccessible accessible;
        int result = AccessibleObjectFromWindow(window, OBJID_CLIENT, ref iid, out accessible);
        if (result < 0 || accessible == null)
            Marshal.ThrowExceptionForHR(result);
        return accessible;
    }
}
'@

$fileVersion = (Get-Item -LiteralPath $ExecutablePath).VersionInfo.FileVersion
Assert-True ($fileVersion -eq '1.3.3.0') 'file version is 1.3.3.0'

$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path $ExecutablePath).Path)
$allStatic = [Reflection.BindingFlags]'Static,Public,NonPublic'
$desktopType = $assembly.GetType('DesktopIcons', $true)
$nativeType = $assembly.GetType('NativeMethods', $true)
$shellType = $assembly.GetType('ShellFolderView', $true)
$pointType = $nativeType.GetNestedType('POINT', [Reflection.BindingFlags]'Public,NonPublic')

$watcherType = $assembly.GetType('DesktopMouseWatcher', $true)
$watcher = [Activator]::CreateInstance($watcherType, $true)
try {
    $dispatcherField = $watcherType.GetField('dispatcher', [Reflection.BindingFlags]'Instance,NonPublic')
    $dispatcher = [System.Windows.Forms.Control]$dispatcherField.GetValue($watcher)
    Assert-True $dispatcher.IsHandleCreated 'mouse hook work is dispatched through a message-window handle'
}
finally {
    ([IDisposable]$watcher).Dispose()
}

$findList = $desktopType.GetMethod('FindDesktopListView', $allStatic)
$listView = [IntPtr]$findList.Invoke($null, @())
Assert-True ($listView -ne [IntPtr]::Zero) 'desktop list view is discoverable'

$accessible = [GHideTestNative]::GetAccessibleClient($listView)
try {
    $iconCount = $accessible.accChildCount
    Assert-True ($iconCount -gt 0) "desktop exposes icons through accessibility ($iconCount found)"

    $left = 0; $top = 0; $width = 0; $height = 0
    $accessible.accLocation([ref]$left, [ref]$top, [ref]$width, [ref]$height, 1)
    Assert-True ($width -gt 0 -and $height -gt 0) 'first desktop icon has a valid bounding rectangle'

    $hitTest = $nativeType.GetMethod('TryHitTestAccessibleChild', $allStatic)
    $iconPoint = [Activator]::CreateInstance($pointType)
    $pointType.GetField('X', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($iconPoint, $left + [int]($width / 2))
    $pointType.GetField('Y', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($iconPoint, $top + [int]($height / 2))
    $iconArguments = @($listView, $iconPoint, $false)
    $iconHitSucceeded = [bool]$hitTest.Invoke($null, $iconArguments)
    Assert-True ($iconHitSucceeded -and [bool]$iconArguments[2]) 'first desktop icon is classified as an icon, not blank space'

    $blankFound = $false
    for ($x = $left + $width + 20; $x -lt $left + 1200 -and -not $blankFound; $x += 31) {
        for ($y = $top; $y -lt $top + 800 -and -not $blankFound; $y += 31) {
            $blankPoint = [Activator]::CreateInstance($pointType)
            $pointType.GetField('X', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($blankPoint, $x)
            $pointType.GetField('Y', [Reflection.BindingFlags]'Instance,NonPublic,Public').SetValue($blankPoint, $y)
            $blankArguments = @($listView, $blankPoint, $false)
            if ([bool]$hitTest.Invoke($null, $blankArguments) -and -not [bool]$blankArguments[2]) {
                $blankFound = $true
            }
        }
    }
    Assert-True $blankFound 'desktop list background is classified as blank space'
}
finally {
    if ([Runtime.InteropServices.Marshal]::IsComObject($accessible)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($accessible)
    }
}

$getVisible = $shellType.GetMethod('TryGetIconsVisible', $allStatic)
$setVisible = $shellType.GetMethod('TrySetIconsVisible', $allStatic)
$toggle = $desktopType.GetMethod('Toggle', $allStatic)
$visibleArguments = @($false)
$readInitial = [bool]$getVisible.Invoke($null, $visibleArguments)
$initialVisible = [bool]$visibleArguments[0]
Assert-True $readInitial 'initial desktop icon visibility is readable'

try {
    $toggleResult = [bool]$toggle.Invoke($null, @())
    Start-Sleep -Milliseconds 400
    $afterArguments = @($false)
    $readAfter = [bool]$getVisible.Invoke($null, $afterArguments)
    Assert-True ($toggleResult -and $readAfter -and ([bool]$afterArguments[0] -ne $initialVisible)) 'toggle changes and verifies desktop icon visibility'
}
finally {
    [void]$setVisible.Invoke($null, @($initialVisible))
    Start-Sleep -Milliseconds 400
}

$restoredArguments = @($false)
$readRestored = [bool]$getVisible.Invoke($null, $restoredArguments)
Assert-True ($readRestored -and [bool]$restoredArguments[0] -eq $initialVisible) 'desktop icon visibility is restored after test'

$process = Start-Process -FilePath $ExecutablePath -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 2
    Assert-True (-not $process.HasExited) 'application starts and remains resident'
    $awareness = -1
    $dpiResult = [GHideTestNative]::GetProcessDpiAwareness($process.Handle, [ref]$awareness)
    Assert-True ($dpiResult -eq 0 -and $awareness -eq 2) 'application is per-monitor DPI aware'
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}

$taskbarType = $assembly.GetType('TaskbarTransparency', $true)
$applyTaskbar = $taskbarType.GetMethod('Apply', $allStatic)
$restoreTaskbar = $taskbarType.GetMethod('Restore', $allStatic)
$isAppliedProperty = $taskbarType.GetProperty('IsApplied', $allStatic)
[void]$applyTaskbar.Invoke($null, @())
Start-Sleep -Milliseconds 300
Assert-True ([bool]$isAppliedProperty.GetValue($null, @())) 'taskbar transparency applies without error'
[void]$restoreTaskbar.Invoke($null, @())
Assert-True (-not [bool]$isAppliedProperty.GetValue($null, @())) 'taskbar transparency is restored after test'

$logPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GHide\GHide.log'
Assert-True (Test-Path -LiteralPath $logPath) 'diagnostic log is created'

Write-Host 'ALL TESTS PASSED'
