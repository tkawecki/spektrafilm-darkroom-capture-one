@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Install-Spektrafilm.ps1" %*
set "SpektrafilmInstallerExitCode=%errorlevel%"
echo.
pause
exit /b %SpektrafilmInstallerExitCode%
