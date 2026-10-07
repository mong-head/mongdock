# mongdock 배포용 빌드: .NET 설치 없이 실행되는 단일 mongdock.exe 를 만들어 dist\ 에 zip 과 설치 프로그램(setup.exe)으로 묶는다.
#   powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version v0.2.0
# 설치 프로그램은 Inno Setup 6 (ISCC.exe) 이 있을 때만 만든다. 없으면 경고 후 zip 만.
param([string]$Version = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$out = Join-Path $dist "mongdock"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet publish (Join-Path $root "src\MyDock\MyDock.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    $(if ($Version) { "-p:Version=$($Version.TrimStart('v', 'V'))" }) -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "publish 실패" }

Copy-Item (Join-Path $root "README.md") $out
$name = if ($Version) { "mongdock-$Version-win-x64.zip" } else { "mongdock-win-x64.zip" }
$zip = Join-Path $dist $name
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "완료: $zip"

# --- 설치 프로그램 (Inno Setup) ---
$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    Write-Warning "Inno Setup 6 (ISCC.exe) 을 찾지 못해 설치 프로그램은 만들지 않았습니다. zip 만 만들었습니다."
    return
}

# v0.2.0-test → 표시 버전 0.2.0-test, 파일 버전 정보용 숫자 0.2.0
$appVersion = if ($Version) { $Version.TrimStart('v', 'V') } else { "0.0.0" }
$numeric = if ($appVersion -match '^\d+(\.\d+){0,3}') { $Matches[0] } else { "0.0.0" }
$setupName = if ($Version) { "mongdock-$Version-setup" } else { "mongdock-setup" }
$setup = Join-Path $dist "$setupName.exe"
if (Test-Path $setup) { Remove-Item $setup -Force }

& $iscc /Q "/DAppVersion=$appVersion" "/DNumericVersion=$numeric" "/DSourceDir=$out" "/DOutputDir=$dist" "/DOutputName=$setupName" `
    (Join-Path $root "installer\mongdock.iss")
if ($LASTEXITCODE -ne 0) { throw "설치 프로그램 컴파일 실패 (ISCC)" }
Write-Host "완료: $setup"
