<#
  run-9.ps1 이 관리자 권한(UAC 1번)으로 부르는 단계. 직접 실행하지 않음.
  인증서 신뢰 → WACK(없으면 오프라인 설치본으로 설치) → WACK 시험 → 패키지 설치(같은 사용자) → 신뢰 저장소의 시험 인증서 제거.
  WACK 가 실패해도 설치는 진행. 결과는 $Out\admin-result.txt, WACK 보고서는 $Out\wack-report.xml.
#>
param(
    [Parameter(Mandatory = $true)][string]$Msix,
    [Parameter(Mandatory = $true)][string]$Cer,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Subject,
    [string]$ExpectedUser = ''
)
$ErrorActionPreference = 'Stop'
$result = Join-Path $Out 'admin-result.txt'
function Note($m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; Write-Host $line; Add-Content $result $line -Encoding UTF8 }

# 'R' 은 PowerShell 기본 별칭(Invoke-History)이라 함수 이름으로 쓰면 안 됨 — 그래서 Note
Note "시작 (관리자 단계, $([Security.Principal.WindowsIdentity]::GetCurrent().Name))"

Write-Host ''
Write-Host '  ===============================================================' -ForegroundColor Yellow
Write-Host '   몽독 스토어판 시험 중 — 이 창을 닫지 마세요 (10~20분, 끝나면 저절로 닫힘)' -ForegroundColor Yellow
Write-Host '  ===============================================================' -ForegroundColor Yellow
Write-Host ''

try {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ($ExpectedUser -and $me -ne $ExpectedUser) { throw "관리자 계정($me)이 사용자 계정($ExpectedUser)과 달라 패키지가 다른 사람에게 깔림 — 중단" }

    # 1) 신뢰
    Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Note "인증서 신뢰: $Subject"

    # 2) WACK — 실패해도 설치는 계속
    try {
        $appcert = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
        if (-not (Test-Path $appcert)) {
            $setup = 'C:\dev\mongdock-tmp\wack\layout\winsdksetup.exe'
            if (-not (Test-Path $setup)) { throw "WACK 설치본 없음: $setup" }
            Note 'WACK 설치 중 (오프라인 설치본, OptionId.WindowsSoftwareLogoToolkit)…'
            $p = Start-Process $setup -ArgumentList "/features OptionId.WindowsSoftwareLogoToolkit /quiet /norestart /log `"$Out\wack-install.log`"" -Wait -PassThru
            Note "WACK 설치 exit $($p.ExitCode)$(if ($p.ExitCode -eq 3010) { ' (재부팅 필요 표시 — 시험은 보통 그대로 됨)' })"
            if (-not (Test-Path $appcert)) { throw 'WACK 설치 뒤에도 appcert.exe 없음' }
        }
        Get-AppxPackage -Name 'mongdock.mongdock' | Remove-AppxPackage -ErrorAction SilentlyContinue # 전에 깐 시험본이 있으면 WACK 와 겹치지 않게
        Note 'WACK 시험 시작 (10~20분, 패키지를 스스로 설치·실행·제거)…'
        $report = Join-Path $Out 'wack-report.xml'
        Remove-Item $report -ErrorAction SilentlyContinue
        # 네이티브 exe 의 stderr 가 EAP=Stop 에서 예외가 되지 않게 cmd 로 (콘솔 출력도 그대로 파일에)
        # (PS 5.1 이 이 인자 전체를 따옴표로 한 번 감싸 넘기므로 이 형태가 맞음 — 한 겹 더 감싸면 실패, qtest 로 확인)
        cmd /c "`"$appcert`" reset >nul 2>&1"
        cmd /c "`"$appcert`" test -appxpackagepath `"$Msix`" -reportoutputpath `"$report`" > `"$Out\wack-console.txt`" 2>&1"
        Note "WACK 끝 (exit $LASTEXITCODE)"
        if (Test-Path $report) {
            $x = New-Object xml
            $x.Load($report)
            Note "WACK 결과: $($x.REPORT.OVERALL_RESULT)"
            foreach ($t in $x.SelectNodes("//TEST[@RESULT='FAIL' or normalize-space(RESULT)='FAIL']")) {
                $msgs = ($t.SelectNodes('MESSAGES/MESSAGE') | ForEach-Object { $_.TEXT }) -join ' / '
                Note "  실패: $($t.NAME) — $msgs"
            }
        } else {
            Note 'WACK 보고서 없음 — wack-console.txt 확인'
        }
        $left = Get-Process mongdock -ErrorAction SilentlyContinue
        if ($left) { Note "주의: WACK 뒤 몽독 프로세스가 남음 (pid $($left.Id -join ',')) — cleanup 이 작업 표시줄을 복원함" }
    }
    catch {
        Note "WACK 단계 실패 (설치는 계속): $($_.Exception.Message)"
    }

    # 3) 패키지 설치 (같은 사용자 계정에 등록 — 앱 실행은 run-9.ps1 이 explorer 로)
    Get-AppxPackage -Name 'mongdock.mongdock' | Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Path $Msix
    Note "패키지 설치: $((Get-AppxPackage -Name 'mongdock.mongdock').PackageFullName)"
}
catch {
    Note "실패: $($_.Exception.Message)"
}
finally {
    # 4) 신뢰 저장소에서 시험 인증서 제거 (이번 것 + #3 때의 옛 CN=mongdock-test). 설치된 패키지는 그대로 남음
    foreach ($s in @($Subject, 'CN=mongdock-test')) {
        Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq $s } |
            ForEach-Object { Remove-Item "Cert:\LocalMachine\TrustedPeople\$($_.Thumbprint)"; Note "신뢰 저장소에서 지움: $s ($($_.Thumbprint.Substring(0, 8))…)" }
    }
}
