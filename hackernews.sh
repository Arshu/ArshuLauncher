#!/bin/sh
# Arshu Launcher, Hacker News demo: downloads the launcher and the read-only Hacker News skill into ~/AIWork
# and opens a terminal and a shared browser side by side on it. Usage: curl -fsSL <this file> | sh   (or: sh hackernews.sh [claude|codex|pi])
# Needs your assistant installed and signed in, Chrome or Edge, and Node.js 20+. Nothing else is installed.
set -eu
harness="${1:-claude}"
base=https://raw.githubusercontent.com/Arshu/ArshuLauncher/main
rel=https://github.com/Arshu/ArshuLauncher/releases/latest/download
case "$(uname -s)" in
  Darwin) os=osx ;;
  Linux) os=linux ;;
  *) echo "Use hackernews.ps1 on Windows."; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "Unsupported CPU: $(uname -m)"; exit 1 ;;
esac
dir="$HOME/AIWork"
mkdir -p "$dir/skills/joint-browser" "$dir/skills/joint-hackernews" "$dir/ArshuCost"
cd "$dir"
[ -x runJointLauncher ] || { curl -fsSL "$rel/runJointLauncher-$os-$arch" -o runJointLauncher && chmod +x runJointLauncher; }
for f in joint-browser/SKILL.md joint-browser/browser.mjs joint-hackernews/SKILL.md joint-hackernews/hn.mjs joint-hackernews/hn-inject.js joint-hackernews/modules.json; do
  curl -fsSL "$base/skills/$f" -o "skills/$f"
done
# the AI cost line at the bottom of the page needs .NET 10; without it the page says "cost unavailable"
curl -fsSL "$base/csharp/Tools/ArshuCost/runJointCost.cs" -o ArshuCost/runJointCost.cs
[ -f runJointLauncher.json ] || printf '{\n  "DefaultHarness": "claude",\n  "DefaultSkill": "",\n  "DefaultPosition": "left:right",\n  "DefaultRatio": "1:2",\n  "DefaultBrowser": "",\n  "Skills": { "joint-hackernews": "skills/joint-hackernews/SKILL.md" }\n}\n' > runJointLauncher.json
echo "Folder: $dir"
# Run from inside an assistant session (or any program whose input is no console, which `curl | sh` is) the launcher refuses --skill:
# open a terminal of its own. The assistant is recognised as the launcher does: Claude Code and pi mark their children, any harness
# shows in the parent processes.
inside=0
if [ -n "${CLAUDECODE:-}" ] || [ "${PI_CODING_AGENT:-}" = true ] || [ ! -t 0 ]; then inside=1; fi
pid=$$
n=0
while [ "$inside" = 0 ] && [ "${pid:-0}" -gt 1 ] && [ "$n" -lt 32 ]; do
  case "$(ps -o comm= -p "$pid" | sed 's#.*/##')" in claude|codex|pi) inside=1 ;; esac
  pid=$(ps -o ppid= -p "$pid" | tr -d ' ')
  n=$((n + 1))
done
if [ "$inside" = 1 ]; then exec ./runJointLauncher --harness "$harness" --skill joint-hackernews --newterminal; fi
exec ./runJointLauncher --harness "$harness" --skill joint-hackernews
