using System;
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
            body.Children.Add(Ui.Text(d.Ip + "    " + d.Mac, 12, "#C2BCEB"));
            body.Children.Add(Ui.Text(E.Book.Hint(mac), 12, "#C2BCEB", false, true));
            body.Children.Add(Ui.Label("Name"));
            ((TextBlock)body.Children[body.Children.Count - 1]).Margin = new Thickness(0, 12, 0, 4);
            var box = Ui.Input(d.Named ? d.Name : (d.HostName ?? (d.Vendor != null ? d.Vendor + " " : "")));
            body.Children.Add(box);
            body.Children.Add(Ui.Text("Tip: name everything you own. Only strangers will trigger the alert.", 11, "#C2BCEB", false, true));
            ((TextBlock)body.Children[body.Children.Count - 1]).Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Save", "#607CE8B8", () => { if (!string.IsNullOrWhiteSpace(box.Text)) E.Book.Rename(mac, box.Text); w.Close(); }),
                Ui.Btn("Clear name", "#303A3270", () => { E.Book.Rename(mac, null); w.Close(); }),
                Ui.Btn("Forget", "#50A0406A", () => { E.Book.Forget(mac, E.Watch); w.Close(); })));
            var row2 = Ui.Buttons(Ui.Btn("Block...", "#50FF9C9C", () => BlockHelp(mac)), Ui.Btn("Cancel", "#303A3270", () => w.Close()));
            row2.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(row2);
            ((Button)((System.Windows.Controls.Primitives.UniformGrid)body.Children[body.Children.Count - 2]).Children[2]).ToolTip = "Forget this device: it will alert again as NEW the next time it connects";
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

        public static void DnsFix()
        {
            StackPanel body;
            var w = Ui.Dialog("DNS Shield", 460, out body);
            var dn = E.Dns;
            if (dn != null)
            {
                body.Children.Add(Ui.Text(dn.Headline, 13, null, true, true));
                foreach (var i in dn.Issues) body.Children.Add(Ui.Text(i, 12, "#FFD27A", false, true));
                foreach (var s in dn.Servers)
                    body.Children.Add(Ui.Text((s.Kind == "Router" ? "Router" : s.Address) + "  ·  " + s.Label + "  ·  " + (s.Unused ? "unused" : s.Responds ? s.Ms + " ms" : "not answering"), 11.5, "#C2BCEB", false, true));
            }
            body.Children.Add(Ui.Label("Choose what this PC should use"));
            ((TextBlock)body.Children[body.Children.Count - 1]).Margin = new Thickness(0, 14, 0, 4);
            var r1 = new RadioButton { Content = "Scam filter: Cloudflare 1.1.1.2 / 1.0.0.2 (blocks phishing + malware)", IsChecked = true, Foreground = Ui.B("#F7F5FF"), Margin = new Thickness(0, 4, 0, 0) };
            var r2 = new RadioButton { Content = "Family filter: Cloudflare 1.1.1.3 / 1.0.0.3 (also blocks adult sites)", Foreground = Ui.B("#F7F5FF"), Margin = new Thickness(0, 6, 0, 0) };
            var r3 = new RadioButton { Content = "Automatic: use the router again (removes manual DNS)", Foreground = Ui.B("#F7F5FF"), Margin = new Thickness(0, 6, 0, 0) };
            body.Children.Add(r1); body.Children.Add(r2); body.Children.Add(r3);
            var note = Ui.Text("This changes DNS on THIS PC only (Wi-Fi + Ethernet) and asks for administrator permission.\n\nTo protect every phone and TV at home, set 1.1.1.2 and 1.0.0.2 as DNS on your router's admin page or app. Phones on mobile data: set Private DNS to security.cloudflare-dns.com", 11.5, "#FFD27A", false, true);
            note.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(note);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Apply", "#605B4BC4", async () =>
                {
                    int choice = r1.IsChecked == true ? 1 : r2.IsChecked == true ? 2 : 3;
                    w.Close();
                    string err = null;
                    bool ok = await Task.Run(() => DnsShield.Apply(choice, out err));
                    E.Book.WriteAlert("DNS changed", "Option " + choice + (ok ? " applied" : " failed: " + err));
                    if (!ok && err != null) Ui.Info("DNS Shield", err);
                    E.Dns = null; E.RequestDns();
                }),
                Ui.Btn("Cancel", "#303A3270", () => w.Close())));
            w.ShowDialog();
        }

        public static void About()
        {
            StackPanel body;
            var w = Ui.Dialog("About", 420, out body);
            body.Children.Add(new Image { Source = Ui.AppIcon, Width = 72, Height = 72, Margin = new Thickness(0, 0, 0, 8) });
            var t = Ui.Text(Paths.AppName + "  " + Paths.Version, 16, null, true); t.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(t);
            var by = Ui.Text("Made by Phoenicx · phoenicx.org", 12.5, "#8EC5FF"); by.HorizontalAlignment = HorizontalAlignment.Center; by.Cursor = System.Windows.Input.Cursors.Hand;
            by.MouseLeftButtonUp += (s, e) => Ui.Open("https://phoenicx.org");
            body.Children.Add(by);
            var info = Ui.Text("Everything stays on your PC. Outside connections: the Cloudflare speed test, pings to 1.1.1.1 / 8.8.8.8, DNS test lookups, and the update check when you press it.\n\nData folder: " + Paths.Root, 11.5, "#C2BCEB", false, true);
            info.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(info);
            var status = Ui.Text("", 12, "#FFD27A", false, true); status.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(status);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Check for updates", "#605B4BC4", async () =>
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
                Ui.Btn("Open data folder", "#303A3270", () => Ui.Open(Paths.Root)),
                Ui.Btn("Close", "#303A3270", () => w.Close())));
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
            var hint = Ui.Text("Between the two numbers Ambaan stays quiet. Each check downloads a small 5 MB file.", 11, "#C2BCEB", false, true);
            hint.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(hint);

            var h2 = Ui.Label("Character and sound"); h2.Margin = new Thickness(0, 16, 0, 4); body.Children.Add(h2);
            var chars = new[] { "Ambaan", "Dhamu" };
            var ch = Ui.Combo(chars, Array.IndexOf(chars, c.Character), 120);
            var pv = Ui.Btn("Preview", "#403A3270", () => AlertWindow.Pop("New", PreviewMac(), true, chars[ch.SelectedIndex]));
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
            qRow.Children.Add(qf); qRow.Children.Add(new TextBlock { Text = "  to  ", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.B("#C2BCEB") }); qRow.Children.Add(qt);
            body.Children.Add(Ui.Row(Ui.Text("From"), qRow, 6));

            var h3 = Ui.Label("General"); h3.Margin = new Thickness(0, 16, 0, 4); body.Children.Add(h3);
            var auto = Ui.Check("Start Ambaan when Windows starts", c.AutoStart); body.Children.Add(auto);
            var all = Ui.Check("Alert when my named devices reconnect too", c.AlertAll); body.Children.Add(all);
            var outage = Ui.Check("Tell me when the internet goes down / comes back", c.OutageAlerts); body.Children.Add(outage);
            var glass = Ui.Check("Glass effect (blur behind the widget)", c.Glass); body.Children.Add(glass);

            var err = Ui.Text("", 12, "#FF9C9C", false, true); err.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(err);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Save", "#605B4BC4", () =>
                {
                    double s, f;
                    if (!double.TryParse(slow.Text, out s) || !double.TryParse(fast.Text, out f) || s < 1) { err.Text = "Please type numbers for the speed limits."; return; }
                    if (f <= s) { err.Text = "The good-speed number must be higher than the slow number."; return; }
                    int oldEvery = E.SpeedEvery;
                    c.SlowMbps = s; c.FastMbps = f; c.FastMode = modeKeys[Math.Max(0, mode.SelectedIndex)]; c.SpeedEvery = mins[Math.Max(0, every.SelectedIndex)];
                    c.Character = chars[Math.Max(0, ch.SelectedIndex)]; c.Voice = voice.IsChecked == true; c.Volume = (int)vol.Value;
                    c.QuietHours = quiet.IsChecked == true; c.QuietFrom = qf.SelectedIndex; c.QuietTo = qt.SelectedIndex;
                    c.AlertAll = all.IsChecked == true; c.OutageAlerts = outage.IsChecked == true;
                    bool glassChanged = c.Glass != (glass.IsChecked == true); c.Glass = glass.IsChecked == true;
                    if (c.AutoStart != (auto.IsChecked == true)) { c.AutoStart = auto.IsChecked == true; AutoStart.Apply(c.AutoStart); }
                    c.AutoStartAsked = true;
                    c.Fix(); c.Save();
                    E.SettingsChanged(oldEvery);
                    if (glassChanged) App.Widget?.ApplyGlass();
                    w.Close();
                }),
                Ui.Btn("Reset to default", "#303A3270", () =>
                {
                    var d = new Settings();
                    slow.Text = d.SlowMbps.ToString("0"); fast.Text = d.FastMbps.ToString("0"); mode.SelectedIndex = 0; every.SelectedIndex = 1;
                    ch.SelectedIndex = 0; voice.IsChecked = true; vol.Value = 100; quiet.IsChecked = false; qf.SelectedIndex = 23; qt.SelectedIndex = 7;
                    auto.IsChecked = true; all.IsChecked = false; outage.IsChecked = true; glass.IsChecked = true;
                }),
                Ui.Btn("Cancel", "#303A3270", () => w.Close())));
            var about = new Button { Content = "About Ambaan · phoenicx.org", Style = Ui.Res<Style>("LinkBtn"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            about.Click += (s, e) => Dialogs.About();
            body.Children.Add(about);
            w.Show();
        }

        static string PreviewMac()
        {
            var r = E.Book.Devices.FirstOrDefault(d => !d.Named) ?? E.Book.Devices.FirstOrDefault();
            return r != null ? r.Mac : "AA-BB-CC-DD-EE-FF";
        }
    }
}
