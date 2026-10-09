<#
  #9 스토어판(MSIX) 시험 한 번에 — 사용자가 run-9.cmd 를 더블클릭 (관리자 확인 창은 중간에 딱 1번).
  1) 지금 몽독(설치판) 끄기, Run 키·설정 백업
  2) 시험 인증서 만들기(CurrentUser, 파트너 센터와 같은 CN) → 최신 코드로 MSIX 빌드·서명
  3) [UAC 1번] run-9-admin.ps1: 인증서 신뢰 → WACK 없으면 오프라인 설치본으로 설치 → WACK 시험 → 패키지 설치 → 신뢰 저장소에서 시험 인증서(옛 CN=mongdock-test 포함) 제거
  4) CurrentUser 의 시험 인증서도 제거, 스토어판 몽독을 explorer 로(비관리자) 실행 → QA 기능 시험
  끝나면 run-9-cleanup.cmd (UAC 없음): 스토어판 끄기·제거, Run 키 되돌리기, 원래 몽독 다시 실행.
  결과·로그: C:\dev\mongdock-tmp\run9\ (run9.log, wack-report.xml, summary.txt, ready.txt)
  Claude 셸에서 실행하지 말 것 (MSIX 가상화) — 사용자 더블클릭 또는 explorer 로.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = 'C:\dev\mongdock-tmp\run9'
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'run9.log'
function Say($m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; Write-Host $line; Add-Content $log $line -Encoding UTF8 }
Remove-Item (Join-Path $out 'ready.txt'), (Join-Path $out 'summary.txt'), (Join-Path $out 'admin-result.txt') -ErrorAction SilentlyContinue

$Subject = 'CN=6AB51F71-FBBA-4876-8509-2C6D3952ED98'
$Pfn = 'mongdock.mongdock_5hcg2nc2bhbrc'
$Aumid = "$Pfn!mongdock"
$regularExe = Join-Path $env:LOCALAPPDATA 'Programs\mongdock\mongdock.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

try {
    Say "#9 시작 (저장소 $root)"
    if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Say '주의: 관리자 창에서 실행됨 — 보통 창(더블클릭)에서 실행해야 앱이 사용자 권한으로 뜸'
    }

    # 1) 지금 몽독 끄기 + 백업
    if (Get-Process mongdock -ErrorAction SilentlyContinue) {
        if (Test-Path $regularExe) { & $regularExe --exit }
        for ($i = 0; $i -lt 30 -and (Get-Process mongdock -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    }
    if (Get-Process mongdock -ErrorAction SilentlyContinue) { throw '몽독이 꺼지지 않음 — 트레이에서 종료 후 다시 실행' }
    Say '설치판 몽독 끔'
    $backup = Join-Path $out 'backup'
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach ($f in 'settings.json', 'calendars.json') {
        $src = Join-Path $env:APPDATA "mongdock\$f"
        if (Test-Path $src) { Copy-Item $src (Join-Path $backup $f) -Force }
    }
    $runValue = (Get-ItemProperty $runKey -Name mongdock -ErrorAction SilentlyContinue).mongdock
    Set-Content (Join-Path $backup 'run-key.txt') ($(if ($runValue) { $runValue } else { '' })) -Encoding UTF8
    Say "백업: settings·calendars, Run 키 = '$runValue' (스토어판은 Run 키를 시작 앱으로 이어받으며 지움 → cleanup 이 되돌림)"

    # 2) 시험 인증서(CurrentUser) + 빌드·서명
    & (Join-Path $PSScriptRoot 'test-cert.ps1') -Subject $Subject -CerPath (Join-Path $out 'test.cer') | ForEach-Object { Say $_ }
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if (-not $cert) { throw '시험 인증서를 만들지 못함' }
    Say "MSIX 빌드·서명 (2~3분)…"
    & (Join-Path $PSScriptRoot 'build-msix.ps1') -Version 0.6.0 -CertThumbprint $cert.Thumbprint *> (Join-Path $out 'build.log')
    $msix = Join-Path $root 'dist\msix\mongdock-0.6.0-x64.msix'
    if (-not (Test-Path $msix)) { throw "MSIX 없음: $msix" }
    $sig = Get-AuthenticodeSignature $msix
    Say "MSIX 서명: $($sig.Status) / $($sig.SignerCertificate.Subject)"

    # 3) 관리자 단계 (UAC 1번)
    Say '관리자 확인 창이 뜹니다 → [예] (인증서 신뢰·WACK·패키지 설치. WACK 시험은 10~20분)'
    $admin = Join-Path $PSScriptRoot 'run-9-admin.ps1'
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$admin`"",
        '-Msix', "`"$msix`"", '-Cer', "`"$(Join-Path $out 'test.cer')`"", '-Out', "`"$out`"", '-Subject', "`"$Subject`"")
    Say "관리자 단계 끝 (exit $($p.ExitCode))"
    if (Test-Path (Join-Path $out 'admin-result.txt')) { Get-Content (Join-Path $out 'admin-result.txt') -Encoding UTF8 | ForEach-Object { Say "  $_" } }

    # 4) CurrentUser 시험 인증서 제거 (옛 CN 포함) — 설치된 패키지는 그대로
    foreach ($s in @($Subject, 'CN=mongdock-test')) {
        Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $s -and $_.FriendlyName -like '*delete after test*' } |
            ForEach-Object { Remove-Item "Cert:\CurrentUser\My\$($_.Thumbprint)"; Say "CurrentUser 시험 인증서 지움: $s" }
    }

    $pkg = Get-AppxPackage -Name 'mongdock.mongdock' -ErrorAction SilentlyContinue
    if (-not $pkg) { throw '스토어판 패키지가 설치되지 않음 — admin-result.txt 확인' }
    Say "설치됨: $($pkg.PackageFullName)"
    Start-Process explorer.exe "shell:AppsFolder\$Aumid"
    for ($i = 0; $i -lt 40 -and -not (Get-Process mongdock -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\WindowsApps\*' }); $i++) { Start-Sleep -Milliseconds 500 }
    $running = Get-Process mongdock -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\WindowsApps\*' }
    Say "스토어판 실행: $(if ($running) { "pid $($running.Id)" } else { '확인 못 함' })"
    Set-Content (Join-Path $out 'ready.txt') "ready $(Get-Date -Format s) $($pkg.PackageFullName)" -Encoding UTF8
    Say '준비 끝 — QA 기능 시험 차례. 다 끝나면 tools\msix\run-9-cleanup.cmd'
}
catch {
    Say "실패: $($_.Exception.Message)"
    Say '되돌리려면 tools\msix\run-9-cleanup.cmd 를 실행 (원래 몽독 다시 켜짐)'
}
