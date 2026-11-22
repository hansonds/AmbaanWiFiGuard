# Ambaan WiFi Guard

A Windows Wi-Fi guard with an animated character. Ambaan (or Dhamu) pops up and **shouts when a new device joins your Wi-Fi**, tells you when the **internet gets slow** or **goes down**, and cheers when the speed is good. A glassy corner widget shows your Wi-Fi at a glance.

## Download

Go to **[Releases](https://github.com/hansonds/AmbaanWiFiGuard/releases/latest)** and pick one:

| File | What it is |
|---|---|
| `AmbaanWiFiGuard-Setup-x.y.z.exe` | Installer. No admin needed. Start menu shortcuts, starts with Windows. |
| `AmbaanWiFiGuard-Portable-x.y.z.exe` | One EXE, no install. Saves its data in an `AmbaanWiFiGuard-data` folder next to it. |

Needs Windows 10 or 11. The EXE is not code-signed, so Windows may say "Windows protected your PC": click **More info > Run anyway**.

## Features

**Alerts with an animated character** (Ambaan or Dhamu, with voice and lip-sync)
- New device on your Wi-Fi: shows its name, maker (Samsung, Apple, Xiaomi...), IP and MAC. "It's mine" to name it, "Block..." for router steps.
- Unknown device came back. Optional: alert for your named devices too.
- Slow Wi-Fi (below your limit, confirmed twice) with the likely reason: weak signal, busy router, or slow internet line.
- Good speed (above your limit): once a day, every time, or off.
- Internet down / back notices, with the cause (Wi-Fi, router, or internet provider).
- Quiet hours (no voice at night) and a volume control.

**Glass widget** (Ctrl+Alt+W to show/hide, drag it to any corner)
- Signal gauge, network name, band, channel, Wi-Fi generation, link speed
- Live download/upload of this PC, router and internet ping
- Expanded: live traffic graph, speed, data used today, uptime today, outages today, DNS Shield, device list

**Devices**
- Spots phones the moment they join (DHCP), plus ARP and a 20-second network sweep
- Device maker names from the MAC address, and the name the device announces
- Name, forget or block any device

**History and tools**
- Speed history chart (24 hours / 7 days / 30 days)
- Outage log with uptime % (today, 7 days, 30 days)
- Data usage per day and per month
- Best Wi-Fi channel tip: scans nearby networks and tells you which channel to set on your router
- Alert log

**Full Wi-Fi test**: speed (download + upload), ping, packet loss, DNS, DNS safety, route, nearby networks, a bottleneck guess and an HTML report. Repairs on request: clear DNS cache, renew IP, restart Wi-Fi.

**DNS Shield**: checks whether your DNS blocks scam and malware sites, finds dead or mistyped DNS servers, and switches this PC to Cloudflare's scam or family filter in one click.

## Make your own character

Put files with these names in the `assets` folder inside the data folder (Settings > About > Open data folder):
`new-device.gif`, `new-device.mp3`, `slow-wifi.mp3`, `fast-wifi.mp3` (Ambaan) or `dhamu.gif`, `dhamu-new-device.mp3`... (Dhamu).

## Privacy

Everything stays on your PC. Outside connections: the Cloudflare speed test, pings to 1.1.1.1 / 8.8.8.8, DNS test lookups, and the update check when you press it.

## Limits

- The device list comes from this PC's own view of the network. Sleeping phones can be missing; your router's app has the full list.
- The app can't block devices. Only your router can.
- Windows 11 hides the Wi-Fi name and nearby networks from apps until Location is on for desktop apps. The widget shows a button to turn it on.

## Building

C# / WPF on .NET Framework 4.8 (`app/`). GitHub Actions builds the EXEs: **Actions > Build EXE > Run workflow**, type a version like `2.0.1` to publish a Release (leave it empty for a test build). Each build also starts the app on Windows, takes screenshots (branch `ci-screenshots`) and checks its log.

Made by [Phoenicx](https://phoenicx.org).
