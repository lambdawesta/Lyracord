# Builds Lyracord (Release) for ARM (phones) or x64 (PCs) and writes signed sideload packages.
# Usage: powershell -ExecutionPolicy Bypass -File tools\build.ps1 -Platform ARM [-OutDir <dir>]
param([ValidateSet("ARM","x64")][string]$Platform = "ARM", [string]$OutDir = "")
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq "CN=Lyracord" | Select-Object -First 1
if (-not $cert) { throw "Code signing certificate CN=Lyracord not found in CurrentUser\My" }
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $OutDir) { $OutDir = Join-Path $root "..\..\build-lumicord" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
# MSBuild breaks on paths with spaces: use 8.3 short paths
$fso = New-Object -ComObject Scripting.FileSystemObject
$sln = $fso.GetFile((Join-Path $root "Lyracord.sln")).ShortPath
$out = $fso.GetFolder((Resolve-Path $OutDir)).ShortPath
& $msb $sln /t:Restore "/p:Configuration=Release" "/p:Platform=$Platform" /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { & $msb $sln /t:Restore "/p:Configuration=Release" "/p:Platform=$Platform" /nologo /v:minimal }
# /m:1: the toolkit projects write the same files when built in parallel
& $msb $sln /t:Build "/p:Configuration=Release" "/p:Platform=$Platform" /m:1 /nologo /v:minimal "/p:AppxBundle=Never" "/p:UapAppxPackageBuildMode=SideloadOnly" "/p:PackageCertificateKeyFile=" "/p:PackageCertificateThumbprint=$($cert.Thumbprint)" "/p:AppxPackageDir=$out\pkg-$Platform\\"
"exit=$LASTEXITCODE"
