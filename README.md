# Claude Guardian for Windows

A Windows notification-area (system tray) app that shows the Claude Code sessions running on your PC. It is a port of the macOS menu bar app Claude Guardian.

Like the macOS label `⚙ 3  ✋ 1  ☾ 2`, the notification area shows three icons side by side, one per state, each with its color and count:

| Icon | Meaning |
|---|---|
| terracotta | sessions **working**: Claude is thinking or running tools |
| amber | sessions **waiting for you**: Claude needs a permission or has asked you a question |
| grey | sessions **idle**: the turn is over and they are waiting for a new prompt (this also includes "working" sessions with no activity for more than 10 minutes) |

A count of zero is shown as an outline. With no Claude Code sessions running, only the app icon is shown.

To save space, the app can instead be built with a **single icon** that shows the most urgent state (amber if anyone is waiting for you, otherwise terracotta if anyone is working, otherwise grey) with its count. This is a build option, see [Tray icons: three or one](#tray-icons-three-or-one).

Hover over an icon to see all three counts, or click it (left or right button) to see your sessions grouped by app and then by project, each shown with its title:

```
Working: 2 · Waiting for you: 1 · Idle: 1
─────────────
IntelliJ IDEA
📁 shop
●     Migrate to Spring 3 · Waiting for you · now
─────────────
Visual Studio Code
📁 mangiaresg-app
●     Fix OAuth login · Working · 2 min. ago
●     Refactor menu · Idle · 11 min. ago
```

Apps, projects and sessions are sorted by urgency: anything waiting for you comes first, then working, then idle. Clicking a project brings its window to the front. On each session:

- **click**: jump back to the app and window the session was started from
- **Ctrl+click**: open the project folder in File Explorer
- **Shift+click**: copy diagnostics to the clipboard (windows found, which one gets picked), useful if the wrong window opens

It works with sessions started from a terminal (Windows Terminal, the classic console, Git Bash), VS Code, IntelliJ/JetBrains, and with sessions you drive from your phone through Remote Control, since they all run on your PC.

## How it works

1. A Claude Code hook (`claude-activity-hook.exe`) runs on every state change and writes a small file to `%USERPROFILE%\.claude\activity\<session_id>.json` with the state, folder, project root, transcript path, PID, originating app and console window.
2. The app watches that folder, drops sessions whose process no longer exists, and updates the tray icon.

| Claude Code event | State |
|---|---|
| `SessionStart`, `Stop` | idle |
| `UserPromptSubmit`, `PreToolUse`, `PostToolUse` | working |
| `Notification` (permission or question) | waiting for you |
| `Notification` (`idle_prompt`, "waiting for your input") | idle |
| `Notification` (informational: `auth_success`, `agent_completed`, `elicitation_complete`, `quota_*`…) | unchanged |
| `SessionEnd` | file removed |

Claude Code does not run the `Stop` hook when you interrupt a turn (Esc) or when a turn ends with an API error. The app detects both from the session transcript and marks the session as idle.

The hook is a small executable rather than a PowerShell script because it runs on every tool call: PowerShell alone takes about half a second to start. It is registered in Claude Code's *exec form* (`command` plus `args`), so Claude Code starts it directly without going through Git Bash or PowerShell.

### Session titles

The title shown in the menu is, in order of preference:

1. the name set with `/rename` (or `--name`);
2. the title Claude Code generates automatically;
3. the first message you wrote in the session, truncated to 60 characters.

It is read incrementally from the session transcript (only new lines are read). The transcript format is not a documented API, so the parser accepts several field names and falls back to the first prompt.

## Installation

Requirements:

- Windows 10 (1903 or later) or Windows 11. The app and the hook use .NET Framework 4.8, which is part of Windows, so nothing needs to be installed to **run** them.
- To **build** them: the .NET SDK 8 or later (`winget install Microsoft.DotNet.SDK.8`).

PowerShell does not run downloaded scripts by default, so the commands below use `-ExecutionPolicy Bypass`, which applies only to that command.

### From a release (no build needed)

1. Download `ClaudeGuardian-<version>-windows.zip` from the GitHub release.
2. Before extracting it, right-click the zip › Properties › check **Unblock** › OK (or run `Unblock-File .\ClaudeGuardian-<version>-windows.zip`). Otherwise Windows marks every extracted file as downloaded from the internet and SmartScreen asks for confirmation before running the executables, because they are not signed.
3. Extract it, open PowerShell in the extracted folder and continue from [2. Hooks](#2-hooks).

### 1. Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This produces `build\ClaudeGuardian.exe` (the app) and `build\claude-activity-hook.exe` (the hook). You can also open `src\ClaudeGuardian.sln` in Visual Studio or Rider.

To build the single-icon variant, add `-SingleIcon` (see below):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SingleIcon
```

The build contains only the files needed to run: `ClaudeGuardian.exe`, its `ClaudeGuardian.exe.config` (required: it holds the WinForms setting for sharp menus on monitors with different scaling) and `claude-activity-hook.exe`.

### 2. Hooks

```powershell
powershell -ExecutionPolicy Bypass -File .\install-hooks.ps1
```

This copies the hook to `%USERPROFILE%\.claude\hooks\` and registers it in `%USERPROFILE%\.claude\settings.json` (a backup is made next to the file, and existing hooks and settings are kept). `hooks\hooks-settings.json` shows the entries it adds, if you prefer to merge them yourself.

Restart any Claude Code sessions that are already open so they pick up the hooks.

### 3. App

```powershell
powershell -ExecutionPolicy Bypass -File .\install-app.ps1
```

This installs the app in `%LOCALAPPDATA%\Programs\Claude Guardian` (no administrator rights needed), adds it to the Start menu and starts it. You can enable **Open at Login** from the menu.

Windows hides new tray icons in the overflow area (the `^` arrow). To keep them visible, drag the three icons onto the taskbar, or turn on the Claude Guardian entries in Settings › Personalization › Taskbar › Other system tray icons. You can drag them to change their order.

### Tray icons: three or one

Whether the app shows three icons or one is chosen at build time with the `TrayIcons` property in `src\Directory.Build.props`:

```xml
<TrayIcons Condition="'$(TrayIcons)' == ''">three</TrayIcons>
```

| Value | Notification area |
|---|---|
| `three` (default) | One icon per state (working, waiting for you, idle), each with its count, like the macOS label |
| `single` | One icon with the color and count of the most urgent state |

Change the default there, or override it for a single build:

```powershell
.\build.ps1 -SingleIcon                                        # build script
dotnet build src\App\ClaudeGuardian.csproj -p:TrayIcons=single   # dotnet / CI
```

The property defines the `SINGLE_TRAY_ICON` compilation symbol, which `src\App\TrayApp.cs` checks. There is no setting in the app's menu: to switch, rebuild and run `install-app.ps1` again.

### Publishing a release

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Package
```

Besides `build\`, this creates `dist\ClaudeGuardian-<version>-windows.zip` (with `-SingleIcon`, `…-windows-single-icon.zip`). The version comes from `src\Directory.Build.props`. The zip has the same layout as the repository, because the install scripts look for the executables in `build\` next to themselves:

```
ClaudeGuardian-1.0.0-windows\
├── install-hooks.ps1
├── install-app.ps1
├── uninstall.ps1
├── README.md
└── build\
    ├── ClaudeGuardian.exe
    ├── ClaudeGuardian.exe.config
    └── claude-activity-hook.exe
```

Create a release on GitHub (for example with tag `v1.0.0`) and attach the zip. The same build works on x64, x86 and ARM64: the executables are AnyCPU and .NET Framework compiles them for the processor they run on.

### Version

The version is defined in one place, `src\Directory.Build.props`:

```xml
<Version>1.0.0</Version>
<FileVersion>1.0.0.1</FileVersion>
```

To release a new version, change these values and run `build.ps1`. The last number of `FileVersion` is the build number. The menu reads the version from the executable, so it always matches what File Explorer shows in Properties › Details.

### Icon

The icon lives in `icon\` (`AppIcon.ico` with every size from 16 to 256 px, plus a 1024 px PNG) and is embedded in the executable at build time. It is generated by `icon\make-icon.py`: to change colors or shapes, edit the values at the top of the script and run it again (`python make-icon.py`, requires Pillow). Unlike the macOS icon, it has no ivory background shape, because Windows icons have no common base shape and the terminal window stays readable at 16 px this way.

## Jumping back to the originating app

The hook records the app that started the session, the console window Claude Code is attached to, and the **project root** (the first parent folder containing `.idea`, `.vscode` or `.git`). On click, the app tries, in order:

1. **Console window**: for the classic console it is the terminal window itself; for Windows Terminal it is the terminal window hosting the session.
2. **Window by title**: find the app window whose title contains the project name, the session folder, or the session title, and bring it to the front.
3. **Editors**: ask the editor to open the project root, which brings forward the window already showing that project. This is the same mechanism as `code C:\path` or `idea64 C:\path` on the command line.
4. Otherwise, bring the app's top window to the front, or start the app if it is not running.

| App | Precision |
|---|---|
| Windows Terminal | The right window, even with many windows open; not the specific tab |
| Classic console (cmd, PowerShell) | The exact window |
| VS Code, Cursor, Windsurf, VSCodium, Zed, IntelliJ and other JetBrains IDEs, Android Studio | The project window, even with several projects open and sessions started in subfolders |
| Git Bash (mintty), WezTerm, Alacritty, Tabby, Hyper | The window whose title contains the project name, path or session title; otherwise the app |
| Claude desktop app and others | The app |
| Not identified (e.g. inside WSL or `tmux`) | The folder in File Explorer |

If several copies of the same app are installed (e.g. IntelliJ Ultimate and Community), the one that actually started the session is used.

No special permission is needed. Unlike macOS, Windows does not ask for Accessibility or Automation access.

### Limitations

- **Windows Terminal tabs**: Windows Terminal has no API to select a tab, so its window is brought forward but not the specific tab.
- **Two sessions in the same project** in one editor (two integrated-terminal tabs in the same window): the right window opens, but not the specific tab.
- **Elevated windows**: Windows does not let a normal app bring forward a window running as administrator. If you run Claude Code in an elevated terminal, run Claude Guardian elevated too, or use a normal terminal.
- In terminals without a usable console window, the window is found only if its title contains the project name, path or session title. If several windows match ambiguously, the app is activated instead of risking the wrong window.
- A very long command (e.g. a 15-minute build) can show as idle ("No activity") until it finishes, because there is no hook event or transcript write while it runs.

## Notes

- The hooks are installed in your **user** settings, so they apply to every project.
- Claude Code installed inside a WSL distribution is not tracked: it reads the hooks from the Linux home folder, not from `%USERPROFILE%\.claude`.
- Claude Code on the web (cloud) sessions are not shown: they run on Anthropic's servers, not on your PC.
- The macOS SwiftBar plugin has no Windows counterpart and was not ported.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

This quits the app and removes it from login items, the Start menu, `%LOCALAPPDATA%\Programs\Claude Guardian`, `%USERPROFILE%\.claude\activity`, the hook executable, and the hook entries in `settings.json` (a backup is made first).

## Project layout

```
build.ps1              builds the app and the hook into build\ (and, with -Package, the release zip into dist\)
install-hooks.ps1      installs and registers the hook
install-app.ps1        installs and starts the app
uninstall.ps1          removes everything
hooks\                 reference hook entries for settings.json
icon\                  app icon and its generator
src\Shared\            JSON and process helpers used by both executables
src\Hook\              claude-activity-hook.exe (also registers itself with --install / --uninstall)
src\App\               ClaudeGuardian.exe, the tray app
```

| macOS (Swift) | Windows (C#) |
|---|---|
| `hooks/claude-activity-hook.sh` | `src/Hook/Program.cs`, `ProcessTree.cs` |
| `install-hooks.sh` (jq) | `install-hooks.ps1` + `src/Hook/Installer.cs` |
| `ClaudeGuardianApp.swift` (MenuBarExtra) | `src/App/TrayApp.cs` (NotifyIcon) |
| `MenuBarIcon.swift` | `src/App/TrayIconRenderer.cs` (three icons, or one with `TrayIcons=single`) |
| `ActivityMonitor.swift` | `src/App/ActivityMonitor.cs` |
| `Session.swift`, `SessionGrouping.swift` | `src/App/Session.cs`, `SessionGrouping.cs` |
| `TranscriptTitleReader.swift` | `src/App/TranscriptTitleReader.cs` |
| `SessionFocuser.swift` (AppleScript, Accessibility) | `src/App/SessionFocuser.cs`, `WindowTools.cs` (Win32) |
| `AppInfo.swift` | `src/Directory.Build.props` + `src/App/AppInfo.cs` |
| `SMAppService` (Open at Login) | `src/App/LoginItem.cs` (HKCU `Run` key) |

## License

[MIT](LICENSE) © 2026 Fabrizio Caldarelli. You are free to use, modify and redistribute this software, as long as the copyright notice and the license text are kept.
