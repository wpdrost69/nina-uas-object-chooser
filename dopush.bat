@echo off
cd /d "C:\Users\wpdro\AppData\Local\Temp\nina-repo-push"
git push origin main > push.log 2>&1
echo %ERRORLEVEL% > push.exit
