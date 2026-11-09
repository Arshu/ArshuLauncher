# Arshu Launcher, Hacker News demo: downloads the launcher and the read-only Hacker News skill into ~\Documents\AIWork
# and opens a terminal and a shared browser side by side on it. Usage: irm <this file> | iex
# Needs your assistant installed and signed in, Chrome or Edge, and Node.js 20+. Nothing else is installed.
$ErrorActionPreference = 'Stop'
$harness = if ($env:HN_HARNESS) { $env:HN_HARNESS } else { 'claude' }
$base = 'https://raw.githubusercontent.com/Arshu/ArshuLauncher/main'
$rel = 'https://github.com/Arshu/ArshuLauncher/releases/latest/download'
$dir = Join-Path $HOME 'Documents\AIWork'
New-Item -ItemType Directory -Force "$dir\skills\joint-browser", "$dir\skills\joint-hackernews", "$dir\ArshuCost" | Out-Null
Set-Location $dir
if (-not (Test-Path runJointLauncher.exe)) { Invoke-WebRequest "$rel/runJointLauncher-win-x64.exe" -OutFile runJointLauncher.exe }
foreach ($f in 'joint-browser/SKILL.md', 'joint-browser/browser.mjs', 'joint-hackernews/SKILL.md', 'joint-hackernews/hn.mjs', 'joint-hackernews/hn-inject.js', 'joint-hackernews/modules.json') {
  Invoke-WebRequest "$base/skills/$f" -OutFile "skills\$($f.Replace('/', '\'))"
}
# the AI cost line at the bottom of the page needs .NET 10; without it the page says "cost unavailable"
Invoke-WebRequest "$base/csharp/Tools/ArshuCost/runJointCost.cs" -OutFile ArshuCost\runJointCost.cs
if (-not (Test-Path runJointLauncher.json)) { Set-Content runJointLauncher.json "{`n  `"DefaultHarness`": `"claude`",`n  `"DefaultSkill`": `"`",`n  `"DefaultPosition`": `"left:right`",`n  `"DefaultRatio`": `"1:2`",`n  `"DefaultBrowser`": `"`",`n  `"Skills`": { `"joint-hackernews`": `"skills/joint-hackernews/SKILL.md`" }`n}" }
"Folder: $dir"
& .\runJointLauncher.exe --harness $harness --skill joint-hackernews
