<#
  run-9.ps1 이 관리자 권한(UAC 1번)으로 부르는 단계. 직접 실행하지 않음.
  인증서 신뢰 → WACK(없으면 오프라인 설치본으로 설치) → WACK 시험 → 패키지 설치(같은 사용자) → 신뢰 저장소의 시험 인증서 제거.
  결과는 $Out\admin-result.txt, WACK 보고서는 $Out\wack-report.xml.
#>
param(
    [Parameter(Mandatory = $true)][string]$Msix,
    [Parameter(Mandatory = $true)][string]$Cer,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Subject
)
$ErrorActionPreference = 'Stop'
$result = Join-Path $Out 'admin-result.txt'
function R($m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; Write-Host $line; Add-Content $result $line -Encoding UTF8 }

try {
    # 1) 신뢰
    Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    R "인증서 신뢰: $Subject"

    # 2) WACK
    $appcert = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
    if (-not (Test-Path $appcert)) {
        $setup = 'C:\dev\mongdock-tmp\wack\layout\winsdksetup.exe'
        if (-not (Test-Path $setup)) { throw "WACK 설치본 없음: $setup" }
        R 'WACK 설치 중 (오프라인 설치본, OptionId.WindowsSoftwareLogoToolkit)…'
        $p = Start-Process $setup -ArgumentList '/features OptionId.WindowsSoftwareLogoToolkit /quiet /norestart /log C:\dev\mongdock-tmp\run9\wack-install.log' -Wait -PassThru
        R "WACK 설치 exit $($p.ExitCode)"
        if (-not (Test-Path $appcert)) { throw 'WACK 설치 뒤에도 appcert.exe 없음' }
    }
    Get-AppxPackage -Name 'mongdock.mongdock' | Remove-AppxPackage -ErrorAction SilentlyContinue # 전에 깐 시험본이 있으면 WACK 와 겹치지 않게
    R 'WACK 시험 시작 (10~20분, 패키지를 스스로 설치·실행·제거)…'
    $report = Join-Path $Out 'wack-report.xml'
    Remove-Item $report -ErrorAction SilentlyContinue
    & $appcert reset | Out-Null
    & $appcert test -appxpackagepath $Msix -reportoutputpath $report *> (Join-Path $Out 'wack-console.txt')
    R "WACK 끝 (exit $LASTEXITCODE)"
    if (Test-Path $report) {
        [xml]$x = Get-Content $report
        R "WACK 결과: $($x.REPORT.OVERALL_RESULT)"
        $failed = $x.SelectNodes('//TEST[@RESULT="FAIL"]')
        foreach ($t in $failed) { R "  실패: $($t.NAME) — $($t.DESCRIPTION)" }
    } else {
        R 'WACK 보고서 없음 — wack-console.txt 확인'
    }

    # 3) 패키지 설치 (같은 사용자 계정에 등록 — 앱 실행은 run-9.ps1 이 explorer 로)
    Add-AppxPackage -Path $Msix
    R "패키지 설치: $((Get-AppxPackage -Name 'mongdock.mongdock').PackageFullName)"
}
catch {
    R "실패: $($_.Exception.Message)"
}
finally {
    # 4) 신뢰 저장소에서 시험 인증서 제거 (이번 것 + #3 때의 옛 CN=mongdock-test). 설치된 패키지는 그대로 남음
    foreach ($s in @($Subject, 'CN=mongdock-test')) {
        Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq $s } |
            ForEach-Object { Remove-Item "Cert:\LocalMachine\TrustedPeople\$($_.Thumbprint)"; R "신뢰 저장소에서 지움: $s ($($_.Thumbprint.Substring(0, 8))…)" }
    }
}
