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

set DEST=%WINDIR%\System32\oobe\info\backgrounds

echo Disabling OEMBackground ...
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\Background" /v OEMBackground /t REG_DWORD /d 0 /f >nul

if exist "%DEST%\backgroundDefault.jpg" (
  echo Removing background image ...
  del /f /q "%DEST%\backgroundDefault.jpg"
)

rem clean the now-empty folder if we created it
if exist "%DEST%" (
  dir /b "%DEST%" | findstr . >nul || rd "%DEST%" 2>nul
)

echo.
echo ============================================
echo   DONE. Logon screen restored to default.
echo ============================================
echo.
pause
exit /b 0
