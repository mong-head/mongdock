# 사용자 화면에 남은 시험 창 검사 (PM 10-23: qa-rt-A/B/C 탐색기 창 15개가 사용자 화면에 남았음).
# 시험이 연 것만 찾음: 시험 폴더(mongdock-tmp·임시 폴더의 mongdock-*·qa-*·data-*)를 연 탐색기 창, 시험 프로그램(live-test·report-test·rtest) 창,
# 설치본이 아닌 경로의 mongdock(시험 데이터 몽독). -Close 면 그것들을 닫음 (사용자 창·설치본 몽독은 건드리지 않음).
# 출력 마지막 줄: "남은 시험 창 N개 (닫음 M개)". 개인 정보 없이 — 탐색기는 시험 폴더 경로만, 그 밖은 프로세스 이름만.
param([switch]$Close)

$found = @()
$closed = 0
$testPath = '(?i)\\mongdock-tmp(\\|$)|\\Temp\\mongdock-|\\qa-[^\\]*(\\|$)|\\data-[^\\]*(\\|$)|\\mongdock-(guard|seed|repair-test|recycle)'

# 1) 시험 폴더를 연 탐색기 창
try {
    $shell = New-Object -ComObject Shell.Application
    foreach ($w in @($shell.Windows())) {
        try {
            $url = [string]$w.LocationURL
            if (-not $url.StartsWith('file:///')) { continue }
            $path = [Uri]::UnescapeDataString($url.Substring(8)).Replace('/', '\')
            if ($path -notmatch $testPath) { continue }
            $found += "탐색기: $path"
            if ($Close) { $w.Quit(); $closed++ }
        } catch { }
    }
} catch { $found += "(탐색기 창 목록을 못 읽음: $($_.Exception.GetType().Name))" }

# 2) 시험 프로그램 창·시험 데이터 몽독
$installed = Join-Path $env:LOCALAPPDATA 'Programs\mongdock\mongdock.exe'
foreach ($p in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in 'live-test', 'report-test', 'rtest', 'tabread', 'vdtest', 'mongdock' })) {
    $exe = $null
    try { $exe = $p.Path } catch { }
    if ($p.ProcessName -eq 'mongdock') {
        if (-not $exe -or $exe -ieq $installed -or $exe -like '*\WindowsApps\*') { continue } # 설치본·스토어판 = 사용자 것
    }
    $hasWindow = $p.MainWindowHandle -ne [IntPtr]::Zero
    $found += "프로세스: $($p.ProcessName) (pid $($p.Id)$(if ($hasWindow) { ', 창 있음' }))"
    if ($Close) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; $closed++ } catch { } }
}

$found
"남은 시험 창 $($found.Count)개 (닫음 $closed개)"
