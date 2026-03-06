#!/usr/bin/env bash
# Cross-platform publish script (run from Linux to build both win-x64 and linux-x64)

set -euo pipefail

REPO_BASE="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE_DIR="$REPO_BASE/Source"
DEPLOY_WIN="$REPO_BASE/Deployment/Windows"
DEPLOY_LIN="$REPO_BASE/Deployment/Linux"

echo "Building from: $SOURCE_DIR"
echo "Windows output: $DEPLOY_WIN"
echo "Linux output:   $DEPLOY_LIN"

# Clear and recreate deployment directories
for DIR in "$DEPLOY_WIN" "$DEPLOY_LIN"; do
  if [ -d "$DIR" ]; then
    echo "Clearing $DIR..."
    rm -rf "${DIR:?}"/*
  else
    mkdir -p "$DIR"
  fi
done

# Clean the project
dotnet clean "$SOURCE_DIR" -c Release

# Remove obj and bin to avoid assembly conflicts
rm -rf "$SOURCE_DIR/obj" "$SOURCE_DIR/bin"

# Publish Windows (win-x64)
echo "Publishing win-x64..."
dotnet publish "$SOURCE_DIR" \
  -c Release \
  -r win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  --framework net9.0 \
  -o "$DEPLOY_WIN"

# Publish Linux (linux-x64)
echo "Publishing linux-x64..."
dotnet publish "$SOURCE_DIR" \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  --framework net9.0 \
  -o "$DEPLOY_LIN"

# Create shared deployment structure for both platforms
for DIR in "$DEPLOY_WIN" "$DEPLOY_LIN"; do
  echo "Preparing deployment structure in $DIR..."

  # Remove unnecessary files
  rm -f "$DIR"/*.pdb "$DIR"/*.xml "$DIR"/*.deps.json "$DIR"/*.dev.json \
        "$DIR"/*.example "$DIR"/Trignis.staticwebassets.endpoints.json \
        "$DIR"/web.config

  # Create folder structure
  mkdir -p "$DIR/exports"
  mkdir -p "$DIR/environments"
  mkdir -p "$DIR/log"
  mkdir -p "$DIR/license"
  mkdir -p "$DIR/license/packages"

  # Copy environment config files
  if [ -d "$SOURCE_DIR/environments" ]; then
    cp -r "$SOURCE_DIR/environments/"* "$DIR/environments/" 2>/dev/null || true
    echo "Environment configuration files copied."
  fi

  # Copy SQL folder
  if [ -d "$SOURCE_DIR/SQL" ]; then
    cp -r "$SOURCE_DIR/SQL" "$DIR/sql"
    echo "SQL files copied."
  fi

  # Copy LICENSE
  if [ -f "$REPO_BASE/LICENSE" ]; then
    cp "$REPO_BASE/LICENSE" "$DIR/license/license.txt"
  fi

  # Create source reference file
  echo "https://github.com/melosso/trignis" > "$DIR/license/source.txt"

  # Create README
  cat > "$DIR/README.txt" << 'EOF'
TRIGNIS SERVICE DEPLOYMENT
==========================

TESTING
-------
1. Run 'TrignisBackgroundService.bat test' (Windows) or './Trignis' (Linux) to test in console mode
2. Check logs in the 'log' folder
3. Press Ctrl+C to stop the console test

SERVICE INSTALLATION
--------------------
Windows:
1. Run 'TrignisBackgroundService.bat install' as Administrator
2. Run 'TrignisBackgroundService.bat start' to start the service
3. Run 'TrignisBackgroundService.bat status' to check if it's running

Linux (systemd):
1. Copy Trignis to /usr/local/bin/trignis
2. Create a systemd unit file and enable it

SERVICE MANAGEMENT (Windows)
------------------
- Start: TrignisBackgroundService.bat start
- Stop:  TrignisBackgroundService.bat stop
- Status: TrignisBackgroundService.bat status
- Uninstall: TrignisBackgroundService.bat uninstall

TROUBLESHOOTING
---------------
If the service fails to start:
1. Check logs in the 'log' folder
2. Run 'TrignisBackgroundService.bat status' to see service status
3. Check Windows Event Viewer > Application logs (if enabled)
4. Test in console mode first using 'TrignisBackgroundService.bat test'

CONFIGURATION
-------------
Edit configuration files in the 'Environments' folder
Default environment: Production
EOF
done

# Create Windows-specific service batch file
cat > "$DEPLOY_WIN/TrignisBackgroundService.bat" << 'EOF'
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
EOF

# Mark linux binary as executable
chmod +x "$DEPLOY_LIN/Trignis" 2>/dev/null || true

# Summary
WIN_SIZE=$(du -sh "$DEPLOY_WIN/Trignis.exe" 2>/dev/null | cut -f1 || echo "N/A")
LIN_SIZE=$(du -sh "$DEPLOY_LIN/Trignis" 2>/dev/null | cut -f1 || echo "N/A")

echo ""
echo "SUCCESS: Trignis published successfully"
echo "  Windows (win-x64): $DEPLOY_WIN  [Trignis.exe: $WIN_SIZE]"
echo "  Linux (linux-x64): $DEPLOY_LIN  [Trignis: $LIN_SIZE]"
echo ""
echo "WARNING: Run './Trignis' or 'Trignis.exe' in console mode first to verify everything works!"
