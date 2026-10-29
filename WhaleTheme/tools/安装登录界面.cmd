@echo off
chcp 437 >nul
setlocal
cd /d "%~dp0"

rem --- self elevate ---
net session >nul 2>&1
if errorlevel 1 (
  echo Requesting administrator rights ...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)

set SRC=%~dp0..\assets\login-background.jpg
set DEST=%WINDIR%\System32\oobe\info\backgrounds

if not exist "%SRC%" (
  echo [ERROR] assets\login-background.jpg not found.
  pause
  exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"

echo Copying logon background ...
copy /y "%SRC%" "%DEST%\backgroundDefault.jpg" >nul
if errorlevel 1 (
  echo [ERROR] Copy failed.
  pause
  exit /b 1
)

echo Enabling OEMBackground ...
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\Background" /v OEMBackground /t REG_DWORD /d 1 /f >nul
if errorlevel 1 (
  echo [ERROR] Registry write failed.
  pause
  exit /b 1
)

echo.
echo ============================================
echo   DONE. Logon screen background applied.
echo   Press Win+L to preview.
echo ============================================
echo.
pause
exit /b 0
