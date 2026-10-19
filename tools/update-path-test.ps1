# 업데이트 경로 시험 (PM 10-19): 시험 설치 경로에 0.8.3 을 복사해 감시(--watchdog)·미디어 도우미(--media-host)를 가짜 본체에 붙이고,
# 본체가 끝난 직후 설치 프로그램처럼 exe 를 덮어쓰거나 지울 수 있는지, 감시가 다시 켜 버리지 않는지 확인. 창 없음, 사용자 설치본은 안 건드림.
# 경우: A 업데이트(정상 종료 = 세션 표시 없음 → 바로 덮어쓰기) · B 설치 프로그램 강제 종료(taskkill /F 경로, 코드 1) · C 제거(폴더 지우기)
param([string]$Stage = 'C:\dev\mongdock-tmp\stage')
$root = 'C:\dev\mongdock-tmp\updtest'
$pass = 0; $fail = 0
function Check($what, $ok) { if ($ok) { $script:pass++; "통과  $what" } else { $script:fail++; "실패  $what" } }
function HelperAlive($dir) { @(Get-CimInstance Win32_Process -Filter "Name='mongdock.exe'" | Where-Object { $_.ExecutablePath -like "$dir\*" }).Count }
function HelperMutex { $m = $null; if ([Threading.Mutex]::TryOpenExisting('Local\mongdock.Helper', [ref]$m)) { $m.Dispose(); return $true }; return $false }

function Setup($name) {
    $dir = Join-Path $root $name
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$dir\app", "$dir\data\cache" | Out-Null
    Copy-Item "$Stage\*" "$dir\app" -Recurse -Force
    return $dir
}

# 가짜 본체 + 파이프 서버(도우미가 붙을 곳) + 감시 + 도우미
function StartFamily($dir) {
    $fake = Start-Process powershell.exe -ArgumentList '-NoProfile -Command "Start-Sleep 600"' -WindowStyle Hidden -PassThru
    $pipeName = 'mongdock-media-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $server = New-Object IO.Pipes.NamedPipeServerStream($pipeName, 'InOut', 1, 'Byte', 'Asynchronous')
    $env:MONGDOCK_DATA_DIR = "$dir\data"
    $wd = Start-Process "$dir\app\mongdock.exe" -ArgumentList "--watchdog $($fake.Id)" -PassThru -WindowStyle Hidden
    $mh = Start-Process "$dir\app\mongdock.exe" -ArgumentList "--media-host $pipeName $($fake.Id)" -PassThru -WindowStyle Hidden
    Remove-Item Env:\MONGDOCK_DATA_DIR
    $server.WaitForConnectionAsync().Wait(15000) | Out-Null
    $drain = (New-Object IO.StreamReader($server)).ReadToEndAsync()   # 본체처럼 계속 읽음
    Start-Sleep 3
    return @{ Fake = $fake; Server = $server; Wd = $wd; Mh = $mh }
}

function TryReplace($dir, $ms) {
    # 설치 프로그램처럼 exe 덮어쓰기를 $ms 동안 50ms 마다 시도 → 처음 성공한 시각(ms), 실패면 -1
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        try { Copy-Item "$Stage\mongdock.exe" "$dir\app\mongdock.exe" -Force -ErrorAction Stop; return $sw.ElapsedMilliseconds } catch { Start-Sleep -Milliseconds 50 }
    }
    return -1
}

# ── A 업데이트: 설치 프로그램 --exit → 본체 정상 종료(세션 표시 지움) → 단일 실행 표시가 사라지자마자 덮어쓰기 ──
$d = Setup 'A'
$f = StartFamily $d
Check "A 준비: 감시·도우미 2개가 시험 경로에서 돎 ($(HelperAlive "$d\app")개), 도우미 표시(Local\mongdock.Helper) $(HelperMutex)" ((HelperAlive "$d\app") -eq 2)
Stop-Process -Id $f.Fake.Id -Force   # 본체가 끝남 (세션 표시 없음 = 정상 종료로 봄)
$t0 = [Diagnostics.Stopwatch]::StartNew()
$first = TryReplace $d 0   # 바로 (지금 설치 프로그램이 하는 대로)
$waitMs = -1; while ($t0.ElapsedMilliseconds -lt 10000) { if ((HelperAlive "$d\app") -eq 0) { $waitMs = $t0.ElapsedMilliseconds; break }; Start-Sleep -Milliseconds 25 }
$after = TryReplace $d 3000
Check "A 본체가 끝난 뒤 자식이 모두 끝남 ($waitMs ms)" ($waitMs -ge 0 -and $waitMs -lt 5000)
"정보  A 본체가 끝난 바로 그 순간 덮어쓰기: $(if ($first -ge 0) { '됨' } else { '안 됨(자식이 잡고 있음)' })"
Check "A 자식이 끝나길 기다린 뒤 exe 덮어쓰기 ($after ms)" ($after -ge 0)
$log = Get-Content "$d\data\logs\mongdock.log" -Encoding UTF8 -ErrorAction SilentlyContinue
Check "A 감시가 다시 켜지 않음" (-not ($log -match '몽독 다시 켬'))
$f.Server.Dispose()

# ── B 설치 프로그램 강제 종료(taskkill /F /IM mongdock.exe 경로): 자식까지 같이 끝나고 다시 켜지 않음 ──
$d = Setup 'B'
$f = StartFamily $d
'{"pid":' + $f.Fake.Id + ',"started":"' + $f.Fake.StartTime.ToString('o') + '","ver":"0.8.3"}' | Set-Content "$d\data\cache\session.json" -Encoding utf8
& taskkill /F /T /PID $f.Fake.Id 2>$null | Out-Null   # 본체 강제 종료 (코드 1)
foreach ($p in $f.Wd, $f.Mh) { & taskkill /F /PID $p.Id 2>$null | Out-Null }   # /IM mongdock.exe 는 자식도 같이 끝냄 — 시험 경로 것만
Start-Sleep 2
Check "B 강제 종료 뒤 자식 0개" ((HelperAlive "$d\app") -eq 0)
$after = TryReplace $d 2000
Check "B exe 덮어쓰기 ($after ms)" ($after -ge 0)
$f.Server.Dispose()

# ── C 제거: 본체가 끝난 뒤 폴더 지우기 ──
$d = Setup 'C'
$f = StartFamily $d
Stop-Process -Id $f.Fake.Id -Force
$t0 = [Diagnostics.Stopwatch]::StartNew(); while ($t0.ElapsedMilliseconds -lt 10000 -and (HelperAlive "$d\app") -gt 0) { Start-Sleep -Milliseconds 25 }
$removed = $false; try { Remove-Item "$d\app" -Recurse -Force -ErrorAction Stop; $removed = $true } catch { }
Check "C 자식이 끝난 뒤 설치 폴더 지우기" $removed
$f.Server.Dispose()

Get-CimInstance Win32_Process -Filter "Name='mongdock.exe'" | Where-Object { $_.ExecutablePath -like "$root\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*Start-Sleep 600*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
"합계: 통과 $pass, 실패 $fail"
if ($fail -gt 0) { exit 1 }
