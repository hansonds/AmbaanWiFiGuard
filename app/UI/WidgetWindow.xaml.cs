using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Ambaan
{
    public partial class WidgetWindow : Window
    {
        Engine E => Engine.I;
        HwndSource src;
        int shownVersion = -1;
        string shownSig = "";
        public bool Shown;
        DispatcherTimer slide;

        public WidgetWindow()
        {
            InitializeComponent();
            Logo.Source = Ui.AppIcon;
            Icon = Ui.AppIcon;
            DrawGauge(0);
            Traffic.ShowAxis = false; Traffic.Empty = "";
            More.Visibility = E.Cfg.Expanded ? Visibility.Visible : Visibility.Collapsed;
            BtnExpand.Content = E.Cfg.Expanded ? "" : "";

            BtnSpeed.Click += (s, e) => E.StartSpeedTest(true);
            BtnSpeed2.Click += (s, e) => E.StartSpeedTest(true);
            TileSpeedCard.MouseLeftButtonUp += (s, e) => E.StartSpeedTest(true);
            BtnHistory.Click += (s, e) => HistoryWindow.Open();
            BtnHistory2.Click += (s, e) => HistoryWindow.Open();
            BtnSettings.Click += (s, e) => SettingsWindow.Open();
            BtnExpand.Click += (s, e) => SetExpanded(!E.Cfg.Expanded);
            BtnHide.Click += (s, e) => HideWidget();
            BtnClose.Click += (s, e) => App.Quit();
            BtnFull.Click += (s, e) => FullTestWindow.Open();
            BtnScan.Click += (s, e) => E.RequestScan();
            BtnDnsCheck.Click += (s, e) => { E.Dns = null; E.RequestDns(); };
            BtnDnsFix.Click += (s, e) => Dialogs.DnsFix();
            BtnLocation.Click += (s, e) => Ui.Open("ms-settings:privacy-location");
            ChipDevices.MouseLeftButtonUp += (s, e) => SetExpanded(true);
            ChipDns.MouseLeftButtonUp += (s, e) => SetExpanded(true);
            Shell.MouseLeftButtonDown += Shell_MouseDown;
            SizeChanged += (s, e) => { Glass.Round(this, 18, E.Cfg.Glass); if (Shown && slide == null) PlaceAtCorner(false); };
            SourceInitialized += OnSourceInitialized;
            E.Changed += Refresh;
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            var h = new WindowInteropHelper(this).Handle;
            src = HwndSource.FromHwnd(h);
            src.AddHook(WndProc);
            Glass.ToolWindow(this);
            ApplyGlass();
            if (!Native.RegisterHotKey(h, 9001, 0x0002 | 0x0001 | 0x4000, 0x57))   // Ctrl+Alt+W
                Log.Write("Ctrl+Alt+W is used by another app");
        }

        public void ApplyGlass()
        {
            Glass.Apply(this, E.Cfg.Glass);
            Shell.Background = E.Cfg.Glass ? Ui.Res<Brush>("GlassFill") : Ui.Res<Brush>("GlassFillSolid");
            Glass.Round(this, 18, E.Cfg.Glass);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312 && w.ToInt32() == 9001) { Toggle(); handled = true; }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            try { Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 9001); } catch { }
            base.OnClosed(e);
        }

        // ---------- show / hide / position ----------
        public void ShowWidget()
        {
            if (!IsVisible) Show();
            Shown = true;
            PlaceAtCorner(false);
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            Refresh();
        }

        public void HideWidget()
        {
            Shown = false;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            a.Completed += (s, e) => { if (!Shown) Hide(); };
            BeginAnimation(OpacityProperty, a);
        }

        public void Toggle() { if (Shown) HideWidget(); else ShowWidget(); }

        public void SetExpanded(bool on)
        {
            E.Cfg.Expanded = on; E.Cfg.Save();
            More.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            BtnExpand.Content = on ? "" : "";
            BtnExpand.ToolTip = on ? "Show less" : "Show more";
            if (on && E.Dns == null) E.RequestDns();
            if (!Shown) ShowWidget();
            Refresh();
        }

        Point Target()
        {
            var c = E.Cfg;
            Point anchor = c.ScreenX != null ? new Point(c.ScreenX.Value, c.ScreenY.Value) : new Point(SystemParameters.WorkArea.Right - 10, SystemParameters.WorkArea.Bottom - 10);
            var wa = Ui.WorkArea(this, anchor);
            double m = 12;
            double x = c.Corner.EndsWith("Left") ? wa.Left + m : wa.Right - ActualWidth - m;
            double y = c.Corner.StartsWith("Top") ? wa.Top + m : wa.Bottom - ActualHeight - m;
            return new Point(x, y);
        }

        public void PlaceAtCorner(bool animate)
        {
            if (ActualWidth < 10) { UpdateLayout(); }
            var t = Target();
            if (!animate) { Left = t.X; Top = t.Y; return; }
            var from = new Point(Left, Top); int step = 0;
            slide?.Stop();
            slide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            slide.Tick += (s, e) =>
            {
                step++;
                double k = Math.Min(1.0, step / 16.0), ease = 1 - Math.Pow(1 - k, 3);
                Left = from.X + (t.X - from.X) * ease; Top = from.Y + (t.Y - from.Y) * ease;
                if (k >= 1) { slide.Stop(); slide = null; }
            };
            slide.Start();
        }

        void Shell_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInside(e.OriginalSource as DependencyObject, DevScroll)) return;
            try { DragMove(); } catch { return; }
            var center = new Point(Left + ActualWidth / 2, Top + ActualHeight / 2);
            var wa = Ui.WorkArea(this, center);
            string v = center.Y < wa.Top + wa.Height / 2 ? "Top" : "Bottom";
            string h = center.X < wa.Left + wa.Width / 2 ? "Left" : "Right";
            E.Cfg.Corner = v + h; E.Cfg.ScreenX = center.X; E.Cfg.ScreenY = center.Y; E.Cfg.Save();
            PlaceAtCorner(true);
        }

        static bool IsInside(DependencyObject d, DependencyObject parent)
        {
            while (d != null) { if (d == parent) return true; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
            return false;
        }

        void DeviceRow_Click(object sender, MouseButtonEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as DeviceRow;
            if (row != null) Dialogs.Device(row.Mac);
        }

        // ---------- drawing ----------
        void DrawGauge(int pct)
        {
            GaugeTrack.Data = Arc(270);
            GaugeValue.Data = pct > 0 ? Arc(Math.Max(1, 270.0 * pct / 100)) : null;
        }

        static Geometry Arc(double sweep)
        {
            double cx = 43, cy = 43, r = 38, start = 135;
            Func<double, Point> at = a => new Point(cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180));
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(at(start), false, false);
                c.ArcTo(at(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        static void Chip(Border b, TextBlock t, string text, string color)
        {
            t.Text = text;
            var c = Ui.C(color);
            t.Foreground = new SolidColorBrush(c);
            b.Background = new SolidColorBrush(Color.FromArgb(46, c.R, c.G, c.B));
        }

        public void Refresh()
        {
            if (!Shown) return;
            try
            {
                var w = E.Wifi;
                int sig = w.Signal ?? 0;
                DrawGauge(w.Connected ? sig : 0);
                GaugeText.Text = w.Connected ? (w.Signal != null ? sig + "%" : "on") : (E.Primary != null ? "LAN" : "off");
                GaugeSub.Text = w.Connected ? "signal" : (E.Primary != null ? "cable" : "offline");
                Ssid.Text = w.Connected ? (w.LocationBlocked ? "Wi-Fi (name hidden)" : w.Ssid) : (E.Primary != null ? "Wired network" : "Not connected");
                BandText.Text = w.Connected ? string.Join("  ·  ", new[] { w.Band, w.Channel > 0 ? "ch " + w.Channel : "", w.Gen, w.Rx > 0 ? w.Rx.ToString("0") + " Mbps link" : "" }.Where(x => x != "")) : (w.HasAdapter ? "Wi-Fi not connected" : "");
                DownText.Text = E.Down.ToString(E.Down >= 100 ? "0" : "0.0");
                UpText.Text = E.Up.ToString(E.Up >= 100 ? "0" : "0.0");

                var rm = E.RouterMs;
                Chip(ChipRouter, ChipRouterText, rm != null ? "Router " + rm + " ms" : "Router --", rm == null ? "#FF9C9C" : rm > 30 ? "#FFD27A" : "#7CE8B8");
                var nm = E.NetMs;
                bool down = E.Outages.DownSince != null;
                Chip(ChipNet, ChipNetText, down ? "Internet DOWN" : nm != null ? "Internet " + nm + " ms" : "Internet --", down || nm == null ? "#FF9C9C" : nm > 120 ? "#FFD27A" : "#7CE8B8");
                var online = E.Book.Devices.Where(d => d.Online).ToList();
                int unnamed = online.Count(d => !d.Named);
                Chip(ChipDevices, ChipDevicesText, E.Book.Devices.Count == 0 ? "Devices ..." : unnamed > 0 ? unnamed + " unnamed" : online.Count + " online", unnamed > 0 ? "#FFD27A" : "#7CE8B8");
                var dn = E.Dns;
                Chip(ChipDns, ChipDnsText, dn == null ? "DNS ..." : dn.Shield == "On" ? "Shield ON" : "Shield OFF", dn == null ? "#C2BCEB" : dn.Status == "Good" ? "#7CE8B8" : dn.Status == "Bad" ? "#FF9C9C" : "#FFD27A");
                BtnSpeed.Content = E.Speed.Busy ? "" : "";

                if (More.Visibility != Visibility.Visible) return;

                double peak = Math.Max(1, Math.Max(E.DownHist.DefaultIfEmpty(0).Max(), E.UpHist.DefaultIfEmpty(0).Max()));
                PeakText.Text = "PEAK " + peak.ToString("0.0") + " MBPS";
                var pad = Enumerable.Repeat((double?)null, Math.Max(0, 120 - E.DownHist.Count));
                Traffic.MinMax = 1;
                Traffic.Set(
                    new ChartView.Series { Values = pad.Concat(E.DownHist.Select(v => (double?)v)).ToArray(), Color = Ui.C("#8EC5FF"), Fill = true },
                    new ChartView.Series { Values = pad.Concat(E.UpHist.Select(v => (double?)v)).ToArray(), Color = Ui.C("#C9A7FF") });

                TileRouter.Text = rm != null ? rm + " ms" : "--";
                TileNet.Text = nm != null ? nm + " ms" : "--";
                TileSpeedLabel.Text = E.Speed.Busy ? "SPEED · TESTING" : "SPEED";
                TileSpeed.Text = E.Speed.Last != null ? E.Speed.Last + " Mbps" : E.Speed.LastAt != null ? "no response" : "soon...";
                var today = E.Usage.Day(DateTime.Today);
                TileData.Text = NetUtil.Size(today[0] + today[1]);
                var up = E.Outages.Uptime(1);
                TileUptime.Text = up != null ? up.Value.ToString(up.Value >= 99.95 ? "0" : "0.0") + "%" : "--";
                int outs = E.Outages.CountToday() + (down ? 1 : 0);
                TileOutages.Text = down ? "down now" : outs + " today";
                TileOutages.Foreground = down ? Ui.B("#FF9C9C") : Ui.Res<Brush>("Text");

                LocationHint.Visibility = w.LocationBlocked ? Visibility.Visible : Visibility.Collapsed;

                if (dn != null)
                {
                    DnsDot.Fill = dn.Status == "Good" ? Ui.B("#7CE8B8") : dn.Status == "Bad" ? Ui.B("#FF9C9C") : Ui.B("#FFD27A");
                    DnsHead.Text = dn.Headline;
                    DnsDetail.Text = dn.Issues.Count > 0 ? dn.Issues[0] : dn.Detail;
                    DnsChecked.Text = E.DnsBusy ? "CHECKING..." : "CHECKED " + dn.CheckedAt.ToString("HH:mm");
                    BtnDnsFix.Content = dn.Status == "Good" ? "Change" : "Fix it";
                }
                else { DnsHead.Text = "Checking DNS servers..."; DnsDetail.Text = ""; DnsChecked.Text = ""; }

                string live = E.Watch.DhcpState == "listening" ? "LIVE WATCH ON" : "CHECKS EVERY 20 S";
                DevHeader.Text = "DEVICES · " + online.Count + " ONLINE · " + live;
                BtnScan.Content = E.Scanning ? "scanning..." : "Scan now";
                string sig2 = string.Join("|", E.Book.Devices.Select(d => d.Mac + d.Title + d.Sub + d.Tag + d.Online));
                if (sig2 != shownSig) { shownSig = sig2; DeviceList.ItemsSource = E.Book.Devices.ToList(); }
                NoDevices.Visibility = E.Book.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Log.Error("widget refresh", ex); }
        }
    }
}
