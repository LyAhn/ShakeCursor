# ShakeCursor

Brings the macOS "shake to enlarge" pointer feature to Windows.

Windows has no built-in way to enlarge the pointer on demand. ShakeCursor
watches the mouse and, while you shake it, swaps the system cursors for larger
ones and shrinks them back when you stop. These are real system cursors, so
Windows draws them above everything (taskbar, Alt+Tab, UAC prompts) with no
overlay window and no flicker. The arrow is a vector shape redrawn at the exact
size each frame, so it stays crisp at any size.

![ShakeCursor settings, with the live preview mid-shake](docs/settings.png)

## How it works

1. **`CursorSwapper`** renders the arrow with GDI+ at the current size, turns it
   into a cursor (`CreateIconIndirect`) and installs it into the `OCR_NORMAL`
   slot with `SetSystemCursor`. The other pointer shapes (I-beam, hand, resize,
   crosshair, move, "not allowed", help) are loaded from your own cursor-scheme
   files at the matching size (those files carry crisp images up to 128 px) and
   cached by a background thread. System defaults are restored via
   `SystemParametersInfo(SPI_SETCURSORS)` on every exit path (tray exit,
   process exit, unhandled exception) and once at startup, in case a previous
   run was killed.
2. **`Watchdog`** is the same executable started as `ShakeCursor --watchdog <pid>`.
   It waits for the main process; if that dies without signalling a clean exit
   (Task Manager, crash), it restores the cursors. It does nothing if a new
   instance is already running.
3. **`PointerWatcher`** polls the pointer position at ~600 Hz (1.5 ms ticks) on a
   dedicated above-normal-priority thread and feeds `ShakeDetector`. It opts the
   process out of Windows 11's timer-resolution throttling, which would
   otherwise drop a window-less tray app to ~64 Hz.
4. **`ShakeDetector`** accumulates intensity on high-speed direction reversals
   (it takes a chain of at least three in quick succession, so a flick and a
   correction don't count), decays it exponentially, and maps it to a target
   scale. The scale is
   exponentially smoothed (fast grow, slower shrink) for the fluid macOS feel.
5. **`Settings/` (WinUI 3 app)** is a separate executable, `ShakeCursor.Settings.exe`,
   built with the Windows App SDK: Mica backdrop, a `NavigationView` sidebar,
   Windows Settings-style cards and the native `ColorPicker`. It edits
   `settings.json`; the tray app watches that file and applies changes live, so
   the settings file is their main link. The one other channel is update
   status: the tray app owns the updater and publishes its status to a small
   JSON file, and the settings window asks it to check or restart through named
   events. The preview runs its own `ShakeDetector`.
6. **`Updater`** (in the tray app) checks GitHub Releases and downloads updates
   with Velopack. Copies that can't update themselves (the portable zip, a dev
   build) just point at the newest release page.

## Download

Grab `ShakeCursor-win-Setup.exe` from
[Releases](https://github.com/LyAhn/ShakeCursor/releases) and run it. It installs
per user (no admin prompt) into `%LOCALAPPDATA%\ShakeCursor`, adds a Start menu
shortcut (no desktop shortcut), is self-contained (no runtime install) and needs
Windows 10 1809 or later on x64. The build is unsigned, so SmartScreen may warn
on first run.

To install somewhere else, run `ShakeCursor-win-Setup.exe --installto <folder>`.

An installed copy checks for updates in the background, downloads them, and
offers a **Restart to update** item in the tray menu (plus a balloon tip). You
can also use **Check for updates** there. A portable
`ShakeCursor-win-Portable.zip` is attached to each release too; it does not
update itself. `.sha256` files are published for both downloads.

Releases are cut by pushing a version tag (`git tag -m v1.0.2 v1.0.2 && git push origin v1.0.2`);
the **Release** workflow builds the installer and update packages with
[Velopack](https://velopack.io) and publishes them.

## Building from source

```
dotnet run --project ShakeCursor
```

Building needs the .NET 10 SDK and the Windows App Runtime 1.8. A tray icon appears ("ShakeCursor"). Shake your mouse
vigorously — the cursor grows, then settles back. Open **Settings...** from the
tray menu; exit (and restore your cursor) from the same menu.
`ShakeCursor --settings` opens the settings window on launch. A normal build
of `ShakeCursor.csproj` also builds the settings app into `Settings\` next to
the main exe.

## Settings

A sidebar switches between pages; the preview card at the top is always
visible and animates when you shake, using the values you are editing.
Changes apply live and are saved to `%APPDATA%\ShakeCursor\settings.json`
(the settings app writes it, the tray app reloads it).

| Page | Setting | Range |
|---|---|---|
| Size | At rest | 0.5–2.0× |
| Size | When shaking | 1.5–6.0× |
| Shake | Sensitivity | 0.5–2.0× |
| Shake | Hold time | 0.25–3.0 s |
| Appearance | Fill, Outline | any colour (native colour picker, `#RRGGBB`) |
| Appearance | Shadow | on / off |
| Apps | Excluded apps | process names (pick a running app or browse for an `.exe`) |
| General | Start with Windows | on / off (the tray app applies it to the per-user Run registry key) |
| General | Pause in fullscreen apps | on / off (on by default) |
| General | Pause hotkey | on / off, and the shortcut itself (click it, then press the new keys; default Ctrl+Alt+Shift+P). Needs Ctrl, Alt or Shift plus a letter, number or F-key |

**Pausing:** **Pause ShakeCursor** in the tray menu (or the hotkey) hands the real
cursor back until you resume. ShakeCursor also pauses by itself while a
fullscreen app or game is in front, if that setting is on.

**Excluded apps:** while an excluded app is in the foreground, ShakeCursor
hands the real system cursors back; it takes over again
when you switch away. Matching is by process name, case-insensitive.

## Tuning

Feel constants that are not exposed in the UI live at the top of
`ShakeDetector.cs`:

| Constant | Effect |
|---|---|
| `MinShakeSpeed` | How fast a swing must be for a reversal to count as a shake (divided by the sensitivity setting) |
| `MinChain` / `ChainGap` | How many quick reversals in a row, and how close together, before any of them count |
| `IntensityHoldTau` / `IntensityIdleTau` | How quickly intensity decays while moving / at rest |
| `IntensityStart` / `IntensityFull` | Shake intensity → size ramp |
| `GrowTau` / `ShrinkTau` | Animation speed of growing / shrinking |

## Known limitations

- **Which cursors grow:** the arrow plus the I-beam, hand, crosshair, resize,
  move, "not allowed" and help shapes. The animated busy and wait cursors stay
  at their normal size, and so do cursors an app draws itself (many games and
  some apps).
- **The other shapes keep your scheme's look.** Only the arrow uses the fill,
  outline and shadow settings. Their images are only sharp up to 128 px, so
  they stop growing there even when the arrow can go larger.
- **Cached class cursors:** a few apps register an arrow handle in their window
  class before the swap; those windows keep showing the real arrow.
- **"End process tree"** in Task Manager also kills the watchdog. Ending only
  the main process is covered. If a cursor is stuck at an enlarged size, change
  your pointer scheme in Windows settings, or re-run ShakeCursor and exit it
  cleanly.
- **Mixed-DPI monitors** use the system DPI for sizing, so the cursor may be
  the wrong size on a monitor with a different scale.

## Roadmap

- Per-monitor DPI sizing.
- Custom cursor shapes and styles beyond the arrow.
- Code signing, so SmartScreen stops warning on first run.
- A `winget` package.

## Licence

[MIT](LICENSE)
