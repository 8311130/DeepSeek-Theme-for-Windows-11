@echo off
rem 只验证 assets\whale.ico 能不能被 csc 接受：编译到 %TEMP%，不动正式 exe
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Need .NET Framework 4.x.
  pause
  exit /b 1
)

set "SRC=%~dp0..\WhaleTheme.cs"
set "ICO=%~dp0..\assets\whale.ico"
set "OUT=%TEMP%\whaletheme_ico_test.exe"

if not exist "%ICO%" (
  echo [ERROR] assets\whale.ico not found.
  pause
  exit /b 1
)

echo === icon check ===
echo icon   : %ICO%
echo output : %OUT%  ^(throwaway^)
echo.
"%CSC%" /nologo /target:winexe /platform:anycpu /out:"%OUT%" /win32icon:"%ICO%" ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%SRC%"

echo.
echo EXITCODE=%ERRORLEVEL%
if exist "%OUT%" (
  echo OK: the icon was accepted and embedded.
  del /q "%OUT%"
) else (
  echo FAILED: csc rejected the icon. See the message above.
)
pause
