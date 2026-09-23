@echo off
rem PinCC build: requires the .NET 10 SDK (https://dotnet.microsoft.com/download). No NuGet packages are used.
rem   dist\PinCC.exe             single file, needs the .NET 10 Desktop Runtime (a few MB, starts fastest)
rem   dist\standalone\PinCC.exe  single file with the runtime inside (nothing to install)
setlocal
cd /d "%~dp0"

dotnet run --project tests -c Release || goto :fail

dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=embedded -o dist || goto :fail
dotnet publish src -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o dist\standalone || goto :fail

echo.
echo Built: dist\PinCC.exe  and  dist\standalone\PinCC.exe
exit /b 0

:fail
echo Build failed.
exit /b 1
