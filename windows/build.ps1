# Builds windows\dist\LouisaWiFi.exe with the C# compiler that comes with Windows (.NET Framework 4.x).
# Nothing to install:   powershell -ExecutionPolicy Bypass -File windows\build.ps1 [-Version 1.2.3]
# Release builds pass the version from the git tag (like VERSION=… ./build.sh for the Mac app);
# without one it uses the latest tag. Works the same on GitHub Actions (windows-latest).

param([string]$Version)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path $here -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "C# compiler (.NET Framework 4.x) not found: $csc" }

# Shared with the Mac app: store list and icon.
$stores = Join-Path $repo 'data\stores.json'
$iconPng = Join-Path $repo 'app\AppIcon.png'

if (-not $Version) {
    $tag = git -C $repo describe --tags --abbrev=0 --match 'v*' 2>$null
    $Version = if ($tag) { $tag.TrimStart('v') } else { '0.0.0' }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'" }

$build = Join-Path $here 'build'
$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force -Path $build, $dist | Out-Null

# Version → assembly attributes (shown in the menu, Settings → Apps and the installer).
@"
[assembly: System.Reflection.AssemblyVersion("$Version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$Version.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Encoding UTF8 (Join-Path $build 'Version.cs')

# app.ico for the exe from app/AppIcon.png (PNG-compressed entries, Windows Vista and later).
Add-Type -AssemblyName System.Drawing
$src = New-Object System.Drawing.Bitmap $iconPng
$images = foreach ($size in 16, 24, 32, 48, 64, 256) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}
# 64×64 copy for the tray icon (drawn at 32×32 and greyed out in code)
$tray = New-Object System.Drawing.Bitmap $src, 64, 64
$trayPng = Join-Path $build 'icon.png'
$tray.Save($trayPng, [System.Drawing.Imaging.ImageFormat]::Png)
$tray.Dispose()
$src.Dispose()
$ico = Join-Path $build 'app.ico'
$w = New-Object IO.BinaryWriter([IO.File]::Create($ico))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $w.Write([byte]($i.Size % 256)); $w.Write([byte]($i.Size % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$i.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $w.Write($i.Bytes) }
$w.Close()

$out = Join-Path $dist 'LouisaWiFi.exe'
$sources = @(Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object FullName) + (Join-Path $build 'Version.cs')
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    "/out:$out" "/win32icon:$ico" "/win32manifest:$(Join-Path $here 'src\app.manifest')" `
    "/resource:$stores,LouisaWiFi.stores.json" `
    "/resource:$(Join-Path $here 'stats.html'),LouisaWiFi.stats.html" `
    "/resource:$trayPng,LouisaWiFi.icon.png" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Device.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $out ($Version, $([int]((Get-Item $out).Length / 1KB)) KB)"
