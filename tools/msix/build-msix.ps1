<#
  mongdock MSIX 패키지 만들기 (스토어 제출·설치 시험용). build-release.ps1 과 따로 돈다.

  사용 (저장소 루트):
    powershell -ExecutionPolicy Bypass -File tools\msix\build-msix.ps1 -Version 0.4.2
    # 시험 설치용 서명까지:
    powershell -ExecutionPolicy Bypass -File tools\msix\build-msix.ps1 -Version 0.4.2 -CertThumbprint <CurrentUser\My 인증서 지문>

  결과: dist\msix\mongdock-<버전>-x64.msix (레이아웃 폴더 dist\msix\layout 은 매번 새로 만듦)

  필요한 것: Windows SDK 의 makeappx.exe·makepri.exe·signtool.exe.
    -SdkBin 으로 폴더를 주거나, 없으면 C:\dev\mongdock-tmp\sdk-buildtools (NuGet Microsoft.Windows.SDK.BuildTools 를 압축만 푼 것)
    → Windows Kits\10\bin 순서로 찾는다. 앱 의존성(NuGet 참조)은 추가하지 않는다.

  스토어 제출 때는 파트너 센터의 "제품 ID" 화면 값으로 -IdentityName / -Publisher / -PublisherDisplayName 을 넘기고
  서명 없이 만든다(스토어가 서명). 기본값 CN=mongdock-test 는 이 PC 시험용.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$IdentityName = 'mongdock',
    [string]$Publisher = 'CN=mongdock-test',
    [string]$PublisherDisplayName = 'mong-head',
    [string]$CertThumbprint = '',
    [string]$SdkBin = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outDir = Join-Path $root 'dist\msix'
$layout = Join-Path $outDir 'layout'

# --- 버전: 0.4.2 / v0.4.2 → 0.4.2.0 (MSIX 는 네 자리, 스토어는 마지막 자리가 0 이어야 함) ---
$core = $Version.TrimStart('v', 'V') -replace '[-+ ].*$', ''
$parts = @($core.Split('.') | ForEach-Object { [int]$_ })
while ($parts.Count -lt 4) { $parts += 0 }
if ($parts.Count -gt 4 -or $parts[3] -ne 0) { throw "버전은 a.b.c 형식이어야 합니다 (받은 값: $Version)" }
$msixVersion = $parts -join '.'

# --- SDK 도구 찾기 ---
function Find-SdkBin {
    $candidates = @()
    if ($SdkBin) { $candidates += $SdkBin }
    $candidates += Get-ChildItem 'C:\dev\mongdock-tmp\sdk-buildtools' -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | ForEach-Object { $_.DirectoryName }
    $candidates += Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | ForEach-Object { $_.DirectoryName }
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'makeappx.exe')) { return $c } }
    throw 'makeappx.exe 를 찾지 못했습니다. -SdkBin 으로 Windows SDK bin\x64 폴더를 지정하세요.'
}
$bin = Find-SdkBin
$makeappx = Join-Path $bin 'makeappx.exe'
$makepri = Join-Path $bin 'makepri.exe'
$signtool = Join-Path $bin 'signtool.exe'
Write-Host "SDK 도구: $bin"

# --- 1. self-contained publish (단일 파일 아님: MSIX 는 파일 단위로 압축·차등 업데이트) ---
# .NET 위성 리소스는 한국어·영어만 (나머지 언어 폴더는 크기만 늘림).
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet publish (Join-Path $root 'src\mongdock\mongdock.csproj') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:SatelliteResourceLanguages=ko%3Ben -p:Version=$core -o $layout -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish 실패' }

# --- 2. 로고 + 매니페스트 ---
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icon\make-icon.ps1') -MsixAssets (Join-Path $layout 'Assets')
if ($LASTEXITCODE -ne 0) { throw '로고 생성 실패' }

$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppxManifest.xml'), [Text.Encoding]::UTF8)
$esc = { param($s) [Security.SecurityElement]::Escape($s) }
$manifest = $manifest.Replace('{{IdentityName}}', (& $esc $IdentityName)).Replace('{{Publisher}}', (& $esc $Publisher)).
    Replace('{{PublisherDisplayName}}', (& $esc $PublisherDisplayName)).Replace('{{Version}}', $msixVersion)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))

# --- 3. resources.pri (scale-*/targetsize-* 로고를 고르게 해 줌) ---
# 레이아웃 전체를 색인하면 .NET 위성 어셈블리 폴더(ko 등)를 언어로 보고 pri 를 언어별로 쪼갠다 → 로고만 따로 색인.
$priRoot = Join-Path $outDir 'pri'
New-Item -ItemType Directory -Force $priRoot | Out-Null
Copy-Item (Join-Path $layout 'Assets') $priRoot -Recurse
Copy-Item (Join-Path $layout 'AppxManifest.xml') $priRoot
$priConfig = Join-Path $outDir 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq ko-KR /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig 실패' }
# 리소스 팩(언어·배율별 pri)으로 쪼개지 않고 resources.pri 하나로
$cfg = [IO.File]::ReadAllText($priConfig) -replace '(?s)\s*<packaging>.*?</packaging>', ''
[IO.File]::WriteAllText($priConfig, $cfg)
& $makepri new /pr $priRoot /cf $priConfig /mn (Join-Path $priRoot 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri new 실패' }

# --- 4. 포장 (makeappx 가 매니페스트 스키마 검증도 함) ---
$msix = Join-Path $outDir "mongdock-$core-x64.msix"
& $makeappx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack 실패' }

# --- 5. (선택) 시험용 서명 ---
if ($CertThumbprint) {
    & $signtool sign /fd SHA256 /sha1 $CertThumbprint /s My $msix
    if ($LASTEXITCODE -ne 0) { throw 'signtool sign 실패' }
}

$mb = [Math]::Round((Get-Item $msix).Length / 1MB, 1)
$layoutMb = [Math]::Round(((Get-ChildItem $layout -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "완료: $msix ($mb MB, 풀린 크기 $layoutMb MB, 버전 $msixVersion, 게시자 $Publisher)"
