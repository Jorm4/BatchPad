@echo off
rem Prints each argument, unquoted, one per line until the --end sentinel.
:next
set "ARG=%~1"
setlocal EnableDelayedExpansion
if "!ARG!"=="--end" exit /b 0
echo(!ARG!
endlocal
shift
goto next
