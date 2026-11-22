@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-LutExport.ps1" %*
set "result=%ERRORLEVEL%"
pause
exit /b %result%
