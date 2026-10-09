# report-test

"문제 신고하기"(`src/mongdock/Services/ReportRedactor.cs`, `Views/ReportWindow.cs`) 시험 도구.
앱 프로젝트를 참조만 하고 `mongdock.sln` 에는 넣지 않는다. 외부 NuGet 없음. 네트워크로 아무것도 보내지 않는다.

```
dotnet build tools/report-test/report-test.csproj
tools\report-test\bin\Debug\net8.0-windows10.0.19041.0\report-test.exe            # 가리기 단위 시험 (실패 시 종료 코드 1)
tools\report-test\bin\Debug\net8.0-windows10.0.19041.0\report-test.exe --png DIR  # + 신고 창 라이트/다크 PNG 4장 (접힘/펼침)
```

가리기 시험: 이메일, URL(iCal·webcal·www), 사용자 폴더 경로(`C:\Users\<이름>`, JSON 이스케이프, `/Users/`), 사용자·기기 이름(낱말 단위), 창 제목, 로그의 작은따옴표 문구.
