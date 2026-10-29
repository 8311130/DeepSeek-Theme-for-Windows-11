@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   WhaleTheme build
echo ============================================
echo.

if not exist "WhaleTheme.cs" (
  echo [ERROR] WhaleTheme.cs not found in this folder.
  pause
  exit /b 1
)

if not exist "build\mpv.exe.gz" (
  echo [ERROR] build\mpv.exe.gz not found.
  pause
  exit /b 1
)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Need .NET Framework 4.x.
  pause
  exit /b 1
)

echo compiler : %CSC%
echo building ... please wait, the exe will be about 45 MB
echo.

if exist "WhaleTheme.exe" del /q "WhaleTheme.exe"
if exist "build-log.txt" del /q "build-log.txt"

set "ICON=/win32icon:assets\whale.ico"
if not exist "assets\whale.ico" (
  echo [WARN] assets\whale.ico not found - building without a custom icon.
  set "ICON="
)

set "OPTS=/nologo /target:winexe /platform:anycpu /optimize+ /out:WhaleTheme.exe /resource:build\mpv.exe.gz,mpv.exe.gz %ICON% /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll WhaleTheme.cs"

"%CSC%" %OPTS% > "build-log.txt" 2>&1

echo.
echo ---- compiler output ----
type "build-log.txt"
echo ------------------------
echo.

if exist "WhaleTheme.exe" (
  echo OK: WhaleTheme.exe has been built.
  echo Next: double-click WhaleTheme.exe
) else (
  echo FAILED. Send the text above, or the file build-log.txt, back.
)

echo.
pause
exit /b 0
