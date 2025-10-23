# Set the correct paths for your environment
$repoBase = "D:\Repository\Trignis"
$sourceDir = "$repoBase\Source"
$deploymentDir = "$repoBase\Deployment"

# First completely clear the deployment directory if it exists
if (Test-Path -Path $deploymentDir) {
    Write-Host "Clearing deployment directory $deploymentDir..." -ForegroundColor Yellow
    Remove-Item -Path "$deploymentDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Deployment directory cleared." -ForegroundColor Green
} else {
    # Ensure deployment directory exists
    New-Item -Path $deploymentDir -ItemType Directory -Force | Out-Null
    Write-Host "Created deployment directory $deploymentDir" -ForegroundColor Green
}

# Clean the project
dotnet clean $sourceDir -c Release

# Remove obj and bin directories to avoid assembly conflicts
Remove-Item -Path "$sourceDir\obj" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$sourceDir\bin" -Recurse -Force -ErrorAction SilentlyContinue

# Publish self-contained version
Write-Host "Publishing self-contained version..." -ForegroundColor Cyan
dotnet publish $sourceDir -c Release -r win-x64 -p:PublishSingleFile=true -o "$deploymentDir"

# Create exports folder structure
$exportsDir = "$deploymentDir\exports"
New-Item -Path "$exportsDir\" -ItemType Directory -Force | Out-Null
Write-Host "Exports folder structure created." -ForegroundColor Green

# Remove unnecessary files
Write-Host "Removing unnecessary files..." -ForegroundColor Yellow
$filesToRemove = @(
    "*.pdb",
    "*.xml",
    "*.deps.json",
    "*.dev.json",
    "*.example",
    "Trignis.staticwebassets.endpoints.json",
    "web.config"
)

foreach ($pattern in $filesToRemove) {
    Get-ChildItem -Path $deploymentDir -Filter $pattern -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
}

# Create Environments folder structure in deployment
$envDir = "$deploymentDir\Environments"
New-Item -Path $envDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

# Copy environment config files if they exist
if (Test-Path "$sourceDir\Environments") {
    Copy-Item -Path "$sourceDir\Environments\*" -Destination $envDir -Recurse -Force
    Write-Host "Environment configuration files copied." -ForegroundColor Green
}

# Copy SQL folder if it exists
if (Test-Path "$sourceDir\SQL") {
    $lowerSqlDir = "$deploymentDir\sql"
    Copy-Item -Path "$sourceDir\SQL" -Destination $lowerSqlDir -Recurse -Force
    Write-Host "SQL files copied to 'sql' folder." -ForegroundColor Green
}

# Create logs folder
New-Item -Path "$deploymentDir\log" -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

# Copy LICENSE file to deployment directory under /license/
$licenseDir = Join-Path $deploymentDir "license"
if (-not (Test-Path $licenseDir)) {
  New-Item -Path $licenseDir -ItemType Directory -Force | Out-Null
}
$licensePath = Join-Path $repoBase "LICENSE"
$deploymentLicensePath = Join-Path $licenseDir "license.txt"
if (Test-Path $licensePath) {
  Write-Host "Copying LICENSE file to deployment directory under /license/..."
  Copy-Item -Path $licensePath -Destination $deploymentLicensePath -Force
}

# Create an additional source file in /license/
$sourceFilePath = Join-Path $licenseDir "source.txt"
"https://github.com/melosso/trignis" | Set-Content -Path $sourceFilePath -Encoding UTF8
Write-Host "Created source file at $sourceFilePath" -ForegroundColor Green

# Generate and include third-party package licenses
Write-Host "Collecting package licenses..." -ForegroundColor Cyan
$packageLicenseDir = Join-Path $licenseDir "packages"
$projectPath = Join-Path $sourceDir "Trignis.csproj"
New-Item -Path $packageLicenseDir -ItemType Directory -Force | Out-Null

try {
    dotnet tool run dotnet-project-licenses --input $projectPath --export-license-texts --output-directory $packageLicenseDir

    $licenseCount = (Get-ChildItem $packageLicenseDir -File).Count
    if ($licenseCount -gt 0) {
        Write-Host "Collected $licenseCount package licenses into $packageLicenseDir" -ForegroundColor Green
    } else {
        Write-Host "No package licenses were generated." -ForegroundColor Yellow
    }
}
catch {
    Write-Host "Failed to collect package licenses: $($_.Exception.Message)" -ForegroundColor Red
}

# Create service management batch file
Write-Host "Creating service management batch file..." -ForegroundColor Cyan

# Unified Service Management Batch
$serviceBat = @"
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
"@
Set-Content -Path "$deploymentDir\TrignisBackgroundService.bat" -Value $serviceBat -Encoding ASCII

# Create README
$readme = @"
TRIGNIS SERVICE DEPLOYMENT
==========================

TESTING
-------
1. Run 'TrignisBackgroundService.bat test' to test the application in console mode
2. Check logs in the 'log' folder
3. Press Ctrl+C to stop the console test

SERVICE INSTALLATION
--------------------
1. Run 'TrignisBackgroundService.bat install' as Administrator
2. Run 'TrignisBackgroundService.bat start' to start the service
3. Run 'TrignisBackgroundService.bat status' to check if it's running

SERVICE MANAGEMENT
------------------
- Start: TrignisBackgroundService.bat start
- Stop: TrignisBackgroundService.bat stop
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
"@
Set-Content -Path "$deploymentDir\README.txt" -Value $readme -Encoding UTF8

# Get file size info for the main executable
$exeFile = Get-ChildItem "$deploymentDir\Trignis.exe" -ErrorAction SilentlyContinue
if ($exeFile) {
    $sizeInMB = [math]::Round($exeFile.Length / 1MB, 2)
    Write-Host ""
    Write-Host "SUCCESS: Trignis published successfully to $deploymentDir" -ForegroundColor Green
    Write-Host "   - Executable Size: $sizeInMB MB" -ForegroundColor Green
    Write-Host ""
    Write-Host "Files created:" -ForegroundColor Cyan
    Write-Host "   - TrignisBackgroundService.bat" -ForegroundColor Yellow
    Write-Host "   - README.txt" -ForegroundColor White
    Write-Host ""
    Write-Host "WARNING: Run 'TrignisBackgroundService.bat test' first to verify everything works!" -ForegroundColor Yellow
} else {
    Write-Host "ERROR: Publishing failed - executable not found" -ForegroundColor Red
}
