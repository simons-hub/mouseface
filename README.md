# MouseFace

<img src="assets/logo.png" alt="MouseFace logo: an orange cartoon mouse" width="128" align="right">

Remap your Evoluent mouse's buttons and dim your screens on Windows 10 and 11, without Evoluent's drivers.

- **Remap buttons**: single and double clicks, keyboard shortcuts, click lock, or disable.
- **Dim the screens** by holding Shift and turning the wheel. Off until you turn it on.
- **No driver, no administrator permission, no network.** One small program: download it, or build from the source on your own machine.

MouseFace is not affiliated with or endorsed by Evoluent LLC. Evoluent and VerticalMouse are trademarks of their owners.

## Why

Evoluent's own Mouse Manager installs two filter drivers and two virtual devices. On some machines it mismaps buttons, loses clicks or leaves a button stuck down until it is restarted. MouseFace doesn't touch the driver stack: it watches clicks through Windows' standard input hooks and sends replacement clicks, so on any failure it falls back to a plain mouse.

## Install

First uninstall other mouse software (Evoluent Mouse Manager or X-Mouse Button Control), as two remappers would fight over clicks.

### Download and run

1. Download `MouseFace-Setup.exe` from the [latest release](https://github.com/simons-hub/mouseface/releases/latest).
2. Double-click it. It isn't code-signed yet, so Windows may say "Windows protected your PC": choose **More info**, then **Run anyway**. If it says Smart App Control blocked it, see [If Windows blocks it](#if-windows-blocks-it).
3. Choose **Install**. It's set up just for you on this PC and needs no administrator permission. (Don't use **Run as administrator**: with User Account Control on, as Windows has it by default, MouseFace refuses to run that way.)
4. The first time, the Settings window opens: choose what each button does, then **Save**.

Each release lists the file's SHA-256 and carries GitHub's build attestation, which shows it was built from this repository's source.

### If Windows blocks it

Smart App Control, which is on by default on some new Windows 11 PCs, runs only apps that Microsoft already knows or that are code-signed, and it has no **Run anyway**. MouseFace isn't signed yet, so on those PCs Windows blocks the download, or setup says Windows stopped MouseFace from starting. Building it yourself doesn't get round it, as the program it builds isn't signed either.

To check, open Windows Security > App & browser control > Smart App Control settings. To use MouseFace on that PC, turn Smart App Control off, then run setup again. That lowers the PC's protection against unknown apps. On some PCs it can't be turned back on without resetting Windows; where it can, expect it to block MouseFace again.

### Or build it yourself

1. On this page, choose **Code > Download ZIP**.
2. Right-click the ZIP and choose **Extract All**.
3. Double-click `install.cmd` in the folder that appears. If Windows asks whether to run it, choose **Run**.
4. It builds MouseFace with the C# compiler that comes with Windows, tests it and installs it. Then the Settings window opens, as above.

It downloads nothing and needs nothing else.

### After installing

MouseFace runs in the background, with its icon in the notification area by the clock (click **^** there if it's hidden). It starts by itself each time you sign in. To start it by hand, open the Start menu and choose **MouseFace**; to stop it, right-click the icon and choose **Quit MouseFace**.

### Update or uninstall

**To update,** run the newer `MouseFace-Setup.exe` or `install.cmd`; your settings are kept. Setup never replaces a newer version with an older one: to go back, uninstall first.

**To uninstall,** find MouseFace in Settings > Apps > Installed apps (Apps & features on Windows 10) and choose **Uninstall**. Running `MouseFace-Setup.exe` again, or `install.cmd -Uninstall`, also uninstalls it. Your settings stay in `%LOCALAPPDATA%\MouseFace`; delete that folder to remove them too. The Installed apps entry is the only thing MouseFace writes to the registry, and it is under your account.

### From a script or an AI agent

The steps above open windows that wait for a person. To install with no prompts, write `%LOCALAPPDATA%\MouseFace\settings.ini` first (the format is under [settings.ini](#settingsini)), so the Settings window has nothing to set up, then run one of these. Each prints what happened and exits with 0 when it worked.

```powershell
.\MouseFace-Setup.exe --install | Out-String; $LASTEXITCODE    # the download; --uninstall removes it
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Quiet    # from source; -Uninstall removes it
```

Keep the `| Out-String` (or run it through `cmd /c`): MouseFace-Setup.exe is a Windows program, and without it PowerShell neither waits for it nor shows what it printed.

Use `install.ps1` directly rather than `install.cmd`, which waits for a key press at the end. If other mouse software is running, both stop and say so.

## Settings

Double-click the tray icon, or right-click it and choose **Settings...**. Pick the mouse, what each button does and the dimming, then **Save**: changes apply at once. Pressing a button on your mouse lights up its row, so you can see which is which. Nothing is remapped, and dimming is off, until you save.

Each button can be:

| Choice | What it does |
|---|---|
| Left click, Right click, Middle click, Back button, Forward button | that mouse button, held for as long as you hold yours |
| Double click | a double click. Holding the button and moving selects word by word |
| Click lock | press once to hold the left button down (for long drags), press again to let go |
| Shortcut | keys joined by `+`, like `Ctrl+C` or `Alt+F4`, pressed and let go as you press the button. A single key, like `F13` or `BrowserBack`, and `Ctrl`, `Shift`, `Alt` and `Win` on their own or together, are held for as long as you hold the button (for push-to-talk, Ctrl+click and the like). Windows keeps shortcuts with the Windows key for itself, so the window can't record them; type those, like `Win+D`, in settings.ini |
| Disabled | nothing |

Key names: letters, digits, `F1` to `F24`, `Ctrl`, `Shift`, `Alt`, `Win`, `Enter`, `Esc`, `Tab`, `Space`, `Backspace`, `Delete`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, `Up`, `Down`, `Left`, `Right`, `PrintScreen`, `CapsLock`, `Menu`, `Plus`, `Minus`, `Comma`, `Period`, `Semicolon`, `Slash`, `Backslash`, `Quote`, `Backquote`, `OpenBracket`, `CloseBracket`, `VolumeUp`, `VolumeDown`, `Mute`, `PlayPause`, `NextTrack`, `PrevTrack`, `MediaStop`, `BrowserBack`, `BrowserForward`, `BrowserRefresh`, `BrowserHome`.

### settings.ini

The Settings window saves to `%LOCALAPPDATA%\MouseFace\settings.ini`, which you can also edit by hand (**Edit settings.ini** in the window). Saved changes apply at once. If a line has a mistake, a message names it and the previous settings stay in use.

```ini
[Mouse]
Match = Evoluent          ; any Evoluent mouse; or Any for every mouse with an ID; or vendor:product like 1A7C:0191

[Buttons]                 ; Left, Right, Middle, Back, Forward, Double, ClickLock, Keys Ctrl+C or None
Left    = Left
Middle  = Right
Right   = Double
Forward = None
Back    = Keys Alt+Left

[Double]
WholeName = yes           ; a Double click selects the whole name

[Dimming]
Key   = Shift             ; Shift, Shift+Alt, Shift+Win or Off
Step  = 3                 ; percent per wheel notch, 1 to 20
Popup = yes
```

## The tray menu

- **Pause:** the mouse works normally and the screens go to full brightness until you choose **Resume**. Restarting resumes it.
- **Reset brightness:** back to 100%.
- **Settings...:** also opens with a double-click on the icon.
- **About MouseFace:** the version, and a link to see whether there is a newer one. MouseFace never checks by itself, since it never goes online.
- **Quit MouseFace.**

## Which mice

| Model | How it is recognised |
|---|---|
| VerticalMouse 4 Right, Left and Small, VerticalMouse C and D | Evoluent's vendor ID, over a cable or a USB receiver |
| VerticalMouse 4 Bluetooth and Mac models | Evoluent's vendor ID in either Bluetooth format, if Windows pairs the mouse (Evoluent lists these models as Mac-only) |
| Other mice | Pick it under **Mouse** in Settings, or `Match = vendor:product`. Its five standard buttons should work, but only Evoluent mice have been tried |

**All mice** in Settings (`Match = Any`) means every mouse that reports an ID. Most laptop touchpads report none, so they are left alone.

Tested primarily on the wired VerticalMouse 4 Right. Results from other models are welcome: please open an issue with your mouse details.

The VerticalMouse 4 Right's buttons without a driver:

| Button | Name in `[Buttons]` |
|---|---|
| Top finger | `Left` |
| Middle finger | `Middle` |
| Bottom finger | `Right` |
| Wheel click | `Forward` |
| Upper thumb | `Back` |
| Lower thumb | none: it only works with Evoluent's driver |
| Pointer speed button | not needed: the mouse handles it itself, so it keeps working |

MouseFace is Windows-only. On a Mac, Karabiner-Elements (free) and SteerMouse can remap mouse buttons.

## Limits

- **Administrator windows** (Task Manager, installers): while one is in front, your buttons do their normal clicks, because Windows keeps ordinary programs out of those windows. A remapped button held down onto one and let go there is let go once you're back in an ordinary window.
- **If MouseFace is force-closed** (End task), or a UAC prompt or the lock screen comes up, while you hold a remapped button, that button can stay down. Press it once to release it.
- **Tools that share one mouse between PCs** (Mouse Without Borders, Barrier, Input Leap): while the pointer is on the other PC, MouseFace may still remap and dim this one. Pause it from the tray while you work over there.
- **Games that read the mouse directly** (through Raw Input) see your physical button as well as its replacement.

## How it works

- Windows shows MouseFace each click before any program gets it. If the click came from your chosen mouse and you've remapped that button, MouseFace sends your choice instead. Every other click goes through untouched.
- Dimming lays a dark, see-through layer over each screen. It doesn't change the backlight, and screenshots leave it out (on Windows 10 version 2004 and later).
- If MouseFace stops or crashes, your mouse goes back to its normal clicks.

## Building and testing

`install.ps1 -Test` builds MouseFace and runs its tests without installing anything; `-Trial` also runs it, without adding the sign-in shortcut.

## License

MIT. See [LICENSE](LICENSE).
