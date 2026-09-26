@echo off
echo compiling one
echo compiling two
echo src\thing.cs(3,5): error CS1002: ; expected
echo a warning on stderr 1>&2
exit /b 1
