@echo off
rem Runs csharp/Tools/ArshuLauncher/runJointLauncher.cs from this folder (the workspace), passing its options through
setlocal
pushd "%~dp0"
dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- %*
set "rc=%errorlevel%"
popd
exit /b %rc%
