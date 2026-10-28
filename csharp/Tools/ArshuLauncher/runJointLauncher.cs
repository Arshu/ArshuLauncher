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
/// Self-contained and repository-independent: copy this one file into any folder (a repository or not) on Windows,
/// macOS or Linux and run it from there with .NET 10+. It needs no package. runJointBuild.cs compiles it to the
/// NativeAOT single file runJointLauncher-&lt;os&gt;-&lt;arch&gt; (e.g. runJointLauncher-win-x64.exe), which runs the
/// same way without .NET; the help, the doctor and the skills name whichever was run (LaunchCommand). It carries the skills joint-launcher,
/// joint-browser (Playwright on the shared browser), joint-launcher-install and joint-launcher-doctor (EmbeddedSkills) and writes them to the harness's skills folder
/// there when missing, never over a copy already there; other skills are named in runJointLauncher.json with their
/// SKILL.md path. Launching checks, in order: the harness is supported and installed (else it says how to install it
/// and stops), the embedded skills are in place, then what the session needs (a terminal and a browser). Anything of
/// that missing starts the harness in the launcher's own console on joint-launcher-install (a bootstrap) instead.
///
/// runJointLauncher.json (beside where it runs, per machine; created on the first session) holds the defaults for
/// --harness, --skill, --position and --ratio, Skills and Browser; a --skill, --position or --ratio given on the command
/// line becomes the new default once the session opens.
///
/// The session records where its browser is (&lt;local app data&gt;/joint-launcher/session.json: cdp, harness, skill)
/// for a skill to attach to.
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
    /// A skill the launcher carries (EmbeddedSkills), written to the harness's skills folder when it is not there yet;
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

    // The skill the harness starts on when no skill is given: it lists the available skills and loads the chosen one.
    private const string ChooserSkill = "joint-launcher";
    // The skill a bootstrap starts on (the harness in the launcher's console) when the session cannot open yet.
    private const string InstallSkill = "joint-launcher-install";
    private const string DoctorSkill = "joint-launcher-doctor";
    // Drives the shared browser with Playwright (its helper browser.mjs beside SKILL.md); the same skill in runJointSession.cs.
    private const string BrowserSkill = "joint-browser";

    /// <summary>
    /// The launcher's defaults (runJointLauncher.json); an empty DefaultHarness makes --harness mandatory. Skills names
    /// the skills besides the embedded ones, each with its SKILL.md path (relative to the launch folder unless
    /// absolute); one is offered only while that file exists. Browser is "auto" (FindBrowser's order) or the path of a
    /// Chromium-based browser.
    /// </summary>
    sealed record JointDefaults(string DefaultHarness, string DefaultSkill, string DefaultPosition, string DefaultRatio,
        IReadOnlyDictionary<string, string> Skills, string Browser);

    private static readonly JointDefaults InitialDefaults = new("", "", "left:right", "1:2", new Dictionary<string, string>(), "auto");
    private static readonly string[] DefaultsKeys = { "DefaultHarness", "DefaultSkill", "DefaultPosition", "DefaultRatio", "Browser" };

    static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 0; }
        string? harnessName = null, skillName = null, skillArgs = null, position = null, ratio = null;
        bool doctor = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--help" or "-h") { PrintUsage(); return 0; }
            if (a == "--doctor") doctor = true;
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
        harnessName ??= defaults.DefaultHarness;
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

        bool isWindows = OperatingSystem.IsWindows(), isMac = OperatingSystem.IsMacOS(), isLinux = OperatingSystem.IsLinux();
        if (!isWindows && !isMac && !isLinux)
        {
            Console.WriteLine("Error: runJointLauncher lays out its windows on Windows, macOS and Linux only.");
            return 1;
        }

        // 1. The harness: one of the supported ones, named by --harness or DefaultHarness.
        if (harnessName.Length == 0)
        {
            Console.WriteLine($"Error: no harness: pass --harness {HarnessNames()} or set DefaultHarness in {defaultsFile}.");
            return 1;
        }
        Harness? harness = FindHarness(harnessName);
        if (harness == null)
        {
            Console.WriteLine($"Error: harness '{harnessName}' is not supported; use {HarnessNames()}.");
            return 1;
        }
        // 2. The harness is installed: without it there is nothing to start, so this is the one prerequisite the
        // launcher cannot hand to the harness.
        string? harnessPath = HarnessPath(harness);
        if (harnessPath == null)
        {
            Console.WriteLine($"Error: {harness.Name} is not on PATH. Install it with:\n  {(isWindows ? harness.InstallWindows : harness.InstallUnix)}\n" +
                              $"then sign in once by running {harness.Command} in a NEW terminal, and run the launcher again from there.");
            return 1;
        }
        // 3. The embedded skills in the harness's skills folder (a copy already there is kept).
        string? skillsError = InstallSkills(workspaceDir, harness);
        if (skillsError != null)
        {
            Console.WriteLine(skillsError);
            return 1;
        }

        // No skill starts the harness on joint-launcher, which asks the user which skill to load.
        string startSkill = skillName.Length == 0 ? ChooserSkill : skillName;
        string? skillFile = SkillFile(workspaceDir, harness, defaults, startSkill);
        if (skillFile == null)
        {
            Console.WriteLine($"Error: skill '{startSkill}' is neither embedded ({EmbeddedSkillNames()}) nor configured in Skills of {defaultsFile}" +
                              $"{(defaults.Skills.Count == 0 ? "" : $" ({string.Join(", ", defaults.Skills.Keys)})")}.");
            return 1;
        }
        if (!File.Exists(skillFile))
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
        string? skillPrompt = SkillPrompt(workspaceDir, harness, skillFile, startSkill, skillArgs, out string promptError);
        if (skillPrompt == null)
        {
            Console.WriteLine(promptError);
            return 1;
        }
        string title = $"Joint session - {startSkill}";

        // 4-5. What the session needs besides the harness. Anything missing is no refusal: the harness starts in this
        // console on joint-launcher-install (a bootstrap) to set it up, and the next launch opens the session.
        var missing = new List<string>();
        void Missing(string token, string why)
        {
            missing.Add(token);
            Console.WriteLine($"[joint-launcher] missing {token}: {why}");
        }
        // Linux places windows through X11; Wayland lets no app place windows, so there both open unplaced.
        bool placeWindows = !(isLinux && IsWayland());
        string[]? linuxTerminal = null;
        if (isWindows && FindOnPath("wt.exe") == null)
            Missing("terminal", "Windows Terminal (wt.exe) is not on PATH: winget install --id Microsoft.WindowsTerminal -e");
        if (isMac && FindOnPath("osascript") == null)
            Missing("terminal", "osascript is not on PATH (it opens Terminal.app); it ships with macOS in /usr/bin");
        if (isLinux)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                Missing("desktop", "no desktop session (DISPLAY and WAYLAND_DISPLAY are empty): run the launcher from a terminal on the desktop, not over ssh");
            linuxTerminal = LinuxTerminalCommand(workspaceDir, title, harness.Command, skillPrompt);
            if (linuxTerminal == null)
                Missing("terminal", "no supported terminal on PATH (gnome-terminal, konsole, xfce4-terminal or xterm): sudo apt install gnome-terminal");
        }
        string? foundBrowser = FindBrowser(defaults.Browser, out string browserFound);
        if (foundBrowser == null) Missing("browser", browserFound);
        else Console.WriteLine($"[joint-launcher] browser: {foundBrowser} ({browserFound})");

        if (missing.Count > 0)
            return Bootstrap(workspaceDir, harness, harnessPath, missing);
        string chrome = foundBrowser!;   // nothing is missing past the bootstrap

        if (PortAnswers(CdpPort))
        {
            Console.WriteLine($"Error: 127.0.0.1:{CdpPort} is already in use (a joint session's browser, or another browser started with " +
                              "--remote-debugging-port, is still open). Close it and relaunch.");
            return 1;
        }

        string deskDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "joint-launcher");
        string profileDir = Path.Combine(deskDir, "profile");
        string sessionFile = Path.Combine(deskDir, "session.json");
        string statusPage = Path.Combine(deskDir, "desk.html");
        try
        {
            // Nothing holds the profile once 9222 is free, so every session starts from an empty one.
            if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true);
            Directory.CreateDirectory(profileDir);
            // Until the harness navigates the window it shows what to do next instead of a blank page.
            File.WriteAllText(statusPage, StatusPage(harness.Name, skillPrompt), new UTF8Encoding(false));
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
        Rect terminalArea = default, browserArea = default;
        if (placeWindows)
        {
            Layout(work, position, terminalPart, browserPart, out terminalArea, out browserArea);
            Console.WriteLine($"[joint-launcher] work area {work.Right - work.Left}x{work.Bottom - work.Top} at {work.Left},{work.Top}, {position} {ratio}: " +
                              $"terminal {terminalArea.Right - terminalArea.Left}x{terminalArea.Bottom - terminalArea.Top} | " +
                              $"browser {browserArea.Right - browserArea.Left}x{browserArea.Bottom - browserArea.Top}");
        }
        else
            Console.WriteLine("[joint-launcher] Wayland lets no app place windows: the terminal and the browser open where the desktop puts them; " +
                              "arrange them yourself, or log in to an X11 session for the layout.");

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

        // The terminal runs the harness as a session of its own, not as a child of a Claude that ran this launcher.
        ForgetParentClaudeSession();
        string? terminalError = isWindows ? OpenWindowsTerminal(workspaceDir, title, harness.Command, skillPrompt, terminalArea)
            : isMac ? OpenMacTerminal(workspaceDir, title, harness.Command, skillPrompt, terminalArea)
            : OpenLinuxTerminal(linuxTerminal!, browser.Id, placeWindows ? terminalArea : (Rect?)null);
        if (terminalError != null)
        {
            Console.WriteLine(terminalError);
            CloseSession(browser);
            return 1;
        }

        WriteSession(sessionFile, browser.Id, profileDir, workspaceDir, harness.Name, startSkill);
        // Only an opened session makes its command-line choices the defaults, so a refused run changes nothing.
        if (defaultsChanged || !File.Exists(defaultsFile))
        {
            WriteDefaults(defaultsFile, defaults with { DefaultSkill = skillName, DefaultPosition = position, DefaultRatio = ratio });
            Console.WriteLine($"[joint-launcher] defaults saved to {defaultsFile}: skill {(skillName.Length == 0 ? "(none: " + ChooserSkill + " asks)" : skillName)}, position {position}, ratio {ratio}");
        }

        Console.WriteLine($"[joint-launcher] browser pid {browser.Id} on CDP 127.0.0.1:{CdpPort}; session file {sessionFile}");
        Console.WriteLine($"[joint-launcher] the {position.Split(':')[0]} terminal starts {harness.Name} on: {skillPrompt}");
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

    static void WriteSession(string sessionFile, int browserPid, string profileDir, string workspaceDir, string harness, string skill)
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
        json.WriteString("startedUtc", DateTime.UtcNow.ToString("o"));
        json.WriteEndObject();
    }

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

        public static void Close(IntPtr window) => Send(window, "_NET_CLOSE_WINDOW", 0 /* now */, SourcePager);

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
        string script = $"cd {ShQuote(workspaceDir)} && {harnessCommand} {ShQuote(skillPrompt)}; exec bash";
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

    // The embedded skills write {{launch}} and {{launch --doctor}} for the command that runs the launcher.
    static string LaunchText(string text) => text.Replace("{{launch --doctor}}", LaunchCommand("--doctor")).Replace("{{launch}}", LaunchCommand());

    /// <summary>
    /// Opens a Terminal.app window running the harness on the skill prompt in the workspace, titled and placed through
    /// AppleScript (Terminal's bounds are points, as the work area). macOS asks once to let the calling terminal
    /// control Terminal.
    /// </summary>
    static string? OpenMacTerminal(string workspaceDir, string title, string harnessCommand, string skillPrompt, Rect area)
    {
        static string AppleScriptString(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string command = $"cd {ShQuote(workspaceDir)} && {harnessCommand} {ShQuote(skillPrompt)}";
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
        foreach (var arg in new[] { "-w", "new", "-d", workspaceDir, "--title", title, "cmd.exe", "/k", harnessCommand, skillPrompt })
            wt.ArgumentList.Add(arg);
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
        if (!browser.HasExited) browser.Kill(true);
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
            var absent = EmbeddedSkills.Where(s => !SkillInstalled(workspaceDir, h, s)).Select(s => s.Name).ToList();
            Check(true, absent.Count == 0 ? $"{h.Name} skills in {h.SkillsDir}: {EmbeddedSkillNames()}"
                : $"{h.Name} skills in {h.SkillsDir}: the launcher installs {string.Join(", ", absent)} on its next run", "");
        }
        if (chosen == null) Check(Harnesses.Any(h => HarnessPath(h) != null), $"a supported harness installed ({HarnessNames()})", "install one of them (above)");
        if (defaults != null)
        {
            foreach (var (name, path) in defaults.Skills)
            {
                string file = Path.GetFullPath(path, workspaceDir);
                Check(File.Exists(file), $"skill {name} ({file})", $"fix its path in Skills of {DefaultsFileName}, or remove it", required: false);
            }
        }
        if (isWindows) Check(FindOnPath("wt.exe") != null, "Windows Terminal (wt.exe)", "winget install --id Microsoft.WindowsTerminal -e");
        if (isMac) Check(FindOnPath("osascript") != null, "osascript (opens Terminal.app)", "part of macOS; check PATH includes /usr/bin");
        if (isLinux)
        {
            bool desktop = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            Check(desktop, "a desktop session (DISPLAY or WAYLAND_DISPLAY)", "run the launcher from a terminal on the desktop, not over ssh");
            Check(!IsWayland(), "an X11 session (the windows are placed)", "log in to an X11 session (\"Ubuntu on Xorg\", or xrdp for a remote desktop); on Wayland both windows open unplaced", required: false);
            Check(LinuxTerminalCommand(workspaceDir, "", "", "") != null, "a terminal: gnome-terminal, konsole, xfce4-terminal or xterm", "sudo apt install gnome-terminal (or xterm)");
        }
        string? browser = FindBrowser(defaults?.Browser ?? InitialDefaults.Browser, out string browserFound);
        Check(browser != null, browser != null ? $"browser {browser} ({browserFound})" : "a Chromium-based browser", browserFound);
        Check(!PortAnswers(CdpPort), $"port {CdpPort} free (no joint session open)", "close the open session's browser window");
        // The launch's own skill checks on the defaults, once nothing above is missing: Ready means a launch with this
        // harness and no --skill opens the session (a missing or command-line-unsafe DefaultSkill file refused it).
        if (chosen != null && defaults != null && missing == 0)
        {
            string startSkill = defaults.DefaultSkill.Length == 0 ? ChooserSkill : defaults.DefaultSkill;
            string? skillFile = SkillFile(workspaceDir, chosen, defaults, startSkill);
            string? problem = skillFile == null ? $"skill '{startSkill}' is neither embedded ({EmbeddedSkillNames()}) nor configured in Skills of {DefaultsFileName}"
                : !Array.Exists(EmbeddedSkills, s => s.Name == startSkill) && !File.Exists(skillFile)
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
    /// not a silent default, so the doctor reports it before a launch would.
    /// </summary>
    static JointDefaults? ReadDefaults(string file, out string error)
    {
        error = "";
        if (!File.Exists(file)) return InitialDefaults;
        string Fix(string what) => $"Error: {file} {what}; fix it or delete the file to start from the initial defaults.";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Fix("is not a JSON object");
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
                values[key] = value.GetString()!.Trim();
            }
            if (values["Browser"].Length == 0)
            {
                error = Fix("has an empty Browser (use \"auto\" or a browser's path)");
                return null;
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
                if (!Regex.IsMatch(skill.Name, "^[a-z0-9]+(-[a-z0-9]+)*$") || Array.Exists(EmbeddedSkills, s => s.Name == skill.Name)
                    || skill.Value.ValueKind != JsonValueKind.String || skill.Value.GetString()!.Trim().Length == 0)
                {
                    error = Fix($"has Skills entry '{skill.Name}': a skill name is lowercase letters, digits and single hyphens, not an embedded skill ({EmbeddedSkillNames()}), with its SKILL.md path as a string");
                    return null;
                }
                skills[skill.Name] = skill.Value.GetString()!.Trim();
            }
            string defaultSkill = values["DefaultSkill"];
            if (defaultSkill.Length > 0 && !Array.Exists(EmbeddedSkills, s => s.Name == defaultSkill) && !skills.ContainsKey(defaultSkill))
            {
                error = Fix($"has DefaultSkill '{defaultSkill}' (use \"\", an embedded skill ({EmbeddedSkillNames()}) or a name in Skills)");
                return null;
            }
            return new JointDefaults(harness, defaultSkill, position, values["DefaultRatio"], skills, values["Browser"]);
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
        json.WriteString("Browser", defaults.Browser);
        json.WriteEndObject();
    }

    static bool ParseRatio(string text, out int terminalPart, out int browserPart)
    {
        terminalPart = browserPart = 0;
        var m = Regex.Match(text, @"^\s*(\d{1,2})\s*:\s*(\d{1,2})\s*$");
        return m.Success
            && int.TryParse(m.Groups[1].Value, out terminalPart) && terminalPart > 0
            && int.TryParse(m.Groups[2].Value, out browserPart) && browserPart > 0;
    }

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

    static string EmbeddedSkillNames() => string.Join(", ", EmbeddedSkills.Select(s => s.Name));

    /// <summary>Where the harness finds the named skill in the launch folder: &lt;SkillsDir&gt;/&lt;name&gt;/SKILL.md.</summary>
    static string HarnessSkillFile(string workspaceDir, Harness harness, string name) =>
        Path.GetFullPath(Path.Combine(workspaceDir, harness.SkillsDir, name, "SKILL.md"));

    /// <summary>Whether every file of the embedded skill is in the harness's skills folder.</summary>
    static bool SkillInstalled(string workspaceDir, Harness harness, JointSkill skill)
    {
        string dir = Path.GetDirectoryName(HarnessSkillFile(workspaceDir, harness, skill.Name))!;
        return skill.Files().All(f => File.Exists(Path.Combine(dir, f.Name)));
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

    /// <summary>
    /// Writes each embedded skill the harness's skills folder lacks; a copy already there is the user's and is kept.
    /// Null, or why a skill could not be written.
    /// </summary>
    static string? InstallSkills(string workspaceDir, Harness harness)
    {
        foreach (var skill in EmbeddedSkills)
        {
            string dir = Path.GetDirectoryName(HarnessSkillFile(workspaceDir, harness, skill.Name))!;
            foreach (var (name, text) in skill.Files())
            {
                string file = Path.Combine(dir, name);
                if (File.Exists(file)) continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(file, LaunchText(text).ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return $"Error: could not write the {skill.Name} skill's {name} to {file} ({e.Message}); run the launcher from a folder you can write to.";
                }
                Console.WriteLine($"[joint-launcher] installed the {skill.Name} skill's {name} for {harness.Name}: {file}");
            }
        }
        return null;
    }

    /// <summary>
    /// The named skill's SKILL.md for this harness: an embedded skill's in the harness's skills folder, a configured
    /// one's at its path in Skills; null when it is neither.
    /// </summary>
    static string? SkillFile(string workspaceDir, Harness harness, JointDefaults defaults, string name) =>
        Array.Exists(EmbeddedSkills, s => s.Name == name) ? HarnessSkillFile(workspaceDir, harness, name)
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
    static int Bootstrap(string workspaceDir, Harness harness, string harnessPath, List<string> missing)
    {
        string prompt = $"{harness.SkillPrefix}{InstallSkill} missing {string.Join(" ", missing)}";
        Console.WriteLine($"[joint-launcher] the session cannot open until that is set up: {harness.Name} starts here on {InstallSkill} to install it. " +
                          "When it is done, exit it and run the launcher again.");
        ForgetParentClaudeSession();
        // cmd resolves the harness's .exe or .cmd on Windows; elsewhere the harness runs directly.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : harnessPath) { UseShellExecute = false, WorkingDirectory = workspaceDir };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(harness.Command);
        }
        start.ArgumentList.Add(prompt);
        // Ctrl+C belongs to the harness (it interrupts its turn); the launcher waits for it to exit.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {harnessPath}");
        process.WaitForExit();
        return process.ExitCode;
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

Opens a joint session and exits: a terminal running an AI harness and a shared Chromium-based browser (DevTools
protocol on 127.0.0.1:9222) splitting the primary monitor. Copy this one file into any folder (a repository or not)
and run it from there, on the desktop (not over ssh): runJointLauncher.cs with the .NET 10 SDK or later, or the
single-file binary runJointLauncher-<os>-<arch> (e.g. runJointLauncher-win-x64.exe), which needs no .NET. It needs
nothing else beside it; runJointLauncher.json is created there on the first session.

  --harness <name>       what the terminal starts, for this run only (never saved): claude, codex or pi; required
                         while DefaultHarness in runJointLauncher.json is empty. It must be installed: the launcher
                         prints its install command when it is not.
  --skill <name>         the skill the harness starts on (default: DefaultSkill; when that is empty too, the harness
                         starts on joint-launcher, which asks what to do). The launcher carries joint-launcher,
                         joint-browser (drives the shared browser with Playwright: Node.js and a standard Playwright),
                         joint-launcher-install and joint-launcher-doctor and writes them to the harness's skills folder
                         (.claude/skills, .agents/skills, .pi/skills) when missing, never over a copy already there;
                         other skills are named with their SKILL.md path in Skills of runJointLauncher.json.
  --skillargs "<args>"   the skill's own arguments, e.g. --skill my-review --skillargs "login page".
  --position <t:b>       where the terminal and the browser go: left:right, right:left (side by side) or
                         top:bottom, bottom:top (stacked); the terminal is the first named side.
  --ratio <t>:<b>        the terminal's share against the browser's along the split, e.g. 1:2, 1:4, 1:5.
  --doctor               check every prerequisite and print how to fix what is missing; opens no window.

When something the session needs is missing (a terminal, a browser) but the harness is installed, the launcher starts
the harness in its own console on joint-launcher-install, told what is missing; run the launcher again once it is
installed.

Defaults live in runJointLauncher.json in the folder the launcher runs from (DefaultHarness, DefaultSkill,
DefaultPosition, DefaultRatio, Skills, Browser; initially "", "", left:right, 1:2, {}, auto). A --skill, --position or
--ratio given here becomes the new default once the session opens; the rest change only by editing the file. Skills
maps a skill's name to its SKILL.md path (relative to the launch folder unless absolute); Browser "auto" takes the
newest Playwright Chromium, else Google Chrome, else Microsoft Edge or Chromium, or name a browser's path.

Platforms: Windows (Windows Terminal), macOS (Terminal.app; allow it to be controlled when macOS asks) and Linux
(gnome-terminal, konsole, xfce4-terminal or xterm; the windows are placed on X11, on Wayland both open unplaced).

Writes <local app data>/joint-launcher/session.json (cdp, browserPid, harness, skill) for a skill to attach to. The
session ends when you close its two windows.
""");
    }

    static string StatusPage(string harnessName, string skillPrompt)
    {
        string starting = $"    <li>{WebUtility.HtmlEncode(harnessName)} is starting in the terminal on <code>{WebUtility.HtmlEncode(skillPrompt)}</code>.</li>\n";
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
  <p class="sub">This window is shared with the AI session in the terminal (DevTools protocol on 127.0.0.1:9222).</p>
  <ol>

""" + starting + """
    <li>Whatever the session opens shows here; a browser tool attached to 127.0.0.1:9222 drives this window.</li>
  </ol>
</body></html>
""";
    }

    /// <summary>
    /// The session's browser: Browser's path, or for "auto" the first found of the newest Playwright Chromium (what a
    /// Playwright harness installs and launches itself), Google Chrome, then Microsoft Edge or Chromium. Any
    /// Chromium-based browser serves: the session gives it a profile of its own and the DevTools port. Null with why
    /// and how to get one; otherwise found says where it came from.
    /// </summary>
    static string? FindBrowser(string configured, out string found)
    {
        if (configured != "auto")
        {
            string path = Environment.ExpandEnvironmentVariables(configured);
            bool exists = File.Exists(path);
            found = exists ? $"Browser in {DefaultsFileName}" : $"Browser in {DefaultsFileName} is {path}, which does not exist: fix the path, or set it to \"auto\"";
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
        found = "no Chromium-based browser (Playwright Chromium, Google Chrome, Microsoft Edge or Chromium): " +
                (OperatingSystem.IsWindows() ? "winget install --id Google.Chrome -e" : OperatingSystem.IsMacOS() ? "brew install --cask google-chrome"
                    : "install Google Chrome's .deb (wget https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb && sudo apt install ./google-chrome-stable_current_amd64.deb)") +
                $", or with Node.js: npx playwright install chromium; or set Browser in {DefaultsFileName} to a browser's path";
        return null;
    }

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
        new(BrowserSkill, """
---
name: joint-browser
description: Drive the joint session's shared browser (Chromium on DevTools port 127.0.0.1:9222) with Playwright together with the user - open pages, click, fill, read and check what changed - for any web app in any project, including a joint review of a flow (walk it step by step, validate every action, discuss, fix the code, re-run). Use for "drive the browser", "joint browser", "review this page/flow with me", "joint review" when no project-specific review skill applies.
---

# joint-browser

A joint session (started by `runJointLauncher.cs` or `runJointSession.cs`) is this terminal plus a shared
Chromium-based browser the user watches, with its DevTools protocol on http://127.0.0.1:9222. You drive that window
with Playwright through `browser.mjs`, the helper beside this SKILL.md (in `.claude/skills/joint-browser/`,
`.agents/skills/joint-browser/` or `.pi/skills/joint-browser/`): one action per call, each printing a JSON report. It
attaches to the open window and never launches or closes a browser.

## 0. Ready

1. `node <this folder>/browser.mjs status`. It needs Node.js 20 or later, as Playwright does (`node --version`;
   install from nodejs.org, `winget install --id OpenJS.NodeJS.LTS -e`, `brew install node`, or on Debian/Ubuntu
   NodeSource's LTS: `curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash - && sudo apt install -y
   nodejs`; the distro's own `nodejs` can be older, e.g. 18 on Ubuntu 24.04). Ask before installing.
2. `"playwright": null`: the helper uses the project's own Playwright when the project has one (`playwright` or
   `playwright-core` in its `node_modules`); otherwise ask the user, then run `node <this folder>/browser.mjs setup`
   (installs `playwright-core` with npm into `~/.joint-browser/node`; no browser download, the shared one is used).
3. `"browser": null`: no joint session browser is open; ask the user to start the launcher (or to close whatever else
   holds port 9222).
4. Tell the user it is ready and ask what to do, unless the arguments already say it.

## 1. Drive

Do what the user asks, at human pace, in the shared window: they watch every step. The commands
(`node browser.mjs help` prints all of them with the options):

| Command | Does |
|---|---|
| `pages` | the open tabs; `--page <index>` picks one for any command (default: the newest tab) |
| `goto <url>` / `back` / `forward` / `reload` | navigate |
| `click`, `dblclick`, `hover`, `check`, `uncheck <selector>` | act on an element |
| `fill <selector> <text>` / `type <selector> <text>` / `press <key> [selector]` / `select <selector> <value>` / `upload <selector> <file>` | input |
| `snapshot [selector]` | url, title and the ARIA tree: roles, names, states; read it before choosing a selector |
| `text`, `attr`, `count <selector>` / `eval <expression>` | read the page |
| `screenshot <file> [selector]` (`--full`) | a PNG to look at; save it outside the project (a temp or scratch folder) |
| `wait <selector> [state]` / `console` | wait for an element / listen for console output |
| `newtab [url]` / `closetab` | tabs (the last tab, the shared window, is never closed) |

Selectors are Playwright's: prefer role and text (`"role=button[name='Sign in']"`, `"text=Forgot password"`) over
CSS, as a user names things; take the names from `snapshot`. Quote each argument for your shell.

**Every action is validated.** A click that did nothing must not pass silently:
- Before acting on an element, make sure it is the one meant (`snapshot` / `text`), and the state it acts on is
  what it should be (a selection, a checked box).
- Give the action the effect it should have, so the helper asserts it: `--expect-url <part>`, `--expect-text
  <text>`, `--expect-visible <selector>`, `--expect-hidden <selector>`. `"ok": false` with `expect_failed` = the
  effect did not happen.
- Read `"clean"`: console errors and warnings, page errors, unexpected dialogs (dismissed) and failed or 4xx/5xx
  requests raised by the action are listed; exit code 2. A dialog that IS the effect (a confirm before a delete) is
  accepted with `--accept-dialog [text]`, reported as `dialogs_accepted`, and fails the action when none appears. Each is a finding to show the user, unless the
  user agreed it is expected (`--allow <regex>` then stops counting it).

## 2. Joint review

When the user wants to review a page or a flow with you (a sign-up, a checkout, a settings page):

1. **Agree the scope**: the app's URL, the flow and its steps, the account or data to use. If the app is not running,
   ask how it starts. Look for its own run command in the project (README, AGENTS.md / CLAUDE.md, package.json
   scripts, a Makefile) and start it only when the user agrees, in the background, with its output in a log file.
   If the project has its own review skill or test harness for this, say so and offer it; this skill needs none.
2. **Walk the flow one step at a time**: say what the step will do, do it with its `--expect-*`, report what changed
   (`snapshot` / `text`, a screenshot when the look matters) and what was raised. Pause for the user's comments
   before the next step; they may also click in the window themselves, so `snapshot` again before acting after a
   pause.
3. **Keep the findings** as you go, in a short list in the conversation (step, what happened, what was expected,
   evidence: the report lines, a screenshot path). Each finding is an implementation or a test question: ask
   which.
4. **Fix with the user**: change the project's code only when the user agrees, rebuild or restart the app the
   project's way, then re-run the step (and the steps before it when they set it up) and show the result.
5. **Keep what passed**: when the user wants it, turn the agreed steps into a test in the project's own test setup
   (its Playwright tests if it has them: the same selectors and expectations), never a test that only passes here.

Nothing else: no server or test starts unless the user agrees, and the shared window stays open for the next step.
""", "browser.mjs", """
#!/usr/bin/env node
// joint-browser: one Playwright action per call on the joint session's shared browser (CDP, default
// http://127.0.0.1:9222). It attaches to the open window and never launches or closes a browser. Playwright comes from
// the project (node_modules/playwright or playwright-core under the current folder), else from ~/.joint-browser/node,
// where `node browser.mjs setup` installs playwright-core with npm. Every action prints a JSON report: what it did, the
// URL before and after, and every console error, page error, dialog and failed request it raised; --expect-* asserts
// the action's effect. Exit 0 = ok and clean, 2 = ok but something unexpected was raised, 1 = failed.
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const PLAYWRIGHT_VERSION = '1.64.0';
const NODE_DIR = path.join(os.homedir(), '.joint-browser', 'node');
const MIN_NODE = 20;   // what Playwright requires
const NODE_INSTALL = 'nodejs.org; winget install --id OpenJS.NodeJS.LTS -e; brew install node; on Debian/Ubuntu ' +
  'curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash - && sudo apt install -y nodejs (the distro\'s own nodejs can be older)';

const USAGE = `Usage: node browser.mjs <command> [args] [options]

Setup and state:
  setup                         npm install playwright-core@${PLAYWRIGHT_VERSION} into ${NODE_DIR}
  status                        where Playwright comes from and whether the browser answers
  pages                         the open tabs (index, url, title); --page <index> picks one for any command
Actions (each reports its effect):
  goto <url>                    navigate
  click <selector>              click (dblclick, hover, check, uncheck likewise)
  fill <selector> <text>        replace a field's text
  type <selector> <text>        type key by key (for fields that react per key)
  press <key> [selector]        press a key (Enter, Tab, Control+A ...) on the page or an element
  select <selector> <value>     choose an <option> by value or label
  upload <selector> <file...>   set files on an <input type=file>
  back | forward | reload       history and reload
  newtab [url]                  open a tab (it becomes the newest tab)
  closetab                      close the chosen tab (--page; the window itself is never closed)
  wait <selector> [state]       wait until visible (or attached, detached, hidden)
Reading:
  snapshot [selector]           url, title and the ARIA tree (roles, names, states) of the page or an element
  text <selector>               an element's visible text
  attr <selector> <name>        an attribute's value
  count <selector>              how many elements match
  eval <expression>             evaluate JavaScript in the page and print the result
  screenshot <file> [selector]  save a PNG of the viewport (--full: whole page) or an element
  console                       console lines and page errors raised while waiting --settle ms

Selectors are Playwright selectors: CSS ("#email", "button.primary"), text ("text=Sign in"), role
("role=button[name='Sign in']"), label ("internal:label=\\"Email\\"i") or XPath ("xpath=//h1").
Options:
  --cdp <url>                   the browser's DevTools endpoint (default: $JOINT_CDP, else http://127.0.0.1:9222)
  --page <index>                the tab (from 'pages'); default: the newest tab
  --timeout <ms>                how long an action may wait for its element (default 10000)
  --settle <ms>                 how long to keep listening after the action (default 700)
  --expect-url <text>           fail unless the URL then contains the text
  --expect-text <text>          fail unless the page then shows the text
  --expect-visible <selector>   fail unless the element is then visible
  --expect-hidden <selector>    fail unless the element is then hidden or gone
  --accept-dialog [text]        accept a dialog (alert/confirm/prompt, with the prompt text) instead of dismissing it
  --allow <regex>               do not count console errors / failed requests whose text matches as unexpected
  --full                        screenshot: the whole page`;

function parse(argv) {
  const positional = [];
  const options = { cdp: process.env.JOINT_CDP || 'http://127.0.0.1:9222', page: null, timeout: 10000, settle: 700, expect: [], acceptDialog: null, allow: null, full: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const value = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    if (a === '--cdp') options.cdp = value();
    else if (a === '--page') options.page = Number(value());
    else if (a === '--timeout') options.timeout = Number(value());
    else if (a === '--settle') options.settle = Number(value());
    else if (a === '--expect-url') options.expect.push(['url', value()]);
    else if (a === '--expect-text') options.expect.push(['text', value()]);
    else if (a === '--expect-visible') options.expect.push(['visible', value()]);
    else if (a === '--expect-hidden') options.expect.push(['hidden', value()]);
    else if (a === '--accept-dialog') options.acceptDialog = i + 1 < argv.length && !argv[i + 1].startsWith('--') ? argv[++i] : '';
    else if (a === '--allow') options.allow = new RegExp(value());
    else if (a === '--full') options.full = true;
    else if (a.startsWith('--')) throw new Error(`unknown option ${a}`);
    else positional.push(a);
  }
  return { command: positional[0], args: positional.slice(1), options };
}

/** Playwright from the project first (its own install), then from ~/.joint-browser/node; null when neither has it. */
function loadPlaywright() {
  for (const base of [process.cwd(), NODE_DIR]) {
    const require = createRequire(path.join(base, 'joint-browser.cjs'));
    for (const name of ['playwright', 'playwright-core']) {
      let file;
      try { file = require.resolve(name); } catch { continue; }
      return { playwright: require(file), from: file, version: require(path.join(path.dirname(require.resolve(`${name}/package.json`)), 'package.json')).version };
    }
  }
  return null;
}

function setup() {
  fs.mkdirSync(NODE_DIR, { recursive: true });
  const args = ['install', '--prefix', NODE_DIR, '--no-fund', '--no-audit', `playwright-core@${PLAYWRIGHT_VERSION}`];
  console.log(`npm ${args.join(' ')}`);
  // npm is npm.cmd on Windows, which only a shell runs.
  const result = spawnSync('npm', args, { stdio: 'inherit', shell: process.platform === 'win32' });
  if (result.error) throw new Error(`npm could not start (${result.error.message}): install Node.js (it brings npm)`);
  return result.status ?? 1;
}

async function cdpVersion(cdp) {
  try {
    const response = await fetch(new URL('/json/version', cdp), { signal: AbortSignal.timeout(2000) });
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
}

function print(report) {
  console.log(JSON.stringify(report, null, 2));
}

async function main() {
  const { command, args, options } = parse(process.argv.slice(2));
  if (!command || command === 'help' || command === '--help') { console.log(USAGE); return 0; }
  const nodeMajor = Number(process.versions.node.split('.')[0]);
  if (nodeMajor < MIN_NODE) throw new Error(`Node.js ${process.versions.node} is too old: Playwright needs ${MIN_NODE} or later (${NODE_INSTALL})`);
  if (command === 'setup') return setup();

  const loaded = loadPlaywright();
  const browserInfo = await cdpVersion(options.cdp);
  if (command === 'status') {
    print({ playwright: loaded ? { from: loaded.from, version: loaded.version } : null, cdp: options.cdp, browser: browserInfo ? browserInfo.Browser : null });
    return loaded && browserInfo ? 0 : 1;
  }
  if (!loaded) throw new Error(`no Playwright: run "node browser.mjs setup" (installs playwright-core into ${NODE_DIR}), or "npm install -D playwright" in the project`);
  if (!browserInfo) throw new Error(`no browser answers on ${options.cdp}: open the joint session first (it starts the shared browser with --remote-debugging-port=9222)`);

  const browser = await loaded.playwright.chromium.connectOverCDP(options.cdp);
  const context = browser.contexts()[0];
  if (!context) throw new Error('the browser has no window to drive');
  const pages = context.pages();

  if (command === 'pages') {
    const list = [];
    for (const [index, p] of pages.entries()) list.push({ index, url: p.url(), title: await p.title() });
    print({ pages: list });
    return 0;
  }
  if (command === 'newtab') {
    const opened = await context.newPage();
    if (args[0]) await opened.goto(args[0], { timeout: options.timeout });
    await opened.bringToFront();
    print({ ok: true, action: 'newtab', index: context.pages().indexOf(opened), url: opened.url() });
    return 0;
  }
  if (pages.length === 0) throw new Error('the browser has no open tab (newtab opens one)');
  if (options.page != null && !(options.page >= 0 && options.page < pages.length)) throw new Error(`--page ${options.page}: the browser has tabs 0-${pages.length - 1}`);
  const page = pages[options.page ?? pages.length - 1];
  page.setDefaultTimeout(options.timeout);

  // What the action raises: listened to from before it starts until --settle ms after it ends.
  const raised = { console_errors: [], page_errors: [], dialogs: [], failed_requests: [] };
  const allowed = (text) => options.allow != null && options.allow.test(text);
  page.on('console', (m) => { if ((m.type() === 'error' || m.type() === 'warning') && !allowed(m.text())) raised.console_errors.push(`${m.type()}: ${m.text()}`); });
  page.on('pageerror', (e) => raised.page_errors.push(String(e.message ?? e)));
  page.on('requestfailed', (r) => { const t = `${r.method()} ${r.url()} ${r.failure()?.errorText ?? ''}`; if (!allowed(t)) raised.failed_requests.push(t); });
  page.on('response', (r) => { const t = `${r.status()} ${r.request().method()} ${r.url()}`; if (r.status() >= 400 && !allowed(t)) raised.failed_requests.push(t); });
  // A dialog the action was told to accept is its expected effect; any other is dismissed and counted.
  const accepted = [];
  page.on('dialog', async (d) => {
    if (options.acceptDialog != null) { accepted.push(`${d.type()}: ${d.message()}`); await d.accept(options.acceptDialog); }
    else { raised.dialogs.push(`${d.type()}: ${d.message()}`); await d.dismiss(); }
  });

  const need = (n, what) => { if (args.length < n) throw new Error(`${command} needs ${what}`); };
  const urlBefore = page.url();
  let result;
  switch (command) {
    case 'goto': need(1, '<url>'); await page.goto(args[0]); break;
    case 'click': need(1, '<selector>'); await page.locator(args[0]).click(); break;
    case 'dblclick': need(1, '<selector>'); await page.locator(args[0]).dblclick(); break;
    case 'hover': need(1, '<selector>'); await page.locator(args[0]).hover(); break;
    case 'check': need(1, '<selector>'); await page.locator(args[0]).check(); break;
    case 'uncheck': need(1, '<selector>'); await page.locator(args[0]).uncheck(); break;
    case 'fill': need(2, '<selector> <text>'); await page.locator(args[0]).fill(args[1]); break;
    case 'type': need(2, '<selector> <text>'); await page.locator(args[0]).pressSequentially(args[1], { delay: 30 }); break;
    case 'press': need(1, '<key>'); if (args[1]) await page.locator(args[1]).press(args[0]); else await page.keyboard.press(args[0]); break;
    case 'select': need(2, '<selector> <value>'); result = await page.locator(args[0]).selectOption(args[1]); break;
    case 'upload': need(2, '<selector> <file...>'); await page.locator(args[0]).setInputFiles(args.slice(1)); break;
    case 'back': await page.goBack(); break;
    case 'forward': await page.goForward(); break;
    case 'reload': await page.reload(); break;
    case 'closetab':
      if (pages.length === 1) throw new Error('closetab would close the last tab and with it the shared window; goto another page instead');
      await page.close();
      print({ ok: true, action: 'closetab', closed: urlBefore });
      return 0;
    case 'wait': need(1, '<selector>'); await page.locator(args[0]).first().waitFor({ state: args[1] ?? 'visible' }); break;
    case 'snapshot': result = await page.locator(args[0] ?? 'body').first().ariaSnapshot(); break;
    case 'text': need(1, '<selector>'); result = await page.locator(args[0]).first().innerText(); break;
    case 'attr': need(2, '<selector> <name>'); result = await page.locator(args[0]).first().getAttribute(args[1]); break;
    case 'count': need(1, '<selector>'); result = await page.locator(args[0]).count(); break;
    case 'eval': need(1, '<expression>'); result = await page.evaluate(args[0]); break;
    case 'screenshot':
      need(1, '<file>');
      if (args[1]) await page.locator(args[1]).first().screenshot({ path: args[0] });
      else await page.screenshot({ path: args[0], fullPage: options.full });
      result = path.resolve(args[0]);
      break;
    case 'console': break;
    default: throw new Error(`unknown command '${command}' (node browser.mjs help lists them)`);
  }
  // An action that navigates settles here; a page the action closed throws, and the report says so.
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(options.settle);

  const failed = [];
  for (const [kind, expected] of options.expect) {
    if (kind === 'url' && !page.url().includes(expected)) failed.push(`url ${page.url()} does not contain ${expected}`);
    if (kind === 'text' && (await page.getByText(expected).count()) === 0) failed.push(`the page shows no text ${expected}`);
    if (kind === 'visible' && !(await page.locator(expected).first().isVisible())) failed.push(`${expected} is not visible`);
    if (kind === 'hidden' && (await page.locator(expected).first().isVisible())) failed.push(`${expected} is still visible`);
  }
  if (options.acceptDialog != null && accepted.length === 0) failed.push('--accept-dialog: no dialog appeared');
  const clean = Object.values(raised).every((list) => list.length === 0);
  const report = { ok: failed.length === 0, clean, action: [command, ...args].join(' '), url_before: urlBefore, url: page.url(), title: await page.title() };
  if (failed.length) report.expect_failed = failed;
  if (accepted.length) report.dialogs_accepted = accepted;
  if (result !== undefined) report.result = result;
  for (const [kind, list] of Object.entries(raised)) if (list.length) report[kind] = list;
  print(report);
  return failed.length ? 1 : clean ? 0 : 2;
}

// The connection is dropped by exiting, never by browser.close(): the shared window stays as it is.
main().then((code) => process.exit(code), (error) => {
  print({ ok: false, error: String(error.message ?? error).split('\n')[0] });
  process.exit(1);
});
"""),
        new(ChooserSkill, """
---
name: joint-launcher
description: Start a joint session by asking the user what to do - load one of the skills configured in runJointLauncher.json, install the launcher's prerequisites (joint-launcher-install) or troubleshoot them (joint-launcher-doctor) - then do it. The joint-session launcher (runJointLauncher.cs) starts the harness on this skill when no skill is given.
---

# joint-launcher

The user opened a joint session with the launcher (`{{launch}}`, run from this folder): this
terminal plus a shared browser whose DevTools protocol answers on http://127.0.0.1:9222. Any browser tool that can
attach to a running Chromium over CDP (e.g. a Playwright MCP server started with `--cdp-endpoint
http://127.0.0.1:9222`) acts on the window the user watches. The launcher records the session in
`<local app data>/joint-launcher/session.json` (`cdp`, `harness`, `skill`). No skill was named, so ask what to do,
then do it.

## 1. Ask

Read `runJointLauncher.json` in this folder (it may not exist yet: then no skills are configured). Its `Skills` object
maps each configured skill's name to its SKILL.md path (relative to this folder unless absolute). A skill is available
only while that file exists: check each one, do not assume.

Ask the user to choose (with your question tool if you have one, else as a numbered list): one option per available
skill (the description from its SKILL.md frontmatter), then `joint-launcher-install` (install or update the
prerequisites), `joint-launcher-doctor` (check the prerequisites and troubleshoot a launch that went wrong),
`joint-browser` (drive the shared browser with Playwright, or review a page or flow together) and "something else"
(the user says what to work on). Name configured skills whose file is missing
in the question text as "not available", never as options.

## 2. Do it

- A configured skill: read its SKILL.md and follow it from its start; it asks for its own arguments.
- `joint-browser`, `joint-launcher-install` or `joint-launcher-doctor`: skills beside this one in the same skills
  folder; load the chosen one and follow it.
- Something else: do what the user asks, using the shared browser on 127.0.0.1:9222 where it helps.

To skip this question next time, the user passes `--skill <name>` to the launcher (it then becomes `DefaultSkill` in
`runJointLauncher.json`).
"""),
        new(InstallSkill, """
---
name: joint-launcher-install
description: Install or update what the joint-session launcher (runJointLauncher.cs) needs on this machine besides .NET and the harness - a terminal, a Chromium-based browser, and for joint-browser Node.js 20+ with Playwright - asking before each install. The launcher starts the harness on this skill in its own console when the session cannot open yet, with "missing <items>" as the arguments.
---

# joint-launcher-install

The joint-session launcher (run from this folder with `{{launch}}`) opens
a terminal running this harness beside a shared Chromium-based browser. When it started you here with the arguments
`missing <items>`, those items stopped the session from opening (`terminal`, `desktop`, `browser`): start with them.

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

The browser is chosen by `Browser` in `runJointLauncher.json`: `auto` takes the newest Playwright Chromium, else
Google Chrome, else Microsoft Edge or Chromium; a path names one. With Node.js, `npx playwright install chromium`
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
     set `Browser` in `runJointLauncher.json` to Google Chrome.
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
    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
}
