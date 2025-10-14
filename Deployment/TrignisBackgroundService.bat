@echo off
setlocal enabledelayedexpansion

set SERVICE_NAME=TrignisAgent
set DISPLAY_NAME=Trignis Agent - SQL Change Monitor
set EXE_PATH=%~dp0Trignis.exe

if "%1"=="" (
    echo Usage: %0 {install^|start^|stop^|status^|uninstall^|test}
    echo.
    echo Commands:
    echo   install   - Install the service
    echo   start     - Start the service
    echo   stop      - Stop the service
    echo   status    - Check service status and recent logs
    echo   uninstall - Stop and uninstall the service
    echo   test      - Run in console mode for testing
    goto :eof
)

if /i "%1"=="install" (
    echo Installing "%SERVICE_NAME%"...
    sc create "%SERVICE_NAME%" binPath= "\"%~dp0Trignis.exe\"" start= auto displayname= "%DISPLAY_NAME%"
    if !ERRORLEVEL! EQU 0 (
        echo Service installed successfully.
        echo Use '%0 start' to start it.
    ) else (
        echo Failed to install service. Make sure you're running as Administrator.
    )
    goto :eof
)

if /i "%1"=="start" (
    echo Starting "%SERVICE_NAME%"...
    sc start "%SERVICE_NAME%"
    if !ERRORLEVEL! EQU 0 (
        echo Service started successfully.
    ) else (
        echo Failed to start service. Check Event Viewer for errors.
    )
    goto :eof
)

if /i "%1"=="stop" (
    echo Stopping "%SERVICE_NAME%"...
    sc stop "%SERVICE_NAME%"
    if !ERRORLEVEL! EQU 0 (
        echo Service stopped successfully.
    ) else (
        echo Failed to stop service or service was not running.
    )
    goto :eof
)

if /i "%1"=="status" (
    echo Querying "%SERVICE_NAME%" status...
    sc query "%SERVICE_NAME%"
    echo.
    echo Recent Application Event Log entries:
    powershell -Command "Get-EventLog -LogName Application -Source '.NET Runtime' -Newest 5 | Format-Table -AutoSize" 2>nul
    if !ERRORLEVEL! NEQ 0 (
        echo No recent .NET Runtime events found.
    )
    goto :eof
)

if /i "%1"=="uninstall" (
    echo Stopping and uninstalling "%SERVICE_NAME%"...
    sc stop "%SERVICE_NAME%" >nul 2>&1
    timeout /t 2 /nobreak > nul
    sc delete "%SERVICE_NAME%"
    if !ERRORLEVEL! EQU 0 (
        echo Service uninstalled successfully.
    ) else (
        echo Failed to uninstall service. Make sure you're running as Administrator.
    )
    goto :eof
)

if /i "%1"=="test" (
    echo Testing Trignis in console mode...
    echo Press Ctrl+C to stop
    echo.
    "%~dp0Trignis.exe"
    goto :eof
)

echo Invalid command: %1
echo Use '%0' without arguments for help.
goto :eof
