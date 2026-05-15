@echo off
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] Please run this script as Administrator.
    pause & exit /b 1
)

set SVC_NAME=ProcessMemMonitor

echo Stopping service ...
net stop %SVC_NAME% >nul 2>&1

echo Deleting service ...
sc delete %SVC_NAME%

echo Done.
pause
