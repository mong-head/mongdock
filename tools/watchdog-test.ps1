# 감시 프로세스(#36) 시험: 시험 데이터 폴더(MONGDOCK_DATA_DIR)만 씀 — 작업 표시줄 기록이 없어 진짜 작업 표시줄·애니메이션은 안 건드림.
# 가짜 「몽독」(powershell, 정해진 종료 코드로 끝남) 의 pid 로 세션 표시를 써 두고 mongdock.exe --watchdog 을 돌려 로그를 확인.
# 다시 켜기는 dev 빌드 mongdock.exe --crash-restart — 설치본이 실행 중이면 단일 실행 표시 때문에 조용히 끝남(그것도 확인).
param([string]$Exe = "$PSScriptRoot\..\src\mongdock\bin\Debug\net8.0-windows10.0.19041.0\mongdock.exe")
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$pass = 0; $fail = 0
function Check($what, $ok) { if ($ok) { $script:pass++; "통과  $what" } else { $script:fail++; "실패  $what" } }

function RunCase($name, $code, $sleepSec, $clean, $restartsBefore) {
    $data = Join-Path $env:TEMP ("mongdock-wd-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force "$data\cache" | Out-Null
    if ($restartsBefore -gt 0) {
        $times = @(1..$restartsBefore | ForEach-Object { (Get-Date).AddMinutes(-1).ToString('o') })
        ConvertTo-Json -InputObject $times | Set-Content "$data\cache\watchdog-restarts.json" -Encoding utf8
    }
    $fake = Start-Process powershell.exe -ArgumentList "-NoProfile -Command `"Start-Sleep $sleepSec; exit $code`"" -WindowStyle Hidden -PassThru
    if (-not $clean) {
        $started = $fake.StartTime.ToString('o')
        $exiting = $(if ($name -eq 'exiting') { ',"exiting":true' } else { '' })
        "{""pid"":$($fake.Id),""started"":""$started"",""ver"":""0.0.0""$exiting}" | Set-Content "$data\cache\session.json" -Encoding utf8
    }
    $env:MONGDOCK_DATA_DIR = $data
    $wd = Start-Process $Exe -ArgumentList "--watchdog $($fake.Id)" -PassThru -WindowStyle Hidden
    if (-not $wd.WaitForExit(($sleepSec + 15) * 1000)) { $wd.Kill(); "실패  $name : 감시 프로세스가 안 끝남"; $script:fail++; return }
    Start-Sleep -Milliseconds 1500
    Remove-Item Env:\MONGDOCK_DATA_DIR
    $log = if (Test-Path "$data\logs\mongdock.log") { Get-Content "$data\logs\mongdock.log" -Encoding UTF8 } else { @() }
    $text = $log -join "`n"
    $log | Where-Object { $_ -match '감시' } | ForEach-Object { "    [$name] " + $_.Substring([Math]::Min(24, $_.Length)) }
    switch ($name) {
        'crash' {
            Check "크래시(0xC0000374): 정상 종료 없이 끝남 기록" ($text -match '정상 종료 없이 끝남 \(종료 코드 0xC0000374\)')
            Check "크래시: 작업 표시줄 되돌리기 시도(기록 없음 — 시험 폴더)" ($text -match '감시: 갑자기 꺼짐: 작업 표시줄 상태 기록 없음')
            Check "크래시: 몽독 다시 켬" ($text -match '감시: 몽독 다시 켬')
            Check "크래시: 다시 켠 몽독은 설치본이 있어 조용히 끝남(창·깨우기 없음)" (-not ($text -match '확인 카드|mongdock 시작'))
        }
        'killed' {
            Check "작업 끝내기(코드 1): 되돌림은 함" ($text -match '종료 코드 0x00000001')
            Check "작업 끝내기: 다시 켜지 않음" ($text -match '크래시 코드 아님' -and -not ($text -match '몽독 다시 켬'))
        }
        'clean' { Check "정상 종료: 아무 기록 없음" (-not ($text -match '감시:')) }
        'early' { Check "시작 20초 안 크래시: 다시 켜지 않음" ($text -match '시작 20초 안에 꺼짐' -and -not ($text -match '몽독 다시 켬')) }
        'exiting' { Check "끝내는 중에 끝남: 되돌리기만, 다시 켜지 않음" ($text -match '끝내는 중에 끝남' -and $text -match '감시: 끝내는 중 끝남: 작업 표시줄 상태 기록 없음' -and -not ($text -match '몽독 다시 켬')) }
        'loop' { Check "10분에 3번 넘음: 다시 켜지 않음" ($text -match '넘게 꺼짐' -and -not ($text -match '몽독 다시 켬')) }
    }
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}

# 'crash' 는 dev 빌드를 --crash-restart 로 정말 켬 — 설치본이 실행 중(단일 실행 표시가 있음)일 때만 (없으면 dev 몽독이 진짜 작업 표시줄을 숨길 수 있음, 리뷰)
$m = $null
if ([Threading.Mutex]::TryOpenExisting('Local\mongdock.SingleInstance', [ref]$m)) { $m.Dispose(); RunCase 'crash' -1073740940 21 $false 0 }
else { "건너뜀  크래시 경우 (실행 중인 몽독 없음 — 다시 켜기 시험이 진짜 몽독을 띄우게 됨)" }
RunCase 'exiting' -1073740940 21 $false 0
RunCase 'killed' 1 21 $false 0
RunCase 'clean' 0 2 $true 0
RunCase 'early' -1073740940 2 $false 0
RunCase 'loop' -1073740940 21 $false 3
"합계: 통과 $pass, 실패 $fail"
if ($fail -gt 0) { exit 1 }
