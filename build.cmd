@echo off
rem PinCC build: requires the .NET 10 SDK. No NuGet packages are used.
rem   dist\PinCC.exe             single file, needs the .NET 10 Desktop Runtime
rem   dist\standalone\PinCC.exe  single file with the runtime inside
rem Publish to temporary staging, validate actual GUI lifecycles, then replace dist.
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Build.ps1
exit /b %errorlevel%
