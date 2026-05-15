@echo off
:: ============================================================
:: Process Memory Monitor — Install as Windows Service
:: Must be run as Administrator
:: EXE must already exist in the same folder as this script.
:: ============================================================
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] Please run this script as Administrator.
    pause & exit /b 1
)

set SCRIPT_DIR=%~dp0
set EXE=%SCRIPT_DIR%SvchostMonitor.exe
set SVC_NAME=ProcessMemMonitor
set SVC_DISPLAY=Process Memory Monitor

:: Verify EXE exists before proceeding
if not exist "%EXE%" (
    echo [ERROR] SvchostMonitor.exe not found in %SCRIPT_DIR%
    pause & exit /b 1
)

echo ============================================================
echo  Installing: %SVC_NAME%
echo  EXE:        %EXE%
echo ============================================================

:: Remove old service if it exists
sc query %SVC_NAME% >nul 2>&1
if %errorLevel% equ 0 (
    echo [1/3] Removing existing service ...
    net stop %SVC_NAME% >nul 2>&1
    sc delete %SVC_NAME% >nul 2>&1
    timeout /t 2 /nobreak >nul
) else (
    echo [1/3] No existing service found, skipping removal.
)

:: Create service
echo [2/3] Creating service ...
sc create %SVC_NAME% ^
    binPath= "\"%EXE%\"" ^
    DisplayName= "%SVC_DISPLAY%" ^
    start= auto ^
    obj= "LocalSystem"
if %errorLevel% neq 0 ( echo [ERROR] sc create failed & pause & exit /b 1 )

sc description %SVC_NAME% "Monitors a Windows process and kills instances exceeding the configured memory threshold. Sends Telegram alerts."

:: Start service
echo [3/3] Starting service ...
net start %SVC_NAME%
if %errorLevel% neq 0 ( echo [WARN] Service start failed - check Event Viewer )

echo.
echo Done!
echo.
echo  Edit sys.ini to set Telegram BotToken + ChatId, then restart:
echo    net stop %SVC_NAME%  ^&  net start %SVC_NAME%
echo.
pause
