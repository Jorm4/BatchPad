@echo off
rem Publishes BatchPad as one self-contained exe: dist\BatchPad.exe
setlocal
cd /d "%~dp0.."
if exist dist rmdir /s /q dist
dotnet publish src\BatchPad.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o dist
exit /b %errorlevel%
