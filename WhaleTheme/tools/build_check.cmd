@echo off
rem 语法/类型检查：不带 mpv 资源、输出到 %TEMP%，不碰正在运行的 WhaleTheme.exe
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Need .NET Framework 4.x.
  pause
  exit /b 1
)

set "SRC=%~dp0..\WhaleTheme.cs"
set "OUT=%TEMP%\whaletheme_check.exe"

echo === csc syntax check ===
echo source : %SRC%
echo output : %OUT%  ^(throwaway^)
echo.
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%OUT%" ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
 /r:Microsoft.CSharp.dll "%SRC%"

echo.
echo EXITCODE=%ERRORLEVEL%
if exist "%OUT%" (
  echo OK: compiled without errors. This temp exe has no mpv resource and is NOT usable.
  del /q "%OUT%"
)
pause
