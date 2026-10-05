# =====================================================================
#  Build the VCClient launcher (single-file C# -> winexe)
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File build.ps1
#    powershell -ExecutionPolicy Bypass -File build.ps1 -DeployTo <official package dir>
#
#  Output: VCClient.exe (in this directory). With -DeployTo it is also
#          copied next to main.exe as VCClient-launcher.exe (a distinct
#          name so it does not collide with the official VCClient.exe).
#
#  NOTE: keep this file ASCII-only so Windows PowerShell 5.1 parses it
#        regardless of the console code page.
# =====================================================================

param(
    # Official package directory (the one containing main.exe).
    # When given, the built exe is deployed there as well.
    [string]$DeployTo = ''
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $here 'VcClientApp.cs'
$out  = Join-Path $here 'VCClient.exe'

# Try the C# compilers shipped with .NET Framework (no extra install needed)
$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$csc = $null
foreach ($c in $cscCandidates) {
    if (Test-Path $c) { $csc = $c; break }
}

if (-not $csc) {
    Write-Host "[ERROR] csc.exe not found (.NET Framework compiler)" -ForegroundColor Red
    Write-Host "        Expected under C:\Windows\Microsoft.NET\Framework64\v4.0.30319\"
    exit 1
}

if (-not (Test-Path $src)) {
    Write-Host "[ERROR] source not found: $src" -ForegroundColor Red
    exit 1
}

Write-Host "Compiler : $csc"
Write-Host "Source   : $src"
Write-Host "Output   : $out"
Write-Host ""

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /out:"$out" `
    /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Management.dll `
    "$src"

if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAILED] compile error, exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "[OK] VCClient.exe built ($size KB)" -ForegroundColor Green
Write-Host ""

# Optional deployment to the official package directory
if ($DeployTo) {
    if (-not (Test-Path $DeployTo)) {
        Write-Host "[WARN] deploy target not found: $DeployTo" -ForegroundColor Yellow
        exit 0
    }
    if (-not (Test-Path (Join-Path $DeployTo 'main.exe'))) {
        Write-Host "[WARN] no main.exe in $DeployTo - is that really the official package dir?" -ForegroundColor Yellow
    }
    $dest = Join-Path $DeployTo 'VCClient-launcher.exe'
    Copy-Item $out $dest -Force
    Write-Host "[OK] deployed to $dest" -ForegroundColor Green
} else {
    Write-Host "Deploy: copy VCClient.exe next to main.exe (as VCClient-launcher.exe),"
    Write-Host "        or re-run with -DeployTo <official package dir>"
}
