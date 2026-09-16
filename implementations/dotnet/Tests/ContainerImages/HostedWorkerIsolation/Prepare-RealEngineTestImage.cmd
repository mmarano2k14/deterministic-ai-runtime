@echo off
setlocal

set "SCRIPT=%~dp0Prepare-RealEngineTestImage.ps1"

where pwsh.exe >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
)

exit /b %ERRORLEVEL%
