@echo off
setlocal
python "%~dp0launcher\launcher.py" %*
exit /b %ERRORLEVEL%
