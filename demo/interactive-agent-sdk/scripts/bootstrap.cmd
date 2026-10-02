@echo off
setlocal
python "%~dp0bootstrap.py" %*
exit /b %ERRORLEVEL%
