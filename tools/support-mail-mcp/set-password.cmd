@echo off
chcp 65001 >nul
echo.
echo  mongdock 지원 메일함 연결
echo  ------------------------------------------------------------
echo  Gmail 앱 비밀번호(16자리)를 윈도우 자격 증명 관리자에 저장해요.
echo  비밀번호는 이 창에서 직접 입력하고, 화면·파일·AI 에는 남지 않아요.
echo.
set /p ADDR= 지원 메일 주소 (대표 계정: mongdock@gmail.com):
if "%ADDR%"=="" goto :eof
echo.
echo  이어서 "Password:" 가 나오면 앱 비밀번호를 붙여 넣고 Enter (입력이 안 보여도 정상).
cmdkey /generic:mongdock-support-mail /user:%ADDR% /pass
echo.
echo  저장했어요. 지우려면: cmdkey /delete:mongdock-support-mail
pause
