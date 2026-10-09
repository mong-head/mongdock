# taskbar-watch

작업 표시줄(`Shell_TrayWnd`·`Shell_SecondaryTrayWnd`)이 언제 다시 보였고 몇 ms 만에 숨었는지 기록하는 관찰 도구 (#13).
작업 표시줄을 건드리지 않고 이벤트만 읽는다. 앱 코드와 무관하고 `mongdock.sln` 밖, 외부 NuGet 없음.

```
dotnet build tools/taskbar-watch/taskbar-watch.csproj
tools\taskbar-watch\bin\Debug\net8.0-windows\taskbar-watch.exe 30 C:\dev\mongdock-tmp\taskbar-watch.txt   # 30분
```

줄마다: 보인 시각·창·위치·마우스 위치·포그라운드(프로세스/클래스, 제목 없음)·직전 포그라운드 변화, 숨은 시각과 보인 시간(ms).
끝에 보인 횟수·평균/최대/합계 시간.
