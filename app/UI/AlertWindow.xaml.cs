using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Ambaan
{
    /// <summary>The pop-up with Ambaan / Dhamu: animated GIF, voice, mouth-sync bounce and a panic shake on loud words.</summary>
    public partial class AlertWindow : Window
    {
        static AlertWindow inst;
        static MediaPlayer player;
        static readonly Random rnd = new Random();
        Engine E => Engine.I;
        string kind, mac;
        GifFrames gif;
        readonly Stopwatch clock = new Stopwatch();
        double[] env;
        bool playing, voiceFallback;
        double amp;
        readonly DispatcherTimer frame = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        readonly DispatcherTimer autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        Action act1, act2;

        AlertWindow()
        {
            InitializeComponent();
            frame.Tick += (s, e) => Animate();
            autoClose.Tick += (s, e) => CloseAlert();
            BtnClose.Click += (s, e) => CloseAlert();
            Btn1.Click += (s, e) => act1?.Invoke();
            Btn2.Click += (s, e) => act2?.Invoke();
            MouseMove += (s, e) => { autoClose.Stop(); autoClose.Start(); };
            SourceInitialized += (s, e) => Glass.ToolWindow(this);
            Closing += (s, e) => { e.Cancel = true; CloseAlert(); };
        }

        public static void Pop(string kind, string mac, bool preview = false, string characterOverride = null)
        {
            try
            {
                if (inst == null) inst = new AlertWindow();
                inst.Open(kind, mac, preview, characterOverride);
            }
            catch (Exception ex) { Log.Error("alert window", ex); }
        }

        static IEnumerable<string> ClipNames(string kind)
        {
            switch (kind)
            {
                case "New": return new[] { "new-device" };
                case "Slow": return new[] { "slow-wifi" };
                case "Fast": return new[] { "fast-wifi" };
                case "Unnamed": return new[] { "rejoin", "new-device" };
                default: return new string[0];
            }
        }

        static string FindClip(string prefix, string kind)
        {
            foreach (var n in ClipNames(kind))
                foreach (var ext in new[] { "mp3", "wav", "m4a" })
                {
                    var f = Assets.Find(prefix + n + "." + ext);
                    if (f != null) return f;
                }
            return null;
        }

        void Open(string kind, string mac, bool preview, string characterOverride)
        {
            this.kind = kind; this.mac = mac;
            string ch = characterOverride ?? E.Cfg.Character;
            string prefix = ch == "Dhamu" ? "dhamu-" : "";

            // picture: a per-alert GIF (timed to its voice) if there is one, else the character's main GIF
            string gifPath = null;
            foreach (var n in ClipNames(kind)) { gifPath = Assets.Find(prefix + n + ".gif"); if (gifPath != null) break; }
            if (gifPath == null) gifPath = Assets.Find(ch == "Dhamu" ? "dhamu.gif" : "new-device.gif") ?? Assets.Find("new-device.gif");
            gif = GifFrames.Load(gifPath);
            Character.Source = gif?.Frames[0];

            Fill();
            PlaceNearWidget();
            if (!IsVisible) Show();
            // pop in
            PopScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
            PopScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            clock.Restart();
            frame.Start();
            autoClose.Stop(); autoClose.Start();
            PlayVoice(prefix, kind);
        }

        void PlayVoice(string prefix, string kind)
        {
            playing = false; env = null; voiceFallback = false;
            if (!E.Cfg.Voice || kind == "Ok" || E.Cfg.InQuietHours()) return;
            string file = FindClip(prefix, kind);
            if (file == null) { System.Media.SystemSounds.Exclamation.Play(); return; }
            try
            {
                if (player == null) player = new MediaPlayer();
                player.Stop();
                player.Open(new Uri(file));
                player.Volume = E.Cfg.Volume / 100.0;
                player.Play();
                playing = true;
                env = Json.LoadNumbers(Path.ChangeExtension(file, ".env.json"));
                voiceFallback = env == null;   // custom sound without lip-sync data: wobble while it plays
            }
            catch (Exception ex) { Log.Error("voice", ex); System.Media.SystemSounds.Exclamation.Play(); }
        }

        void Fill()
        {
            Line.Visibility = Visibility.Visible; Big.Visibility = Visibility.Visible; Detail.Visibility = Visibility.Visible;
            string accent;
            if (kind == "New" || kind == "Unnamed")
            {
                var d = E.Book.Row(mac ?? "");
                accent = kind == "New" ? "#FF9C9C" : "#FFD27A";
                Title1.Text = kind == "New" ? "New device on your Wi-Fi!" : "Unknown device is back!";
                Big.Visibility = Visibility.Collapsed;
                Line.Text = (!string.IsNullOrEmpty(d.HostName) ? d.HostName + "  ·  " : "") + d.Ip;
                Detail.Text = d.Vendor != null ? "Made by " + d.Vendor + "  ·  " + d.Mac : NetUtil.IsRandomMac(d.Mac) ? "Private address (phone or laptop)  ·  " + d.Mac : d.Mac;
                Set(Btn1, "It's mine", "#607CE8B8", () => { string m = mac; CloseAlert(); Dialogs.Device(m); });
                Set(Btn2, "Block...", "#60FF9C9C", () => Dialogs.BlockHelp(mac));
            }
            else
            {
                var sp = E.Speed;
                accent = kind == "Fast" ? "#7CE8B8" : kind == "Slow" ? "#8EC5FF" : "#C9A7FF";
                Title1.Text = kind == "Fast" ? "Wi-Fi is flying today!" : kind == "Ok" ? "Speed is OK, not super fast" : "Wi-Fi is slow again!";
                Big.Text = sp.Last != null ? sp.Last + " Mbps" : "No internet";
                Line.Visibility = Visibility.Collapsed;
                Detail.Text = sp.Diag + (sp.Usual != null && kind == "Slow" ? "  Usual: " + sp.Usual + " Mbps." : "");
                if (kind == "Slow") Set(Btn1, "Full test", "#60C9A7FF", () => { CloseAlert(); FullTestWindow.Open(); });
                else Set(Btn1, "Test again", "#60C9A7FF", () => { CloseAlert(); E.StartSpeedTest(true); });
                Set(Btn2, "OK", "#608EC5FF", CloseAlert);
            }
            Accent.Background = Ui.B(accent);
            Title1.Foreground = Ui.B(accent);
        }

        void Set(System.Windows.Controls.Button b, string text, string tint, Action a)
        {
            b.Content = text; b.Background = Ui.B(tint);
            if (b == Btn1) act1 = a; else act2 = a;
        }

        void PlaceNearWidget()
        {
            UpdateLayout();
            double w = Width, h = ActualHeight > 0 ? ActualHeight : 380;
            var wd = App.Widget;
            var c = E.Cfg;
            Rect wa;
            if (wd != null && wd.Shown && wd.IsVisible)
            {
                wa = Ui.WorkArea(wd, new Point(wd.Left + wd.ActualWidth / 2, wd.Top + wd.ActualHeight / 2));
                double x = c.Corner.EndsWith("Left") ? wd.Left : wd.Left + wd.ActualWidth - w;
                double y = c.Corner.StartsWith("Bottom") ? wd.Top - h - 6 : wd.Top + wd.ActualHeight + 6;
                if (y < wa.Top || y + h > wa.Bottom)   // no room above/below: sit beside the widget
                {
                    y = Math.Max(wa.Top + 10, Math.Min(wd.Top, wa.Bottom - h - 10));
                    x = c.Corner.EndsWith("Left") ? wd.Left + wd.ActualWidth + 8 : wd.Left - w - 8;
                }
                Left = x; Top = y; return;
            }
            wa = SystemParameters.WorkArea;
            Left = c.Corner.EndsWith("Left") ? wa.Left + 12 : wa.Right - w - 12;
            Top = c.Corner.StartsWith("Top") ? wa.Top + 12 : wa.Bottom - h - 12;
        }

        void Animate()
        {
            if (!IsVisible) { frame.Stop(); return; }
            if (gif != null) Character.Source = gif.At(clock.ElapsedMilliseconds);
            double a = 0;
            if (playing && player != null)
            {
                bool done = player.NaturalDuration.HasTimeSpan && player.Position >= player.NaturalDuration.TimeSpan;
                if (done) playing = false;
                else if (env != null)
                {
                    int i = (int)Math.Floor(player.Position.TotalSeconds / 0.04);
                    if (i >= 0 && i < env.Length) a = env[i];
                }
                else if (voiceFallback && player.Position.TotalMilliseconds > 0) a = 0.25 + 0.35 * rnd.NextDouble();
            }
            a = Math.Max(a, amp * 0.7);   // ease back to rest
            if (a < 0.01) a = 0;
            amp = a;
            double sc = 1 + 0.14 * a;
            CharScale.ScaleX = sc; CharScale.ScaleY = sc;
            CharMove.Y = -6 * a;
            CharMove.X = 0;
            if (a > 0.45) { CharMove.X = rnd.Next(-3, 4) * a; CharMove.Y += rnd.Next(-2, 3) * a; }   // panic shake on loud words
        }

        void CloseAlert()
        {
            autoClose.Stop();
            try { player?.Stop(); } catch { }
            playing = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (s, e) => { frame.Stop(); Hide(); };
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
