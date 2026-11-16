# Changelog

## 1.0.0

First public release.

- Remaps only the chosen mouse: each click is matched to the device Windows names for it, so a touchpad or a second mouse is never remapped.
- Recognises Evoluent mice over USB, USB receivers and both Bluetooth formats. Another mouse can be picked in Settings; its standard buttons should work, but only Evoluent mice have been tried.
- A Settings window: the mouse, each button's action, whole-name selection and dimming. Pressing a mouse button lights up its row. It saves to `settings.ini`, changing only the lines that changed, and applies at once.
- Actions: Left, Right and Middle click, Back and Forward, Double click (with whole-name selection), Click lock, keyboard shortcuts, and Disabled.
- Screen dimming with Shift and the wheel, off until turned on. It shows only while the chosen mouse is attached and MouseFace is not paused.
- `MouseFace-Setup.exe` installs, updates and uninstalls itself for the current user, with no admin rights and nothing in the registry. Or build it from source with `install.cmd`.
- Tray menu: Pause, Reset brightness, Settings, About, Quit.
- No polling and no network: it reacts to Windows' notifications only, and never goes online.
- Fails to a plain mouse: a crash lets go of every held button before exiting, and an error in click handling passes the click through unchanged.
- Won't run beside Evoluent Mouse Manager or X-Mouse Button Control.
