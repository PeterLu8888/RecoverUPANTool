# Build RecoverUPANTool with the .NET Framework C# compiler (no SDK required).
$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw "csc.exe not found - .NET Framework 4.x is required" }

$root = $PSScriptRoot
& $csc /nologo /target:exe /platform:x64 /optimize+ /out:"$root\RawImager.exe" "$root\src\RawImager.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed: RawImager.cs" }

& $csc /nologo /target:exe /platform:x64 /optimize+ /out:"$root\Verify.exe" "$root\src\Verify.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed: Verify.cs" }

Write-Host "built: RawImager.exe, Verify.exe"
