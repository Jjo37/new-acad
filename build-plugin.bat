@echo off
cd /d "%~dp0.."
echo === new-acad 编译 ===
echo.
cd plugin\AcBridge-v24\src
echo 清理旧编译...
if exist obj rmdir /s /q obj
echo 编译...
dotnet build -c Release 2>&1
if %ERRORLEVEL% NEQ 0 (
  echo.
  echo 编译失败，检查上面的错误
  pause
  exit /b 1
)
echo.
echo 复制 DLL...
copy /Y bin\Release\net8.0-windows\Civil3DMcpPlugin.dll ..\Civil3DMcpPlugin.dll >nul
copy /Y bin\Release\net8.0-windows\Civil3DMcpPlugin.deps.json ..\ >nul 2>nul
copy /Y bin\Release\net8.0-windows\Civil3DMcpPlugin.runtimeconfig.json ..\ >nul 2>nul
echo.
echo 编译成功! DLL: 681KB
echo.
echo 下一步: C3D 命令行输 NETLOAD, 选这个 DLL
echo       然后输 HANK_SHOW 看面板
pause
