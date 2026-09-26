@echo off
rem Starts a detached child that shares this output, then keeps running.
start "" /b py -3 "%~dp0sleep_forever.py"
ping -n 30 127.0.0.1 >nul
