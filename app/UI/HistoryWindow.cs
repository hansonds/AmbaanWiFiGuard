using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Ambaan
{
    /// <summary>History and tools: speed chart, outages + uptime, data usage, best Wi-Fi channel, alert log.</summary>
    public static class HistoryWindow
    {
        static Window open;
        static Engine E => Engine.I;

        public static void Open(string tab = "Speed")
        {
            if (open != null) { open.Activate(); return; }
            StackPanel body;
            var w = Ui.Dialog("History and tools", 620, out body, true);
            open = w; w.Closed += (s, e) => open = null;

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var content = new ContentControl { MinHeight = 340 };
            var names = new[] { "Speed", "Outages", "Data usage", "Wi-Fi channels", "Alerts" };
            var buttons = new List<Button>();
            Action<string> show = null;
            show = name =>
            {
                foreach (var b in buttons) b.Background = Ui.B((string)b.Content == name ? "#705B4BC4" : "#20FFFFFF");
                try
                {
                    content.Content = name == "Speed" ? SpeedTab() : name == "Outages" ? OutageTab() : name == "Data usage" ? UsageTab()
                                    : name == "Wi-Fi channels" ? ChannelTab() : AlertsTab();
                }
                catch (Exception ex) { Log.Error("history tab", ex); content.Content = Ui.Text("Could not load this tab.", 13); }
            };
            foreach (var n in names)
            {
                string name = n;
                var b = Ui.Btn(name, "#20FFFFFF", () => show(name)); b.Margin = new Thickness(0, 0, 6, 0); b.Height = 30;
                buttons.Add(b); tabs.Children.Add(b);
            }
            body.Children.Add(tabs);
            body.Children.Add(content);
            show(names.Contains(tab) ? tab : "Speed");
            w.Show();
        }

        static UIElement Stat(string label, string value, string color = null)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Label(label));
            sp.Children.Add(Ui.Text(value, 17, color, true));
            return new Border { Style = Ui.Res<Style>("Card"), Child = sp, Margin = new Thickness(0, 0, 8, 0) };
        }

        static UniformGrid Stats(params UIElement[] items)
        {
            var g = new UniformGrid { Columns = items.Length, Margin = new Thickness(0, 0, -8, 10) };
            foreach (var i in items) g.Children.Add(i);
            return g;
        }

        static UIElement SpeedTab()
        {
            var sp = new StackPanel();
            var chart = new ChartView { Height = 230, Dots = true, MinMax = 10, Empty = "No speed tests in this time range yet" };
            var legend = Ui.Text("", 11, "#C2BCEB");
            var stats = new ContentControl();
            Action<int> load = days =>
            {
                var pts = SpeedHistory.Load(days);
                var good = pts.Where(p => p.Down != null).ToList();
                stats.Content = Stats(
                    Stat("Tests", pts.Count.ToString()),
                    Stat("Average", good.Count > 0 ? good.Average(p => p.Down.Value).ToString("0.0") + " Mbps" : "--", "#8EC5FF"),
                    Stat("Best", good.Count > 0 ? good.Max(p => p.Down.Value).ToString("0.0") + " Mbps" : "--", "#7CE8B8"),
                    Stat("Worst", good.Count > 0 ? good.Min(p => p.Down.Value).ToString("0.0") + " Mbps" : "--", "#FF9C9C"));
                chart.XLabels = pts.Select(p => days <= 1 ? p.Time.ToString("HH:mm") : p.Time.ToString("dd MMM")).ToArray();
                chart.Set(new ChartView.Series { Values = pts.Select(p => p.Down).ToArray(), Color = Ui.C("#8EC5FF"), Fill = true },
                          new ChartView.Series { Values = pts.Select(p => p.Ping.HasValue ? (double?)p.Ping.Value : null).ToArray(), Color = Ui.C("#FFBD70") });
                legend.Text = "Blue: download Mbps   ·   Orange: internet ping ms   ·   " + (pts.Count(p => p.Down == null)) + " tests had no internet";
            };
            var range = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            foreach (var r in new[] { Tuple.Create("24 hours", 1), Tuple.Create("7 days", 7), Tuple.Create("30 days", 30) })
            {
                var b = new Button { Content = r.Item1, Style = Ui.Res<Style>("LinkBtn"), Margin = new Thickness(0, 0, 6, 0) };
                int days = r.Item2; b.Click += (s, e) => load(days);
                range.Children.Add(b);
            }
            sp.Children.Add(stats);
            sp.Children.Add(range);
            sp.Children.Add(Ui.Card(chart));
            sp.Children.Add(legend); legend.Margin = new Thickness(2, 6, 0, 0);
            var btns = Ui.Buttons(Ui.Btn("Speed test now", "#507CE8B8", () => E.StartSpeedTest(true)),
                                  Ui.Btn("Full test + report", "#50C9A7FF", FullTestWindow.Open),
                                  Ui.Btn("Open last report", "#508EC5FF", () => { var r = FullTest.LatestReport(); if (r != null) Ui.Open(r); else Ui.Info("Report", "Run a Full test first to create a report."); }));
            sp.Children.Add(btns);
            load(7);
            return sp;
        }

        static UIElement OutageTab()
        {
            var sp = new StackPanel();
            var o = E.Outages;
            Func<double?, string> pct = v => v != null ? v.Value.ToString(v.Value >= 99.95 ? "0" : "0.00") + "%" : "--";
            var list = o.Recent(100);
            sp.Children.Add(Stats(
                Stat("Uptime today", pct(o.Uptime(1)), "#7CE8B8"),
                Stat("Last 7 days", pct(o.Uptime(7)), "#7CE8B8"),
                Stat("Last 30 days", pct(o.Uptime(30)), "#7CE8B8"),
                Stat("Outages (30 d)", list.Count(x => x.Start > DateTime.Now.AddDays(-30)).ToString(), "#FF9C9C")));
            if (o.DownSince != null)
                sp.Children.Add(Ui.Card(Ui.Text("Internet is down right now (since " + o.DownSince.Value.ToString("HH:mm") + "): " + o.Cause, 13, "#FF9C9C", true, true)));
            var lv = new StackPanel();
            if (list.Count == 0) lv.Children.Add(Ui.Text("No outages recorded. Ambaan checks the internet every 10 seconds while your PC is on.", 12.5, "#C2BCEB", false, true));
            foreach (var x in list)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var a = Ui.Text(x.Start.ToString("ddd dd MMM  HH:mm"), 12.5); row.Children.Add(a);
                var b = Ui.Text(NetUtil.Minutes(x.Minutes), 12.5, "#FFD27A", true); Grid.SetColumn(b, 1); row.Children.Add(b);
                var c = Ui.Text(x.Cause, 12.5, "#C2BCEB"); Grid.SetColumn(c, 2); row.Children.Add(c);
                lv.Children.Add(row);
            }
            var sv = new ScrollViewer { Content = lv, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            sp.Children.Add(Ui.Card(sv, 8));
            var note = Ui.Text("\"Router not answering\" or \"Wi-Fi disconnected\" = a problem at home. \"Internet line (ISP) down\" = your provider. Keep this list when you complain to them.", 11, "#C2BCEB", false, true);
            note.Margin = new Thickness(2, 8, 0, 0); sp.Children.Add(note);
            return sp;
        }

        static UIElement UsageTab()
        {
            var sp = new StackPanel();
            var u = E.Usage;
            var t = u.Day(DateTime.Today); var m = u.Month();
            var y = u.Day(DateTime.Today.AddDays(-1));
            sp.Children.Add(Stats(
                Stat("Today", NetUtil.Size(t[0] + t[1]), "#8EC5FF"),
                Stat("Yesterday", NetUtil.Size(y[0] + y[1])),
                Stat("This month", NetUtil.Size(m[0] + m[1]), "#C9A7FF"),
                Stat("Month upload", NetUtil.Size(m[1]))));
            var days = Enumerable.Range(0, 14).Select(i => DateTime.Today.AddDays(-13 + i)).ToList();
            var chart = new ChartView { Height = 220, Bars = true, MinMax = 0.1, Empty = "No data yet" };
            chart.XLabels = days.Select(d => d.ToString("dd MMM")).ToArray();
            chart.Set(new ChartView.Series { Values = days.Select(d => (double?)(u.Day(d)[0] / 1e9)).ToArray(), Color = Ui.C("#8EC5FF") },
                      new ChartView.Series { Values = days.Select(d => (double?)(u.Day(d)[1] / 1e9)).ToArray(), Color = Ui.C("#C9A7FF") });
            sp.Children.Add(Ui.Card(chart));
            var note = Ui.Text("GB per day, last 14 days. Blue: downloaded · Purple: uploaded. Counts THIS PC only (Wi-Fi + cable), while Ambaan is running.", 11, "#C2BCEB", false, true);
            note.Margin = new Thickness(2, 6, 0, 0); sp.Children.Add(note);
            return sp;
        }

        static UIElement ChannelTab()
        {
            var sp = new StackPanel();
            var summary = Ui.Text("Scan nearby Wi-Fi networks to find the least crowded channel for your router.", 14, null, true, true);
            var detail = Ui.Text("", 12, "#C2BCEB", false, true); detail.Margin = new Thickness(0, 6, 0, 0);
            var c24 = new ChartView { Height = 120, Bars = true, MinMax = 2, Empty = "Press Scan" };
            var c5 = new ChartView { Height = 120, Bars = true, MinMax = 2, Empty = "" };
            var nets = new StackPanel();
            var info = new StackPanel(); info.Children.Add(summary); info.Children.Add(detail);
            sp.Children.Add(Ui.Card(info));
            var charts = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
            var l24 = new StackPanel(); l24.Children.Add(Ui.Label("2.4 GHz · networks per channel")); l24.Children.Add(c24);
            var l5 = new StackPanel(); l5.Children.Add(Ui.Label("5 GHz · networks per channel")); l5.Children.Add(c5);
            var b24 = Ui.Card(l24); b24.Margin = new Thickness(0, 0, 4, 0); var b5 = Ui.Card(l5); b5.Margin = new Thickness(4, 0, 0, 0);
            charts.Children.Add(b24); charts.Children.Add(b5);
            sp.Children.Add(charts);
            sp.Children.Add(Ui.Card(new ScrollViewer { Content = nets, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 8));
            Button scan = null;
            scan = Ui.Btn("Scan now", "#605B4BC4", async () =>
            {
                scan.IsEnabled = false; summary.Text = "Scanning nearby Wi-Fi (about 5 seconds)...";
                var me = E.Wifi;
                ChannelAdvice a = null;
                try { a = await Task.Run(() => ChannelAdvisor.Analyze(me)); } catch (Exception ex) { Log.Error("channels", ex); }
                scan.IsEnabled = true;
                if (a == null) { summary.Text = "Scan failed."; return; }
                summary.Text = a.Summary; detail.Text = a.Detail;
                var k24 = a.Count24.Keys.OrderBy(k => k).ToArray();
                c24.XLabels = k24.Select(k => k.ToString()).ToArray();
                c24.Set(new ChartView.Series { Values = k24.Select(k => (double?)a.Count24[k]).ToArray(), Color = Ui.C("#8EC5FF") });
                var k5 = a.Count5.Keys.OrderBy(k => k).ToArray();
                c5.XLabels = k5.Select(k => k.ToString()).ToArray();
                c5.Set(new ChartView.Series { Values = k5.Select(k => (double?)a.Count5[k]).ToArray(), Color = Ui.C("#C9A7FF") });
                nets.Children.Clear();
                foreach (var n in a.Networks.Take(40))
                    nets.Children.Add(Ui.Text((n.Bssid == me.Bssid ? "★ " : "") + (string.IsNullOrEmpty(n.Ssid) ? "(hidden)" : n.Ssid) + "   ·   " + n.Band + " ch " + n.Channel + "   ·   " + n.Rssi + " dBm", 11.5, n.Bssid == me.Bssid ? "#7CE8B8" : "#C2BCEB"));
                if (a.Blocked) nets.Children.Add(Ui.Text("Windows hides nearby networks until Location is on for desktop apps (Settings > Privacy > Location).", 11.5, "#FFD27A", false, true));
            });
            var btns = Ui.Buttons(scan, Ui.Btn("Open router page", "#408EC5FF", () => { if (E.Primary != null) Ui.Open("http://" + E.Primary.Gateway); }));
            sp.Children.Add(btns);
            return sp;
        }

        static UIElement AlertsTab()
        {
            var sp = new StackPanel();
            var rows = Csv.Read(Path.Combine(Paths.Data, "Alerts.csv"), 300);
            rows.Reverse();
            var lv = new StackPanel();
            if (rows.Count == 0) lv.Children.Add(Ui.Text("No alerts yet.", 12.5, "#C2BCEB"));
            foreach (var r in rows)
            {
                string ts = r.ContainsKey("Timestamp") ? r["Timestamp"].Replace("T", "  ") : "";
                string ev = r.ContainsKey("Event") ? r["Event"] : "";
                string color = ev.Contains("New") ? "#FF9C9C" : ev.Contains("down") || ev.Contains("Slow") ? "#FFD27A" : ev.Contains("back") ? "#7CE8B8" : "#8EC5FF";
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                var top = new StackPanel { Orientation = Orientation.Horizontal };
                top.Children.Add(Ui.Text(ev, 12.5, color, true));
                var t = Ui.Text("   " + ts, 11, "#C2BCEB"); t.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(t);
                row.Children.Add(top);
                row.Children.Add(Ui.Text(r.ContainsKey("Detail") ? r["Detail"] : "", 11, "#C2BCEB", false, true));
                lv.Children.Add(row);
            }
            sp.Children.Add(Ui.Card(new ScrollViewer { Content = lv, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
            sp.Children.Add(Ui.Buttons(Ui.Btn("Open data folder", "#303A3270", () => Ui.Open(Paths.Root))));
            return sp;
        }
    }

    public static class FullTestWindow
    {
        static Window open;
        static Engine E => Engine.I;

        public static void Open()
        {
            if (open != null) { open.Activate(); return; }
            StackPanel body;
            var w = Ui.Dialog("Full Wi-Fi test", 560, out body, true);
            open = w; w.Closed += (s, e) => open = null;
            var status = Ui.Text("Ready. The test takes about a minute and downloads ~25 MB.", 13, null, true, true);
            var bar = new ProgressBar { IsIndeterminate = false, Height = 4, Margin = new Thickness(0, 8, 0, 0), Foreground = Ui.B("#8EC5FF"), Background = Ui.B("#20FFFFFF"), BorderThickness = new Thickness(0) };
            var results = new StackPanel();
            var sv = new ScrollViewer { Content = results, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(status); body.Children.Add(bar); body.Children.Add(sv);
            var repairTitle = Ui.Label("Repairs (asks for administrator permission)"); repairTitle.Margin = new Thickness(0, 12, 0, 0);
            var repairMsg = Ui.Text("", 12, "#FFD27A", false, true);
            Button run = null, report = null;
            report = Ui.Btn("Open report", "#508EC5FF", () => { var r = FullTest.LatestReport(); if (r != null) Ui.Open(r); });
            report.IsEnabled = FullTest.LatestReport() != null;
            run = Ui.Btn("Start test", "#607CE8B8", async () =>
            {
                run.IsEnabled = false; bar.IsIndeterminate = true; results.Children.Clear();
                var ft = new FullTest(msg => w.Dispatcher.BeginInvoke(new Action(() => status.Text = msg)));
                try { await Task.Run(() => ft.Run()); }
                catch (Exception ex) { Log.Error("full test", ex); status.Text = "The test stopped: " + ex.Message; }
                bar.IsIndeterminate = false; run.IsEnabled = true; run.Content = "Run again";
                if (ft.ReportPath == null) return;
                status.Text = ft.Overall + "  ·  " + ft.Likely;
                status.Foreground = ft.Overall == "NO ISSUES DETECTED" ? Ui.B("#7CE8B8") : Ui.B("#FFD27A");
                foreach (var c in ft.Results)
                {
                    string col = c.Status == "PASS" ? "#7CE8B8" : c.Status == "WARNING" ? "#FFD27A" : c.Status == "FAIL" ? "#FF9C9C" : "#8EC5FF";
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.Children.Add(Ui.Text(c.Status, 11, col, true));
                    var n = Ui.Text(c.Name, 12); Grid.SetColumn(n, 1); g.Children.Add(n);
                    var v = Ui.Text(c.Value, 12, null, true); Grid.SetColumn(v, 2); v.ToolTip = c.Explanation; g.Children.Add(v);
                    results.Children.Add(g);
                }
                foreach (var r in ft.Recommendations) results.Children.Add(Ui.Text("→ " + r, 12, "#C2BCEB", false, true));
                report.IsEnabled = true;
                if (ft.LocalIssue) repairMsg.Text = "Your router answers slowly or drops packets. Renewing the IP address or restarting Wi-Fi can help.";
                Ui.Open(ft.ReportPath);
            });
            body.Children.Add(Ui.Buttons(run, report));
            body.Children.Add(repairTitle);
            body.Children.Add(repairMsg);
            body.Children.Add(Ui.Buttons(
                Ui.Btn("Clear DNS cache", "#303A3270", () => repairMsg.Text = FullTest.FlushDns()),
                Ui.Btn("Renew IP", "#303A3270", async () => { repairMsg.Text = "Renewing (Wi-Fi drops for a few seconds)..."; repairMsg.Text = await Task.Run(() => FullTest.RenewIp()); }),
                Ui.Btn("Restart Wi-Fi", "#303A3270", async () =>
                {
                    if (!Ui.Confirm("Restart Wi-Fi", "This disconnects Wi-Fi for a few seconds. Continue?", "Restart")) return;
                    string name = E.Wifi.Name;
                    repairMsg.Text = "Restarting Wi-Fi..."; repairMsg.Text = await Task.Run(() => FullTest.RestartWifi(name));
                })));
            ((UniformGrid)body.Children[body.Children.Count - 1]).Margin = new Thickness(0, 6, 0, 0);
            w.Show();
        }
    }
}
