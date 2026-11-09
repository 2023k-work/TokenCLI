@echo off
setlocal
cd /d "%~dp0"
dotnet build tests\TokenCLI.Tests\TokenCLI.Tests.csproj -c Release --verbosity quiet
if errorlevel 1 exit /b 1
tests\TokenCLI.Tests\bin\Release\net8.0\TokenCLI.Tests.exe
exit /b %errorlevel%
