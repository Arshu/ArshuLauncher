# How to use Arshu Launcher

This guide assumes you have never used a terminal before. Follow the steps for your computer, in order. You only do
Steps 1 and 2 once.

- [Step 1: Install what you need](#step-1-install-what-you-need)
- [Step 2: Download the launcher](#step-2-download-the-launcher)
- [Step 3: Start a session](#step-3-start-a-session)
- [Step 4: Work with the assistant](#step-4-work-with-the-assistant)
- [Changing the layout](#changing-the-layout)
- [Ending a session](#ending-a-session)
- [If something goes wrong](#if-something-goes-wrong)

---

## First: how to open a terminal

A **terminal** is a window where you type commands. Each command is one line: type it (or paste it), then press
**Enter**.

| Computer | How to open a terminal |
|---|---|
| **Windows** | Press the **Windows key**, type `Terminal`, press **Enter**. (On older Windows, type `PowerShell` instead.) |
| **Mac** | Press **⌘ Command + Space**, type `Terminal`, press **Enter**. |
| **Linux** | Press **Ctrl + Alt + T**, or find **Terminal** in your applications menu. |

> **Tip:** to paste into a terminal, use **Ctrl + V** on Windows, **⌘ + V** on Mac, **Ctrl + Shift + V** on Linux.

---

## Step 1: Install what you need

Copy each command into your terminal and press Enter. After installing something, **close the terminal and open a
new one**, so the new program is found.

### 1a. An AI assistant (pick one)

| Assistant | Windows | Mac / Linux |
|---|---|---|
| **Claude Code** | `irm https://claude.ai/install.ps1 \| iex` | `curl -fsSL https://claude.ai/install.sh \| bash` |
| **Codex** | `npm install -g @openai/codex` *(needs Node.js, see 1c)* | `npm install -g @openai/codex` |
| **pi** | `npm install -g @earendil-works/pi-coding-agent` *(needs Node.js)* | same |

Then open a **new** terminal, type the assistant's name (`claude`, `codex` or `pi`) and press Enter **once** to sign
in. Close it again when you are signed in.

### 1b. A browser

Any **Chromium-based** browser works:

- **Windows:** Microsoft Edge is already installed. Nothing to do.
- **Mac:** install Google Chrome from https://www.google.com/chrome/ (or `brew install --cask google-chrome`).
- **Linux:** install Google Chrome from https://www.google.com/chrome/ (download the `.deb` file and open it).

### 1c. Optional: Node.js 20 or newer (lets the assistant drive the browser)

| Computer | Command |
|---|---|
| Windows | `winget install --id OpenJS.NodeJS.LTS -e` |
| Mac | `brew install node` |
| Linux (Ubuntu/Debian) | `curl -fsSL https://deb.nodesource.com/setup_lts.x \| sudo -E bash - && sudo apt install -y nodejs` |

Or download it from https://nodejs.org (choose **LTS**).

> **Linux note:** Ubuntu's own `sudo apt install nodejs` gives an older version (18) that is too old. Use the command
> above instead.

### 1d. Windows only: Windows Terminal

Windows 11 already has it. On Windows 10: `winget install --id Microsoft.WindowsTerminal -e`

---

## Step 2: Download the launcher

The launcher is **one program file** for your computer, from the
[latest release](https://github.com/Arshu/ArshuLauncher/releases/latest). It needs nothing else installed.

1. Make a folder for your work, for example `Documents\AIWork` (Windows) or `Documents/AIWork` (Mac/Linux), and go
   into it in the terminal:

   | Computer | Command |
   |---|---|
   | Windows | `mkdir $HOME\Documents\AIWork; cd $HOME\Documents\AIWork` |
   | Mac / Linux | `mkdir -p ~/Documents/AIWork && cd ~/Documents/AIWork` |

2. Download the launcher into it, saved as `runJointLauncher` (`runJointLauncher.exe` on Windows):

   | Computer | Command |
   |---|---|
   | Windows | `iwr https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-win-x64.exe -OutFile runJointLauncher.exe` (Windows on ARM: `runJointLauncher-win-arm64.exe`) |
   | Mac (Apple silicon or Intel) | `curl -fsSL https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-osx-$(uname -m \| sed s/x86_64/x64/) -o runJointLauncher && chmod +x runJointLauncher` |
   | Linux (x64 or ARM) | `curl -fsSL https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-linux-$(uname -m \| sed 's/x86_64/x64/;s/aarch64/arm64/') -o runJointLauncher && chmod +x runJointLauncher` |

> **Downloaded with the browser instead?** Rename the file to `runJointLauncher` (`runJointLauncher.exe` on
> Windows) and move it into your folder. On a Mac or Linux, then run `chmod +x runJointLauncher` in the folder; on a
> Mac also `xattr -d com.apple.quarantine runJointLauncher` (otherwise macOS refuses to open a file from the web).

> **Prefer to run the source?** With **.NET 10** or later installed (`winget install --id Microsoft.DotNet.SDK.10 -e`,
> `brew install --cask dotnet-sdk` or `sudo apt install dotnet-sdk-10.0`), any of these does exactly what the program
> file does: type its command wherever this guide says `./runJointLauncher`. The folder you run it from is the folder
> the assistant works in, and where the launcher keeps its settings file (`runJointLauncher.json`).
>
> | How | Command | The folder you work in |
> |---|---|---|
> | Download [runJointLauncher.cs](https://raw.githubusercontent.com/Arshu/ArshuLauncher/main/csharp/Tools/ArshuLauncher/runJointLauncher.cs) into your folder | `dotnet run runJointLauncher.cs --` | your folder |
> | Clone this repository (`git clone https://github.com/Arshu/ArshuLauncher.git`), then in the clone | `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs --` | the clone |
> | The same, with the clone's script | `runJointLauncher.bat` (Windows), `./runJointLauncher.sh` (Linux), `./runJointLauncher.command` or double-click it (Mac) | the clone |
> | In the clone, from the launcher's own folder (`cd csharp/Tools/ArshuLauncher`) | `dotnet run runJointLauncher.cs --` | `csharp/Tools/ArshuLauncher` |

---

## Step 3: Start a session

1. Open a terminal and go to your folder (`cd …` as in Step 2).
2. **Check everything is ready** (this opens nothing, it only checks):

   ```
   ./runJointLauncher --doctor
   ```

   Every line should say `[ok]`. A line saying `[missing]` tells you what to install and how. `[optional]` lines
   are fine to ignore.

3. **Start the session** (use your assistant's name):

   ```
   ./runJointLauncher --harness claude
   ```

The first time takes a little longer. Then **two windows open**: the terminal with your assistant, and the shared
browser. The assistant asks what you want to do:

| Choice | What it does |
|---|---|
| **joint-browser** | the assistant works in the shared browser with you: open pages, fill forms, check a flow |
| **joint-launcher-install** | installs anything still missing, asking you before each install |
| **joint-launcher-doctor** | finds out what is wrong when something didn't work |
| **Something else** | just tell it what you want |

> **The first time only:** your assistant may ask whether you **trust this folder**. Say yes if it is your own work
> folder. On a **Mac**, it may also ask to let the terminal **control Terminal**: click **OK**.

### Go straight to the browser next time

Add `--skill joint-browser` and the assistant starts working in the browser without asking first:

```
./runJointLauncher --harness claude --skill joint-browser
```

The launcher remembers that choice (in the settings file `runJointLauncher.json` it creates in your folder). Running
`./runJointLauncher` with nothing after it only shows the help.

---

## Step 4: Work with the assistant

Just type what you want in the terminal, in plain words. Some examples:

- *"Open https://example.com and tell me what's on the page."*
- *"Go to my site at http://localhost:3000 and walk me through the sign-up, one step at a time."*
- *"Fill the contact form with test data and check that the thank-you message appears."*
- *"Review the checkout page with me and list anything that looks broken."*

The assistant does each step **in the shared browser** while you watch, and tells you what happened. If something
didn't work (a button that does nothing, an error on the page), it tells you that too.

The first time it drives the browser, the assistant may ask to run a one-time setup (it installs a small, standard
tool called Playwright into a folder of your own). Say yes.

You can also click and type in the shared browser yourself at any time.

---

## Changing the layout

| You want | Add this to the start command |
|---|---|
| Terminal on the left, browser on the right (the default) | `--position left:right` |
| Terminal on the right | `--position right:left` |
| Terminal on top, browser below | `--position top:bottom` |
| Terminal at the bottom | `--position bottom:top` |
| A smaller terminal | `--ratio 1:4` (the terminal gets one fifth) |
| Half and half | `--ratio 1:1` |

Example: `./runJointLauncher --position bottom:top --ratio 1:3`

The launcher **remembers** your choice for next time.

---

## Ending a session

Close the two windows: the terminal and the shared browser. That's it. Nothing keeps running in the background.

---

## If something goes wrong

First run the check: `./runJointLauncher --doctor`. It names every problem and how to fix it.

| You see | What to do |
|---|---|
| `runJointLauncher` is not recognized / not found | the terminal is not in the folder you downloaded it to (`cd` into it, Step 2), or you left out the `./` in front. In the old Command Prompt (cmd), type `runJointLauncher` without `./`. |
| `permission denied` (Mac/Linux) | run `chmod +x runJointLauncher` in your folder. |
| macOS says the file can't be opened or is from an unidentified developer | run `xattr -d com.apple.quarantine runJointLauncher` in your folder (only needed when it was downloaded with the browser). |
| `claude is not on PATH` (or codex, pi) | install the assistant (Step 1a), then open a **new** terminal. |
| `127.0.0.1:9222 is already in use` | a shared browser from an earlier session is still open. Close it and try again. |
| `no harness` | add `--harness claude` (or codex, pi) to the command. |
| The assistant started in the **same** terminal instead of opening new windows | something is missing (it says what). Let it help you install it, then close it and run the start command again. |
| The windows are not side by side (Linux) | your desktop uses **Wayland**, which doesn't let apps arrange windows. Arrange them yourself, or log in with **"Ubuntu on Xorg"**. |
| The browser opens and closes at once (Ubuntu 23.10 or newer) | install Google Chrome from google.com/chrome, it works best on Ubuntu. |
| `Node.js 18… is too old` | install Node.js 20 or newer (Step 1c). |
| Still stuck | start with `./runJointLauncher --skill joint-launcher-doctor` and tell the assistant what happened. |

---

## Advanced: the settings file

`runJointLauncher.json` (in your folder, created on the first session):

```json
{
  "DefaultHarness": "claude",
  "DefaultSkill": "",
  "DefaultPosition": "left:right",
  "DefaultRatio": "1:2",
  "Skills": {},
  "Browser": "auto"
}
```

| Setting | Meaning |
|---|---|
| `DefaultHarness` | your assistant: `claude`, `codex` or `pi` |
| `DefaultSkill` | what to start on (empty = the assistant asks); `--skill <name>` sets it |
| `DefaultPosition` / `DefaultRatio` | the layout; `--position` and `--ratio` set them |
| `Skills` | your own assistant skills: a name and the path of its `SKILL.md` |
| `Browser` | `auto` (finds Chrome, Edge or Chromium), or the full path of the browser to use |

If the file gets broken, delete it: the next session creates a fresh one.

The launcher writes its skills into your folder (`.claude/skills`, `.agents/skills` or `.pi/skills`) and **never
overwrites** them. To get an updated copy after downloading a newer launcher, delete those `joint-…` folders and
start a session again.
