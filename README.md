# Ambaan WiFi Guard

A small Windows Wi-Fi guard with an animated character: he tells you when a **new device joins your Wi-Fi**, when the **internet gets slow**, and when the **speed is good again**. It also has a corner widget, a DNS scam-filter check, and a full Wi-Fi test with history charts.

## Download

Go to **Releases** and pick one:

| File | What it is |
|---|---|
| `AmbaanWiFiGuard-Setup-x.y.z.exe` | Installer. No admin needed. Adds Start menu shortcuts. |
| `AmbaanWiFiGuard-Portable-x.y.z.exe` | One EXE, no install. Saves its data in an `AmbaanWiFiGuard-data` folder next to it. |
| `AmbaanWiFiGuard-Scripts-x.y.z.zip` | The plain PowerShell scripts. Run `Start-Floating-Widget.bat`. |

The EXEs are not code-signed, so Windows may say "Windows protected your PC". Click **More info > Run anyway**.

## Using it

- **Ctrl+Alt+W** shows or hides the widget. The gear icon opens Settings.
- Name your own devices so only strangers trigger the alert.
- Pick the alert character in Settings: **Ambaan** or **Dhamu**.
- Start menu > **Full Wi-Fi test** (or `AmbaanWiFiGuard.exe --analyzer`) runs the full test and opens an HTML report.

## Where data is kept

- Installed: `%LOCALAPPDATA%\AmbaanWiFiGuard`
- Portable: the `AmbaanWiFiGuard-data` folder next to the EXE
- Everything stays on your PC. Outside connections: the Cloudflare speed test, pings to 1.1.1.1 / 8.8.8.8, and DNS test lookups.

## Limits

- The device list comes from this PC's own network scan. Sleeping phones can be missing. Your router's app has the full list.
- The app can't block devices. Only your router can.

## Building

GitHub Actions builds everything (`.github/workflows/build.yml`):

- **Actions > Build EXE > Run workflow**, type a version like `1.0.1`: builds the Setup EXE, Portable EXE and ZIP and publishes a Release.
- Same, with the version left empty: test build, files under the run's Artifacts.
- Pushing a tag like `v1.0.1` also publishes a Release.

To rename the app, change `APP_NAME` and `APP_ID` at the top of the workflow.

Layout: `src/` = the app (PowerShell + assets), `launcher/` = the small C# EXE that starts it, `installer/` = Inno Setup script.

Made by [Phoenicx](https://phoenicx.org).
