<#
  MSIX 자체 서명 시험 설치용 인증서 (#9). 스토어 제출본은 서명하지 않는다(스토어가 서명) — 이 PC 시험 설치에만.
  MSIX 의 Publisher 와 인증서 Subject 가 정확히 같아야 설치되므로, 기본값은 build-msix.ps1 의 Publisher(파트너 센터 값)와 같다.

  사용 (저장소 루트, 사용자가 있을 때 — -Trust·-Remove 는 관리자 확인 창(UAC)이 뜬다):
    powershell -ExecutionPolicy Bypass -File tools\msix\test-cert.ps1                # 만들기(이미 있으면 그대로) + 지문 출력 (UAC 없음)
    powershell -ExecutionPolicy Bypass -File tools\msix\test-cert.ps1 -Trust         # + LocalMachine\TrustedPeople 에 넣기 (UAC)
    powershell -ExecutionPolicy Bypass -File tools\msix\build-msix.ps1 -Version 0.6.0 -CertThumbprint <지문>
    powershell -ExecutionPolicy Bypass -File tools\msix\test-cert.ps1 -Remove        # 시험 끝: 두 저장소에서 지우기 (UAC)

  Claude 데스크톱 셸은 MSIX 가상화가 걸리므로 C:\dev\mongdock-tmp 의 .cmd 를 explorer 로 띄워 실행한다.
  인증서는 30일 뒤 만료되고 이름(FriendlyName)에 "delete after test" 가 붙는다.
#>
param(
    [string]$Subject = 'CN=6AB51F71-FBBA-4876-8509-2C6D3952ED98',
    [switch]$Trust,
    [switch]$Remove,
    [string]$CerPath = 'C:\dev\mongdock-tmp\mongdock-test.cer'
)

$ErrorActionPreference = 'Stop'

if ($Remove) {
    $thumbs = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.FriendlyName -like '*delete after test*' } | ForEach-Object { $_.Thumbprint })
    foreach ($t in $thumbs) { Remove-Item "Cert:\CurrentUser\My\$t" -DeleteKey } # 개인 키 파일까지
    if ($thumbs.Count -gt 0) {
        $list = ($thumbs | ForEach-Object { "'$_'" }) -join ','
        Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList "-NoProfile -Command foreach (`$t in @($list)) { Remove-Item Cert:\LocalMachine\TrustedPeople\`$t -ErrorAction SilentlyContinue }"
    }
    "지운 시험 인증서: $($thumbs.Count)개"
    return
}

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.FriendlyName -like '*delete after test*' -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Subject -KeyUsage DigitalSignature `
        -FriendlyName 'mongdock MSIX test (delete after test)' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddDays(30)
}
Export-Certificate -Cert $cert -FilePath $CerPath -Force | Out-Null
"지문: $($cert.Thumbprint)  ($Subject, 만료 $($cert.NotAfter.ToString('yyyy-MM-dd')))"

if ($Trust) {
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList "-NoProfile -Command Import-Certificate -FilePath '$CerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    $ok = [bool](Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })
    "TrustedPeople 에 넣기: $(if ($ok) { '됨' } else { "안 됨 (exit $($p.ExitCode))" })"
}
