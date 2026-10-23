# Arshu Launcher

> [!WARNING]
> **This software is fully "vibe coded"**: it was written entirely with an AI coding assistant. Every part of it
> has been **verified by testing on Windows 11, macOS and Ubuntu Linux**, but it comes with no warranty (see the
> [MIT License](LICENSE)). Read the one file it is built from, [`runJointLauncher.cs`](runJointLauncher.cs), if you
> want to know exactly what it does, and use it at your own risk.

**Work side by side with an AI assistant, and watch everything it does in the browser.**

Arshu Launcher opens two windows on your screen with one command:

- on one side, a **terminal** where you chat with an AI assistant (Claude Code, Codex or pi);
- on the other side, a **browser window** that you and the assistant share.

When you ask the assistant to open a web page, fill a form or check a sign-up flow, it does it **in that browser, in
front of you**. You can watch every step, stop it, or click around yourself.

```
┌──────────────────────┬──────────────────────────────────────────┐
│  Terminal            │  Shared browser                          │
│                      │                                          │
│  > Check the sign-up │   ┌──────────────────────────────┐       │
│    page of my site   │   │  Sign up                     │       │
│                      │   │  Email  [ann@example.com   ] │       │
│  Opening the page…   │   │  [ Create account ]          │       │
│  Filling the email…  │   └──────────────────────────────┘       │
│  ✓ Account created   │                                          │
└──────────────────────┴──────────────────────────────────────────┘
```

**Website:** https://launcher.arshucs.com/

## Who is it for?

- Anyone who wants an AI assistant to **show** its work on a website, not just describe it.
- People who build or test websites and want a second pair of eyes: *"walk through my checkout with me"*.
- Teachers and teams who want to demonstrate an AI assistant working in a real browser.

You don't need to know how to program. You do type a few commands into a terminal once, and
[USAGE.md](USAGE.md) walks you through every one of them.

## What you need

| | What | Why |
|---|---|---|
| 1 | A Windows, Mac or Linux computer | to run it on |
| 2 | An AI assistant: **Claude Code**, **Codex** or **pi** | the one you chat with |
| 3 | A Chromium-based browser: **Chrome** or **Edge** | the shared window (Windows already has Edge) |
| 4 | *Optional:* **Node.js 20 or newer** | lets the assistant drive the browser |

You only install these once. [USAGE.md](USAGE.md) gives the exact install steps for each one. The launcher itself is
**one program file** that needs nothing else installed.

## Get started in 3 steps

1. **Install** the things above (see [USAGE.md → Step 1](USAGE.md#step-1-install-what-you-need)).
2. **Download** the launcher for your computer from the
   [latest release](https://github.com/Arshu/ArshuLauncher/releases/latest) into the folder you want to work in
   (the exact command is in [USAGE.md → Step 2](USAGE.md#step-2-download-the-launcher)):

   | Computer | File |
   |---|---|
   | Windows | [`runJointLauncher-win-x64.exe`](https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-win-x64.exe) |
   | Windows on ARM | [`runJointLauncher-win-arm64.exe`](https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-win-arm64.exe) |
   | Mac (Apple silicon) | [`runJointLauncher-osx-arm64`](https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-osx-arm64) |
   | Mac (Intel) | [`runJointLauncher-osx-x64`](https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-osx-x64) |
   | Linux | [`runJointLauncher-linux-x64`](https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-linux-x64) |
   | Linux on ARM | [`runJointLauncher-linux-arm64`](https://github.com/Arshu/ArshuLauncher/releases/latest/download/runJointLauncher-linux-arm64) |

3. **Run** it from a terminal opened in that folder (USAGE.md saves it as `runJointLauncher`):

   ```
   ./runJointLauncher --harness claude
   ```

   (Use `--harness codex` or `--harness pi` if that is your assistant.)

The two windows open, and the assistant asks you what you'd like to do.

Prefer to run the source? With **.NET 10** or later installed, download [`runJointLauncher.cs`](runJointLauncher.cs)
instead and run `dotnet run runJointLauncher.cs -- --harness claude`: it does exactly the same.

## Learn more

- [USAGE.md](USAGE.md): step-by-step guide for Windows, Mac and Linux, what to expect, and fixes for common
  problems.
- [FEATURE.md](FEATURE.md): everything it can do, explained simply.
- [LICENSE](LICENSE): MIT, free to use, share and change.

## Is it safe?

- The launcher is built from **one readable file** ([`runJointLauncher.cs`](runJointLauncher.cs)). It installs
  nothing by itself: when something is missing, the assistant asks before installing anything.
- The shared browser uses a **fresh, empty profile** every time: it can't see your normal browser's passwords,
  bookmarks or history.
- The browser's remote control port (`127.0.0.1:9222`) only accepts connections from **your own computer**.

## License

[MIT](LICENSE) © 2026 Sridharan Srinivasan
