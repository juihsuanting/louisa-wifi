# Offline checks for the Windows app (windows\tests\Tests.cs): store data, address parsing,
# data-file compatibility, menu sizing. No Wi-Fi needed; also runs on GitHub Actions.
#   powershell -ExecutionPolicy Bypass -File windows\test.ps1

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path $here -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$stores = Join-Path $repo 'data\stores.json'
$build = Join-Path $here 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null

# Same sources as the app, with the test entry point instead of Program.Main.
$exe = Join-Path $build 'tests.exe'
$sources = @(Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object FullName) + (Join-Path $here 'tests\Tests.cs')
& $csc /nologo /target:exe /codepage:65001 /main:LouisaWiFi.Tests "/out:$exe" `
    "/resource:$stores,LouisaWiFi.stores.json" `
    "/resource:$(Join-Path $here 'stats.html'),LouisaWiFi.stats.html" `
    "/resource:$(Join-Path $repo 'app\AppIcon.png'),LouisaWiFi.icon.png" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Device.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Test build failed" }

# Empty data folder, so the tests never read or touch the real %APPDATA%\LouisaWiFi.
$data = Join-Path $build 'test-data'
if (Test-Path $data) { Remove-Item -Recurse -Force $data }
New-Item -ItemType Directory -Path $data | Out-Null
$env:LOUISAWIFI_DATA = $data

& $exe $stores
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
