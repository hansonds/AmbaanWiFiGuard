using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ambaan
{
    public static class Dialogs
    {
        static Engine E => Engine.I;

        /// <summary>Name / forget / block one device.</summary>
        public static void Device(string mac)
        {
            var d = E.Book.Row(mac);
            StackPanel body;
            var w = Ui.Dialog("Name this device", 420, out body);
            body.Children.Add(Ui.Text(d.Ip + "    " + d.Mac, 12, "#A3ADBD"));
            body.Children.Add(Ui.Text(E.Book.Hint(mac), 12, "#A3ADBD", false, true));
            body.Children.Add(Ui.Label("Name"));
            ((TextBlock)body.Children[body.Children.Count - 1]).Margin = new Thickness(0, 12, 0, 4);
            var box = Ui.Input(d.Named ? d.Name : (d.HostName ?? (d.Vendor != null ? d.Vendor + " " : "")));
            body.Children.Add(box);
            body.Children.Add(Ui.Text("Tip: name everything you own. Only strangers will trigger the alert.", 11, "#A3ADBD", false, true));
            ((TextBlock)body.Children[body.Children.Count - 1]).Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Save", "#80268A5B", () => { if (!string.IsNullOrWhiteSpace(box.Text)) E.Book.Rename(mac, box.Text); w.Close(); }),
                Ui.Btn("Clear name", "#1FFFFFFF", () => { E.Book.Rename(mac, null); w.Close(); }),
                Ui.Btn("Forget", "#80B5444B", () => { E.Book.Forget(mac, E.Watch); w.Close(); })));
            var watch = Ui.Check("Tell me when this device arrives or leaves", E.Book.Watched.Contains(mac));
            watch.Checked += (s, e) => E.Book.SetWatched(mac, true);
            watch.Unchecked += (s, e) => E.Book.SetWatched(mac, false);
            watch.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(watch);
            var wakeMsg = Ui.Text("", 11.5, "#5FD39A");
            var row2 = Ui.Buttons(Ui.Btn("Wake up (WoL)", "#1FFFFFFF", () => wakeMsg.Text = NetUtil.WakeOnLan(mac, E.Primary) ? "Wake signal sent. Works only for PCs/TVs with Wake-on-LAN turned on." : "Could not send the wake signal."),
                                  Ui.Btn("Block...", "#80B5444B", () => BlockHelp(mac)), Ui.Btn("Cancel", "#1FFFFFFF", () => w.Close()));
            row2.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(row2);
            body.Children.Add(wakeMsg);

            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            box.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter && !string.IsNullOrWhiteSpace(box.Text)) { E.Book.Rename(mac, box.Text); w.Close(); } };
            w.ShowDialog();
        }

        public static void BlockHelp(string mac)
        {
            try { Clipboard.SetText(mac ?? ""); } catch { }
            var d = E.Book.Row(mac ?? "");
            Ui.Info("Block this device",
                "MAC address copied:  " + mac + "\n\n" +
                "To block it:\n" +
                "1. Open your router's app or admin page" + (E.Primary != null ? " (http://" + E.Primary.Gateway + ")" : "") + "\n" +
                "2. Go to Connected devices (or Clients / Device list)\n" +
                "3. Find the device with this MAC (or IP " + d.Ip + ") and tap Block\n\n" +
                "Not sure which one it is? Change the Wi-Fi password there: every device that is not yours gets disconnected.\n\n" +
                "This PC cannot remove devices from Wi-Fi. Only the router can.");
        }

        /// <summary>DNS Guard (SilentDNS inside Ambaan): presets, custom IPv4/IPv6, encrypted DNS, keep-it-set, benchmark.</summary>
        public static void DnsPanel()
        {
            var c = E.Cfg;
            StackPanel body;
            var w = Ui.Dialog("DNS", 540, out body);
            var live = DnsGuard.Live();
            var dn = E.Dns;
            var now = new StackPanel();
            now.Children.Add(Ui.Label("Right now"));
            now.Children.Add(Ui.Text(live[0] + "   IPv4: " + live[1], 12.5, null, true, true));
            now.Children.Add(Ui.Text("IPv6: " + live[2], 12, "#A3ADBD", false, true));
            if (dn != null) now.Children.Add(Ui.Text(dn.Headline, 12, dn.Status == "Good" ? "#5FD39A" : "#F5C26B", false, true));
            body.Children.Add(Ui.Card(now));

            var guard = Ui.Check("DNS Guard: keep this DNS set (puts it back after Wi-Fi changes, cables, sleep)", c.DnsGuard);
            guard.FontWeight = FontWeights.SemiBold; guard.Margin = new Thickness(0, 12, 0, 4);
            body.Children.Add(guard);

            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var radios = new List<RadioButton>();
            var p4 = Ui.Input(c.DnsP4, 150); var s4 = Ui.Input(c.DnsS4, 150); var p6 = Ui.Input(c.DnsP6, 230); var s6 = Ui.Input(c.DnsS6, 230);
            bool filling = false;
            Action<DnsPreset> fill = pr => { filling = true; p4.Text = pr.P4; s4.Text = pr.S4; p6.Text = pr.P6; s6.Text = pr.S6; filling = false; };
            foreach (var pr in DnsGuard.Presets)
            {
                var pp = pr;
                var rb = new RadioButton { GroupName = "dns", Tag = pr.Name, IsChecked = c.DnsPreset == pr.Name, Foreground = Ui.B("#F2F5F9"), Margin = new Thickness(0, 3, 0, 0) };
                var t = new TextBlock(); t.Inlines.Add(new System.Windows.Documents.Run(pr.Name) { FontWeight = FontWeights.SemiBold });
                t.Inlines.Add(new System.Windows.Documents.Run("   " + pr.Note + "  ·  " + pr.P4 + ", " + pr.S4) { Foreground = Ui.B("#A3ADBD") });
                rb.Content = t;
                rb.Checked += (s, e) => fill(pp);
                radios.Add(rb); list.Children.Add(rb);
            }
            var custom = new RadioButton { GroupName = "dns", Tag = "Custom", Content = "Custom", IsChecked = DnsGuard.Find(c.DnsPreset) == null, Foreground = Ui.B("#F2F5F9"), Margin = new Thickness(0, 3, 0, 0), FontWeight = FontWeights.SemiBold };
            radios.Add(custom); list.Children.Add(custom);
            foreach (var tb in new[] { p4, s4, p6, s6 }) { tb.FontSize = 12.5; tb.TextChanged += (s, e) => { if (!filling && tb.IsKeyboardFocused) custom.IsChecked = true; }; }
            body.Children.Add(list);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 8, -10, 0) };
            foreach (var pair in new[] { Tuple.Create("IPv4 primary", p4), Tuple.Create("IPv4 secondary", s4), Tuple.Create("IPv6 primary", p6), Tuple.Create("IPv6 secondary", s6) })
            {
                var cell = new StackPanel { Margin = new Thickness(0, 4, 10, 0) };
                cell.Children.Add(Ui.Text(pair.Item1, 11, "#A3ADBD"));
                pair.Item2.Width = double.NaN; pair.Item2.HorizontalAlignment = HorizontalAlignment.Stretch; pair.Item2.Margin = new Thickness(0, 2, 0, 0);
                cell.Children.Add(pair.Item2);
                grid.Children.Add(cell);
            }
            body.Children.Add(grid);
            var doh = Ui.Check("Encrypted DNS (DNS-over-HTTPS, Windows 11)", c.DnsEncrypted); body.Children.Add(doh);

            var msg = Ui.Text("", 12, "#F5C26B", false, true); msg.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(msg);
            Func<bool> read = () =>
            {
                IPAddress a;
                if (!IPAddress.TryParse(p4.Text.Trim(), out a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { msg.Text = "IPv4 primary must be an address like 1.1.1.1"; return false; }
                if (s4.Text.Trim() != "" && (!IPAddress.TryParse(s4.Text.Trim(), out a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) { msg.Text = "IPv4 secondary must be an address like 8.8.8.8"; return false; }
                foreach (var tb in new[] { p6, s6 })
                    if (tb.Text.Trim() != "" && (!IPAddress.TryParse(tb.Text.Trim(), out a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)) { msg.Text = "\"" + tb.Text + "\" is not an IPv6 address (example 2606:4700:4700::1111)"; return false; }
                var sel = radios.First(r => r.IsChecked == true);
                c.DnsPreset = (string)sel.Tag; c.DnsP4 = p4.Text.Trim(); c.DnsS4 = s4.Text.Trim(); c.DnsP6 = p6.Text.Trim(); c.DnsS6 = s6.Text.Trim();
                c.DnsEncrypted = doh.IsChecked == true; c.DnsGuard = guard.IsChecked == true;
                c.Save();
                return true;
            };

            var apply = Ui.Btn("Apply", "#D01A73B8", async () =>
            {
                if (!read()) return;
                if (DnsGuard.IsAdmin)
                {
                    msg.Text = "Applying...";
                    var cc = c;
                    await Task.Run(() => { DnsGuard.Apply(cc, true); AutoStart.Sync(cc); });
                    E.GuardStatus = cc.DnsGuard ? "on" : "";
                    E.Book.WriteAlert("DNS changed", cc.DnsPreset + (cc.DnsGuard ? " (DNS Guard on)" : ""));
                    E.Dns = null; E.RequestDns();
                    w.Close();
                }
                else if (c.DnsGuard)
                {
                    // DNS Guard needs Ambaan itself to run with admin rights: restart it that way (one prompt; after that it starts so at sign-in)
                    msg.Text = "Windows will ask for permission...";
                    if (DnsGuard.RunElevated("--elevated-start", false)) { w.Close(); App.Quit(); }
                    else msg.Text = "Administrator permission was not given, so nothing changed.";
                }
                else
                {
                    msg.Text = "Windows will ask for permission...";
                    bool ok = await Task.Run(() => DnsGuard.RunElevated("--dns-apply", true));
                    if (!ok) { msg.Text = "Administrator permission was not given, so nothing changed."; return; }
                    E.Book.WriteAlert("DNS changed", c.DnsPreset);
                    E.Dns = null; E.RequestDns();
                    w.Close();
                }
            });
            var off = Ui.Btn("Turn off", "#1FFFFFFF", async () =>
            {
                c.DnsGuard = false; c.Save(); E.GuardStatus = "";
                if (DnsGuard.IsAdmin) await Task.Run(() => { DnsGuard.Reset(); AutoStart.Sync(c); });
                else if (!await Task.Run(() => DnsGuard.RunElevated("--dns-reset", true))) { msg.Text = "Administrator permission was not given."; return; }
                E.Book.WriteAlert("DNS changed", "Automatic DNS (from the router)");
                E.Dns = null; E.RequestDns();
                w.Close();
            });
            off.ToolTip = "Stop DNS Guard and go back to automatic DNS from the router";
            body.Children.Add(Ui.Buttons(apply, off, Ui.Btn("Cancel", "#1FFFFFFF", () => w.Close())));

            var results = new StackPanel();
            var bench = Ui.Btn("Which DNS is fastest here?", "#2EFFFFFF", () => { });
            bench.Click += async (s, e) =>
            {
                bench.IsEnabled = false; results.Children.Clear();
                results.Children.Add(Ui.Text("Testing 5 lookups on each server...", 12, "#A3ADBD"));
                var servers = new List<Tuple<string, string>>();
                if (E.Primary != null) servers.Add(Tuple.Create("Your router", E.Primary.Gateway.ToString()));
                foreach (var x in live[1].Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
                    if (!servers.Any(v => v.Item2 == x) && x != "-") servers.Add(Tuple.Create("In use now", x));
                foreach (var pr in DnsGuard.Presets) if (!servers.Any(v => v.Item2 == pr.P4)) servers.Add(Tuple.Create(pr.Name, pr.P4));
                servers.Add(Tuple.Create("OpenDNS", "208.67.222.222"));
                var tasks = servers.Select(v => Task.Run(() => Tuple.Create(v.Item1, v.Item2, DnsGuard.Benchmark(v.Item2)))).ToList();
                var res = await Task.WhenAll(tasks);
                results.Children.Clear();
                var ok = res.Where(r => r.Item3 != null).OrderBy(r => r.Item3).ToList();
                int max = ok.Count > 0 ? Math.Max(1, ok.Max(r => r.Item3.Value)) : 1;
                foreach (var r in ok.Concat(res.Where(r => r.Item3 == null)))
                {
                    var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                    bool best = ok.Count > 0 && r == ok[0];
                    g.Children.Add(Ui.Text(r.Item1 + "  (" + r.Item2 + ")", 11.5, best ? "#5FD39A" : null, best));
                    var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                        Width = r.Item3 != null ? Math.Max(4, 180.0 * r.Item3.Value / max) : 0, Background = Ui.B(best ? "#5FD39A" : "#4CC2FF") };
                    Grid.SetColumn(bar, 1); g.Children.Add(bar);
                    var v = Ui.Text(r.Item3 != null ? r.Item3 + " ms" : "no answer", 11.5, r.Item3 == null ? "#FF7B7B" : null, true); v.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(v, 2); g.Children.Add(v);
                    results.Children.Add(g);
                }
                bench.IsEnabled = true;
            };
            var flush = Ui.Btn("Flush DNS cache", "#2EFFFFFF", () => msg.Text = FullTest.FlushDns());
            var row = Ui.Buttons(bench, flush); row.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(row);
            body.Children.Add(results);
            var note = Ui.Text("Changing DNS needs administrator permission. With DNS Guard on, Ambaan restarts with admin rights once, and from then on starts that way at sign-in with no prompts. Check it works at 1.1.1.1/help. In Chrome or Edge set \"Use secure DNS\" to \"With your current service provider\".", 11, "#A3ADBD", false, true);
            note.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(note);
            w.ShowDialog();
        }

        public static void About()
        {
            StackPanel body;
            var w = Ui.Dialog("About", 420, out body);
            body.Children.Add(new Image { Source = Ui.AppIcon, Width = 72, Height = 72, Margin = new Thickness(0, 0, 0, 8) });
            var t = Ui.Text(Paths.AppName + "  " + Paths.Version, 16, null, true); t.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(t);
            var by = Ui.Text("Made by Phoenicx · phoenicx.org", 12.5, "#4CC2FF"); by.HorizontalAlignment = HorizontalAlignment.Center; by.Cursor = System.Windows.Input.Cursors.Hand;
            by.MouseLeftButtonUp += (s, e) => Ui.Open("https://phoenicx.org");
            body.Children.Add(by);
            var info = Ui.Text("Everything stays on your PC. Outside connections: the Cloudflare speed test, pings to 1.1.1.1 / 8.8.8.8, DNS test lookups, and the update check when you press it.\n\nData folder: " + Paths.Root, 11.5, "#A3ADBD", false, true);
            info.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(info);
            var status = Ui.Text("", 12, "#F5C26B", false, true); status.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(status);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Check for updates", "#D01A73B8", async () =>
                {
                    status.Text = "Checking...";
                    string latest = await Task.Run(() => Updates.Latest());
                    if (latest == null) status.Text = "Could not reach GitHub. Try again later.";
                    else if (Updates.Newer(latest, Paths.Version))
                    {
                        status.Text = "Version " + latest + " is available. Opening the download page...";
                        Ui.Open("https://github.com/hansonds/AmbaanWiFiGuard/releases/latest");
                    }
                    else status.Text = "You have the latest version.";
                }),
                Ui.Btn("Open data folder", "#1FFFFFFF", () => Ui.Open(Paths.Root)),
                Ui.Btn("Close", "#1FFFFFFF", () => w.Close())));
            w.ShowDialog();
        }
    }

    public static class Updates
    {
        public static string Latest()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/hansonds/AmbaanWiFiGuard/releases/latest");
                req.UserAgent = "AmbaanWiFiGuard/" + Paths.Version; req.Timeout = 10000;
                using (var resp = req.GetResponse())
                using (var r = new System.IO.StreamReader(resp.GetResponseStream()))
                {
                    var o = Json.Parse(r.ReadToEnd()) as System.Collections.Generic.Dictionary<string, object>;
                    return o != null && o.ContainsKey("tag_name") ? Convert.ToString(o["tag_name"]).TrimStart('v') : null;
                }
            }
            catch { return null; }
        }

        public static bool Newer(string a, string b)
        {
            Version va, vb;
            return Version.TryParse(a, out va) && Version.TryParse(b, out vb) && va > vb;
        }
    }

    public static class SettingsWindow
    {
        static Window open;
        static Engine E => Engine.I;

        public static void Open()
        {
            if (open != null) { open.Activate(); return; }
            var c = E.Cfg;
            StackPanel body;
            var w = Ui.Dialog("Ambaan settings", 470, out body);
            open = w; w.Closed += (s, e) => open = null;

            body.Children.Add(Ui.Label("Speed alerts"));
            var slow = Ui.Input(c.SlowMbps.ToString("0"), 80);
            var fast = Ui.Input(c.FastMbps.ToString("0"), 80);
            body.Children.Add(Ui.Row(Ui.Text("Slow alert when speed drops below (Mbps)"), slow, 2));
            body.Children.Add(Ui.Row(Ui.Text("Good-speed alert when speed reaches (Mbps)"), fast));
            var modes = new[] { "Once a day", "Every time", "Off" }; var modeKeys = new[] { "Daily", "Every", "Off" };
            var mode = Ui.Combo(modes, Array.IndexOf(modeKeys, c.FastMode));
            body.Children.Add(Ui.Row(Ui.Text("Good-speed alert"), mode));
            var mins = new[] { 15, 30, 60, 120 };
            int mi = Array.IndexOf(mins, c.SpeedEvery); if (mi < 0) mi = 1;
            var every = Ui.Combo(mins.Select(m => m + " minutes"), mi);
            body.Children.Add(Ui.Row(Ui.Text("Check speed every"), every));
            var hint = Ui.Text("Between the two numbers Ambaan stays quiet. Each check downloads a small 5 MB file.", 11, "#A3ADBD", false, true);
            hint.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(hint);

            var h2 = Ui.Label("Character and sound"); h2.Margin = new Thickness(0, 16, 0, 4); body.Children.Add(h2);
            var chars = new[] { "Ambaan", "Dhamu" };
            var ch = Ui.Combo(chars, Array.IndexOf(chars, c.Character), 120);
            var pv = Ui.Btn("Preview", "#1FFFFFFF", () => AlertWindow.Pop("New", PreviewMac(), true, chars[ch.SelectedIndex]));
            pv.Margin = new Thickness(8, 0, 0, 0); pv.Height = 28;
            var chRow = new StackPanel { Orientation = Orientation.Horizontal }; chRow.Children.Add(ch); chRow.Children.Add(pv);
            body.Children.Add(Ui.Row(Ui.Text("Alert character"), chRow));
            var voice = Ui.Check("Character voice on alerts", c.Voice); body.Children.Add(voice);
            var vol = new Slider { Minimum = 0, Maximum = 100, Value = c.Volume, Width = 170, IsSnapToTickEnabled = true, TickFrequency = 5 };
            body.Children.Add(Ui.Row(Ui.Text("Voice volume"), vol, 6));
            var quiet = Ui.Check("Quiet hours (no voice at night)", c.QuietHours); body.Children.Add(quiet);
            var hours = Enumerable.Range(0, 24).Select(x => x.ToString("00") + ":00").ToList();
            var qf = Ui.Combo(hours, c.QuietFrom, 80); var qt = Ui.Combo(hours, c.QuietTo, 80);
            var qRow = new StackPanel { Orientation = Orientation.Horizontal };
            qRow.Children.Add(qf); qRow.Children.Add(new TextBlock { Text = "  to  ", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.B("#A3ADBD") }); qRow.Children.Add(qt);
            body.Children.Add(Ui.Row(Ui.Text("From"), qRow, 6));

            var h3 = Ui.Label("General"); h3.Margin = new Thickness(0, 16, 0, 4); body.Children.Add(h3);
            var auto = Ui.Check("Start Ambaan when Windows starts", c.AutoStart); body.Children.Add(auto);
            var all = Ui.Check("Alert when my named devices reconnect too", c.AlertAll); body.Children.Add(all);
            var outage = Ui.Check("Tell me when the internet goes down / comes back", c.OutageAlerts); body.Children.Add(outage);
            var glass = Ui.Check("Glass effect (blur behind the widget)", c.Glass); body.Children.Add(glass);

            var h4 = Ui.Label("Data limits (this PC, 0 = off)"); h4.Margin = new Thickness(0, 16, 0, 4); body.Children.Add(h4);
            var capD = Ui.Input(c.DayCapGB.ToString("0.#"), 80); var capM = Ui.Input(c.MonthCapGB.ToString("0.#"), 80);
            body.Children.Add(Ui.Row(Ui.Text("Warn me when today's data passes (GB)"), capD, 2));
            body.Children.Add(Ui.Row(Ui.Text("Warn me when this month's data passes (GB)"), capM));
            var dnsBtn = Ui.Btn("DNS and DNS Guard...", "#2EFFFFFF", () => Dialogs.DnsPanel()); dnsBtn.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(dnsBtn);

            var err = Ui.Text("", 12, "#FF7B7B", false, true); err.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(err);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Save", "#D01A73B8", () =>
                {
                    double s, f;
                    if (!double.TryParse(slow.Text, out s) || !double.TryParse(fast.Text, out f) || s < 1) { err.Text = "Please type numbers for the speed limits."; return; }
                    if (f <= s) { err.Text = "The good-speed number must be higher than the slow number."; return; }
                    int oldEvery = E.SpeedEvery;
                    c.SlowMbps = s; c.FastMbps = f; c.FastMode = modeKeys[Math.Max(0, mode.SelectedIndex)]; c.SpeedEvery = mins[Math.Max(0, every.SelectedIndex)];
                    c.Character = chars[Math.Max(0, ch.SelectedIndex)]; c.Voice = voice.IsChecked == true; c.Volume = (int)vol.Value;
                    c.QuietHours = quiet.IsChecked == true; c.QuietFrom = qf.SelectedIndex; c.QuietTo = qt.SelectedIndex;
                    c.AlertAll = all.IsChecked == true; c.OutageAlerts = outage.IsChecked == true;
                    double cd, cm;
                    c.DayCapGB = double.TryParse(capD.Text, out cd) && cd > 0 ? cd : 0;
                    c.MonthCapGB = double.TryParse(capM.Text, out cm) && cm > 0 ? cm : 0;
                    bool glassChanged = c.Glass != (glass.IsChecked == true); c.Glass = glass.IsChecked == true;
                    if (c.AutoStart != (auto.IsChecked == true)) { c.AutoStart = auto.IsChecked == true; var cc = c; Task.Run(() => AutoStart.Sync(cc)); }
                    c.AutoStartAsked = true;
                    c.Fix(); c.Save();
                    E.SettingsChanged(oldEvery);
                    if (glassChanged) App.Widget?.ApplyGlass();
                    w.Close();
                }),
                Ui.Btn("Reset to default", "#1FFFFFFF", () =>
                {
                    var d = new Settings();
                    slow.Text = d.SlowMbps.ToString("0"); fast.Text = d.FastMbps.ToString("0"); mode.SelectedIndex = 0; every.SelectedIndex = 1;
                    ch.SelectedIndex = 0; voice.IsChecked = true; vol.Value = 100; quiet.IsChecked = false; qf.SelectedIndex = 23; qt.SelectedIndex = 7;
                    auto.IsChecked = true; all.IsChecked = false; outage.IsChecked = true; glass.IsChecked = true; capD.Text = "0"; capM.Text = "0";
                }),
                Ui.Btn("Cancel", "#1FFFFFFF", () => w.Close())));
            var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            var about = new Button { Content = "About Ambaan · phoenicx.org", Style = Ui.Res<Style>("LinkBtn") };
            about.Click += (s, e) => Dialogs.About();
            var exit = new Button { Content = "Exit Ambaan", Style = Ui.Res<Style>("LinkBtn"), Foreground = Ui.B("#FF7B7B") };
            exit.Click += (s, e) => { w.Close(); App.Quit(); };
            links.Children.Add(about); links.Children.Add(exit);
            body.Children.Add(links);
            w.Show();
        }

        static string PreviewMac()
        {
            var r = E.Book.Devices.FirstOrDefault(d => !d.Named) ?? E.Book.Devices.FirstOrDefault();
            return r != null ? r.Mac : "AA-BB-CC-DD-EE-FF";
        }
    }
}
