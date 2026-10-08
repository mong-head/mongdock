# mongdock 배포용 빌드: dist\ 에 두 가지를 만든다.
#   - zip (휴대용): .NET 설치 없이 실행되는 self-contained 단일 mongdock.exe (크다, dist\mongdock-portable)
#   - setup.exe   : .NET 8 Desktop Runtime 을 쓰는 framework-dependent 단일 mongdock.exe (작다, dist\mongdock-setup).
#                   런타임이 없으면 설치 프로그램이 마이크로소프트에서 받아 설치한다 (installer\mongdock.iss).
#   powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version v0.2.0
# 설치 프로그램은 Inno Setup 6 (ISCC.exe) 이 있을 때만 만든다. 없으면 경고 후 zip 만.
# 릴리스 노트: src\mongdock\Changelog.json 에서 -Version 항목을 읽어 dist\release-notes-vX.Y.Z.md 를 만든다 (gh release create --notes-file 용).
#   노트만 만들기: powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version v0.3.0 -NotesOnly
param([string]$Version = "", [switch]$NotesOnly)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$out = Join-Path $dist "mongdock-portable"       # zip 용 (self-contained)
$outSetup = Join-Path $dist "mongdock-setup"     # setup.exe 용 (framework-dependent)

# --- 릴리스 노트 (Changelog.json) ---
function Write-ReleaseNotes([string]$tag) {
    # v0.3.0-test → 0.3.0 으로 찾음 (파일 이름·설치 파일 이름은 받은 태그 그대로)
    $core = $tag.TrimStart('v', 'V') -replace '[-+ ].*$', ''
    $path = Join-Path $root "src\mongdock\Changelog.json"
    $json = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $release = $json.versions | Where-Object { $_.version -eq $core } | Select-Object -First 1
    if (-not $release) {
        Write-Warning "Changelog.json 에 버전 $core 항목이 없어 릴리스 노트를 만들지 않았습니다. src\mongdock\Changelog.json 에 추가하세요."
        return
    }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("## 설치 / 업데이트")
    $lines.Add("- **``mongdock-$tag-setup.exe``** 를 받아 실행하세요 (권장). 실행 중인 mongdock 을 알아서 끄고 덮어쓰며, 설정은 그대로 유지됩니다. 관리자 권한이 필요 없습니다. (.NET 8 Desktop Runtime 이 없는 PC 는 처음 한 번 마이크로소프트에서 받아 설치하며, 이때만 관리자 권한 확인 창이 뜹니다.)")
    $lines.Add("- 설치 없이 쓰려면 (런타임 포함, 큼) ``mongdock-$tag-win-x64.zip`` 을 풀어 ``mongdock.exe`` 실행.")
    $lines.Add("- 코드 서명이 없어 `"Windows의 PC 보호`" 창이 뜨면 `"추가 정보`" → `"실행`".")
    $lines.Add("- 바뀐 점은 앱의 설정 → 변경 내역에서도 볼 수 있습니다.")
    # 이번 버전 핵심: headline + major 항목 (major 는 아래 "새 기능" 에서 빼서 겹치지 않게)
    $majors = @($release.entries | Where-Object { $_.major -eq $true })
    if ($release.headline -or $majors.Count -gt 0) {
        $lines.Add("")
        $lines.Add("## 이번 버전 핵심")
        if ($release.headline) { $lines.Add("**$($release.headline)**"); $lines.Add("") }
        foreach ($e in $majors) { $lines.Add("- $($e.text)") }
    }
    foreach ($section in @(@("feature", "새 기능"), @("improvement", "개선"), @("fix", "고친 문제"))) {
        $items = @($release.entries | Where-Object { $_.kind -eq $section[0] -and $_.major -ne $true })
        if ($items.Count -eq 0) { continue }
        $lines.Add("")
        $lines.Add("## $($section[1])")
        foreach ($e in $items) { $lines.Add("- $($e.text)") }
    }
    $issues = @($release.knownIssues | Where-Object { $_ })
    if ($issues.Count -gt 0) {
        $lines.Add("")
        $lines.Add("## 알려진 한계")
        foreach ($i in $issues) { $lines.Add("- $i") }
    }
    if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
    $notes = Join-Path $dist "release-notes-v$core.md"
    [IO.File]::WriteAllText($notes, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "완료: $notes"
}

if ($Version) { Write-ReleaseNotes $Version }
elseif ($NotesOnly) { Write-Warning "-NotesOnly 에는 -Version 이 필요합니다." }
if ($NotesOnly) { return }
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }

$versionArg = if ($Version) { "-p:Version=$($Version.TrimStart('v', 'V'))" } else { $null }
# (dist\mongdock 은 이전 버전 스크립트의 출력 폴더)
foreach ($d in @($out, $outSetup, (Join-Path $dist "mongdock"))) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }

# 1) 휴대용 zip: self-contained 단일 파일 (런타임 포함, 압축)
& $dotnet publish (Join-Path $root "src\mongdock\mongdock.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    $versionArg -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "publish 실패 (self-contained)" }

# 2) 설치 프로그램용: framework-dependent 단일 파일 (.NET 8 Desktop Runtime 은 설치 프로그램이 보장).
#    ReadyToRun: 시작 때 JIT 을 줄인다 (exe 약 +2MB, setup 은 압축 후 약 +1MB).
#    파일 구성(mongdock.exe + README.md + Fonts\OFL.txt)이 이전 self-contained 설치와 같은 이름이라 덮어쓰기로 바뀐다.
& $dotnet publish (Join-Path $root "src\mongdock\mongdock.csproj") -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none `
    $versionArg -o $outSetup --nologo
if ($LASTEXITCODE -ne 0) { throw "publish 실패 (framework-dependent)" }

foreach ($d in @($out, $outSetup)) { Copy-Item (Join-Path $root "README.md") $d }
$name = if ($Version) { "mongdock-$Version-win-x64.zip" } else { "mongdock-win-x64.zip" }
$zip = Join-Path $dist $name
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host ("완료: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

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

& $iscc /Q "/DAppVersion=$appVersion" "/DNumericVersion=$numeric" "/DSourceDir=$outSetup" "/DOutputDir=$dist" "/DOutputName=$setupName" `
    (Join-Path $root "installer\mongdock.iss")
if ($LASTEXITCODE -ne 0) { throw "설치 프로그램 컴파일 실패 (ISCC)" }
Write-Host ("완료: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
