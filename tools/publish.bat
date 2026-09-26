@echo off
rem Publishes BatchPad as one self-contained exe, dist\BatchPad.exe, plus its console shim dist\batchpad.com
rem Extra arguments go to dotnet publish, e.g. tools\publish.bat -p:Version=1.2.3
setlocal
cd /d "%~dp0.."
if exist dist rmdir /s /q dist
dotnet publish src\BatchPad.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o dist %*
if errorlevel 1 exit /b %errorlevel%
rem Trimmed single file rather than native AOT, so publishing needs only the .NET SDK, not the C++ build tools.
dotnet publish src\BatchPad.Shim -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o dist %*
if errorlevel 1 exit /b %errorlevel%
rem cmd finds batchpad.com before BatchPad.exe, so "batchpad run ..." goes through the console shim.
move /y dist\BatchPad.Shim.exe dist\batchpad.com >nul
