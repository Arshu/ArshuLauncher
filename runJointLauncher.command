#!/bin/sh
# Runs csharp/Tools/ArshuLauncher/runJointLauncher.cs from this folder (the workspace), passing its options through (double-click it in Finder)
cd "$(dirname "$0")" || exit 1
exec dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- "$@"
