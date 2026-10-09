<#
  #9 끝: 스토어판 몽독 끄기·제거 → 설정·Run 키 되돌리기 → 원래 몽독(설치판) 다시 실행.
  보통 UAC 없음. 관리자 단계가 중간에 끊겨 신뢰 저장소에 시험 인증서가 남아 있을 때만 UAC 1번으로 지움.
  run-9-cleanup.cmd 로 (사용자 더블클릭 또는 explorer). 로그: C:\dev\mongdock-tmp\run9\run9.log
#>
$ErrorActionPreference = 'Continue'
$out = 'C:\dev\mongdock-tmp\run9'
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'run9.log'
function Say($m) { $line = "$(Get-Date -Format 'HH:mm:ss') [cleanup] $m"; Write-Host $line; Add-Content $log $line -Encoding UTF8 }
$regularExe = Join-Path $env:LOCALAPPDATA 'Programs\mongdock\mongdock.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$backup = Join-Path $out 'backup'
$subjects = @('CN=6AB51F71-FBBA-4876-8509-2C6D3952ED98', 'CN=mongdock-test')

# 1) 스토어판 끄기 (정상 종료 = 작업 표시줄·알림 팝업 원래대로). 별칭이 없으면 설치판 exe 로 같은 종료 신호
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\mongdock.exe'
if (Get-Process mongdock -ErrorAction SilentlyContinue) {
    if (Test-Path $alias) { & $alias --exit } elseif (Test-Path $regularExe) { & $regularExe --exit }
    for ($i = 0; $i -lt 30 -and (Get-Process mongdock -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
}
$killed = $false
Get-Process mongdock -ErrorAction SilentlyContinue | ForEach-Object { Say "강제 종료 pid $($_.Id)"; Stop-Process -Id $_.Id -Force; $killed = $true }
if ($killed -and (Test-Path $regularExe)) {
    Start-Sleep 1
    & $regularExe --restore-taskbar # 강제 종료면 작업 표시줄 숨김·자동 숨김 기록이 남으므로 되돌림
    Start-Sleep 2
    Say '작업 표시줄 복원 (--restore-taskbar)'
}

# 2) 패키지 제거 + 확인
Get-AppxPackage -Name 'mongdock.mongdock' | ForEach-Object { Remove-AppxPackage $_.PackageFullName }
Say "패키지: $(if (Get-AppxPackage -Name 'mongdock.mongdock') { '남아 있음 — 설정 → 앱에서 mongdock 제거 필요' } else { '제거됨' })"

# 3) 설정 되돌리기 (스토어판 첫 실행이 StartWithWindows·시작 질문을 저장하고, QA 중 바꾼 설정도 있으므로)
if (Test-Path $backup) {
    $absent = @()
    if (Test-Path (Join-Path $backup 'absent.txt')) { $absent = @((Get-Content (Join-Path $backup 'absent.txt') -Encoding UTF8) | Where-Object { $_ }) }
    foreach ($f in 'settings.json', 'calendars.json', 'notifications-hidden.json', 'stats.json') {
        $dst = Join-Path $env:APPDATA "mongdock\$f"
        $src = Join-Path $backup $f
        if (Test-Path $src) { Copy-Item $src $dst -Force; Say "되돌림: $f" }
        elseif ($absent -contains $f -and (Test-Path $dst)) { Remove-Item $dst -Force; Say "시험 중 새로 생긴 $f 지움" }
    }

    # 4) Run 키 되돌리기 (스토어판이 시작 앱으로 이어받으며 지움). 백업이 비었으면 설정 기준으로 다시 등록
    $value = ''
    if (Test-Path (Join-Path $backup 'run-key.txt')) { $value = (Get-Content (Join-Path $backup 'run-key.txt') -Raw -Encoding UTF8).Trim() }
    if (-not $value -and (Test-Path $regularExe)) {
        try {
            $s = Get-Content (Join-Path $env:APPDATA 'mongdock\settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($s.startWithWindows) { $value = "`"$regularExe`"" }
        } catch { }
    }
    if ($value) { Set-ItemProperty $runKey -Name mongdock -Value $value; Say "Run 키: $value" } else { Say 'Run 키: 없음 (원래 없었음)' }

    # 다음 run-9 가 새로 백업하도록 이번 백업은 시각을 붙여 보관
    Rename-Item $backup ("backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
} else {
    Say '백업 폴더 없음 — 설정·Run 키는 그대로 둠'
}

# 5) 시험 인증서: CurrentUser 는 바로(개인 키까지), LocalMachine 에 남아 있으면 그때만 UAC 1번
foreach ($s in $subjects) {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $s -and $_.FriendlyName -like '*delete after test*' } |
        ForEach-Object { Remove-Item "Cert:\CurrentUser\My\$($_.Thumbprint)" -DeleteKey; Say "CurrentUser 시험 인증서 지움: $s" }
}
if (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $subjects -contains $_.Subject }) {
    Say '신뢰 저장소에 시험 인증서가 남아 있음 → 관리자 확인 창 1번으로 지움'
    $list = ($subjects | ForEach-Object { "'$_'" }) -join ','
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList "-NoProfile -Command Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { @($list) -contains `$_.Subject } | Remove-Item"
}

# 6) 원래 몽독 다시 실행 (explorer 경유 — 비관리자)
if (Test-Path $regularExe) { Start-Process explorer.exe "`"$regularExe`""; Say '설치판 몽독 다시 실행' }
Start-Sleep 6
$run = (Get-ItemProperty $runKey -Name mongdock -ErrorAction SilentlyContinue).mongdock
$certsLeft = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople, Cert:\CurrentUser\My | Where-Object { $subjects -contains $_.Subject }).Count
Say "요약: 실행 중 = $((Get-Process mongdock -ErrorAction SilentlyContinue | ForEach-Object { $_.Path }) -join ', ') / 패키지 = $(if (Get-AppxPackage -Name 'mongdock.mongdock') { '남음' } else { '없음' }) / Run 키 = '$run' / 시험 인증서 남은 수 = $certsLeft"
