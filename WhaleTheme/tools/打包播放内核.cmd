@echo off
rem 把 mpv.exe 压成 WhaleTheme\build\mpv.exe.gz（编译时打进 exe 的那份）
rem 用法：把 mpv.exe 拖到本文件上，或  打包播放内核.cmd "D:\path\mpv.exe"
setlocal
cd /d "%~dp0.."

if "%~1"=="" (
  echo 用法：把 mpv.exe 拖到本文件上，或者
  echo       "%~nx0" "D:\path\to\mpv.exe"
  echo.
  echo 去哪拿 mpv：
  echo   https://mpv.io/installation/  （Windows 构建，约 120MB）
  pause
  exit /b 1
)

if not exist "%~f1" (
  echo [ERROR] 找不到文件: %~f1
  pause
  exit /b 1
)

if /i not "%~nx1"=="mpv.exe" (
  echo [WARN] 文件名不是 mpv.exe，仍然继续: %~nx1
)

if not exist "build" mkdir "build"

set "IN=%~f1"
set "OUT=%CD%\build\mpv.exe.gz"

echo === 打包播放内核 ===
echo source : %IN%
echo target : %OUT%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$i=[IO.File]::OpenRead($env:IN); $o=[IO.File]::Create($env:OUT); $g=New-Object IO.Compression.GZipStream($o,[IO.Compression.CompressionMode]::Compress); $i.CopyTo($g); $g.Close(); $o.Close(); $i.Close()"

if not exist "build\mpv.exe.gz" (
  echo [ERROR] 打包失败。
  pause
  exit /b 1
)

for %%A in ("build\mpv.exe.gz") do echo OK: %%~zA bytes
echo 现在可以双击 编译.cmd 生成 WhaleTheme.exe 了。
pause
