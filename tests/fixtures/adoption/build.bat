@echo off
rem Builds the native targets; the exit code is MSBuild's.
rem
rem   build.bat                       every game and every test suite
rem   build.bat SpaceTrader           just that target (any case)
rem   build.bat --games               only the games
rem   build.bat --tests               only the test suites
rem   build.bat --asan [names]        Debug with ASan; add --release for Release with ASan
set "GAMES=KartRacer BikeTrials RobotManager SpaceTrader RallyRacer ChessTrainer"
set "BENCH=ContainersBench PathfindBench SpaceTraderBench"
set "TESTS=MathTests ContainersTests SpriteTests AudioTests InputTests SaveTests TileMapTests ScriptingTests MenuTests PhysicsTests NetTests"
