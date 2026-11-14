# NETDock

A RocketDock-style dock for Windows. It is a single native .NET 11 AOT executable (about 4 MB) with no runtime, WebView or browser engine. It draws with Direct2D into a per-pixel-alpha layered window.

## Build

```
dotnet test NETDock.Tests
dotnet publish NETDock.App -c Release -o out/NETDock
```

## Layout

| Project | What |
| --- | --- |
| `NETDock.Core` | Settings model + JSON (source-generated), portable data location, path tokens, RocketDock importer, magnification maths. No Win32 dependencies; covered by tests. |
| `NETDock.App` | The dock: raw-vtable Direct2D/DirectWrite (`Native/Direct2D.cs`), Shell COM (`Native/Shell.cs`), dock window, stack flyout, menus, properties dialog. |
| `NETDock.Tests` | xunit tests for Core. |

## RocketDock bugs this fixes

1. **Stacks lose their folder/icon on restart.** RocketDock saves a stack's settings in `Docklets\Settings.ini` as `[DockletN]`, where N is the icon's position at the time. Moving or inserting icons orphans that section. NETDock stores `StackOptions` (folder, view, sort, columns) on the item itself. The importer also recovers orphaned sections.
2. **Not portable.** If a `portable` file or a `Data` folder sits next to `NETDock.exe`, everything lives in `.\Data`; otherwise it lives in `%APPDATA%\NETDock`. Paths are stored with tokens (`%ProgramFiles%`, `%USERPROFILE%`, `%NETDOCK%` …). Custom icons are copied into `Data\icons`. *Settings → Export backup* writes one zip that *Import backup* restores on another PC.
3. **Start In flips to Program Files (x86).** RocketDock is 32-bit, so WOW64 redirects its paths. NETDock is x64-only. It tokenizes on whole path segments, keeps `.lnk` files as `.lnk` (so the shortcut's own Start In applies), and never rewrites a stored path. On import, it repairs paths that RocketDock already redirected.

## Usage

- Drag files, shortcuts or folders onto the dock to add them. A folder becomes a stack.
- Drag icons to reorder them. Drag one off the dock to remove it (*Options → Lock icons* prevents this).
- Click a running app to switch to it; click it again to minimise it. Shift+click or middle-click starts a new copy. Ctrl+Shift+click runs it as administrator.
- Right-click for everything else: Properties, Change icon, stack view/sort/columns, position, sizes and Settings.
- `NETDock.exe --import-rocketdock` imports your RocketDock setup without asking.

## Credits

Ported in spirit from [Aero Dock](https://github.com/TheAgencyMGE/aero-dock) (MIT, © 2026 Ryan Panda). Its platform layer (app discovery, Shell icon extraction, running-window tracking) informed the Win32 code here. See `THIRD-PARTY-NOTICES.md`.
