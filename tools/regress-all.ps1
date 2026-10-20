# regress-all: 설치 준비 전 전체 회귀 점검 한 번에 (2026-10-18 규칙).
# report-test 의 화면 없는 점검 전부 + live-test 의 화면 밖 점검(사용자 화면에 아무것도 안 보이고 실제 입력 없음)을 차례로 돌리고
# 항목마다 통과/실패 줄 수·종료 코드·시간을 한 표로. 화면이 필요한 시험은 맨 아래 「화면 필요」 목록 (돌리지 않음).
# 쓰기: powershell -NoProfile -ExecutionPolicy Bypass -File tools\regress-all.ps1 [-Out 폴더] [-Data 시험데이터폴더]
# 사용자 설정·레지스트리·몽독 설치본은 건드리지 않음 (시험 데이터 폴더 MONGDOCK_DATA_DIR 만 씀).
param(
    [string]$Out = "",
    [string]$Data = "C:\dev\mongdock-tmp\data43",
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path "C:\dev\mongdock-tmp\regress" (Get-Date -Format "yyyyMMdd-HHmmss") }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$rt = Join-Path $root "tools\report-test\bin\Debug\net8.0-windows10.0.19041.0\report-test.exe"
$lt = Join-Path $root "tools\live-test\bin\Debug\net8.0-windows10.0.19041.0\live-test.exe"

"빌드..."
foreach ($p in @("src\mongdock", "tools\report-test", "tools\live-test")) {
    $b = & dotnet build (Join-Path $root $p) 2>&1 | Select-String -Pattern "오류 \d+개|error" | Select-Object -First 3
    "  $p : $($b -join ' ')"
}
$head = (& git -C $root rev-parse --short HEAD) 2>$null

# 이름, 실행 파일, 인자 (OUT 은 결과 폴더로 바뀜)
$items = @(
    @("가리기·기본", $rt, @()),
    @("둘러보기 카드 (8개 언어)", $rt, @("--tourcheck")),
    @("볼륨·소리 판·최소화 효과", $rt, @("--volumecheck", "OUT\volume")),
    @("시작 순서 (포커스 링)", $rt, @("--focuscheck")),
    @("단축키", $rt, @("--hotkeycheck")),
    @("자동 숨김 독 커서 정지 (#39)", $rt, @("--idlehide")),
    @("라이선스·스토어", $rt, @("--license")),
    @("검색 줄 자리", $rt, @("--spotrows")),
    @("검색 순서", $rt, @("--searchrank")),
    @("문제 신고 캡처", $rt, @("--clipcheck")),
    @("작업 표시줄 AppBar", $rt, @("--appbarcheck")),
    @("루틴 브라우저 계정", $rt, @("--profilecheck")),
    @("0.5.0 검토 점검", $rt, @("--reviewcheck")),
    @("둘러보기 그림 메모리", $rt, @("--tourmem")),
    @("설정 창 모든 페이지 그림", $rt, @("--settings", "OUT\settings")),
    @("상태 판 그림", $rt, @("--panels", "OUT\panels")),
    @("앱 모음 판 그림", $rt, @("--allapps", "OUT\allapps")),
    @("루틴 그림", $rt, @("--routines", "OUT\routines")),
    @("소리 판 넘김·판 6개 열기 (화면 밖)", $lt, @("OUT\soundpanel", "--soundpanel")),
    @("최소화 덮개 시작 시간 (화면 밖)", $lt, @("OUT\overlay", "--overlaytiming")),
    @("갑자기 꺼짐 감시 (#36, 시험 폴더만)", "powershell.exe", @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $root "tools\watchdog-test.ps1"))),
    @("업데이트·제거 때 자식 프로세스 (시험 경로만)", "powershell.exe", @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $root "tools\update-path-test.ps1"), "-Stage", (Join-Path $root "src\mongdock\bin\Debug\net8.0-windows10.0.19041.0")))
)

$rows = @()
$env:MONGDOCK_DATA_DIR = $Data
foreach ($it in $items) {
  $tries = $(if ($it[1] -eq $lt) { 2 } else { 1 }) # 화면 밖 live-test 는 시간 측정이 PC 사용 중이면 흔들림 → 실패면 한 번 더 (표에 「재시도」)
  for ($try = 1; $try -le $tries; $try++) {
    $name = $it[0]; $exe = $it[1]
    $argv = @($it[2] | ForEach-Object { if ($_ -like "OUT*") { $_ -replace '^OUT', $Out } else { $_ } })
    foreach ($a in $argv) { if ($a -like "$Out*" -and -not (Test-Path $a)) { New-Item -ItemType Directory -Force -Path $a | Out-Null } }
    $log = Join-Path $Out (($name -replace '[^\w가-힣]+', '_') + ".txt")
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = (($argv | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::GetEncoding(949)
    $psi.StandardErrorEncoding = [Text.Encoding]::GetEncoding(949)
    $psi.EnvironmentVariables["MONGDOCK_DATA_DIR"] = $Data
    $p = [Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch {}; $code = "시간 초과" } else { $p.WaitForExit(); $code = $p.ExitCode }
    $sw.Stop()
    $outText = $outTask.Result
    $errText = $errTask.Result
    [IO.File]::WriteAllText($log, $outText, [Text.Encoding]::UTF8)
    if ($errText) { [IO.File]::WriteAllText($log + ".err", $errText, [Text.Encoding]::UTF8) }
    $text = $outText -split "`r?`n"
    $pass = @($text | Where-Object { $_ -match '^\s*통과' }).Count
    $fail = @($text | Where-Object { $_ -match '^\s*실패' }).Count
    $saved = @($text | Where-Object { $_ -match '^\s*저장' }).Count
    $crash = @(($text + ($errText -split "`r?`n")) | Where-Object { $_ -match 'Unhandled exception' }).Count
    $ok = ($code -eq 0) -and ($fail -eq 0) -and ($crash -eq 0)
    if (-not $ok -and $try -lt $tries) { "  (재시도) $name — 실패 $fail 줄: $(@($text | Where-Object { $_ -match '^\s*실패' }) -join ' / ')"; continue }
    if ($try -gt 1) { $name = "$name (재시도)" }
    $rows += [pscustomobject]@{ 결과 = $(if ($ok) { "통과" } else { "실패" }); 항목 = $name; 통과줄 = $pass; 실패줄 = $fail; 그림 = $saved; 종료 = $code; 초 = [math]::Round($sw.Elapsed.TotalSeconds) }
    "{0}  {1}  (통과 {2}, 실패 {3}, 그림 {4}, 종료 {5}, {6}초)" -f $(if ($ok) { "통과" } else { "실패" }), $name, $pass, $fail, $saved, $code, [math]::Round($sw.Elapsed.TotalSeconds)
    break
  }
}

$screen = @(
    "최소화 효과 되살리기 깜빡임 (live-test --minimize [--electron]) — 화면을 찍어야 함 (DXGI)",
    "앱 모음 판 열기·닫기·끌기 (live-test OUT, --panelreopen, --dragfolders, --dragfollow, --folderorder, --introzoom)",
    "둘러보기 카드 깜빡임 (live-test --tourflicker), 메모리 시나리오 (--memscenario)",
    "독 아이콘 클릭·창 미리 보기·다른 데스크톱 이동 (#30)",
    "검색 창 열기·실제 입력 결과·속도 (#31)",
    "탐색기 최대화 재현 (#29) — 시험용 가상 데스크톱"
)
$sum = @()
$sum += "regress-all $head  $(Get-Date -Format 'yyyy-MM-dd HH:mm')  결과 폴더 $Out"
$sum += "합계: 항목 $($rows.Count)개 중 통과 $(@($rows | Where-Object { $_.결과 -eq '통과' }).Count), 실패 $(@($rows | Where-Object { $_.결과 -eq '실패' }).Count) / 통과 줄 $(($rows | Measure-Object 통과줄 -Sum).Sum), 실패 줄 $(($rows | Measure-Object 실패줄 -Sum).Sum)"
$sum += ($rows | Format-Table -AutoSize | Out-String).TrimEnd()
$sum += ""
$sum += "화면 필요 (여기서 안 돌림):"
$sum += ($screen | ForEach-Object { "  - $_" })
$sum | Out-File -FilePath (Join-Path $Out "summary.txt") -Encoding utf8
""
$sum
