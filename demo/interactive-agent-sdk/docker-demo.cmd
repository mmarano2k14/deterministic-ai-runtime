@echo off
setlocal
powershell -NoProfile -File "%~dp0docker-demo.ps1" %*
exit /b %ERRORLEVEL%
