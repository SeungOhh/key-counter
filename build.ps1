# KeyboardCounter build script (ASCII only, so Windows PowerShell reads it correctly
# regardless of the console code page).
#
# Uses the C# compiler that ships with Windows (.NET Framework 4.8) - no SDK to install.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "C# compiler not found: $csc"
}

$src = Join-Path $root 'KeyboardCounter.cs'
$out = Join-Path $root 'KeyboardCounter.exe'

# A running widget holds a lock on the exe, so stop it before overwriting.
$running = Get-Process KeyboardCounter -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running widget (pid $($running.Id -join ', '))"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

Write-Host "Compiling: $src"

# /codepage:65001 is required: the source is UTF-8 without a BOM, and the legacy
# compiler would otherwise decode Korean string literals using the ANSI code page.
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    /out:"$out" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$src"

if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "Build OK: $out ($size KB)"
