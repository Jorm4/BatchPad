@echo off
rem Builds the native targets.
rem
rem   build_examples.bat                   Every game and every TEST suite
rem   build_examples.bat SpaceTrader       just that target (name is case-insensitive)
rem   build_examples.bat --games           only the games
rem   build_examples.bat --tests	only the test suites
rem   build_examples.bat --asan [names]    Debug with ASan; add --release for Release
echo %*
