# Security

MouseFace sees every mouse click on your computer, so its security matters. If you find a problem, please report it privately: open this repository's **Security** tab and choose **Report a vulnerability**. Please don't open a public issue for a security problem.

## What MouseFace can and can't do

- It runs as you, never as administrator, and does not ask Windows for any extra permissions.
- It never goes online: nothing is downloaded or uploaded, and nothing is sent anywhere.
- It watches mice only, through a low-level mouse hook and Raw Input for mice. It installs no keyboard hook; it only checks whether the dimming key is held when the mouse wheel turns.
- It sends input only as the replacements you set in settings.
- You can build it on your own machine from this source, with the C# compiler that ships with Windows. The release download is built by GitHub from the same source.

## Supported versions

Fixes go into the latest release only.
