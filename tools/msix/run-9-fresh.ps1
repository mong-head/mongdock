<#
  #9 추가: 스토어판을 "처음 설치한 사람" 상태로 다시 켜기 (run-9 로 스토어판이 깔린 뒤, ready.txt 다음). UAC 없음.
  스토어판 --exit → %APPDATA%\mongdock 폴더 통째로 C:\dev\mongdock-tmp\run9\fresh-aside\mongdock 로 옮김 → explorer 로 스토어판 다시 실행.
  → 빈 데이터 폴더라 첫 실행 흐름: 첫 안내(작업 표시줄 숨김 안내 → 알림 허용 카드 → …) → 마무리, 시작 질문(스토어판), 통계 배너, 기본 독 구성.
  되돌리기는 run-9-cleanup.cmd 가 함: 시험으로 생긴 폴더는 run9\fresh-used-<시각> 으로 보관하고 fresh-aside 를 제자리로 → 백업 4개·Run 키 복원.
  run9\backup(run-9 가 만든 백업)이 없으면 시작하지 않음.
#>
$ErrorActionPreference = 'Stop'
$out = 'C:\dev\mongdock-tmp\run9'
$log = Join-Path $out 'run9.log'
function Say($m) { $line = "$(Get-Date -Format 'HH:mm:ss') [fresh] $m"; Write-Host $line; Add-Content $log $line -Encoding UTF8 }
$data = Join-Path $env:APPDATA 'mongdock'
$aside = Join-Path $out 'fresh-aside'
$Aumid = 'mongdock.mongdock_5hcg2nc2bhbrc!mongdock'
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\mongdock.exe'

try {
    if (-not (Test-Path (Join-Path $out 'backup\run-key.txt'))) { throw 'run9\backup 이 없음 — run-9.cmd 를 먼저' }
    if (-not (Get-AppxPackage -Name 'mongdock.mongdock')) { throw '스토어판 패키지가 없음 — run-9.cmd 를 먼저' }
    if (Test-Path (Join-Path $aside 'mongdock')) { throw "이미 옮겨 둔 폴더가 있음: $aside\mongdock — cleanup 뒤에 다시" }

    # 1) 스토어판 정상 종료 (작업 표시줄·알림 팝업 원래대로)
    if (Get-Process mongdock -ErrorAction SilentlyContinue) {
        if (Test-Path $alias) { & $alias --exit }
        for ($i = 0; $i -lt 30 -and (Get-Process mongdock -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    }
    if (Get-Process mongdock -ErrorAction SilentlyContinue) { throw '몽독이 꺼지지 않음 — 트레이에서 종료 후 다시' }
    Say '스토어판 끔'

    # 2) 데이터 폴더 통째로 옆으로 (설정·캘린더·숨긴 알림·통계·라이선스 캐시·아이콘·소리·로그·캐시 — 첫 실행 판단에 쓰는 것 모두)
    New-Item -ItemType Directory -Force $aside | Out-Null
    if (Test-Path $data) {
        Move-Item $data (Join-Path $aside 'mongdock')
        Say "옮김: $data → $aside\mongdock"
    } else {
        New-Item -ItemType File (Join-Path $aside 'was-empty.txt') -Force | Out-Null
        Say '원래 데이터 폴더 없음 (표시만 남김)'
    }

    # 3) 처음 설치한 상태로 스토어판 실행
    Start-Process explorer.exe "shell:AppsFolder\$Aumid"
    for ($i = 0; $i -lt 40 -and -not (Get-Process mongdock -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Say "처음 상태로 실행: $(if (Get-Process mongdock -ErrorAction SilentlyContinue) { '됨' } else { '확인 못 함 — 시작 메뉴에서 mongdock' })"
    Say '끝나면 run-9-cleanup.cmd (원래 폴더를 제자리로 돌림)'
}
catch {
    Say "실패: $($_.Exception.Message)"
}
