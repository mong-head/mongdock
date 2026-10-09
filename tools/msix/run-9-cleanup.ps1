<#
  #9 끝: 스토어판 몽독 끄기·제거 → Run 키 되돌리기 → 원래 몽독(설치판) 다시 실행. UAC 없음.
  run-9-cleanup.cmd 로 (사용자 더블클릭 또는 explorer). 로그: C:\dev\mongdock-tmp\run9\run9.log
#>
$ErrorActionPreference = 'Continue'
$out = 'C:\dev\mongdock-tmp\run9'
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'run9.log'
function Say($m) { $line = "$(Get-Date -Format 'HH:mm:ss') [cleanup] $m"; Write-Host $line; Add-Content $log $line -Encoding UTF8 }
$regularExe = Join-Path $env:LOCALAPPDATA 'Programs\mongdock\mongdock.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# 1) 스토어판 끄기 (실행 별칭 → --exit, 작업 표시줄 등 원래대로) 후 제거
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\mongdock.exe'
if (Get-Process mongdock -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\WindowsApps\*' }) {
    if (Test-Path $alias) { & $alias --exit }
    for ($i = 0; $i -lt 30 -and (Get-Process mongdock -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
}
Get-Process mongdock -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\WindowsApps\*' } | ForEach-Object { Say "강제 종료 pid $($_.Id)"; Stop-Process -Id $_.Id -Force }
Get-AppxPackage -Name 'mongdock.mongdock' | ForEach-Object { Remove-AppxPackage $_.PackageFullName; Say "패키지 제거: $($_.PackageFullName)" }

# 2) Run 키 되돌리기 (스토어판이 시작 앱으로 이어받으며 지움)
$saved = Join-Path $out 'backup\run-key.txt'
if (Test-Path $saved) {
    $value = (Get-Content $saved -Raw -Encoding UTF8).Trim()
    if ($value) { Set-ItemProperty $runKey -Name mongdock -Value $value; Say "Run 키 되돌림: $value" }
}

# 3) 남은 시험 인증서 (CurrentUser 만 — LocalMachine 은 관리자 단계가 지움. 남아 있으면 알림)
foreach ($s in 'CN=6AB51F71-FBBA-4876-8509-2C6D3952ED98', 'CN=mongdock-test') {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $s -and $_.FriendlyName -like '*delete after test*' } |
        ForEach-Object { Remove-Item "Cert:\CurrentUser\My\$($_.Thumbprint)"; Say "CurrentUser 시험 인증서 지움: $s" }
    if (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq $s }) { Say "주의: LocalMachine\TrustedPeople 에 $s 가 남아 있음 (관리자 권한으로 지워야 함)" }
}

# 4) 원래 몽독 다시 실행 (explorer 경유 — 비관리자)
if (Test-Path $regularExe) { Start-Process explorer.exe "`"$regularExe`""; Say '설치판 몽독 다시 실행' }
Start-Sleep 5
Say "실행 중: $((Get-Process mongdock -ErrorAction SilentlyContinue | ForEach-Object { $_.Path }) -join ', ')"
