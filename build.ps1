param(
    [string]$OutputDirectory = "$PSScriptRoot\dist",
    # MSYS2 bash path; on CI, setup-msys2 may install to a non-default location,
    # pass the real path via this parameter.
    [string]$MsysBash = 'C:\msys64\usr\bin\bash.exe'
)

$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'GHide.cs'
$iconPath = Join-Path $PSScriptRoot 'assets\GHide.ico'
$outputPath = Join-Path $OutputDirectory 'GHide.exe'
$nativeDir = Join-Path $PSScriptRoot 'native'
$nativeDll = Join-Path $nativeDir 'taskbar_transparency.dll'

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

# Build the native injection DLL for taskbar transparency (requires MSYS2 / MinGW-w64).
# If MSYS2 is missing, skip with a warning; the main program still builds.
if (Test-Path $MsysBash) {
    # Convert Windows path to MSYS style (D:\a\b -> /d/a/b).
    # Build with Substring to avoid PowerShell expanding $1 inside -replace.
    if (-not $nativeDir) {
        throw 'Cannot resolve native directory.'
    }
    $nativePosix = '/' + $nativeDir.Substring(0, 1).ToLower() + $nativeDir.Substring(2).Replace('\', '/')
    Write-Host "Building native DLL via MSYS2: $nativePosix"
    & $MsysBash -lc "cd '$nativePosix' && ./build_native.sh"
    if ($LASTEXITCODE -ne 0) {
        throw 'Native DLL build failed.'
    }
    Copy-Item -LiteralPath $nativeDll -Destination $OutputDirectory -Force
    Write-Host "Native DLL copied: $(Join-Path $OutputDirectory 'taskbar_transparency.dll')"
}
else {
    Write-Warning 'MSYS2 not found; taskbar_transparency.dll will not be rebuilt. If a prebuilt native\taskbar_transparency.dll exists it will still be embedded.'
}

if (Test-Path $outputPath) {
    Remove-Item -LiteralPath $outputPath
}

# Embed the native DLL into the exe so a single GHide.exe works without
# shipping a side-by-side DLL. Side-by-side DLL still takes priority at runtime.
$resourceOptions = ''
if (Test-Path $nativeDll) {
    $resourceOptions = " /resource:`"$nativeDll`",GHide.taskbar_transparency.dll"
    Write-Host "Embedding taskbar_transparency.dll into GHide.exe (single-file distribution)."
}
else {
    Write-Warning 'taskbar_transparency.dll not found; Win11 taskbar transparency will be unavailable and NOT embedded.'
}

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$parameters = New-Object System.CodeDom.Compiler.CompilerParameters
[void]$parameters.ReferencedAssemblies.Add('System.dll')
[void]$parameters.ReferencedAssemblies.Add('Accessibility.dll')
[void]$parameters.ReferencedAssemblies.Add('System.Drawing.dll')
[void]$parameters.ReferencedAssemblies.Add('System.Windows.Forms.dll')
$parameters.GenerateExecutable = $true
$parameters.GenerateInMemory = $false
$parameters.IncludeDebugInformation = $false
$parameters.OutputAssembly = $outputPath
$parameters.CompilerOptions = "/target:winexe /optimize+ /win32icon:`"$iconPath`"" + $resourceOptions

$result = $provider.CompileAssemblyFromFile($parameters, $sourcePath)
$provider.Dispose()

if ($result.Errors.HasErrors) {
    $result.Errors | ForEach-Object { Write-Error $_.ToString() }
    throw 'Build failed.'
}

Write-Host "Build complete: $outputPath"
