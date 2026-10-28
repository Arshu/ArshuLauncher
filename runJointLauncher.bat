@echo off
rem Runs csharp/Tools/ArshuLauncher/runJointLauncher.cs from this folder (the workspace), passing its options through
setlocal
pushd "%~dp0"
dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- %*
set "rc=%errorlevel%"
popd
rem A double-clicked window closes when the tool exits: on a failure, keep it open so the message can be read.
if not "%rc%"=="0" pause
exit /b %rc%
