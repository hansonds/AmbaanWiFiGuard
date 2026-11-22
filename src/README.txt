AMBAAN - WI-FI GUARD FOR WINDOWS  (by ShiftLogic Labs, built with Claude)
=========================================================================

WHAT'S NEW (latest update)
- Settings window: click the gear icon on the widget (or right-click the tray
  icon > Settings...). Everything below is changed there, no file editing.
- Starts with Windows: Ambaan opens by itself when you sign in. Turn it off in
  Settings > "Start Ambaan when Windows starts".
- Your own speed limits: slow alert below X Mbps (default 10) and good-speed
  alert at Y Mbps (default 50). Between the two, Ambaan stays quiet.
- Good-speed alert: once a day, every time, or off.
- Speed check every 15 / 30 / 60 / 120 minutes (default 30).
- New character: Dhamu, with his own voice and animation for all 3 alerts.
  Pick him in Settings > Alert character > Dhamu (Preview, then Save).
  His files are in assets\dhamu*.gif / dhamu-*.mp3. Swap in your own GIF and
  mp3 with the same names to make your own character.
- Voice on/off switch.
- Fixed: the X button closes the app. Demo test button removed.

START
1. Unzip this folder anywhere (for example Documents).
2. Double-click Start-Floating-Widget.bat  -> Ambaan's widget appears in the screen corner.
3. Double-click Run-WiFi-Analyzer.bat      -> full test + HTML report with history charts.

Windows may show "Windows protected your PC" the first time, because these are
unsigned scripts. Click "More info" -> "Run anyway". Read the .ps1 files first if
you want to check exactly what they do.

WHAT AMBAAN DOES
- New device alert: when a device you have not named joins your Wi-Fi, Ambaan
  pops up and shouts. Name your own devices so only strangers trigger it.
- Slow Wi-Fi alert: if speed stays below your slow limit (default 10 Mbps),
  he tells you. Good speed at or above your good limit: he tells you that too.
- Corner widget: signal, live download/upload of THIS PC, router and internet
  ping, number of devices online, DNS Shield status.
- Ctrl+Alt+W shows / hides the widget. The X button closes the app.
  Gear icon = Settings. Right-click the tray icon for more options.
- DNS Shield: checks whether your DNS blocks known scam and malware sites.
  "Fix it"/"Change" asks for administrator permission and changes THIS PC only.
- Full test: download/upload speed, latency, signal and a pass/info table, plus
  24-hour / 7-day / 30-day speed history (each run is one point on the chart).

LIMITS (be honest with yourself)
- The device list comes from this PC's own scan (ping + ARP). Sleeping phones
  can be missing. Your router's app or admin page has the authoritative list.
- This app cannot block devices. Only your router can (router app or admin page).
- Live traffic = this PC only, not the whole home network.

PRIVACY
- Everything stays on your PC, in a MonitorData folder created on first run.
- Outside connections: Cloudflare speed test, pings to 1.1.1.1 / 8.8.8.8 and
  DNS test lookups of example.com. Nothing is uploaded anywhere.
- Before sharing screenshots, blur IP addresses and MAC addresses.

Guide and full build prompt: https://shiftlogiclabs.in/docs/ambaan-wifi-guard
