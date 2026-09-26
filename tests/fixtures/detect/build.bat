@echo off
setlocal enabledelayedexpansion
rem Builds the apps and the test projects.
rem
rem Usage:
rem   build.bat [--release | --final] [--asan] [name ...]
rem
rem   --release   optimised build
rem   --final     retail build
rem   --asan      address sanitizer build

set "APPS=Alpha Beta"
:parse
if /i "%~1"=="--release" (set CONFIG=release& shift & goto parse)
if /i "%~1"=="--final" (set CONFIG=retail& shift & goto parse)
if /i "%~1"=="--asan" (set ASAN=1& shift & goto parse)
if /i "%~1"=="--verbose" (set VERBOSE=1& shift & goto parse)
echo Building %CONFIG% %*
