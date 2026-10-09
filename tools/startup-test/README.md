# startup-test

"로그인 시 자동 실행"과 설치 방식 판별(`AppInfo.IsPackaged`) 시험 도구. 앱 프로젝트를 참조만 하고 `mongdock.sln` 에는 넣지 않는다. 외부 NuGet 없음.

```
dotnet build tools/startup-test/startup-test.csproj
tools\startup-test\bin\Debug\net8.0-windows10.0.19041.0\startup-test.exe   # 실패 시 종료 코드 1
```

다른 모드:

```
startup-test.exe --transfer   # 설정 옮기기(.mongdock) 내보내기·가져오기 시험 — 임시 폴더에서만 (사용자 설정 안 건드림). 실패 시 종료 코드 1
startup-test.exe --pins       # 첫 실행 기본 독 구성(Finder·Launchpad + 작업 표시줄 고정 앱 + 자주 쓰는 앱) 미리 보기 — 읽기만
```

- 일반판(Run 키): 시험용 값 이름 `mongdock-selftest` 로 켜고 끈다 — 실제 `mongdock` 값은 건드리지 않는다.
- 스토어판(StartupTask): 패키지 밖에서는 "꺼짐·바꿀 수 없음"으로 안전하게 동작하는지만 본다. 실제 켜기·끄기·사용자가 끈 상태는 MSIX 설치 시험에서.
