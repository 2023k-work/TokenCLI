@echo off
setlocal
cd /d "%~dp0"
call Build-Test.cmd
if errorlevel 1 exit /b 1
dotnet publish src\TokenCLI\TokenCLI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None --source https://api.nuget.org/v3/index.json -o dist\0.1.2\win-x64
if errorlevel 1 exit /b 1
echo Published dist\0.1.2\win-x64\TokenCLI.exe
copy /y Start-TokenCLI.bat dist\0.1.2\win-x64\Start-TokenCLI.bat >nul
if errorlevel 1 exit /b 1
exit /b 0
