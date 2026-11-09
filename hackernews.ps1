# Arshu Launcher, Hacker News demo: downloads the launcher and the read-only Hacker News skill into ~\AIWork
# and opens a terminal and a shared browser side by side on it. Usage: irm <this file> | iex
# Needs your assistant installed and signed in, Chrome or Edge, and Node.js 20+. Nothing else is installed.
$ErrorActionPreference = 'Stop'
$harness = if ($env:HN_HARNESS) { $env:HN_HARNESS } else { 'claude' }
$base = 'https://raw.githubusercontent.com/Arshu/ArshuLauncher/main'
$rel = 'https://github.com/Arshu/ArshuLauncher/releases/latest/download'
$dir = Join-Path $HOME 'AIWork'
New-Item -ItemType Directory -Force "$dir\skills\joint-browser", "$dir\skills\joint-hackernews" | Out-Null
Set-Location $dir
# The launcher is downloaded when it is missing or is not the latest release's (the release publishes the SHA-256 of each file).
$asset = 'runJointLauncher-win-x64.exe'
$want = ((Invoke-RestMethod 'https://api.github.com/repos/Arshu/ArshuLauncher/releases/latest').assets | Where-Object name -eq $asset).digest -replace '^sha256:', ''
if (-not $want) { throw "the latest release lists no $asset (or no digest for it)" }
$have = if (Test-Path runJointLauncher.exe) { (Get-FileHash runJointLauncher.exe -Algorithm SHA256).Hash } else { '' }
if ($have -ne $want) {
  Invoke-WebRequest "$rel/$asset" -OutFile runJointLauncher.exe.new
  if ((Get-FileHash runJointLauncher.exe.new -Algorithm SHA256).Hash -ne $want) { throw "the downloaded $asset does not match the release's SHA-256" }
  # A launcher that is still running keeps its file: close its windows first (this stops, and the old file stays, if it cannot be replaced).
  Move-Item -Force runJointLauncher.exe.new runJointLauncher.exe
}
foreach ($f in 'joint-browser/SKILL.md', 'joint-browser/browser.mjs', 'joint-hackernews/SKILL.md', 'joint-hackernews/hn.mjs', 'joint-hackernews/hn-inject.js', 'joint-hackernews/modules.json') {
  Invoke-WebRequest "$base/skills/$f" -OutFile "skills\$($f.Replace('/', '\'))"
}
if (-not (Test-Path runJointLauncher.json)) { Set-Content runJointLauncher.json "{`n  `"DefaultHarness`": `"claude`",`n  `"DefaultSkill`": `"`",`n  `"DefaultPosition`": `"left:right`",`n  `"DefaultRatio`": `"1:2`",`n  `"DefaultBrowser`": `"`",`n  `"Skills`": { `"joint-hackernews`": `"skills/joint-hackernews/SKILL.md`" }`n}" }
"Folder: $dir"
# Run from inside an assistant session (or any program whose input is no console) the launcher refuses --skill: open a terminal of its own.
# The assistant is recognised as the launcher does: Claude Code and pi mark their children, any harness shows in the parent processes.
$inside = $env:CLAUDECODE -or $env:PI_CODING_AGENT -eq 'true' -or [Console]::IsInputRedirected
for ($p = $PID; -not $inside -and $p; ) {
  $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$p"
  if (-not $proc) { break }
  if ($proc.Name -match '^(claude|codex|pi)(\.exe)?$') { $inside = $true }
  $p = $proc.ParentProcessId
}
$extra = if ($inside) { @('--newterminal') } else { @() }
& .\runJointLauncher.exe --harness $harness --skill joint-hackernews @extra
