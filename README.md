# Borderless Editor for Unity

Hide the Windows title bar of the Unity Editor and swap the light native menu bar for a dark one, so the whole editor window matches the dark theme.

<!-- TODO: add a before/after screenshot or GIF here, e.g. ![Before and after](Documentation~/before-after.png) -->

> **Windows only. Unity 6.3 (6000.3) or newer.** On macOS/Linux the scripts compile to nothing and the package does nothing.

## Features

- **Hide Title Bar**: removes the Windows caption and frame, and fits the editor to the monitor's work area so the taskbar stays visible.
- **Dark Menu Bar**: a dark menu row above the main toolbar that replaces the native (always light) menu bar. Supports submenus, shortcut hints, checkmarks, disabled items, hover-to-open submenus and keyboard navigation (arrows, Enter, Esc). Items run through `EditorApplication.ExecuteMenuItem`, so every menu, including ones added by your own scripts and packages, works.
- **Window controls**: borderless toggle, minimize and close buttons, placed in the dark menu row or, when the dark menu bar is off, in Unity's main toolbar.
- **Title area**: the empty part of the menu row shows `Project — Scene •` (the dot means unsaved changes). Drag it to move the window, double-click to maximize/restore, right-click for a small window menu.
- Both features are independent and **off by default**. Your choice is stored per user in `EditorPrefs`.

## Installation

### Package Manager (git URL)

1. Open **Window > Package Manager**.
2. Click **+ > Install package from git URL...**
3. Paste:

```
https://github.com/AFreoN/unity-borderless-editor.git
```

To pin a version, append a tag, for example `#1.0.0`.

### Manual

Download or clone this repository and copy it into your project's `Assets/` or `Packages/` folder.

## Usage

Everything lives under **Window > Borderless Editor**:

| Menu item | Shortcut | What it does |
|---|---|---|
| Hide Title Bar | `Ctrl+Shift+F11` | Toggles the borderless window |
| Dark Menu Bar | – | Toggles the dark menu row |

To leave borderless mode with the title bar hidden, use the toggle button in the dark menu row or toolbar, double-click the title area, or press the shortcut.

## How it works

The package talks to the editor's main window through Win32 (`user32.dll`): it strips `WS_CAPTION` / `WS_THICKFRAME`, sizes the window to the monitor work area, and starts native window drags through `WM_NCLBUTTONDOWN`. The dark menu bar detaches the native `HMENU`, subclasses the main window so the menu stays hidden when Unity tries to re-attach it, and draws its own row with UI Toolkit.

## Known limitations

- **It relies on Unity internals.** The dark menu bar uses reflection on non-public editor types (`UnityEditor.MainView`, `UnityEditor.MainToolbarWindow`, `Menu.GetMenuItems`). A future Unity version may change them. If something it needs is missing or throws, the dark menu bar switches itself off for the session, logs a `[Dark Menu Bar]` warning and restores the native menu bar.
- Tested on **Unity 6000.3.7f1** on Windows 11. Other versions and setups (multi-monitor with mixed DPI, unusual taskbar layouts) are not guaranteed.
- Windows 10/11 only. There is no macOS or Linux support.
- The toolbar buttons use the `MainToolbarElement` API, which is why Unity 6.3 is the minimum.
- Installed as an **immutable package**, Unity will not let you edit the scripts in place. Embed the package (copy it into `Assets/` or `Packages/`) if you want to modify it.

## Troubleshooting

If the editor window ends up in a bad state, turn the features off from **Window > Borderless Editor**. If you can't reach the menu, clear the stored settings by running this from any editor script (or removing the package and deleting the keys from the registry under `HKCU\Software\Unity Technologies`):

```csharp
UnityEditor.EditorPrefs.DeleteKey("AFreoN.BorderlessEditor.Enabled");
UnityEditor.EditorPrefs.DeleteKey("AFreoN.BorderlessEditor.DarkMenuBar.Enabled");
```

Found a bug? Please open an issue with your Unity version, Windows version, monitor setup and the console output.

## License

[MIT](LICENSE) © 2026 Sahid Afridi

---

Made by [Sahid Afridi](https://afreon.dev) · [LinkedIn](https://www.linkedin.com/in/afridi-s)
