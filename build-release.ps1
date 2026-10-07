# mongdock 배포용 빌드: .NET 설치 없이 실행되는 단일 mongdock.exe 를 만들어 dist\ 에 zip 으로 묶는다.
#   powershell -ExecutionPolicy Bypass -File build-release.ps1
param([string]$Version = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist\mongdock"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet publish (Join-Path $root "src\MyDock\MyDock.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "publish 실패" }

Copy-Item (Join-Path $root "README.md") $out
$name = if ($Version) { "mongdock-$Version-win-x64.zip" } else { "mongdock-win-x64.zip" }
$zip = Join-Path $root "dist\$name"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "완료: $zip"
