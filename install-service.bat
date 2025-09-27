@echo off
setlocal ENABLEEXTENSIONS ENABLEDELAYEDEXPANSION

:: ================================
:: WatchTowerService Installer (.NET Framework 4.8)
:: Requires: Administrator
:: ================================

set "SVC_NAME=CF.Watch-Tower"
set "DISPLAY_NAME=CF.Watch-Tower"
set "DESC=Watch and restart Windows CF services per appsettings.json"

:: Optional: set a custom account (uncomment and fill in to use)
:: set "SERVICE_ACCOUNT=.\LocalSystem"
:: set "SERVICE_PASSWORD="

:: Resolve paths
set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%WatchTowerService.exe"
set "LOGDIR=%SCRIPT_DIR%logs"

echo.
echo === Installing %SVC_NAME% ===
echo Script dir : "%SCRIPT_DIR%"
echo Binary     : "%EXE%"
echo Logs dir   : "%LOGDIR%"
echo.

:: Admin check (writes to system32 needs elevation)
net session >nul 2>&1
if NOT %ERRORLEVEL%==0 (
  echo [ERROR] This script must be run as Administrator.
  echo        Right-click the .bat and choose "Run as administrator".
  exit /b 1
)

if not exist "%EXE%" (
  echo [ERROR] Could not find "%EXE%".
  echo         Make sure WatchTowerService.exe is in the same folder as this script.
  exit /b 2
)

:: Create logs directory
if not exist "%LOGDIR%" (
  mkdir "%LOGDIR%" 2>nul
)

:: If service exists, stop and delete (upgrade scenario)
sc.exe query "%SVC_NAME%" >nul 2>&1
if %ERRORLEVEL%==0 (
  echo Service "%SVC_NAME%" already exists. Stopping and deleting for a clean install...
  sc.exe stop "%SVC_NAME%" >nul 2>&1
  :: wait up to ~20s for stop
  for /l %%i in (1,1,20) do (
    sc.exe query "%SVC_NAME%" | findstr /I "STOPPED" >nul && goto :deleteSvc
    >nul timeout /t 1 /nobreak
  )
  :deleteSvc
  sc.exe delete "%SVC_NAME%" >nul 2>&1
  >nul timeout /t 1 /nobreak
)

:: Create service (LocalSystem by default)
echo Creating service "%SVC_NAME%" ...
sc.exe create "%SVC_NAME%" binPath= "\"%EXE%\"" start= auto DisplayName= "%DISPLAY_NAME%"
if NOT %ERRORLEVEL%==0 (
  echo [ERROR] Failed to create the service.
  exit /b 3
)

:: Optional: configure account if SERVICE_ACCOUNT specified
if defined SERVICE_ACCOUNT (
  echo Configuring logon account...
  sc.exe config "%SVC_NAME%" obj= "%SERVICE_ACCOUNT%" password= "%SERVICE_PASSWORD%"
)

:: Description
sc.exe description "%SVC_NAME%" "%DESC%" >nul 2>&1

:: Failure actions: restart after 60s, up to 3 times, reset counter after 1 day
sc.exe failure "%SVC_NAME%" reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul 2>&1

:: Start service
echo Starting service...
sc.exe start "%SVC_NAME%"
if NOT %ERRORLEVEL%==0 (
  echo [WARN] Service failed to start from installer. Check logs\watch-tower-*.log and Windows Event Log.
  exit /b 4
)

echo.
echo [OK] Service "%SVC_NAME%" installed and started.
exit /b 0
