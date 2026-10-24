@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\start-acceptance.ps1" -PackageRoot "%~dp0"
echo.
echo 验收流程已结束，按任意键关闭窗口。
pause >nul
