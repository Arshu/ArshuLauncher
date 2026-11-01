#!/usr/bin/dotnet run

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>
/// A one-shot, command-line launcher for a joint session: a terminal running an AI harness (claude, codex or pi) and a
/// shared Chromium-based browser splitting the primary monitor's work area (side by side or stacked, in a chosen
/// ratio), with the DevTools protocol on 127.0.0.1:9222 so the harness's browser tools act on the window the user
/// watches. It opens both windows and exits: no panel, no capture, no background process. The terminal starts the
/// harness on a skill (--skill, else DefaultSkill, else joint-launcher, which asks what to do; --skillargs is the
/// skill's own arguments).
///
/// Repository-independent: copy this file and the skills folder into any folder (a repository or not) on Windows,
/// macOS or Linux and run it from there with .NET 10+. It needs no package. runJointBuild.cs compiles it to the
/// NativeAOT single file runJointLauncher-&lt;os&gt;-&lt;arch&gt; (e.g. runJointLauncher-win-x64.exe), which runs the
/// same way without .NET; the help, the doctor and the skills name whichever was run (LaunchCommand). It embeds the skills joint-launcher-install and joint-launcher-doctor (EmbeddedSkills), takes joint-launcher
/// and joint-browser (FolderSkills) from the nearest skills folder at or above it (absent, the install skill downloads them from
/// SkillsRemote in runJointLauncher.json) and writes them to the harness's skills folder
/// there when missing, never over a copy already there; other skills are named in runJointLauncher.json with their
/// SKILL.md path. Launching checks, in order: the harness is supported and installed (else it says how to install it
/// and stops), the skills are in place, then what the session needs (a terminal and a browser). Anything of
/// that missing starts the harness in the launcher's own console on joint-launcher-install (a bootstrap) instead.
///
/// A launch opens no second terminal unless asked to. Inside a harness (CallingHarness), or started by any other program (input that
/// is no console), the caller's harness is the session's: the browser opens beside its terminal, or over the whole work
/// area when that window cannot be moved, nothing else starts, and what is missing is printed with its fix. A named
/// harness only says whose skills folder gets the embedded skills; it need not be on PATH. From a plain terminal the
/// harness starts in that terminal, beside the browser. --newterminal opens a terminal of its own instead, and so does a
/// harness named inside another one, whose terminal then stays where it is.
///
/// runJointLauncher.json (beside where it runs, per machine; created on the first session) holds the defaults for
/// --harness, --skill, --position and --ratio, Skills and DefaultBrowser; a --skill, --position or --ratio given on the command
/// line becomes the new default once the session opens.
///
/// The session records where its browser is (&lt;local app data&gt;/joint-launcher/session.json: cdp, harness, skill,
/// skipHarness) for a skill to attach to.
///
/// Platforms (run it from the desktop session, not over ssh): Windows (Windows Terminal, Win32 placement), macOS
/// (Terminal.app through AppleScript, the browser placed by its window flags) and Linux (gnome-terminal, konsole,
/// xfce4-terminal or xterm; on X11 both windows are placed through libX11, on Wayland both open unplaced).
///
/// Usage: dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- (or runJointLauncher-&lt;os&gt;-&lt;arch&gt;) [--harness claude|codex|pi] [--skill <name>] [--skillargs "<args>"]
///        [--position left:right|right:left|top:bottom|bottom:top] [--ratio 1:4]
/// </summary>
class RunJointLauncher
{
    private const int CdpPort = 9222;               // the usual DevTools port browser MCP servers attach to
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string ConsoleWindowClass = "ConsoleWindowClass";
    private const string ChromeWindowClass = "Chrome_WidgetWin_1";
    private const string DefaultsFileName = "runJointLauncher.json";
    // This file by its path from the workspace (the current folder), as `dotnet run` takes it from there.
    private static string LauncherSourcePath => Path.GetRelativePath(Directory.GetCurrentDirectory(), (string)AppContext.GetData("EntryPointFilePath")!).Replace('\\', '/');
    // Run from source, the SDK sets EntryPointFilePath; the published single-file binary (runJointBuild.cs) has none.
    private static readonly bool FromSource = AppContext.GetData("EntryPointFilePath") is string;
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(30);
    // terminal:browser placement; the first named side is the terminal's.
    private static readonly string[] Positions = { "left:right", "right:left", "top:bottom", "bottom:top" };

    /// <summary>
    /// A harness the terminal can start: its command, the folder it reads a project's skills from (under the folder
    /// the launcher runs from), how a prompt starts one of them, and how to install it.
    /// </summary>
    sealed record Harness(string Name, string Command, string SkillsDir, string SkillPrefix, string InstallWindows, string InstallUnix);

    private static readonly Harness[] Harnesses =
    {
        new("claude", "claude", ".claude/skills", "/",
            "irm https://claude.ai/install.ps1 | iex   (PowerShell)", "curl -fsSL https://claude.ai/install.sh | bash"),
        new("codex", "codex", ".agents/skills", "$",
            "npm install -g @openai/codex   (Node.js first: winget install --id OpenJS.NodeJS.LTS -e)",
            "npm install -g @openai/codex   (Node.js first), or on macOS brew install --cask codex"),
        new("pi", "pi", ".pi/skills", "/skill:",
            "npm install -g @earendil-works/pi-coding-agent   (Node.js first: winget install --id OpenJS.NodeJS.LTS -e)",
            "npm install -g @earendil-works/pi-coding-agent   (Node.js first)"),
    };

    /// <summary>
    /// A skill embedded in the tool (EmbeddedSkills), written to the harness's skills folder when it is not there yet;
    /// a copy already there is the user's and is never overwritten.
    /// </summary>
    sealed record JointSkill(string Name, string Text, string HelperName = "", string HelperText = "")
    {
        /// <summary>The files the skill folder holds: SKILL.md, and the helper when the skill has one.</summary>
        public IEnumerable<(string Name, string Text)> Files()
        {
            yield return ("SKILL.md", Text);
            if (HelperName.Length > 0) yield return (HelperName, HelperText);
        }
    }

    /// <summary>A skill read from the skills folder: its name and the files its folder holds.</summary>
    sealed record FolderSkill(string Name, string[] Files);

    // The skill the harness starts on when no skill is given: it lists the available skills and loads the chosen one.
    private const string ChooserSkill = "joint-launcher";
    // The skill a bootstrap starts on (the harness in the launcher's console) when the session cannot open yet.
    private const string InstallSkill = "joint-launcher-install";
    private const string DoctorSkill = "joint-launcher-doctor";
    // Drives the shared browser with Playwright (its helper browser.mjs beside SKILL.md); the same skill in runJointSession.cs.
    private const string BrowserSkill = "joint-browser";
    // The skills not embedded: a launch copies them from the tracked skills folder (FindSkillsFolder), one source for every tool.
    private const string SkillsFolderName = "skills";
    private static readonly FolderSkill[] FolderSkills = { new(ChooserSkill, new[] { "SKILL.md" }), new(BrowserSkill, new[] { "SKILL.md", "browser.mjs" }) };

    /// <summary>
    /// The launcher's defaults (runJointLauncher.json); an empty DefaultHarness makes --harness mandatory. Skills names
    /// the skills besides the embedded ones, each with its SKILL.md path (relative to the launch folder unless
    /// absolute); one is offered only while that file exists. DefaultBrowser is "" (Google Chrome, on every OS), "auto"
    /// (FindBrowser's order) or the path of a Chromium-based browser.
    /// </summary>
    sealed record JointDefaults(string DefaultHarness, string DefaultSkill, string DefaultPosition, string DefaultRatio,
        IReadOnlyDictionary<string, string> Skills, string DefaultBrowser, string SkillsRemote);

    private static readonly JointDefaults InitialDefaults = new("", "", "left:right", "1:2", new Dictionary<string, string>(), "", "https://raw.githubusercontent.com/Arshu/ArshuLauncher/main/skills");
    private static readonly string[] DefaultsKeys = { "DefaultHarness", "DefaultSkill", "DefaultPosition", "DefaultRatio", "DefaultBrowser" };

    static int Main(string[] args)
    {
        string? harnessName = null, skillName = null, skillArgs = null, position = null, ratio = null;
        bool doctor = false, newTerminal = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--help" or "-h") { PrintUsage(); return 0; }
            if (a == "--doctor") doctor = true;
            else if (a == "--newterminal") newTerminal = true;
            else if (a == "--harness" && i + 1 < args.Length) harnessName = args[++i].ToLowerInvariant();
            else if (a == "--skill" && i + 1 < args.Length) skillName = args[++i];
            else if (a == "--skillargs" && i + 1 < args.Length) skillArgs = args[++i].Trim();
            else if (a == "--position" && i + 1 < args.Length) position = args[++i].ToLowerInvariant();
            else if (a == "--ratio" && i + 1 < args.Length) ratio = args[++i];
            else
            {
                Console.WriteLine($"Error: unknown argument '{a}'.");
                PrintUsage();
                return 1;
            }
        }

        // Never a second terminal: a harness, or any program (input that is no console), is the session's own, and a plain terminal runs the harness itself.
        var chain = ParentChain();
        Harness? caller = doctor || newTerminal ? null : CallingHarness(chain);
        // A harness named other than the caller starts in a terminal of its own; the caller's stays where it is.
        if (caller != null && harnessName != null && harnessName != caller.Name) { newTerminal = true; caller = null; }
        // Inside a harness nothing needs saying, so no arguments open the session; anywhere else they print the usage.
        if (args.Length == 0 && caller == null) { PrintUsage(); return 0; }
        bool skipHarness = !doctor && !newTerminal && (caller != null || Console.IsInputRedirected);
        if (skipHarness && (skillName != null || skillArgs != null))
        {
            Console.WriteLine(caller != null
                ? $"Error: this runs inside {caller.Name}, which is the session's harness, so --skill and --skillargs have nothing to start; load the skill here, or pass --newterminal for a terminal of its own."
                : "Error: a program started this, so it starts no harness and --skill and --skillargs have nothing to start; pass --newterminal for a terminal of its own.");
            return 1;
        }

        var workspaceDir = Directory.GetCurrentDirectory();
        string defaultsFile = Path.Combine(workspaceDir, DefaultsFileName);
        if (doctor) return Doctor(workspaceDir, defaultsFile, harnessName);
        JointDefaults? defaults = ReadDefaults(defaultsFile, out string defaultsError);
        if (defaults == null)
        {
            Console.WriteLine(defaultsError);
            return 1;
        }
        // --harness is for this run only; DefaultHarness is changed by editing the file.
        bool defaultsChanged = skillName != null || position != null || ratio != null;
        harnessName ??= caller?.Name ?? defaults.DefaultHarness;
        position ??= defaults.DefaultPosition;
        ratio ??= defaults.DefaultRatio;
        skillName ??= defaults.DefaultSkill;
        if (Array.IndexOf(Positions, position) < 0)
        {
            Console.WriteLine($"Error: position '{position}' is not one of {string.Join(", ", Positions)} (terminal:browser).");
            return 1;
        }
        if (!ParseRatio(ratio, out int terminalPart, out int browserPart))
        {
            Console.WriteLine($"Error: ratio '{ratio}' is not <terminal>:<browser> with whole numbers 1-99, e.g. 1:4.");
            return 1;
        }
        ratio = $"{terminalPart}:{browserPart}";
        // Side by side a terminal narrower than this falls short of the width it can take (Windows Terminal stopped at 691 px of 2560).
        if (position is "left:right" or "right:left" && browserPart > 2 * terminalPart)
        {
            Console.WriteLine("[joint-launcher] side by side the terminal takes at least 1:2: using 1:2.");
            (terminalPart, browserPart, ratio, defaultsChanged) = (1, 2, "1:2", true);
        }

        bool isWindows = OperatingSystem.IsWindows(), isMac = OperatingSystem.IsMacOS(), isLinux = OperatingSystem.IsLinux();
        if (!isWindows && !isMac && !isLinux)
        {
            Console.WriteLine("Error: runJointLauncher lays out its windows on Windows, macOS and Linux only.");
            return 1;
        }

        // 1. The harness: one of the supported ones, named by --harness or DefaultHarness.
        if (harnessName.Length == 0 && !skipHarness)
        {
            Console.WriteLine($"Error: no harness: pass --harness {HarnessNames()} or set DefaultHarness in {defaultsFile}.");
            return 1;
        }
        Harness? harness = harnessName.Length == 0 ? null : FindHarness(harnessName);
        if (harness == null && harnessName.Length > 0)
        {
            Console.WriteLine($"Error: harness '{harnessName}' is not supported; use {HarnessNames()}.");
            return 1;
        }
        // 2. The harness is installed: without it there is nothing to start, so this is the one prerequisite the
        // launcher cannot hand to the harness. A session inside its caller starts none.
        string? harnessPath = harness == null ? null : HarnessPath(harness);
        if (harnessPath == null && !skipHarness)
        {
            Console.WriteLine($"Error: {harness!.Name} is not on PATH. Install it with:\n  {(isWindows ? harness.InstallWindows : harness.InstallUnix)}\n" +
                              $"then sign in once by running {harness.Command} in a NEW terminal, and run the launcher again from there.");
            return 1;
        }
        // 3. The embedded skills in the harness's skills folder (a copy already there is kept).
        if (harness != null)
        {
            string? skillsError = InstallSkills(workspaceDir, harness, FindSkillsFolder());
            if (skillsError != null)
            {
                Console.WriteLine(skillsError);
                return 1;
            }
        }
        else Console.WriteLine($"[joint-launcher] no harness named: the skills step is skipped (--harness {HarnessNames()} installs {BrowserSkill} for the calling session).");

        string startSkill = "", skillPrompt = "", title = "";
        if (!skipHarness)
        {
            // No skill starts the harness on joint-launcher, which asks the user which skill to load.
            startSkill = skillName.Length == 0 ? ChooserSkill : skillName;
            string? skillFile = SkillFile(workspaceDir, harness!, defaults, startSkill);
            if (skillFile == null)
            {
                Console.WriteLine($"Error: skill '{startSkill}' is neither embedded nor in the skills folder ({CarriedSkillNames()}) nor configured in Skills of {defaultsFile}" +
                                  $"{(defaults.Skills.Count == 0 ? "" : $" ({string.Join(", ", defaults.Skills.Keys)})")}.");
                return 1;
            }
            if (!IsCarriedSkill(startSkill) && !File.Exists(skillFile))
            {
                Console.WriteLine($"Error: skill '{startSkill}' is configured at {skillFile}, which does not exist; fix its path in Skills of {defaultsFile}.");
                return 1;
            }
            if (startSkill == ChooserSkill && !string.IsNullOrEmpty(skillArgs))
            {
                Console.WriteLine($"Error: --skillargs are a named skill's arguments; pass --skill too, or leave both out for {ChooserSkill}.");
                return 1;
            }
            if (!string.IsNullOrEmpty(skillArgs) && !SafeSkillArgs(skillArgs))
            {
                Console.WriteLine("Error: --skillargs takes letters, digits, spaces and - _ , . / = : only (it is typed into the terminal's command line).");
                return 1;
            }
            var lostSkills = LostSkills(workspaceDir, harness!, FindSkillsFolder());
            if (lostSkills.Contains(startSkill))
            {
                // A skill is not required: the terminal and the browser are useful without one, so the harness starts plain.
                Console.WriteLine($"[joint-launcher] no skill: {LostSkillsWhy(lostSkills, FindSkillsFolder(), defaults.SkillsRemote)}; {harness!.Name} starts without {startSkill}.");
                title = "Joint session";
            }
            else
            {
                string? prompt = SkillPrompt(workspaceDir, harness!, skillFile, startSkill, skillArgs, out string promptError);
                if (prompt == null)
                {
                    Console.WriteLine(promptError);
                    return 1;
                }
                skillPrompt = prompt;
                title = $"Joint session - {startSkill}";
            }
        }

        // A session opens a terminal of its own only on --newterminal.
        bool hereTerminal = !skipHarness && !newTerminal;
        bool opensTerminal = newTerminal;

        // 4-5. What the session needs besides the harness. Anything missing is no refusal: the harness starts in this
        // console on joint-launcher-install (a bootstrap) to set it up, and the next launch opens the session. Inside its caller
        // this console is the caller's shell, so what is missing is only printed with its fix.
        var missing = new List<string>();
        void Missing(string token, string why)
        {
            missing.Add(token);
            Console.WriteLine($"[joint-launcher] missing {token}: {why}");
        }
        // Linux places windows through X11; Wayland lets no app place windows, so there both open unplaced.
        bool placeWindows = !(isLinux && IsWayland());
        string[]? linuxTerminal = null;
        if (opensTerminal && isWindows && FindOnPath("wt.exe") == null)
            Missing("terminal", "Windows Terminal (wt.exe) is not on PATH: winget install --id Microsoft.WindowsTerminal -e");
        if (opensTerminal && isMac && FindOnPath("osascript") == null)
            Missing("terminal", "osascript is not on PATH (it opens Terminal.app); it ships with macOS in /usr/bin");
        if (isLinux)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                Missing("desktop", "no desktop session (DISPLAY and WAYLAND_DISPLAY are empty): run the launcher from a terminal on the desktop, not over ssh");
            if (opensTerminal)
            {
                linuxTerminal = LinuxTerminalCommand(workspaceDir, title, harness!.Command, skillPrompt);
                if (linuxTerminal == null)
                    Missing("terminal", "no supported terminal on PATH (gnome-terminal, konsole, xfce4-terminal or xterm): sudo apt install gnome-terminal");
            }
        }
        string? foundBrowser = FindBrowser(defaults.DefaultBrowser, out string browserFound);
        if (foundBrowser == null) Missing("browser", browserFound);
        else Console.WriteLine($"[joint-launcher] browser: {foundBrowser} ({browserFound})");

        if (missing.Count > 0)
            return skipHarness ? 1 : Bootstrap(workspaceDir, harness!, harnessPath!, missing);
        string chrome = foundBrowser!;   // nothing is missing past the bootstrap

        string deskDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "joint-launcher");
        string profileDir = Path.Combine(deskDir, "profile");
        string sessionFile = Path.Combine(deskDir, "session.json");
        if (isMac) CloseWindowlessBrowser(sessionFile);
        if (PortAnswers(CdpPort))
        {
            Console.WriteLine($"Error: 127.0.0.1:{CdpPort} is already in use (a joint session's browser, or another browser started with " +
                              "--remote-debugging-port, is still open). Nothing was started: attach to it, or close it and relaunch.");
            return 1;
        }

        string statusPage = Path.Combine(deskDir, "desk.html");
        try
        {
            // Nothing holds the profile once 9222 is free, so every session starts from an empty one.
            if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true);
            Directory.CreateDirectory(profileDir);
            // Until the harness navigates the window it shows what to do next instead of a blank page.
            File.WriteAllText(statusPage, StatusPage(harness?.Name ?? "", skillPrompt, skipHarness), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A browser can hold the profile without answering on 9222: one that lost its DevTools port, or was started without it.
            Console.WriteLine($"Error: could not prepare the session's browser profile in {deskDir} ({e.Message}); close any browser still using {profileDir} and relaunch.");
            return 1;
        }

        Rect work = default;
        if (isWindows)
        {
            // Window placement works in physical pixels across monitors only when this process is per-monitor DPI aware.
            SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);
            if (!SystemParametersInfo(SpiGetWorkArea, 0, ref work, 0))
            {
                Console.WriteLine($"Error: SystemParametersInfo(SPI_GETWORKAREA) failed ({Marshal.GetLastWin32Error()}).");
                return 1;
            }
        }
        else if (placeWindows)
        {
            string? areaError = isMac ? MacWorkArea(out work) : X11.WorkArea(out work);
            if (areaError != null)
            {
                Console.WriteLine(areaError);
                return 1;
            }
        }
        Func<Rect, string?>? moveCaller = null;
        string callerWhy = "";
        if (placeWindows && !opensTerminal) moveCaller = FindCallerTerminal(chain, out callerWhy);
        Rect terminalArea = default, browserArea = default;
        if (placeWindows && !opensTerminal && moveCaller == null)
        {
            browserArea = work;
            Console.WriteLine($"[joint-launcher] work area {work.Right - work.Left}x{work.Bottom - work.Top} at {work.Left},{work.Top}: the browser alone ({callerWhy})");
        }
        else if (placeWindows)
        {
            Layout(work, position, terminalPart, browserPart, out terminalArea, out browserArea);
            Console.WriteLine($"[joint-launcher] work area {work.Right - work.Left}x{work.Bottom - work.Top} at {work.Left},{work.Top}, {position} {ratio}: " +
                              $"terminal {terminalArea.Right - terminalArea.Left}x{terminalArea.Bottom - terminalArea.Top} | " +
                              $"browser {browserArea.Right - browserArea.Left}x{browserArea.Bottom - browserArea.Top}");
        }
        else
            Console.WriteLine($"[joint-launcher] Wayland lets no app place windows: {(skipHarness ? "the browser opens" : "the terminal and the browser open")} where the desktop puts them; " +
                              "arrange them yourself, or log in to an X11 session for the layout.");
        // Browser first, as with a terminal of its own; this terminal moves once the session can open.
        void MoveCaller()
        {
            if (moveCaller?.Invoke(terminalArea) is string moveError) Console.WriteLine($"[joint-launcher] {moveError}");
        }

        string browserLog = Path.Combine(deskDir, "browser.log");
        // Browser first: it is the slower window to appear, and the terminal is the one the user types into next.
        var chromeArgs = new List<string>
        {
            $"--remote-debugging-port={CdpPort}",
            $"--user-data-dir={profileDir}",
            "--no-first-run",
            "--no-default-browser-check",
        };
        // macOS places the browser by these flags alone (points, as the work area); Linux moves it through X11 after.
        if (!isWindows && placeWindows)
        {
            chromeArgs.Add($"--window-position={browserArea.Left},{browserArea.Top}");
            chromeArgs.Add($"--window-size={browserArea.Right - browserArea.Left},{browserArea.Bottom - browserArea.Top}");
        }
        chromeArgs.Add(new Uri(statusPage).AbsoluteUri);

        using var browser = OpenBrowser(chrome, chromeArgs, browserLog, browserArea, isWindows, isLinux && placeWindows, out string browserError);
        if (browser == null)
        {
            Console.WriteLine(browserError);
            return 1;
        }
        // From here a launch that fails closes what it opened, so no browser outlives a refused session.
        if (!WaitForCdp(browser))
        {
            Console.WriteLine(browser.HasExited
                ? $"Error: the browser exited with code {browser.ExitCode} before its DevTools protocol answered on 127.0.0.1:{CdpPort}." +
                  (isWindows ? "" : $"\nIts output ({browserLog}):\n{TailOf(browserLog)}")
                : $"Error: the browser opened but its DevTools protocol never answered on 127.0.0.1:{CdpPort}.");
            CloseSession(browser);
            return 1;
        }

        MoveCaller();
        if (opensTerminal)
        {
            // The terminal runs the harness as a session of its own, not as a child of a Claude that ran this launcher.
            ForgetParentClaudeSession();
            string? terminalError = isWindows ? OpenWindowsTerminal(workspaceDir, title, harness!.Command, skillPrompt, terminalArea)
                : isMac ? OpenMacTerminal(workspaceDir, title, harness!.Command, skillPrompt, terminalArea)
                : OpenLinuxTerminal(linuxTerminal!, browser.Id, placeWindows ? terminalArea : (Rect?)null);
            if (terminalError != null)
            {
                Console.WriteLine(terminalError);
                CloseSession(browser);
                return 1;
            }
        }

        WriteSession(sessionFile, browser.Id, profileDir, workspaceDir, harness?.Name ?? "", startSkill, skipHarness);
        // Only an opened session makes its command-line choices the defaults, so a refused run changes nothing.
        // A run that starts no harness does not create the file.
        if (defaultsChanged || (!skipHarness && !File.Exists(defaultsFile)))
        {
            WriteDefaults(defaultsFile, defaults with { DefaultSkill = skillName, DefaultPosition = position, DefaultRatio = ratio });
            Console.WriteLine($"[joint-launcher] defaults saved to {defaultsFile}: skill {(skillName.Length == 0 ? "(none: " + ChooserSkill + " asks)" : skillName)}, position {position}, ratio {ratio}");
        }

        Console.WriteLine($"[joint-launcher] browser pid {browser.Id} on CDP 127.0.0.1:{CdpPort}; session file {sessionFile}");
        if (skipHarness)
        {
            Console.WriteLine($"[joint-launcher] done: the browser stays open on its own; attach from your harness with {BrowserSkill} (CDP http://127.0.0.1:{CdpPort}) and close it to end the session.");
            return 0;
        }
        if (hereTerminal)
        {
            Console.WriteLine($"[joint-launcher] this terminal starts {harness!.Name} {OnSkill(skillPrompt)}; exiting it closes the browser.");
            // Windows ends this process as soon as the handler returns, so closing the console closes the browser in the handler.
            using var hungUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => CloseSession(browser));
            int exitCode = RunHarnessHere(workspaceDir, harness, harnessPath!, skillPrompt, new List<string>());
            CloseSession(browser);
            return exitCode;
        }
        Console.WriteLine($"[joint-launcher] the {position.Split(':')[0]} terminal starts {harness!.Name} {OnSkill(skillPrompt)}");
        Console.WriteLine("[joint-launcher] done: the windows stay open on their own; close them to end the session.");
        return 0;
    }

    /// <summary>
    /// Opens the browser in its area, detached from this console so it outlives the launcher. Null, with the error,
    /// when its window never appears (the browser is closed again).
    /// </summary>
    static Process? OpenBrowser(string chrome, List<string> chromeArgs, string browserLog, Rect browserArea, bool isWindows, bool placeOnX11, out string error)
    {
        error = "";
        Process opened;
        IntPtr? window;
        if (isWindows)
        {
            // Shell-executed so the browser inherits none of this console's handles: an inherited stdout keeps a
            // caller's pipe (`dotnet run ... | tail`) open until the browser closes, which hangs the caller.
            var chromeStart = new ProcessStartInfo(chrome) { UseShellExecute = true };
            foreach (var arg in chromeArgs) chromeStart.ArgumentList.Add(arg);
            opened = Process.Start(chromeStart) ?? throw new InvalidOperationException($"could not start {chrome}");
            window = WaitForWindow(ChromeWindowClass, hwnd => WindowProcessId(hwnd) == opened.Id);
            if (window is IntPtr shown) Place(shown, browserArea);
        }
        else
        {
            opened = StartDetached(chrome, chromeArgs, browserLog);
            if (!placeOnX11) return opened;
            window = X11.WaitForWindow(w => w.Pid == opened.Id);
            if (window is IntPtr x11Window) X11.Place(x11Window, browserArea);
        }
        if (window != null) return opened;
        error = NeverAppeared("the browser's window");
        CloseSession(opened);
        opened.Dispose();
        return null;
    }

    static void WriteSession(string sessionFile, int browserPid, string profileDir, string workspaceDir, string harness, string skill, bool skipHarness)
    {
        using var stream = File.Create(sessionFile);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("cdp", $"http://127.0.0.1:{CdpPort}");
        json.WriteNumber("browserPid", browserPid);
        json.WriteString("profileDir", profileDir);
        json.WriteString("workspace", workspaceDir);
        json.WriteString("harness", harness);
        json.WriteString("skill", skill);
        json.WriteBoolean("skipHarness", skipHarness);
        json.WriteString("startedUtc", DateTime.UtcNow.ToString("o"));
        json.WriteEndObject();
    }

    /// <summary>The processes this one runs under, nearest first (itself included): pid and lowercase name without extension.</summary>
    static List<(int Pid, string Name)> ParentChain()
    {
        var chain = new List<(int Pid, string Name)>();
        if (OperatingSystem.IsWindows())
        {
            var processes = new Dictionary<int, (int Parent, string Name)>();
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            var entry = new ProcessEntry32 { Size = Marshal.SizeOf<ProcessEntry32>() };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                processes[entry.ProcessId] = (entry.ParentProcessId, entry.ExeFile);
            CloseToolhelpSnapshot(snapshot);
            for (int pid = Environment.ProcessId; chain.Count < MaxParents && processes.TryGetValue(pid, out var process); pid = process.Parent)
                chain.Add((pid, Path.GetFileNameWithoutExtension(process.Name).ToLowerInvariant()));
            // An exited parent ends the walk (a nested script of a harness's shell); Claude Code names its own process, which leads on to the terminal.
            if (!chain.Exists(p => p.Name == "windowsterminal") && int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_PID"), out int harness))
            {
                for (int pid = harness; chain.Count < MaxParents && processes.TryGetValue(pid, out var process); pid = process.Parent)
                    chain.Add((pid, Path.GetFileNameWithoutExtension(process.Name).ToLowerInvariant()));
            }
            return chain;
        }
        for (int pid = Environment.ProcessId; pid > 1 && chain.Count < MaxParents;)
        {
            string[] parts = RunTool("ps", new[] { "-o", "ppid=", "-o", "comm=", "-p", pid.ToString() }, out int exit).Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (exit != 0 || parts.Length < 2 || !int.TryParse(parts[0], out int parent)) break;
            chain.Add((pid, Path.GetFileNameWithoutExtension(parts[1]).ToLowerInvariant()));
            pid = parent;
        }
        return chain;
    }

    /// <summary>
    /// The harness this launch runs inside, which is then the session's own: Claude Code and pi mark their children (CLAUDECODE, PI_CODING_AGENT), and any
    /// harness shows in the parent processes by its command (one running on node shows as node, but its tools' input is no console).
    /// </summary>
    static Harness? CallingHarness(List<(int Pid, string Name)> chain)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDECODE"))) return FindHarness("claude");
        if (Environment.GetEnvironmentVariable("PI_CODING_AGENT") == "true") return FindHarness("pi");
        foreach (var (_, name) in chain)
            if (FindHarness(name) is Harness harness) return harness;
        return null;
    }

    private const int MaxParents = 32;
    private const uint Th32csSnapProcess = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public int Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public int ModuleId, Threads, ParentProcessId, PriorityBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    static extern bool CloseToolhelpSnapshot(IntPtr handle);

    /// <summary>
    /// What moves the terminal this launch runs in (a second terminal is what the session avoids); null, with why, when
    /// that window cannot be told for certain: an IDE's or an app's terminal, a Windows Terminal tab it cannot find, a
    /// macOS terminal other than Terminal.app, a Linux terminal with no WINDOWID that is not the active window, Wayland. In a Windows Terminal window
    /// with several tabs only this tab moves, into a new window.
    /// </summary>
    static Func<Rect, string?>? FindCallerTerminal(List<(int Pid, string Name)> chain, out string why)
    {
        why = "";
        if (OperatingSystem.IsWindows())
        {
            // The process under Windows Terminal is the shell in this tab, and shares its console.
            int terminal = chain.FindIndex(p => p.Name == "windowsterminal");
            if (terminal > 0)
            {
                int shell = chain[terminal - 1].Pid;
                string[] tab = TerminalTab("find", shell);
                if (tab[0] != "window")
                {
                    why = tab[1];
                    return null;
                }
                var window = (IntPtr)long.Parse(tab[1]);
                if (tab[2] == "1") return area => { Place(window, area); return null; };
                return area =>
                {
                    string[] moved = TerminalTab("move", shell);
                    if (moved[0] != "moved") return $"this tab stays in its window: {moved[1]}";
                    Place((IntPtr)long.Parse(moved[1]), area);
                    return null;
                };
            }
            if (TopLevelWindows(ConsoleWindowClass).Contains(GetConsoleWindow()))
            {
                IntPtr console = GetConsoleWindow();
                return area => { Place(console, area); return null; };
            }
            why = "this runs in no terminal window it can move (an IDE's or an app's)";
            return null;
        }
        if (OperatingSystem.IsMacOS())
        {
            if (Environment.GetEnvironmentVariable("TERM_PROGRAM") != "Apple_Terminal")
            {
                why = "only Terminal.app can be moved";
                return null;
            }
            string id = RunTool("osascript", new[] { "-e", "tell application \"Terminal\" to id of front window" }, out int exit).Trim();
            if (exit != 0 || id.Length == 0)
            {
                why = $"Terminal.app gave no window through osascript: {id}";
                return null;
            }
            return area =>
            {
                string output = RunTool("osascript", new[] { "-e",
                    $"tell application \"Terminal\" to set bounds of (first window whose id is {id}) to {{{area.Left}, {area.Top}, {area.Right}, {area.Bottom}}}" }, out int moved);
                return moved == 0 ? null : $"Terminal.app could not be moved through osascript: {output.Trim()}";
            };
        }
        if (IsWayland())
        {
            why = "Wayland lets no app place windows";
            return null;
        }
        if (long.TryParse(Environment.GetEnvironmentVariable("WINDOWID"), out long x11Window))
            return area => { X11.Place((IntPtr)x11Window, area); return null; };
        // gnome-terminal sets none, but a launch runs in the window just typed in.
        if (X11.ActiveWindow() is X11.Window active && chain.Exists(p => p.Pid == active.Pid))
            return area => { X11.Place(active.Id, area); return null; };
        why = "this terminal sets no WINDOWID and is not the active window";
        return null;
    }

    /// <summary>
    /// Finds or moves the Windows Terminal tab of the shell, whichever window holds it: wt.exe has no move-tab, so this
    /// goes through UI Automation and the tab's command palette. A marker in the console title names the one tab (the
    /// title alone does not: tabs can share one, and a harness animates its own). "find" answers "window handle tabs",
    /// "move" takes the focus for a moment and answers "moved handle"; else the first part is "refused" with why.
    /// </summary>
    static string[] TerminalTab(string action, int shell)
    {
        string script = $"$action = '{action}'\n$shell = {shell}\n{TerminalTabScript}";
        string output = RunTool("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }, out int exit).Trim();
        string[] answer = output.Split(' ', action == "find" ? 3 : 2);
        return exit == 0 && answer.Length > 1 ? answer : new[] { "refused", $"PowerShell failed ({exit}): {output}" };
    }

    private const string TerminalTabScript = """
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class Tab
{
    [DllImport("kernel32")] public static extern bool FreeConsole();
    [DllImport("kernel32")] public static extern bool AttachConsole(int pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern int GetConsoleTitleW(StringBuilder title, int size);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool SetConsoleTitleW(string title);
    [DllImport("kernel32")] static extern uint GetCurrentThreadId();
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] struct Key { public ushort Code, Scan; public uint Flags, Time; public IntPtr Extra; public uint Pad1, Pad2; }
    [StructLayout(LayoutKind.Sequential)] struct KeyInput { public uint Type; public Key Key; }
    [DllImport("user32")] static extern uint SendInput(uint count, KeyInput[] inputs, int size);
    public static void Front(IntPtr window)
    {
        uint process, front = GetWindowThreadProcessId(GetForegroundWindow(), out process), self = GetCurrentThreadId();
        AttachThreadInput(self, front, true);
        SetForegroundWindow(window);
        AttachThreadInput(self, front, false);
    }
    public static string FrontProcess()
    {
        uint process;
        GetWindowThreadProcessId(GetForegroundWindow(), out process);
        return System.Diagnostics.Process.GetProcessById((int)process).ProcessName;
    }
    public static void Keys(params byte[] keys)
    {
        foreach (byte key in keys) keybd_event(key, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, 2, UIntPtr.Zero);
    }
    public static bool Type(string text)
    {
        var inputs = new KeyInput[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[2 * i] = new KeyInput { Type = 1, Key = new Key { Scan = text[i], Flags = 4 } };
            inputs[2 * i + 1] = new KeyInput { Type = 1, Key = new Key { Scan = text[i], Flags = 6 } };
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(KeyInput))) == inputs.Length;
    }
}
'@
$element = [Windows.Automation.AutomationElement]
$controls = [Windows.Automation.ControlType]
$terminalWindow = New-Object Windows.Automation.PropertyCondition($element::ClassNameProperty, 'CASCADIA_HOSTING_WINDOW_CLASS')
$tabItem = New-Object Windows.Automation.PropertyCondition($element::ControlTypeProperty, $controls::TabItem)
$listItem = New-Object Windows.Automation.PropertyCondition($element::ControlTypeProperty, $controls::ListItem)
$selection = [Windows.Automation.SelectionItemPattern]::Pattern
function Answer($text) { [Console]::Out.WriteLine($text); exit 0 }
function TerminalWindows { $element::RootElement.FindAll('Children', $terminalWindow) }
function FindTab
{
    $marker = 'joint-' + [guid]::NewGuid().ToString('N')
    for ($try = 0; $try -lt 40; $try++)
    {
        [Tab]::SetConsoleTitleW($marker) | Out-Null
        Start-Sleep -Milliseconds 40
        foreach ($window in TerminalWindows)
        {
            $tabs = $window.FindAll('Descendants', $tabItem)
            for ($i = 0; $i -lt $tabs.Count; $i++)
            {
                if ($tabs[$i].Current.Name -ceq $marker)
                {
                    [Tab]::SetConsoleTitleW($title) | Out-Null
                    return @{ Window = $window; Index = $i; Count = $tabs.Count }
                }
            }
        }
    }
    [Tab]::SetConsoleTitleW($title) | Out-Null
    Answer 'refused its tab was not found by its title (a tab that keeps its own title cannot be found)'
}
function SelectedTab($window)
{
    $tabs = $window.FindAll('Descendants', $tabItem)
    for ($i = 0; $i -lt $tabs.Count; $i++) { if ($tabs[$i].GetCurrentPattern($selection).Current.IsSelected) { return $i } }
    return -1
}
function FocusTab($index)
{
    $start = New-Object Diagnostics.ProcessStartInfo 'wt.exe'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Arguments = "-w 0 focus-tab -t $index"
    [Diagnostics.Process]::Start($start).WaitForExit()
}
[Tab]::FreeConsole() | Out-Null
if (-not [Tab]::AttachConsole($shell)) { Answer "refused could not attach to the tab's console (process $shell)" }
$buffer = New-Object Text.StringBuilder 1024
[Tab]::GetConsoleTitleW($buffer, 1024) | Out-Null
$title = $buffer.ToString()
$tab = FindTab
$handle = [IntPtr]$tab.Window.Current.NativeWindowHandle
if ($action -eq 'find') { Answer "window $handle $($tab.Count)" }
$selected = SelectedTab $tab.Window
# A browser that has just opened can still take the focus back, so the terminal has to stay in front.
$held = 0
for ($try = 0; $try -lt 100 -and $held -lt 6; $try++)
{
    if ([Tab]::GetForegroundWindow() -eq $handle) { $held++ }
    else
    {
        $held = 0
        [Tab]::Front($handle)
    }
    Start-Sleep -Milliseconds 50
}
if ($held -lt 6) { Answer "refused the terminal did not keep the focus ($([Tab]::FrontProcess()) holds it)" }
FocusTab $tab.Index
Start-Sleep -Milliseconds 250
if ((SelectedTab $tab.Window) -ne $tab.Index) { FocusTab $selected; Answer 'refused its tab could not be selected' }
if ([Tab]::GetForegroundWindow() -ne $handle) { FocusTab $selected; Answer "refused the terminal lost the focus ($([Tab]::FrontProcess()) holds it)" }
[Tab]::Keys(0x11, 0x10, 0x50)
$open = $false
for ($try = 0; $try -lt 20 -and -not $open; $try++)
{
    Start-Sleep -Milliseconds 50
    $open = $element::FocusedElement.Current.ControlType -eq $controls::Edit
}
# Without the palette the text would land in the harness's prompt.
if (-not $open) { FocusTab $selected; Answer 'refused the command palette did not open on Ctrl+Shift+P' }
function InPalette { [Tab]::GetForegroundWindow() -eq $handle -and $element::FocusedElement.Current.ControlType -eq $controls::Edit }
function Offered
{
    foreach ($item in $tab.Window.FindAll('Descendants', $listItem))
    {
        if ($item.GetCurrentPattern($selection).Current.IsSelected) { return $item.Current.Name }
    }
    return $null
}
$command = 'Move tab to a new window'
$match = $null
# The list filters after the typing, and a palette still loading can lose it; the second go replaces what is there.
for ($attempt = 0; $attempt -lt 2 -and $match -cne $command -and (InPalette); $attempt++)
{
    if ($attempt -gt 0) { [Tab]::Keys(0x11, 0x41) }
    if (-not [Tab]::Type($command)) { break }
    for ($try = 0; $try -lt 30 -and $match -cne $command; $try++)
    {
        Start-Sleep -Milliseconds 50
        $match = Offered
    }
}
if ($match -cne $command -or -not (InPalette))
{
    # Out of the palette, Esc would reach the harness and interrupt it.
    $palette = InPalette
    if ($palette)
    {
        $typed = $element::FocusedElement.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
        [Tab]::Keys(0x1B)
        Start-Sleep -Milliseconds 300
    }
    FocusTab $selected
    Answer "refused the command palette offered '$match' for '$typed' (focus in the palette: $palette, front process $([Tab]::FrontProcess()); a terminal in another language names the action differently)"
}
$before = @(TerminalWindows | ForEach-Object { $_.Current.NativeWindowHandle })
[Tab]::Keys(0x0D)
for ($try = 0; $try -lt 60; $try++)
{
    Start-Sleep -Milliseconds 100
    foreach ($window in TerminalWindows)
    {
        if ($before -notcontains $window.Current.NativeWindowHandle)
        {
            $moved = FindTab
            if ($moved.Window.Current.NativeWindowHandle -ne $window.Current.NativeWindowHandle) { Answer 'refused the new window does not hold this tab' }
            Answer "moved $($window.Current.NativeWindowHandle)"
        }
    }
}
Answer 'refused no new window appeared'
""";

    /// <summary>
    /// A launcher started from inside a Claude session (its ! prefix or Bash tool) inherits that session's markers, and
    /// the terminal's Claude would take itself for a child session (it then stops saving its transcript). Only the
    /// per-session markers go; settings a user exports (CLAUDE_CODE_USE_BEDROCK and the like) stay.
    /// </summary>
    static void ForgetParentClaudeSession()
    {
        foreach (var name in new[] { "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH",
                     "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ATTENDED",
                     "CLAUDE_CODE_SESSION_ID", "CLAUDE_PID", "CLAUDE_EFFORT" })
            Environment.SetEnvironmentVariable(name, null);
    }

    static bool IsWayland() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs a tool to completion and returns its standard output (plus its errors when it fails); exitCode is -1 when it could not start.</summary>
    static string RunTool(string file, IEnumerable<string> args, out int exitCode)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;
            return exitCode == 0 ? output : output + errors.Result;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            exitCode = -1;
            return e.Message;
        }
    }

    /// <summary>
    /// Starts a GUI program on macOS/Linux so it outlives this launcher and holds none of its streams (an inherited stdout
    /// keeps a caller's pipe open): sh redirects them to /dev/null and execs the program, so the pid stays the program's.
    /// </summary>
    static Process StartDetached(string file, IEnumerable<string> args, string logFile)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("log=$1; shift; exec nohup \"$0\" \"$@\" </dev/null >\"$log\" 2>&1");
        start.ArgumentList.Add(file);
        start.ArgumentList.Add(logFile);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException($"could not start {file}");
    }

    /// <summary>The main screen's visible frame (below the menu bar, beside the Dock) in points, top-left origin.</summary>
    static string? MacWorkArea(out Rect work)
    {
        work = default;
        const string Script = "ObjC.import('AppKit'); var s = $.NSScreen.mainScreen; var f = s.frame; var v = s.visibleFrame; " +
                              "[v.origin.x, f.size.height - v.origin.y - v.size.height, v.size.width, v.size.height].map(Math.round).join(',')";
        string output = RunTool("osascript", new[] { "-l", "JavaScript", "-e", Script }, out int exit);
        var m = Regex.Match(output, @"^(-?\d+),(-?\d+),(\d+),(\d+)\s*$");
        if (exit != 0 || !m.Success) return $"Error: could not read the screen's visible area through osascript: {output.Trim()}";
        int x = int.Parse(m.Groups[1].Value), y = int.Parse(m.Groups[2].Value);
        work = new Rect { Left = x, Top = y, Right = x + int.Parse(m.Groups[3].Value), Bottom = y + int.Parse(m.Groups[4].Value) };
        return null;
    }

    /// <summary>
    /// The X11 window manager's view through libX11, which every X11 desktop has (the EWMH requests wmctrl makes): the
    /// work area, the managed windows with their pids, and moving, raising and closing one. Window placement on Linux
    /// needs nothing installed besides the desktop.
    /// </summary>
    static class X11
    {
        private const string Lib = "libX11.so.6";
        private const int ClientMessage = 33;
        private const long SubstructureNotifyMask = 1L << 19, SubstructureRedirectMask = 1L << 20;
        private const long SourcePager = 2;   // EWMH source indication: a pager-like tool, which window managers obey
        private static IntPtr display;

        public sealed record Window(IntPtr Id, int Pid);

        static IntPtr Display
        {
            get
            {
                if (display != IntPtr.Zero) return display;
                display = XOpenDisplay(null);
                if (display == IntPtr.Zero) throw new InvalidOperationException($"cannot open the X11 display '{Environment.GetEnvironmentVariable("DISPLAY")}'");
                return display;
            }
        }

        static IntPtr Root => XDefaultRootWindow(Display);
        static IntPtr Atom(string name) => XInternAtom(Display, name, false);

        /// <summary>A window property of 32-bit items (Xlib hands them over as C longs); empty when it is not set.</summary>
        static long[] Property(IntPtr window, string name)
        {
            if (XGetWindowProperty(Display, window, Atom(name), 0, 4096, false, IntPtr.Zero, out _, out int format, out ulong count,
                    out _, out IntPtr data) != 0 || data == IntPtr.Zero)
                return Array.Empty<long>();
            try
            {
                var items = new long[format == 32 ? (int)count : 0];
                for (int i = 0; i < items.Length; i++) items[i] = Marshal.ReadInt64(data, i * 8);
                return items;
            }
            finally { XFree(data); }
        }

        /// <summary>The current desktop's work area (_NET_WORKAREA), in pixels; on several monitors it spans them all.</summary>
        public static string? WorkArea(out Rect work)
        {
            work = default;
            long[] current = Property(Root, "_NET_CURRENT_DESKTOP"), areas = Property(Root, "_NET_WORKAREA");
            int desktop = current.Length > 0 ? (int)current[0] : 0;
            if (areas.Length < desktop * 4 + 4) return "Error: the window manager publishes no work area (_NET_WORKAREA); use an EWMH desktop (GNOME, KDE, Xfce, ...).";
            int x = (int)areas[desktop * 4], y = (int)areas[desktop * 4 + 1];
            work = new Rect { Left = x, Top = y, Right = x + (int)areas[desktop * 4 + 2], Bottom = y + (int)areas[desktop * 4 + 3] };
            return null;
        }

        /// <summary>The managed top-level windows (_NET_CLIENT_LIST) with their _NET_WM_PID.</summary>
        public static List<Window> Windows()
        {
            var windows = new List<Window>();
            foreach (long id in Property(Root, "_NET_CLIENT_LIST"))
            {
                long[] pid = Property(new IntPtr(id), "_NET_WM_PID");
                windows.Add(new Window(new IntPtr(id), pid.Length > 0 ? (int)pid[0] : 0));
            }
            return windows;
        }

        /// <summary>The window with the focus (_NET_ACTIVE_WINDOW) and its _NET_WM_PID; null when none has it.</summary>
        public static Window? ActiveWindow()
        {
            long[] active = Property(Root, "_NET_ACTIVE_WINDOW");
            if (active.Length == 0 || active[0] == 0) return null;
            long[] pid = Property(new IntPtr(active[0]), "_NET_WM_PID");
            return new Window(new IntPtr(active[0]), pid.Length > 0 ? (int)pid[0] : 0);
        }

        /// <summary>The first window that matches, or null when none appears within WindowWait.</summary>
        public static IntPtr? WaitForWindow(Func<Window, bool> match)
        {
            var deadline = DateTime.UtcNow + WindowWait;
            while (DateTime.UtcNow < deadline)
            {
                foreach (var window in Windows())
                {
                    if (match(window)) return window.Id;
                }
                Thread.Sleep(200);
            }
            return null;
        }

        /// <summary>Un-maximizes the window and fits its frame (client area plus decorations) to the rectangle.</summary>
        public static void Place(IntPtr window, Rect area)
        {
            Send(window, "_NET_WM_STATE", 0 /* remove */, (long)Atom("_NET_WM_STATE_MAXIMIZED_VERT"), (long)Atom("_NET_WM_STATE_MAXIMIZED_HORZ"), SourcePager);
            // x and y place the frame's corner while width and height size the client, so the decorations
            // (_NET_FRAME_EXTENTS: left, right, top, bottom; none on windows that draw their own) come off the size
            long[] frame = Property(window, "_NET_FRAME_EXTENTS");
            int horizontal = frame.Length == 4 ? (int)(frame[0] + frame[1]) : 0, vertical = frame.Length == 4 ? (int)(frame[2] + frame[3]) : 0;
            // gravity 0 (the window's own), x, y, width and height given, from a pager
            Send(window, "_NET_MOVERESIZE_WINDOW", (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | (SourcePager << 12),
                area.Left, area.Top, area.Right - area.Left - horizontal, area.Bottom - area.Top - vertical);
        }

        public static void Activate(IntPtr window) => Send(window, "_NET_ACTIVE_WINDOW", SourcePager);

        /// <summary>An EWMH request: a client message about the window, sent to the root window where the window manager listens.</summary>
        static void Send(IntPtr window, string type, long l0 = 0, long l1 = 0, long l2 = 0, long l3 = 0, long l4 = 0)
        {
            var message = new XClientMessageEvent
            {
                type = ClientMessage, window = window, message_type = Atom(type), format = 32, l0 = l0, l1 = l1, l2 = l2, l3 = l3, l4 = l4,
            };
            XSendEvent(Display, Root, false, SubstructureRedirectMask | SubstructureNotifyMask, ref message);
            XFlush(Display);
        }

        /// <summary>XClientMessageEvent inside the 192-byte XEvent union (LP64: x86-64 and arm64).</summary>
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        struct XClientMessageEvent
        {
            [FieldOffset(0)] public int type;
            [FieldOffset(8)] public ulong serial;
            [FieldOffset(16)] public int send_event;
            [FieldOffset(24)] public IntPtr display;
            [FieldOffset(32)] public IntPtr window;
            [FieldOffset(40)] public IntPtr message_type;
            [FieldOffset(48)] public int format;
            [FieldOffset(56)] public long l0;
            [FieldOffset(64)] public long l1;
            [FieldOffset(72)] public long l2;
            [FieldOffset(80)] public long l3;
            [FieldOffset(88)] public long l4;
        }

        [DllImport(Lib)] static extern IntPtr XOpenDisplay(string? name);
        [DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(Lib)] static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
        [DllImport(Lib)] static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length, bool delete,
            IntPtr requestedType, out IntPtr actualType, out int actualFormat, out ulong items, out ulong bytesAfter, out IntPtr data);
        [DllImport(Lib)] static extern int XFree(IntPtr data);
        [DllImport(Lib)] static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, long mask, ref XClientMessageEvent message);
        [DllImport(Lib)] static extern int XFlush(IntPtr display);
    }

    /// <summary>
    /// The first installed Linux terminal, running the harness on the skill prompt in the workspace and staying at a
    /// shell when the harness exits (as cmd /k does on Windows).
    /// </summary>
    static string[]? LinuxTerminalCommand(string workspaceDir, string title, string harnessCommand, string skillPrompt)
    {
        string script = $"cd {ShQuote(workspaceDir)} && {harnessCommand}{(skillPrompt.Length == 0 ? "" : " " + ShQuote(skillPrompt))}; exec bash";
        foreach (var (name, prefix) in new (string, string[])[]
                 {
                     ("gnome-terminal", new[] { "--title", title, "--" }),
                     ("konsole", new[] { "-p", "tabtitle=" + title, "-e" }),
                     ("xfce4-terminal", new[] { "--title", title, "-x" }),
                     ("xterm", new[] { "-T", title, "-e" }),
                 })
        {
            string? path = FindOnPath(name);
            if (path == null) continue;
            var command = new List<string> { path };
            command.AddRange(prefix);
            command.AddRange(new[] { "bash", "-c", script });
            return command.ToArray();
        }
        return null;
    }

    static string ShQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>
    /// The command that runs this launcher with the given arguments, as the help, the doctor and the embedded skills
    /// name it: `dotnet run &lt;this file from the workspace&gt; -- &lt;args&gt;` from source, else the published binary by its path.
    /// </summary>
    static string LaunchCommand(string args = "")
    {
        string binary = Environment.ProcessPath!;
        string launcher = FromSource ? (LauncherSourcePath.Contains(' ') ? $"dotnet run \"{LauncherSourcePath}\"" : $"dotnet run {LauncherSourcePath}") : binary.Contains(' ') ? $"\"{binary}\"" : binary;
        return args.Length == 0 ? launcher : FromSource ? $"{launcher} -- {args}" : $"{launcher} {args}";
    }

    // The embedded skills write {{launch}}, {{launch --doctor}} and {{launch --harness claude}} for the command that runs the launcher.
    static string LaunchText(string text) => text.Replace("{{launch --doctor}}", LaunchCommand("--doctor")).Replace("{{launch --harness claude}}", LaunchCommand("--harness claude")).Replace("{{launch}}", LaunchCommand());

    /// <summary>
    /// Opens a Terminal.app window running the harness on the skill prompt in the workspace, titled and placed through
    /// AppleScript (Terminal's bounds are points, as the work area). macOS asks once to let the calling terminal
    /// control Terminal.
    /// </summary>
    static string? OpenMacTerminal(string workspaceDir, string title, string harnessCommand, string skillPrompt, Rect area)
    {
        static string AppleScriptString(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string command = $"cd {ShQuote(workspaceDir)} && {harnessCommand}{(skillPrompt.Length == 0 ? "" : " " + ShQuote(skillPrompt))}";
        string script = string.Join("\n",
            "tell application \"Terminal\"",
            "  activate",
            $"  set t to do script {AppleScriptString(command)}",
            $"  set custom title of t to {AppleScriptString(title)}",
            // the window holding the new tab, not the front one: the terminal the launcher runs in can be in front
            "  set tt to tty of t",
            "  set w to first window whose tty of selected tab is tt",
            $"  set bounds of w to {{{area.Left}, {area.Top}, {area.Right}, {area.Bottom}}}",
            "end tell");
        string output = RunTool("osascript", new[] { "-e", script }, out int exit);
        return exit == 0 ? null : $"Error: Terminal.app could not be opened through osascript: {output.Trim()}";
    }

    /// <summary>
    /// Opens a Windows Terminal window running the harness on the skill prompt in the workspace and places it. cmd /k
    /// keeps the tab at a prompt in the workspace when the harness exits; cmd resolves the harness's .exe or .cmd from
    /// PATH; the prompt holds no cmd or wt metacharacter (SkillPrompt).
    /// </summary>
    static string? OpenWindowsTerminal(string workspaceDir, string title, string harnessCommand, string skillPrompt, Rect area)
    {
        var terminalsBefore = new HashSet<IntPtr>(TopLevelWindows(TerminalWindowClass));
        var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
        foreach (var arg in new[] { "-w", "new", "-d", workspaceDir, "--title", title, "cmd.exe", "/k", harnessCommand })
            wt.ArgumentList.Add(arg);
        if (skillPrompt.Length > 0) wt.ArgumentList.Add(skillPrompt);
        // wt.exe hands the window to WindowsTerminal.exe and exits, so the new window is found as the one that was not
        // there before.
        Process.Start(wt)?.Dispose();
        if (WaitForWindow(TerminalWindowClass, hwnd => !terminalsBefore.Contains(hwnd)) is not IntPtr window)
            return NeverAppeared("the Windows Terminal window");
        Place(window, area);
        SetForegroundWindow(window);
        return null;
    }

    /// <summary>
    /// Starts the Linux terminal command and, on X11 (area given), places its window: the new window that is not the
    /// browser's.
    /// </summary>
    static string? OpenLinuxTerminal(string[] command, int browserPid, Rect? area)
    {
        var windowsBefore = area != null ? new HashSet<IntPtr>(X11.Windows().ConvertAll(w => w.Id)) : new HashSet<IntPtr>();
        StartDetached(command[0], command[1..], "/dev/null").Dispose();
        if (area is Rect placed)
        {
            // gnome-terminal opens its windows from a D-Bus server, on the display that server started with: one
            // started outside this desktop (e.g. by an ssh login's session bus) opens no window here.
            if (X11.WaitForWindow(w => !windowsBefore.Contains(w.Id) && w.Pid != browserPid) is not IntPtr window)
                return NeverAppeared($"the terminal window ({Path.GetFileName(command[0])})") +
                       " Run the launcher from a terminal on this desktop, whose session bus the terminal's server shares.";
            X11.Place(window, placed);
            X11.Activate(window);
        }
        return null;
    }

    /// <summary>Ends a launch that failed after the browser opened: the terminal is opened last, so only the browser is left.</summary>
    static void CloseSession(Process browser)
    {
        lock (browser)   // the console closing and the harness exiting arrive together
        {
            if (browser.HasExited) return;
            browser.Kill(true);
            browser.WaitForExit();
        }
    }

    /// <summary>
    /// Checks everything the launcher needs and prints each as [ok], [missing] (the session cannot open) or [optional]
    /// with the fix for each. The harness checked is --harness, else DefaultHarness, else each supported one. Exit 0
    /// when the session can open.
    /// </summary>
    static int Doctor(string workspaceDir, string defaultsFile, string? harnessName)
    {
        bool isWindows = OperatingSystem.IsWindows(), isMac = OperatingSystem.IsMacOS(), isLinux = OperatingSystem.IsLinux();
        int missing = 0;
        void Check(bool ok, string what, string fix, bool required = true)
        {
            Console.WriteLine($"  [{(ok ? "ok" : required ? "missing" : "optional")}] {what}");
            if (!ok)
            {
                Console.WriteLine($"            fix: {fix}");
                if (required) missing++;
            }
        }
        string os = isWindows ? "Windows" : isMac ? "macOS" : isLinux ? "Linux" : RuntimeInformation.OSDescription;
        Console.WriteLine($"runJointLauncher doctor ({os}, {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}) in {workspaceDir}");
        Check(isWindows || isMac || isLinux, $"platform {os}", "use Windows, macOS or Linux");
        if (FromSource) Check(Environment.Version.Major >= 10, $".NET {Environment.Version} runs file-based apps (dotnet run file.cs)", "install the .NET 10 SDK or later: https://dotnet.microsoft.com/download");
        JointDefaults? defaults = ReadDefaults(defaultsFile, out string defaultsError);
        Check(defaults != null, File.Exists(defaultsFile) ? $"{DefaultsFileName} readable" : $"{DefaultsFileName} (created on the first session)", defaultsError.Replace("Error: ", ""));
        harnessName ??= defaults?.DefaultHarness ?? "";
        Harness? chosen = harnessName.Length == 0 ? null : FindHarness(harnessName);
        if (harnessName.Length > 0 && chosen == null) Check(false, $"harness '{harnessName}' supported", $"use {HarnessNames()}");
        foreach (var h in Harnesses)
        {
            if (chosen != null && h != chosen) continue;
            string? path = HarnessPath(h);
            Check(path != null, $"{h.Name} on PATH{(path != null ? $" ({path})" : "")}",
                $"{(isWindows ? h.InstallWindows : h.InstallUnix)}, then sign in once by running {h.Command} in a new terminal", required: chosen != null);
            if (path == null) continue;
            var absent = SkillsAbsentFrom(workspaceDir, h);
            Check(true, absent.Count == 0 ? $"{h.Name} skills in {h.SkillsDir}: {CarriedSkillNames()}"
                : $"{h.Name} skills in {h.SkillsDir}: the launcher installs {string.Join(", ", absent)} on its next run", "");
        }
        if (chosen == null) Check(Harnesses.Any(h => HarnessPath(h) != null), $"a supported harness installed ({HarnessNames()})", "install one of them (above)");
        string remote = defaults?.SkillsRemote ?? InitialDefaults.SkillsRemote;
        Check(true, $"skills folder {FindSkillsFolder()}, SkillsRemote {(remote.Length == 0 ? "(none)" : remote)}", "");
        if (defaults != null)
        {
            foreach (var (name, path) in defaults.Skills)
            {
                string file = Path.GetFullPath(path, workspaceDir);
                Check(File.Exists(file), $"skill {name} ({file})", $"fix its path in Skills of {DefaultsFileName}, or remove it", required: false);
            }
        }
        if (chosen != null)
        {
            var lost = LostSkills(workspaceDir, chosen, FindSkillsFolder());
            Check(lost.Count == 0, $"{chosen.Name} skills all at hand", LostSkillsWhy(lost, FindSkillsFolder(), defaults?.SkillsRemote ?? InitialDefaults.SkillsRemote), required: false);
        }
        if (isWindows) Check(FindOnPath("wt.exe") != null, "Windows Terminal (wt.exe; only --newterminal)", "winget install --id Microsoft.WindowsTerminal -e", required: false);
        if (isMac) Check(FindOnPath("osascript") != null, "osascript (moves or opens Terminal.app)", "part of macOS; check PATH includes /usr/bin", required: false);
        if (isLinux)
        {
            bool desktop = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            Check(desktop, "a desktop session (DISPLAY or WAYLAND_DISPLAY)", "run the launcher from a terminal on the desktop, not over ssh");
            Check(!IsWayland(), "an X11 session (the windows are placed)", "log in to an X11 session (\"Ubuntu on Xorg\", or xrdp for a remote desktop); on Wayland both windows open unplaced", required: false);
            Check(LinuxTerminalCommand(workspaceDir, "", "", "") != null, "a terminal: gnome-terminal, konsole, xfce4-terminal or xterm (only --newterminal)", "sudo apt install gnome-terminal (or xterm)", required: false);
        }
        string? browser = FindBrowser(defaults?.DefaultBrowser ?? InitialDefaults.DefaultBrowser, out string browserFound);
        Check(browser != null, browser != null ? $"browser {browser} ({browserFound})" : "a Chromium-based browser", browserFound);
        Check(!PortAnswers(CdpPort), $"port {CdpPort} free (no joint session open)", "close the open session's browser window");
        // The launch's own skill checks on the defaults, once nothing above is missing: Ready means a launch with this
        // harness and no --skill opens the session (a missing or command-line-unsafe DefaultSkill file refused it).
        if (chosen != null && defaults != null && missing == 0)
        {
            string startSkill = defaults.DefaultSkill.Length == 0 ? ChooserSkill : defaults.DefaultSkill;
            string? skillFile = SkillFile(workspaceDir, chosen, defaults, startSkill);
            string? problem = skillFile == null ? $"skill '{startSkill}' is neither embedded nor in the skills folder ({CarriedSkillNames()}) nor configured in Skills of {DefaultsFileName}"
                : !IsCarriedSkill(startSkill) && !File.Exists(skillFile)
                    ? $"skill '{startSkill}' is configured at {skillFile}, which does not exist; fix its path in Skills of {DefaultsFileName}"
                : SkillPrompt(workspaceDir, chosen, skillFile, startSkill, null, out string promptError) == null ? promptError.Replace("Error: ", "")
                : null;
            if (problem != null) Check(false, $"a launch with {chosen.Name} on the defaults", problem);
        }
        Console.WriteLine("joint-browser (optional: drives the shared browser with Playwright):");
        string? node = FindOnPath(isWindows ? "node.exe" : "node");
        string nodeVersion = node == null ? "" : RunTool(node, new[] { "--version" }, out _).Trim();
        bool nodeOk = Regex.Match(nodeVersion, @"^v(\d+)\.") is { Success: true } v && int.Parse(v.Groups[1].Value) >= 20;
        Check(nodeOk, node != null ? $"Node.js 20 or later ({node} {nodeVersion})" : "Node.js 20 or later (Playwright needs it)",
            isWindows ? "winget install --id OpenJS.NodeJS.LTS -e" : isMac ? "brew install node"
                : "NodeSource's LTS: curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash - && sudo apt install -y nodejs (the distro's own nodejs can be older)", required: false);
        string? playwright = PlaywrightPackage(workspaceDir);
        Check(playwright != null, playwright != null ? $"Playwright ({playwright})" : "Playwright (the project's own, or joint-browser's)",
            $"node <harness skills folder>/{BrowserSkill}/browser.mjs setup (playwright-core into ~/.joint-browser/node), or npm install -D playwright in the project", required: false);
        Console.WriteLine(missing == 0 ? $"Ready: {LaunchCommand()}" : $"{missing} required item(s) missing; fix them and run --doctor again.");
        return missing == 0 ? 0 : 1;
    }

    /// <summary>
    /// The defaults file's values, or the initial defaults when the file does not exist yet. A file that exists but
    /// lacks a key, holds a value of the wrong kind or a value a launch would refuse (DefaultHarness not supported,
    /// DefaultPosition or DefaultRatio not one, DefaultSkill neither embedded nor in Skills) is an error naming the key,
    /// not a silent default, so the doctor reports it before a launch would. The old key Browser is refused the same way.
    /// </summary>
    static JointDefaults? ReadDefaults(string file, out string error)
    {
        error = "";
        string Fix(string what) => $"Error: {file} {what}; fix it or delete the file to start from the initial defaults.";
        if (Directory.Exists(file))
        {
            error = Fix("is a folder, not a file");
            return null;
        }
        if (!File.Exists(file)) return InitialDefaults;
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = Fix($"cannot be read ({e.Message})");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Fix("is not a JSON object");
                return null;
            }
            if (doc.RootElement.TryGetProperty("Browser", out _))
            {
                error = Fix("has Browser: renamed to DefaultBrowser (\"\" is Google Chrome, \"auto\" the search, or a browser's path)");
                return null;
            }
            var values = new Dictionary<string, string>();
            foreach (var key in DefaultsKeys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    error = Fix($"has no string {key}");
                    return null;
                }
                values[key] = AsciiTrim(value.GetString()!);
            }
            string harness = values["DefaultHarness"].ToLowerInvariant();
            if (harness.Length > 0 && FindHarness(harness) == null)
            {
                error = Fix($"has DefaultHarness '{values["DefaultHarness"]}' (use \"\" or one of {HarnessNames()})");
                return null;
            }
            string position = values["DefaultPosition"].ToLowerInvariant();
            if (Array.IndexOf(Positions, position) < 0)
            {
                error = Fix($"has DefaultPosition '{values["DefaultPosition"]}' (use one of {string.Join(", ", Positions)}, terminal:browser)");
                return null;
            }
            if (!ParseRatio(values["DefaultRatio"], out _, out _))
            {
                error = Fix($"has DefaultRatio '{values["DefaultRatio"]}' (use <terminal>:<browser> with whole numbers 1-99, e.g. 1:4)");
                return null;
            }
            if (!doc.RootElement.TryGetProperty("Skills", out var skillsElement) || skillsElement.ValueKind != JsonValueKind.Object)
            {
                error = Fix("has no Skills object (name -> SKILL.md path, e.g. \"Skills\": { \"my-skill\": \".claude/skills/my-skill/SKILL.md\" }, or {})");
                return null;
            }
            var skills = new Dictionary<string, string>();
            foreach (var skill in skillsElement.EnumerateObject())
            {
                if (!Regex.IsMatch(skill.Name, "^[a-z0-9]+(-[a-z0-9]+)*\\z") || IsCarriedSkill(skill.Name)
                    || skill.Value.ValueKind != JsonValueKind.String || AsciiTrim(skill.Value.GetString()!).Length == 0)
                {
                    error = Fix($"has Skills entry '{skill.Name}': a skill name is lowercase letters, digits and single hyphens, not one of the tool's skills ({CarriedSkillNames()}), with its SKILL.md path as a string");
                    return null;
                }
                skills[skill.Name] = AsciiTrim(skill.Value.GetString()!);
            }
            string defaultSkill = values["DefaultSkill"];
            if (defaultSkill.Length > 0 && !IsCarriedSkill(defaultSkill) && !skills.ContainsKey(defaultSkill))
            {
                error = Fix($"has DefaultSkill '{defaultSkill}' (use \"\", one of the tool's skills ({CarriedSkillNames()}) or a name in Skills)");
                return null;
            }
            string skillsRemote = InitialDefaults.SkillsRemote;
            if (doc.RootElement.TryGetProperty("SkillsRemote", out var remoteElement))
            {
                if (remoteElement.ValueKind != JsonValueKind.String)
                {
                    error = Fix("has no string SkillsRemote");
                    return null;
                }
                skillsRemote = AsciiTrim(remoteElement.GetString()!);
            }
            if (!ValidSkillsRemote(skillsRemote))
            {
                error = Fix($"has SkillsRemote '{skillsRemote}' (use \"\" for no download, or an https URL of letters, digits and . _ ~ / -)");
                return null;
            }
            return new JointDefaults(harness, defaultSkill, position, values["DefaultRatio"], skills, values["DefaultBrowser"], skillsRemote);
        }
        catch (JsonException e)
        {
            error = Fix($"is not valid JSON ({e.Message})");
            return null;
        }
    }

    static void WriteDefaults(string file, JointDefaults defaults)
    {
        using var stream = File.Create(file);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("DefaultHarness", defaults.DefaultHarness);
        json.WriteString("DefaultSkill", defaults.DefaultSkill);
        json.WriteString("DefaultPosition", defaults.DefaultPosition);
        json.WriteString("DefaultRatio", defaults.DefaultRatio);
        json.WriteStartObject("Skills");
        foreach (var (name, path) in defaults.Skills) json.WriteString(name, path);
        json.WriteEndObject();
        json.WriteString("DefaultBrowser", defaults.DefaultBrowser);
        json.WriteString("SkillsRemote", defaults.SkillsRemote);
        json.WriteEndObject();
    }

    static bool ParseRatio(string text, out int terminalPart, out int browserPart)
    {
        terminalPart = browserPart = 0;
        var m = Regex.Match(text, @"^[ \t]*([0-9]{1,2})[ \t]*:[ \t]*([0-9]{1,2})[ \t]*\z");
        return m.Success
            && int.TryParse(m.Groups[1].Value, out terminalPart) && terminalPart > 0
            && int.TryParse(m.Groups[2].Value, out browserPart) && browserPart > 0;
    }

    // Defaults text is read by ASCII rules only: whitespace is space, tab, CR and LF.
    static string AsciiTrim(string text) => text.Trim(' ', '\t', '\r', '\n');

    /// <summary>
    /// Splits the work area between the terminal and the browser: side by side for left:right / right:left, stacked
    /// for top:bottom / bottom:top, the terminal taking terminalPart/(terminalPart+browserPart) of the split axis.
    /// </summary>
    static void Layout(Rect work, string position, int terminalPart, int browserPart, out Rect terminal, out Rect browser)
    {
        bool sideBySide = position is "left:right" or "right:left";
        bool terminalFirst = position is "left:right" or "top:bottom";
        int start = sideBySide ? work.Left : work.Top;
        int end = sideBySide ? work.Right : work.Bottom;
        int terminalSize = (end - start) * terminalPart / (terminalPart + browserPart);
        int split = terminalFirst ? start + terminalSize : end - terminalSize;
        (int tStart, int tEnd, int bStart, int bEnd) = terminalFirst ? (start, split, split, end) : (split, end, start, split);
        terminal = sideBySide
            ? new Rect { Left = tStart, Right = tEnd, Top = work.Top, Bottom = work.Bottom }
            : new Rect { Left = work.Left, Right = work.Right, Top = tStart, Bottom = tEnd };
        browser = sideBySide
            ? new Rect { Left = bStart, Right = bEnd, Top = work.Top, Bottom = work.Bottom }
            : new Rect { Left = work.Left, Right = work.Right, Top = bStart, Bottom = bEnd };
    }

    static Harness? FindHarness(string name) => Array.Find(Harnesses, h => h.Name == name);

    static string HarnessNames() => string.Join(", ", Harnesses.Select(h => h.Name));

    /// <summary>The harness's executable on PATH (on Windows its .exe, or the .cmd an npm install adds), or null.</summary>
    static string? HarnessPath(Harness harness) => OperatingSystem.IsWindows()
        ? FindOnPath(harness.Command + ".exe") ?? FindOnPath(harness.Command + ".cmd")
        : FindOnPath(harness.Command);

    static string CarriedSkillNames() => string.Join(", ", EmbeddedSkills.Select(s => s.Name).Concat(FolderSkills.Select(s => s.Name)));

    /// <summary>Where the harness finds the named skill in the launch folder: &lt;SkillsDir&gt;/&lt;name&gt;/SKILL.md.</summary>
    static string HarnessSkillFile(string workspaceDir, Harness harness, string name) =>
        Path.GetFullPath(Path.Combine(workspaceDir, harness.SkillsDir, name, "SKILL.md"));

    /// <summary>Whether every file of the embedded skill is in the harness's skills folder.</summary>
    static bool SkillInstalled(string workspaceDir, Harness harness, string name, IEnumerable<string> files)
    {
        string dir = Path.GetDirectoryName(HarnessSkillFile(workspaceDir, harness, name))!;
        return files.All(f => File.Exists(Path.Combine(dir, f)));
    }

    /// <summary>
    /// The Playwright package joint-browser's helper would load: the project's own (playwright or playwright-core in
    /// node_modules here), else the one its setup installs into ~/.joint-browser/node; null when neither is there.
    /// </summary>
    static string? PlaywrightPackage(string workspaceDir)
    {
        string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".joint-browser", "node");
        foreach (string dir in new[] { Path.Combine(workspaceDir, "node_modules", "playwright"), Path.Combine(workspaceDir, "node_modules", "playwright-core"),
                     Path.Combine(shared, "node_modules", "playwright-core") })
        {
            if (File.Exists(Path.Combine(dir, "package.json"))) return dir;
        }
        return null;
    }

    static bool IsCarriedSkill(string name) => Array.Exists(EmbeddedSkills, s => s.Name == name) || Array.Exists(FolderSkills, s => s.Name == name);

    /// <summary>The folder the tool runs from: its source's when run from source, else the binary's.</summary>
    static string ToolDir() => Path.GetDirectoryName(AppContext.GetData("EntryPointFilePath") as string ?? Environment.ProcessPath!)!;

    /// <summary>
    /// The nearest folder named skills that holds the browser skill, from the tool's own folder upward (the repository's
    /// sits above every tool, csharp and lean4 alike); with none, the one beside the tool, where the install skill puts
    /// what it downloads.
    /// </summary>
    static string FindSkillsFolder()
    {
        string tool = ToolDir();
        for (string? dir = tool; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, SkillsFolderName, BrowserSkill, "SKILL.md"))) return Path.Combine(dir, SkillsFolderName);
        return Path.Combine(tool, SkillsFolderName);
    }

    /// <summary>Whether the skills folder holds every file of the skill.</summary>
    static bool FolderHolds(string skillsFolder, FolderSkill skill) => skill.Files.All(f => File.Exists(Path.Combine(skillsFolder, skill.Name, f)));

    /// <summary>The skills, embedded or from the folder, whose files the harness's skills folder lacks.</summary>
    static List<string> SkillsAbsentFrom(string workspaceDir, Harness harness) =>
        EmbeddedSkills.Where(s => !SkillInstalled(workspaceDir, harness, s.Name, s.Files().Select(f => f.Name))).Select(s => s.Name)
            .Concat(FolderSkills.Where(s => !SkillInstalled(workspaceDir, harness, s.Name, s.Files)).Select(s => s.Name)).ToList();

    /// <summary>The folder skills neither the harness's skills folder nor the skills folder holds: no launch can install them.</summary>
    static List<string> LostSkills(string workspaceDir, Harness harness, string skillsFolder) =>
        FolderSkills.Where(s => !SkillInstalled(workspaceDir, harness, s.Name, s.Files) && !FolderHolds(skillsFolder, s)).Select(s => s.Name).ToList();

    /// <summary>A skills remote: empty (no download), or an https URL plain enough for the install skill to put on a command line.</summary>
    static bool ValidSkillsRemote(string text) => text.Length == 0 || Regex.IsMatch(text, @"^https://[A-Za-z0-9._~/-]+\z");

    /// <summary>What is lost and where it comes from, for the doctor and a launch.</summary>
    static string LostSkillsWhy(IReadOnlyList<string> lost, string skillsFolder, string remote) =>
        $"{string.Join(", ", lost)} not in {skillsFolder}: " + (remote.Length == 0
            ? "copy the skills folder that ships with the tool there"
            : $"{InstallSkill} downloads them from {remote} (SkillsRemote in {DefaultsFileName})");

    /// <summary>
    /// Writes each skill file the harness's skills folder lacks, an embedded skill's from the tool and a folder skill's from
    /// skillsFolder; a copy already there is the user's and is kept. Null, or why a file could not be written.
    /// </summary>
    static string? InstallSkills(string workspaceDir, Harness harness, string skillsFolder)
    {
        string? Write(string skill, string name, Func<string> text)
        {
            string dir = Path.GetDirectoryName(HarnessSkillFile(workspaceDir, harness, skill))!;
            string file = Path.Combine(dir, name);
            if (File.Exists(file)) return null;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, LaunchText(text()).ReplaceLineEndings("\n"), new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"Error: could not write the {skill} skill's {name} to {file} ({e.Message}); run the launcher from a folder you can write to.";
            }
            Console.WriteLine($"[joint-launcher] installed the {skill} skill's {name} for {harness.Name}: {file}");
            return null;
        }
        foreach (var skill in EmbeddedSkills)
            foreach (var (name, text) in skill.Files())
                if (Write(skill.Name, name, () => text + "\n") is string error) return error;
        foreach (var skill in FolderSkills)
        {
            if (!FolderHolds(skillsFolder, skill) && !SkillInstalled(workspaceDir, harness, skill.Name, skill.Files))
                Console.WriteLine($"[joint-launcher] the {skill.Name} skill is not in {skillsFolder}; --doctor says how to get it.");
            foreach (string name in skill.Files)
            {
                string source = Path.Combine(skillsFolder, skill.Name, name);
                if (File.Exists(source) && Write(skill.Name, name, () => File.ReadAllText(source)) is string error) return error;
            }
        }
        return null;
    }

    /// <summary>
    /// The named skill's SKILL.md for this harness: an embedded skill's in the harness's skills folder, a configured
    /// one's at its path in Skills; null when it is neither.
    /// </summary>
    static string? SkillFile(string workspaceDir, Harness harness, JointDefaults defaults, string name) =>
        IsCarriedSkill(name) ? HarnessSkillFile(workspaceDir, harness, name)
        : defaults.Skills.TryGetValue(name, out var path) ? Path.GetFullPath(path, workspaceDir) : null;

    /// <summary>
    /// The harness's first message: the skill by the harness's own command (claude /name, codex $name, pi /skill:name)
    /// when it sits in the harness's skills folder, else an instruction to read its SKILL.md and follow it; the
    /// arguments follow either way. Null when the text holds a character the terminal's command line (cmd, wt, sh)
    /// treats specially.
    /// </summary>
    static string? SkillPrompt(string workspaceDir, Harness harness, string skillFile, string name, string? skillArgs, out string error)
    {
        error = "";
        bool native = string.Equals(skillFile, HarnessSkillFile(workspaceDir, harness, name),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        string prompt = native
            ? harness.SkillPrefix + name + (string.IsNullOrEmpty(skillArgs) ? "" : " " + skillArgs)
            : $"Read the skill {skillFile} and follow it" + (string.IsNullOrEmpty(skillArgs) ? "." : $", with the arguments: {skillArgs}");
        foreach (char c in prompt)
        {
            if (c is '"' or '\'' or '`' or ';' or '&' or '|' or '<' or '>' or '^' or '%' or '!' or '\r' or '\n')
            {
                error = $"Error: {harness.Name}'s first message would hold '{c}', which the terminal's command line treats specially: {prompt}";
                return null;
            }
        }
        return prompt;
    }

    /// <summary>
    /// The session cannot open yet: runs the harness in this console on joint-launcher-install, told what is missing,
    /// and returns its exit code. The next launch opens the session once nothing is missing.
    /// </summary>
    static int Bootstrap(string workspaceDir, Harness harness, string harnessPath, List<string> missing) =>
        RunHarnessHere(workspaceDir, harness, harnessPath, $"{harness.SkillPrefix}{InstallSkill} missing {string.Join(" ", missing)}", missing);

    /// <summary>Runs the harness in this console, on the prompt, and returns its exit code.</summary>
    static int RunHarnessHere(string workspaceDir, Harness harness, string harnessPath, string prompt, List<string> missing)
    {
        if (missing.Count > 0)
            Console.WriteLine($"[joint-launcher] the session cannot open until that is set up: {harness.Name} starts here on {InstallSkill} to install it. " +
                              "When it is done, exit it and run the launcher again.");
        // Ctrl+C belongs to the harness (it interrupts its turn); the launcher waits for it to exit.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var process = StartHarnessHere(workspaceDir, harness, harnessPath, prompt);
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Starts the harness on the prompt in this console, a session of its own.</summary>
    static Process StartHarnessHere(string workspaceDir, Harness harness, string harnessPath, string prompt)
    {
        ForgetParentClaudeSession();
        // cmd resolves the harness's .exe or .cmd on Windows; elsewhere the harness runs directly.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : harnessPath) { UseShellExecute = false, WorkingDirectory = workspaceDir };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(harness.Command);
        }
        if (prompt.Length > 0) start.ArgumentList.Add(prompt);
        return Process.Start(start) ?? throw new InvalidOperationException($"could not start {harnessPath}");
    }

    static bool SafeSkillArgs(string text)
    {
        foreach (char c in text)
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or ',' or '.' or '/' or '=' or ':')) return false;
        return true;
    }

    static string? FindOnPath(string file)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim('"'), file);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static void PrintUsage()
    {
        Console.WriteLine($"Usage: {LaunchCommand("[--harness claude|codex|pi] [--skill <name>] [--skillargs \"<skill arguments>\"]")}");
        Console.WriteLine("           [--position <terminal:browser>] [--ratio <terminal>:<browser>]");
        Console.WriteLine($"       {LaunchCommand("--doctor [--harness claude|codex|pi]")}");
        Console.WriteLine("""

Opens a joint session: the terminal it runs in, with its AI harness, and a shared Chromium-based browser (DevTools
protocol on 127.0.0.1:9222) splitting the primary monitor, and never a second terminal. Inside a harness it opens the
browser beside that harness and exits; from a plain terminal the harness starts right there. Copy this file and the skills folder into any folder (a repository or not)
and run it from there, on the desktop (not over ssh): runJointLauncher.cs with the .NET 10 SDK or later, or the
single-file binary runJointLauncher-<os>-<arch> (e.g. runJointLauncher-win-x64.exe), which needs no .NET. Nothing else
is needed; runJointLauncher.json is created there on the first session.

  --harness <name>       what the terminal starts, for this run only (never saved): claude, codex or pi; required
                         while DefaultHarness in runJointLauncher.json is empty. It must be installed: the launcher
                         prints its install command when it is not.
  --skill <name>         the skill the harness starts on (default: DefaultSkill; when that is empty too, the harness
                         starts on joint-launcher, which asks what to do). joint-launcher and joint-browser
                         (drives the shared browser with Playwright: Node.js and a standard Playwright) come from
                         the skills folder beside the launcher or above it, joint-launcher-install and joint-launcher-doctor are embedded;
                         all are written to the harness's skills folder
                         (.claude/skills, .agents/skills, .pi/skills) when missing, never over a copy already there;
                         other skills are named with their SKILL.md path in Skills of runJointLauncher.json.
  --skillargs "<args>"   the skill's own arguments, e.g. --skill my-review --skillargs "login page".
  --position <t:b>       where the terminal and the browser go: left:right, right:left (side by side) or
                         top:bottom, bottom:top (stacked); the terminal is the first named side.
  --ratio <t>:<b>        the terminal's share against the browser's along the split, e.g. 1:2, 1:1. Side by side the
                         terminal takes at least 1:2: a narrower ratio becomes 1:2. Stacked, any ratio holds, e.g. 1:4.
  --newterminal          open a terminal of its own running the harness. Without it no second terminal opens: inside a
                         harness (claude, codex, pi), or started by any other program (input that is no console), that
                         harness is the session's and the browser opens beside its terminal (--position, --ratio; the
                         whole work area when that window cannot be moved), so --skill and --skillargs are refused and
                         --harness is not needed there (from a program it only overrides whose skills folder gets the skills; inside a harness,
                         another one named starts in a terminal of its own and the caller's stays where it is). From a plain terminal the harness
                         starts right there, and its exit ends the session.
  --doctor               check every prerequisite and print how to fix what is missing; opens no window.

When something the session needs is missing (a terminal, a browser) but the harness is installed, the launcher starts
the harness in its own console on joint-launcher-install, told what is missing; run the launcher again once it is
installed. Inside a harness nothing starts: the launcher prints what is missing with its fix and exits 1.

Defaults live in runJointLauncher.json in the folder the launcher runs from (DefaultHarness, DefaultSkill,
DefaultPosition, DefaultRatio, Skills, DefaultBrowser; initially "", "", left:right, 1:2, {}, ""). A --skill, --position or
--ratio given here becomes the new default once the session opens; the rest change only by editing the file. Skills
maps a skill's name to its SKILL.md path (relative to the launch folder unless absolute); DefaultBrowser "" is Google
Chrome on every OS (google-chrome on Linux) and nothing else, "auto" takes the newest Playwright Chromium, else Google
Chrome, else Microsoft Edge or Chromium, or name a browser's path.

Platforms: Windows (Windows Terminal), macOS (Terminal.app; allow it to be controlled when macOS asks) and Linux
(gnome-terminal, konsole, xfce4-terminal or xterm; the windows are placed on X11, on Wayland both open unplaced).

Writes <local app data>/joint-launcher/session.json (cdp, browserPid, harness, skill, skipHarness) for a skill to
attach to. The session ends when you close its windows.
""");
    }

    /// <summary>How a harness start reads in a message: on the skill's prompt, or with none.</summary>
    static string OnSkill(string skillPrompt) => skillPrompt.Length == 0 ? "with no skill" : "on: " + skillPrompt;

    static string StatusPage(string harnessName, string skillPrompt, bool skipHarness)
    {
        string shared = skipHarness ? "This window is shared with the AI session that opened it" : "This window is shared with the AI session in the terminal";
        string starting = skipHarness ? $"    <li>Browser-only joint session: attach from your harness with <code>{BrowserSkill}</code> (CDP <code>http://127.0.0.1:{CdpPort}</code>).</li>\n"
            : $"    <li>{WebUtility.HtmlEncode(harnessName)} is starting in the terminal{(skillPrompt.Length == 0 ? "" : " on <code>" + WebUtility.HtmlEncode(skillPrompt) + "</code>")}.</li>\n";
        return """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Joint session</title>
<style>
  :root { color-scheme: light dark; --fg: #1d1d1f; --bg: #f7f7f5; --muted: #6b6b6b; --code: #ebebe8; }
  @media (prefers-color-scheme: dark) { :root { --fg: #ececec; --bg: #1b1b1d; --muted: #9a9a9a; --code: #2a2a2d; } }
  body { margin: 0; padding: 48px; font: 16px/1.55 system-ui, sans-serif; color: var(--fg); background: var(--bg); }
  h1 { font-size: 22px; margin: 0 0 4px; } p.sub { color: var(--muted); margin: 0 0 28px; }
  ol { padding-left: 22px; } li { margin: 10px 0; }
  code { background: var(--code); padding: 2px 6px; border-radius: 4px; font-size: 14px; }
</style></head>
<body>
  <h1>Joint session</h1>
  <p class="sub">
""" + shared + """
 (DevTools protocol on 127.0.0.1:9222).</p>
  <ol>

""" + starting + """
    <li>Whatever the session opens shows here; a browser tool attached to 127.0.0.1:9222 drives this window.</li>
  </ol>
</body></html>
""";
    }

    /// <summary>
    /// The session's browser: "" is Google Chrome (google-chrome on Linux) and no search, a path is that browser, and
    /// "auto" is the first found of the newest Playwright Chromium (what a Playwright harness installs and launches
    /// itself), Google Chrome, then Microsoft Edge or Chromium. Any Chromium-based browser serves: the session gives it a
    /// profile of its own and the DevTools port. Null with why and how to get one; otherwise found says where it came from.
    /// </summary>
    static string? FindBrowser(string configured, out string found)
    {
        if (configured.Length == 0)
        {
            string?[] chrome = OperatingSystem.IsLinux() ? new[] { FindOnPath("google-chrome") } : InstalledBrowsers()[0].Candidates;
            foreach (var candidate in chrome)
            {
                if (candidate == null || !File.Exists(candidate)) continue;
                found = "Google Chrome";
                return candidate;
            }
            found = $"Google Chrome is not installed: {ChromeInstall()}; or set DefaultBrowser in {DefaultsFileName} to \"auto\" or a browser's path";
            return null;
        }
        if (configured != "auto")
        {
            string path = Environment.ExpandEnvironmentVariables(configured);
            bool exists = File.Exists(path);
            found = exists ? $"DefaultBrowser in {DefaultsFileName}" : $"DefaultBrowser in {DefaultsFileName} is {path}, which does not exist: fix the path, or set it to \"auto\"";
            return exists ? path : null;
        }
        string? playwright = NewestPlaywrightChromium();
        if (playwright != null)
        {
            found = "the newest Playwright Chromium";
            return playwright;
        }
        foreach (var (name, candidates) in InstalledBrowsers())
        {
            foreach (var candidate in candidates)
            {
                if (candidate == null || !File.Exists(candidate)) continue;
                found = name;
                return candidate;
            }
        }
        found = "no Chromium-based browser (Playwright Chromium, Google Chrome, Microsoft Edge or Chromium): " + ChromeInstall() +
                $", or with Node.js: npx playwright install chromium; or set DefaultBrowser in {DefaultsFileName} to a browser's path";
        return null;
    }

    /// <summary>This OS's command that installs Google Chrome.</summary>
    static string ChromeInstall() => OperatingSystem.IsWindows() ? "winget install --id Google.Chrome -e" : OperatingSystem.IsMacOS() ? "brew install --cask google-chrome"
        : "install Google Chrome's .deb (wget https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb && sudo apt install ./google-chrome-stable_current_amd64.deb)";

    /// <summary>The newest chromium-&lt;revision&gt; build in Playwright's browser cache (PLAYWRIGHT_BROWSERS_PATH, else the OS's default).</summary>
    static string? NewestPlaywrightChromium()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string cache, string[] builds) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright"),
               new[] { Path.Combine("chrome-win64", "chrome.exe"), Path.Combine("chrome-win", "chrome.exe") })
            : OperatingSystem.IsMacOS()
                ? (Path.Combine(home, "Library", "Caches", "ms-playwright"),
                   new[] { "chrome-mac-arm64", "chrome-mac-x64" }.Select(d => Path.Combine(d, "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"))
                       .Append(Path.Combine("chrome-mac", "Chromium.app", "Contents", "MacOS", "Chromium")).ToArray())
                : (Path.Combine(home, ".cache", "ms-playwright"),
                   new[] { Path.Combine("chrome-linux64", "chrome"), Path.Combine("chrome-linux", "chrome") });
        string root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } configured ? configured : cache;
        if (!Directory.Exists(root)) return null;
        // The file system lists the revisions in no set order, so they are ordered once here, newest first.
        var revisions = new List<(int Revision, string Dir)>();
        foreach (var dir in Directory.EnumerateDirectories(root, "chromium-*"))
        {
            if (int.TryParse(Path.GetFileName(dir)["chromium-".Length..], out int revision)) revisions.Add((revision, dir));
        }
        revisions.Sort((a, b) => b.Revision.CompareTo(a.Revision));
        foreach (var (_, dir) in revisions)
        {
            foreach (var build in builds)
            {
                string exe = Path.Combine(dir, build);
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    /// <summary>The browsers "auto" looks for after Playwright's, in order, with where each installs on this OS.</summary>
    static (string Name, string?[] Candidates)[] InstalledBrowsers()
    {
        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string chrome = Path.Combine("Google", "Chrome", "Application", "chrome.exe"), edge = Path.Combine("Microsoft", "Edge", "Application", "msedge.exe");
            return new (string, string?[])[]
            {
                ("Google Chrome", new string?[] { Path.Combine(programFiles, chrome), Path.Combine(programFilesX86, chrome), Path.Combine(localAppData, chrome) }),
                ("Microsoft Edge", new string?[] { Path.Combine(programFilesX86, edge), Path.Combine(programFiles, edge) }),
            };
        }
        if (OperatingSystem.IsMacOS())
        {
            string userApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            string chrome = Path.Combine("Google Chrome.app", "Contents", "MacOS", "Google Chrome"), chromium = Path.Combine("Chromium.app", "Contents", "MacOS", "Chromium");
            return new (string, string?[])[]
            {
                ("Google Chrome", new string?[] { Path.Combine("/Applications", chrome), Path.Combine(userApps, chrome) }),
                ("Chromium", new string?[] { Path.Combine("/Applications", chromium), Path.Combine(userApps, chromium) }),
            };
        }
        return new (string, string?[])[]
        {
            ("Google Chrome", new[] { FindOnPath("google-chrome"), FindOnPath("google-chrome-stable") }),
            ("Microsoft Edge", new[] { FindOnPath("microsoft-edge"), FindOnPath("microsoft-edge-stable") }),
            ("Chromium", new[] { FindOnPath("chromium"), FindOnPath("chromium-browser") }),
        };
    }

    /// <summary>
    /// Closes the last session's browser when it has no page left: macOS keeps an app running after its last window
    /// closes, and that browser would hold the port and refuse every later session. The pid must still be a browser on
    /// this port; any other browser there is left alone, and the launch refuses it.
    /// </summary>
    static void CloseWindowlessBrowser(string sessionFile)
    {
        if (!File.Exists(sessionFile) || !PortAnswers(CdpPort)) return;
        using var session = JsonDocument.Parse(File.ReadAllText(sessionFile));
        int pid = session.RootElement.GetProperty("browserPid").GetInt32();
        if (!RunTool("ps", new[] { "-o", "command=", "-p", pid.ToString() }, out _).Contains($"--remote-debugging-port={CdpPort}")) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var targets = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{CdpPort}/json/list").Result);
        foreach (var target in targets.RootElement.EnumerateArray())
            if (target.GetProperty("type").GetString() == "page") return;
        using var browser = Process.GetProcessById(pid);
        browser.Kill(true);
        browser.WaitForExit();
        Console.WriteLine($"[joint-launcher] closed the last session's browser (pid {pid}): its window was closed, but macOS keeps the app running.");
    }

    static bool PortAnswers(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (AggregateException) { return false; }
        catch (SocketException) { return false; }
    }

    static string TailOf(string file)
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();
        return lines.Length == 0 ? "(empty)" : string.Join("\n", lines.TakeLast(10));
    }

    static bool WaitForCdp(Process browser)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + WindowWait;
        while (DateTime.UtcNow < deadline && !browser.HasExited)
        {
            try
            {
                if (http.GetAsync($"http://127.0.0.1:{CdpPort}/json/version").Result.IsSuccessStatusCode) return true;
            }
            catch (AggregateException) { }
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>The first top-level window of the class that matches, or null when none appears within WindowWait.</summary>
    static IntPtr? WaitForWindow(string windowClass, Func<IntPtr, bool> match)
    {
        var deadline = DateTime.UtcNow + WindowWait;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var hwnd in TopLevelWindows(windowClass))
            {
                if (match(hwnd)) return hwnd;
            }
            Thread.Sleep(200);
        }
        return null;
    }

    static string NeverAppeared(string what) => $"Error: {what} did not appear within {WindowWait.TotalSeconds:0}s.";

    static List<IntPtr> TopLevelWindows(string windowClass)
    {
        var found = new List<IntPtr>();
        var name = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            name.Clear();
            if (IsWindowVisible(hwnd) && GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() == windowClass)
                found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static int WindowProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>
    /// Moves a window so its VISIBLE frame fills the given rectangle. Windows 10/11 windows carry invisible resize
    /// borders inside their window rect, so placing the raw rect leaves gaps between the two windows; the DWM
    /// extended frame tells how wide those borders are.
    /// </summary>
    static void Place(IntPtr hwnd, Rect area)
    {
        int x = area.Left, y = area.Top, w = area.Right - area.Left, h = area.Bottom - area.Top;
        ShowWindow(hwnd, SwRestore);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
        if (GetWindowRect(hwnd, out Rect outer)
            && DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) == 0)
        {
            int left = frame.Left - outer.Left, top = frame.Top - outer.Top;
            int right = outer.Right - frame.Right, bottom = outer.Bottom - frame.Bottom;
            SetWindowPos(hwnd, IntPtr.Zero, x - left, y - top, w + left + right, h + top + bottom, SwpNoZOrder | SwpNoActivate);
        }
    }

    /// <summary>
    /// The skills the launcher carries, the same for every harness (each reads SKILL.md with name + description
    /// frontmatter): joint-launcher asks what to do, joint-launcher-install sets up what the session needs,
    /// joint-launcher-doctor troubleshoots a launch.
    /// </summary>
    private static readonly JointSkill[] EmbeddedSkills =
    {
        new(InstallSkill, """
---
name: joint-launcher-install
description: Install or update what the joint-session launcher (runJointLauncher.cs) needs on this machine besides .NET and the harness - a terminal, a Chromium-based browser, and for joint-browser Node.js 20+ with Playwright - asking before each install. The launcher starts the harness on this skill in its own console when the session cannot open yet, with "missing <items>" as the arguments.
---

# joint-launcher-install

The joint-session launcher (run from this folder with `{{launch}}`) opens
a terminal running this harness beside a shared Chromium-based browser. When it started you here with the arguments
`missing <items>`, those items stopped the session from opening (`terminal`, `desktop`, `browser`): start with them.
(Run from a harness, the launcher starts nothing: it prints what is missing and exits.)

A lost skill (never an item of `missing`; the doctor prints it as `[optional]`): a skill this launcher takes from its `skills` folder is neither there nor in this harness's skills folder; the
doctor names that folder. Read `SkillsRemote` in `runJointLauncher.json`. When it is a URL, ask the user, then download
`<SkillsRemote>/joint-launcher/SKILL.md`, `<SkillsRemote>/joint-browser/SKILL.md` and
`<SkillsRemote>/joint-browser/browser.mjs` to the same paths under that folder (`curl -fsSL --create-dirs -o <file> <url>`).
When it is empty nothing is downloaded: the folder ships with the tool, so ask the user to copy it there.

1. Run `{{launch --doctor}}` in this folder and read every `[missing]` and `[optional]` line;
   each names its fix.
2. Show the user the list with the install command for each (the doctor prints it; the table below is the same) and
   ask which to install: installs change the machine, so never run one unasked. Before installing Node.js or
   Playwright, ask whether they will use `joint-browser` at all.
3. Run the agreed commands one at a time and report each result. A tool installed by winget, brew or apt is on PATH
   only in NEW terminals: say so.
4. Run `--doctor` again and report what is now `[ok]` and what is still missing.
5. Tell the user to exit this harness and run `{{launch}}` again (in a new terminal when PATH
   changed): the launcher opens the session once nothing is missing.

| Item | Windows | macOS | Linux (Debian/Ubuntu; dnf/pacman elsewhere) |
|---|---|---|---|
| terminal | Windows Terminal: `winget install --id Microsoft.WindowsTerminal -e` (built into Windows 11) | Terminal.app is built in; allow "control Terminal" when macOS asks | `sudo apt install gnome-terminal` (or konsole, xfce4-terminal, xterm) |
| desktop | - | - | run the launcher from a terminal on the desktop, not over ssh |
| browser | Microsoft Edge is built in; or `winget install --id Google.Chrome -e` | `brew install --cask google-chrome` | Google Chrome's .deb: `wget https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb && sudo apt install ./google-chrome-stable_current_amd64.deb` |
| node, playwright (optional, joint-browser) | Node.js 20+: `winget install --id OpenJS.NodeJS.LTS -e`, then `node .claude/skills/joint-browser/browser.mjs setup` (in the harness's skills folder) | `brew install node`, then the same setup | NodeSource's LTS (the distro's own nodejs can be older than 20): `curl -fsSL https://deb.nodesource.com/setup_lts.x \| sudo -E bash - && sudo apt install -y nodejs`, then the same setup |
| x11-session (optional, window placement) | - | - | log in to an X11 session ("Ubuntu on Xorg"); on Wayland both windows open unplaced |

The browser is chosen by `DefaultBrowser` in `runJointLauncher.json`: `""` is Google Chrome and nothing else (on Linux
`google-chrome`); `auto` takes the newest Playwright Chromium, else Google Chrome, else Microsoft Edge or Chromium; a
path names one. With Node.js, `npx playwright install chromium`
installs Playwright's Chromium. On Ubuntu 23.10+ AppArmor can block the sandbox of a Chromium it has no profile for
(Playwright's included): if the browser exits at once, use Google Chrome's .deb.
"""),
        new(DoctorSkill, """
---
name: joint-launcher-doctor
description: Troubleshoot the joint-session launcher (runJointLauncher.cs) - run its doctor, explain each problem with its fix, match a failed launch against the known causes, and fix what the user agrees to.
---

# joint-launcher-doctor

The joint-session launcher (run from this folder with `{{launch}}`) opens a
terminal running this harness beside a shared browser on DevTools port 127.0.0.1:9222, then exits.

1. Run `{{launch --doctor}}` in this folder and explain each line that is not `[ok]` in plain
   words, with its fix.
2. If the user came here after a failed launch, ask what happened and match it against the known causes:
   - "127.0.0.1:9222 is already in use": a previous session's browser (or another browser started with
     `--remote-debugging-port=9222`) is still open; close that window.
   - "<harness> is not on PATH" right after installing: open a new terminal (PATH is read when a terminal starts).
   - The windows did not land where expected: `--position terminal:browser` and `--ratio terminal:browser` (saved as
     defaults); on Linux Wayland no app can place windows (log in to X11); on macOS allow the terminal to control
     Terminal in System Settings > Privacy & Security > Automation.
   - "the terminal window (gnome-terminal) did not appear": the terminal's D-Bus server runs outside this desktop
     (typical when launched over ssh); run the launcher from a terminal on the desktop. The launcher closes the
     browser it opened.
   - The browser exits at once on Ubuntu 23.10+: AppArmor can block the sandbox of a Chromium it has no profile for;
     set `DefaultBrowser` in `runJointLauncher.json` to Google Chrome (`""`).
   - `runJointLauncher.json` "has Browser: renamed to DefaultBrowser": rename the key in the file; `""` is Google
     Chrome, `auto` the search as before, or a browser's path.
   - `runJointLauncher.json` "has no ..." / "is not valid JSON": fix the named key (the message says what it takes),
     or delete the file to start from the initial defaults.
   - `joint-browser` says `Node.js ... is too old` or `no Playwright`: Playwright needs Node.js 20 or later (the
     distro's own nodejs can be older, e.g. 18 on Ubuntu 24.04; use NodeSource's LTS there), then
     `node <skills folder>/joint-browser/browser.mjs setup` installs playwright-core into `~/.joint-browser/node`
     (or the project's own `npm install -D playwright`).
   - A skill still shows old text after the launcher was updated: the launcher never overwrites a skill's files;
     delete that skill's folder and launch again.
3. Offer to run the fixes, asking before each change to the machine, then run `--doctor` again and report.
"""),
    };

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    const uint SpiGetWorkArea = 0x0030;
    const int SwRestore = 9;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoActivate = 0x0010;
    const int DwmwaExtendedFrameBounds = 9;
    static readonly IntPtr DpiAwarenessPerMonitorV2 = new IntPtr(-4);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, ref Rect rect, uint winIni);
    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();
    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
}
