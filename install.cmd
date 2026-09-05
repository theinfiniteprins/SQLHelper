@echo off
rem SqlHelper installer - double-click this file.
rem Safe to run again any time (e.g. after pulling updates): it checks what's
rem already there and only does the work that's actually needed.

setlocal
set "SCRIPT_DIR=%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%install.ps1"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo Done. Press any key to close this window.
) else (
    echo Installation did not finish - see the messages above. Press any key to close this window.
)
pause >nul
exit /b %EXIT_CODE%
