# NETDock gap analysis (2026-11-14)

Compared against **RocketDock 1.3.5** (its help file and settings) and **Aero Dock** (README and source).
Effort is rough: **S** = an hour or two, **M** = a session, **L** = several sessions.

## Already at or past parity

- Icons: size, zoom, zoom width, zoom duration, opacity, zoom-opaque, icon and separator spacing.
- Position: monitor, edge, centering, edge offset.
- Style: RocketDock skins (original format), built-in panel, platform shelf, labels.
- Items: Icon Settings (library, preview, name, target, start in, arguments, run state, open running, run as admin).
- Stacks with persisted view, sort and columns (RocketDock lost these).
- Portable mode, zip backup and restore, RocketDock import that repairs x86 paths and orphaned stacks.
- Running indicators, click to switch or minimise, launch bounce, drag to reorder, drag off to remove, lock.
- Recycle Bin: drop to recycle, full/empty icon, Empty Recycle Bin.
- Drag and drop onto items: the centre of an icon acts on it (open with the app, copy into a folder or stack, recycle); its edges and the gaps add new items.
- Layering: always on top, normal (comes forward when clicked) or always on bottom, with pop up on mouse-over and a delay.
- Ctrl-drag moves or removes icons even when locked; configurable show/hide hotkey (Ctrl+Alt+R by default), also on the dock menu.

## RocketDock features NETDock is missing

| # | Feature | What RocketDock does | Effort | Priority |
|---|---------|----------------------|--------|----------|
| 1 | ~~**Drop onto an app**~~ (done) | A file dropped on an app icon opens in that app (PNG on Photoshop). NETDock adds it as a new item instead. | S | High |
| 2 | ~~**Drop onto a folder or stack**~~ (done) | Copies the file into that folder, like Explorer. NETDock adds it as a new item instead. | S | High |
| 3 | ~~**Ctrl-drag when locked**~~ (done) | Hold Ctrl to move or remove an item even when icons are locked. | S | Medium |
| 4 | ~~**Show/hide hotkey**~~ (done) | Ctrl+Alt+R hides the dock completely and stops rendering it. | S | Medium |
| 5 | ~~**Layering**~~ (done) | Always on top, Normal, or Always on bottom. NETDock is always on top. | S | Medium |
| 6 | ~~**Pop up on mouse-over + delay**~~ (done) | The dock comes over covering windows when the mouse reaches the edge. | S | Medium |
| 7 | ~~**Auto-hide duration**~~ (done) | The slide speed is configurable. NETDock has only the delay. | S | Low |
| 8 | ~~**Label font and styling**~~ (done) | Font, size, colour, outline colour and opacity, shadow colour and opacity. | M | Medium |
| 9 | ~~**Background opacity**~~ (done) | Skin/panel transparency slider, separate from icon opacity. | S | Medium |
| 10 | ~~**Icon attention effects**~~ (done) | A choice of click animation (bounce, zoom, pulse, ...). Also used when a minimised window flashes for attention. | M | Low |
| 11 | ~~**Icon state dropdown**~~ (done) | Icon Settings can pick the *full* Recycle Bin icon. The model has `IconFull` and import fills it, but the dialog can't edit it yet. | S | Medium |
| 12 | ~~**Special items**~~ (done) | *Add item → Dock Settings* and *Quit* icons. | S | Low |
| 13 | ~~**Minimise windows to the dock**~~ (done, without live previews) | Minimised windows become dock icons with live previews. A big feature that RocketDock was often unstable with. | L | Optional |
| 14 | ~~**"Monitor \*" (span all)**~~ (done) | Treats all monitors as one long edge. | M | Low |
| 15 | ~~**Docklets**~~ (skipped: the only one in use was StackDocklet, which NETDock has built in) | RocketClock and ObjectDock docklets. These are 32-bit native DLLs, so a 64-bit dock can't load them; individual docklets would need rebuilding natively. | L | Low |
| 16 | ~~Languages~~ (strings externalised; no translations yet) | Around 50 translations. | M | Low |

## Aero Dock features worth considering

| # | Feature | Notes | Effort |
|---|---------|-------|--------|
| A | ~~**Live window previews**~~ (done) | Hover a running app to see DWM thumbnails of its windows and click one to switch. Aero Dock's `thumbnails.rs` shows the approach (`DwmRegisterThumbnail`). | M |
| B | ~~**Running apps not pinned**~~ (done) | Show running programs that aren't on the dock (taskbar-style), optionally after a separator. | M |
| C | ~~**Store / UWP apps**~~ (done: running detection by AppUserModelID; launcher apps like Discord matched too) | Pin and launch packaged apps (`shell:AppsFolder\AUMID`). Running detection needs `PKEY_AppUserModel_ID`, because these windows are hosted by ApplicationFrameHost. Launching works today; running dots don't. | M |
| D | ~~**Search overlay**~~ (done: Ctrl+Alt+Space; dock items, all Start/Store apps, recent files) | A key opens a search over installed apps and recent files. | M |
| E | ~~**Per-app volume**~~ (done: scroll an icon in 5 % steps; Mute in its right-click menu, since middle-click starts another copy) | Scroll an icon to change that app's volume, middle-click to mute. | M |
| F | **System corner** | Clock, battery, network, volume tiles. | M |
| G | **Modes** | Switchable sets of pinned apps and window layouts. | L |
| H | Wallpaper accent sync, ambient effects, desktop widgets | Mostly visual polish. | L |

## NETDock's own rough edges

- **Not yet tested with a real mouse:** hover magnification feel, stack flyouts, drag reorder and drag-off removal, the settings windows' controls, and dropping onto the Recycle Bin.
- ~~**No app icon**~~ (done: the NETDock logo is the exe, tray and window icon, and on the About page).
- **Light theme only:** the settings windows don't follow dark mode.
- **Packaging:** no installer or updater, and the exe isn't code-signed, so SmartScreen will warn when it's downloaded. Portable zip is the only distribution so far.
- **Stack flyouts:** no type-ahead or keyboard navigation, no drag-out of files, and the flyout doesn't open on hover (RocketDock's Stacks docklet didn't either).
- **Accessibility:** the dock isn't exposed to screen readers (no UI Automation provider).
- **Start-up:** icon extraction for 38 items happens on every start. A disk cache of decoded icons would make the first frame faster.

## Suggested order

1. Drop onto app / folder / stack (#1, #2), Ctrl-drag when locked (#3), hotkey (#4): small, and they complete RocketDock's everyday behaviour.
2. App icon, Icon Settings state dropdown (#11), background opacity (#9), label styling (#8).
3. Layering and pop-up (#5, #6), Store app running detection (C).
4. Live window previews (A) and running-but-unpinned apps (B): the biggest step past RocketDock.
5. Installer, updater, signing.
