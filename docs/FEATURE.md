# What Arshu Launcher can do

A plain-language tour of every feature. For the step-by-step setup, see [USAGE.md](USAGE.md).

## 1. Two windows, arranged for you

One command opens a **terminal** with your AI assistant and a **shared browser**, and places them neatly side by
side (or one above the other) to fill your screen.

- Choose the side: terminal on the **left, right, top or bottom**.
- Choose the sizes: a small terminal and a big browser (`--ratio 1:4`), half and half (`1:1`), and so on.
- Your choice is **remembered** for next time.
- Works on **Windows** (Windows Terminal), **Mac** (Terminal) and **Linux** (GNOME Terminal, Konsole, Xfce
  Terminal or xterm).

## 2. Your choice of AI assistant

Works with three assistants: **Claude Code**, **Codex** and **pi**. If the one you name isn't installed, the
launcher tells you the exact install command instead of failing with a confusing error.

## 3. A shared browser you can both use

The browser window is shared between you and the assistant:

- the assistant opens pages, clicks buttons and fills forms **while you watch**;
- you can click and type in it yourself at any time;
- it starts with a **fresh, empty profile** every time, separate from your normal browser, so your passwords,
  bookmarks and history stay private;
- it uses the browser you already have: **Chrome**, **Edge** or **Chromium** (found automatically, or name one in
  the settings).

## 4. The assistant drives the browser, step by step (joint-browser)

With the **joint-browser** skill, the assistant controls the shared browser using **Playwright**, the standard
open-source tool for automating browsers. For every action it reports:

- where the page was before and after;
- any **error** the page showed or hit behind the scenes;
- any **pop-up** (such as "Are you sure?"): it never clicks one silently;
- whether the action **did what it was meant to do**. A button that does nothing is reported as a problem, not
  quietly skipped.

It can also read the page, count things, take a **screenshot**, open and close tabs, upload a file, and more.

## 5. Review a website together

Ask for a **joint review** of a page or a flow, for example *"walk through the sign-up with me"*:

1. you agree what to check (the address, the steps, the test account);
2. the assistant does **one step at a time**, shows you the result and waits for your comments;
3. it keeps a list of what's wrong, with the evidence;
4. if it's your own website, it can **fix the code** (only when you agree) and run the step again;
5. when you're happy, it can turn the steps into an **automatic test** in your project.

## 6. It helps you set itself up

- `--doctor` checks your computer and lists, for each missing piece, the exact command to install it.
- If something the session needs is missing, the launcher doesn't just stop: it starts your assistant on a
  **setup helper** that explains what's missing and installs it, **asking you before each install**.
- A **troubleshooting helper** explains common problems in plain words.

## 7. One file, nothing hidden

- The whole launcher is **one file** you can read: [`runJointLauncher.cs`](../csharp/Tools/ArshuLauncher/runJointLauncher.cs). Each
  [release](https://github.com/Arshu/ArshuLauncher/releases/latest) compiles it into **one program file** per
  computer (Windows, Mac, Linux) that runs without installing anything else; with .NET 10 you can run the source
  itself instead.
- It needs no extra packages, and installs nothing by itself.
- It works in **any folder**, with or without a project in it.
- When the windows are open, the launcher **exits**: nothing keeps running in the background. Close the two
  windows to end the session.
- It brings its own small assistant skills and copies them into your folder the first time. It **never
  overwrites** a copy you've changed.

## 8. Add your own skills

If you or your team write your own assistant skills, list them in the settings file and the assistant offers them
when a session starts:

```json
"Skills": { "my-review": ".claude/skills/my-review/SKILL.md" }
```

## Good to know

- **Linux with Wayland:** both windows open, but Wayland doesn't let apps arrange windows, so you place them
  yourself (or log in with an X11 session such as "Ubuntu on Xorg").
- **Run it at the computer itself**, not over a remote command line (ssh). The windows need a desktop to open on.
- The browser's control port (`127.0.0.1:9222`) only accepts connections from your own computer.
