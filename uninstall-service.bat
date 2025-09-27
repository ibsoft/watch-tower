@echo off
setlocal ENABLEEXTENSIONS ENABLEDELAYEDEXPANSION

:: ================================
:: WatchTowerService Uninstaller
:: Requires: Administrator
:: ================================

set "SVC_NAME=CF.Watch-Tower"

echo.
echo === Uninstalling %SVC_NAME% ===

net session >nul 2>&1
if NOT %ERRORLEVEL%==0 (
  echo [ERROR] This script must be run as Administrator.
  exit /b 1
)

:: If service doesn't exist, nothing to do
sc.exe query "%SVC_NAME%" >nul 2>&1
if NOT %ERRORLEVEL%==0 (
  echo Service "%SVC_NAME%" not found. Nothing to uninstall.
  exit /b 0
)

:: Stop service if running
echo Stopping service...
sc.exe stop "%SVC_NAME%" >nul 2>&1
for /l %%i in (1,1,30) do (
  sc.exe query "%SVC_NAME%" | findstr /I "STOPPED" >nul && goto :deleteSvc
  >nul timeout /t 1 /nobreak
)
:deleteSvc

echo Deleting service...
sc.exe delete "%SVC_NAME%"
if NOT %ERRORLEVEL%==0 (
  echo [ERROR] Failed to delete the service.
  exit /b 2
)

echo [OK] Service "%SVC_NAME%" uninstalled.
exit /b 0
