using System;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Ambaan
{
    public partial class App : Application
    {
        Mutex mutex;
        public static WidgetWindow Widget;
        public static Tray TrayIcon;
        static bool quitting;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            bool analyzer = e.Args.Any(a => a.TrimStart('-', '/').Equals("analyzer", StringComparison.OrdinalIgnoreCase));
            bool atLogin = e.Args.Any(a => a.TrimStart('-', '/').Equals("atlogin", StringComparison.OrdinalIgnoreCase));
            Func<string, bool> has = n => e.Args.Any(a => a.TrimStart('-', '/').Equals(n, StringComparison.OrdinalIgnoreCase));

            // helper runs (started with admin rights by the normal copy): change DNS, then quit
            if (has("dns-apply") || has("dns-reset"))
            {
                try
                {
                    Paths.Init();
                    var c = Settings.Load();
                    if (has("dns-apply")) DnsGuard.Apply(c, true); else DnsGuard.Reset();
                    AutoStart.Sync(c);
                }
                catch (Exception ex) { Log.Error("dns helper", ex); }
                Shutdown(0); return;
            }

            // one copy only: a second start just shows the running one. Everyone may open these, so a normal copy can reach an admin copy.
            var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var ms = new MutexSecurity(); ms.AddAccessRule(new MutexAccessRule(world, MutexRights.FullControl, AccessControlType.Allow));
            var es = new EventWaitHandleSecurity(); es.AddAccessRule(new EventWaitHandleAccessRule(world, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            bool created = false;
            var until = DateTime.Now.AddSeconds(has("elevated-start") ? 20 : 0);   // restarting with admin rights: wait for the old copy to close
            while (true)
            {
                try { mutex = new Mutex(true, @"Local\AmbaanWiFiGuard.App", out created, ms); } catch (UnauthorizedAccessException) { created = false; }
                if (created || DateTime.Now >= until) break;
                try { mutex?.Dispose(); } catch { }
                Thread.Sleep(500);
            }
            if (!created)
            {
                try { EventWaitHandle.OpenExisting(analyzer ? @"Local\AmbaanWiFiGuard.Test" : @"Local\AmbaanWiFiGuard.Show").Set(); } catch { }
                Shutdown(); return;
            }
            bool cr;
            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmbaanWiFiGuard.Show", out cr, es);
            var testEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmbaanWiFiGuard.Test", out cr, es);


            Paths.Init();
            Log.Write("start " + Paths.Version + (Paths.Portable ? " (portable)" : "") + (atLogin ? " at login" : "") + (DnsGuard.IsAdmin ? " (admin)" : ""));

            // DNS Guard needs the admin copy. Started normally? Hand over to the sign-in task (admin, no prompt).
            if (!DnsGuard.IsAdmin && !atLogin && Settings.Load().DnsGuard && DnsGuard.TaskExists())
            {
                try { mutex.ReleaseMutex(); mutex.Dispose(); showEvt.Dispose(); testEvt.Dispose(); } catch { }
                if (DnsGuard.TaskRun()) { Log.Write("handed over to the admin task"); Shutdown(); return; }
                bool c2; mutex = new Mutex(true, @"Local\AmbaanWiFiGuard.App", out c2, ms);
                showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmbaanWiFiGuard.Show", out cr, es);
                testEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmbaanWiFiGuard.Test", out cr, es);
            }
            new Thread(() =>
            {
                var hs = new WaitHandle[] { showEvt, testEvt };
                while (true)
                {
                    int i = WaitHandle.WaitAny(hs);
                    Dispatcher.BeginInvoke(new Action(() => { if (i == 0) Widget?.ShowWidget(); else FullTestWindow.Open(); }));
                }
            }) { IsBackground = true }.Start();

            DispatcherUnhandledException += (s, ex) => { Log.Error("ui", ex.Exception); ex.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Write("FATAL " + ex.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            Assets.Extract();
            Engine.I = new Engine();
            Engine.I.Init();
            Engine.I.AlertRaised += (kind, mac) => AlertWindow.Pop(kind, mac);
            Engine.I.BalloonRaised += (t, x) => TrayIcon?.Balloon(t, x);

            TrayIcon = new Tray();
            Widget = new WidgetWindow();
            Widget.ShowWidget();

            if (!Engine.I.Cfg.AutoStartAsked)
            {
                Engine.I.Cfg.AutoStartAsked = true; Engine.I.Cfg.Save();
                TrayIcon.Balloon("Ambaan will start with Windows", "You can turn this off in Settings (the gear icon on the widget).");
            }

            if (atLogin)
            {
                // just signed in: give Wi-Fi time to connect before the first checks
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                t.Tick += (s, ev) => { t.Stop(); Engine.I.Start(); };
                t.Start();
            }
            else Engine.I.Start();
            if (has("elevated-start")) { Engine.I.GuardCheck(true); TrayIcon.Balloon("DNS Guard is on", Engine.I.Cfg.DnsPreset + " is set and Ambaan keeps it that way. From now on it starts with admin rights at sign-in, no prompts."); }

            if (analyzer) FullTestWindow.Open();

            // for automatic screenshots in the build: --show=settings|history|alert-new|alert-slow|alert-fast|fulltest|about
            var show = e.Args.FirstOrDefault(a => a.StartsWith("--show="));
            if (show != null)
            {
                var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t2.Tick += (s, ev) =>
                {
                    t2.Stop();
                    switch (show.Substring(7))
                    {
                        case "settings": SettingsWindow.Open(); break;
                        case "history": HistoryWindow.Open(); break;
                        case "fulltest": FullTestWindow.Open(); break;
                        case "dns": Dialogs.DnsPanel(); break;
                        case "network": HistoryWindow.Open("Network"); break;
                        case "alert-new": AlertWindow.Pop("New", Engine.I.Book.Devices.Select(d => d.Mac).FirstOrDefault() ?? "AA-BB-CC-DD-EE-FF", true); break;
                        case "alert-slow": Engine.I.Speed.Last = 3.2; Engine.I.Speed.Diag = "Weak Wi-Fi signal (31%). Move closer to the router or switch to 5 GHz."; AlertWindow.Pop("Slow", null, true, "Dhamu"); break;
                        case "alert-fast": Engine.I.Speed.Last = 92.4; Engine.I.Speed.Diag = "Signal 88%  ·  router 3 ms  ·  internet 21 ms  ·  tested over Wi-Fi"; AlertWindow.Pop("Fast", null, true); break;
                    }
                };
                t2.Start();
            }
        }

        public static void Quit()
        {
            if (quitting) return;
            quitting = true;
            Log.Write("exit");
            try { Engine.I?.Stop(); } catch { }
            try { TrayIcon?.Dispose(); } catch { }
            Current.Shutdown();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            try { Engine.I?.Stop(); TrayIcon?.Dispose(); } catch { }
            base.OnSessionEnding(e);
        }
    }

    /// <summary>Tray icon + menu (Windows Forms NotifyIcon).</summary>
    public class Tray : IDisposable
    {
        readonly WF.NotifyIcon icon;

        public Tray()
        {
            icon = new WF.NotifyIcon { Text = "Ambaan WiFi Guard - Ctrl+Alt+W to show/hide", Visible = true };
            try { using (var s = Assets.Open("assets/ambaan.ico")) icon.Icon = new System.Drawing.Icon(s, WF.SystemInformation.SmallIconSize); }
            catch { icon.Icon = System.Drawing.SystemIcons.Information; }
            var m = new WF.ContextMenuStrip();
            m.Items.Add("Show / hide   (Ctrl+Alt+W)", null, (s, e) => App.Widget?.Toggle());
            m.Items.Add("Expand / collapse", null, (s, e) => App.Widget?.SetExpanded(!Engine.I.Cfg.Expanded));
            var corner = new WF.ToolStripMenuItem("Move to corner");
            foreach (var c in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
            {
                string cc = c;
                corner.DropDownItems.Add(c.Replace("Top", "Top ").Replace("Bottom", "Bottom ").Trim(), null, (s, e) =>
                {
                    Engine.I.Cfg.Corner = cc; Engine.I.Cfg.ScreenX = null; Engine.I.Cfg.ScreenY = null; Engine.I.Cfg.Save();
                    if (App.Widget != null) { if (!App.Widget.Shown) App.Widget.ShowWidget(); else App.Widget.PlaceAtCorner(true); }
                });
            }
            m.Items.Add(corner);
            m.Items.Add("Settings...", null, (s, e) => SettingsWindow.Open());
            var dns = new WF.ToolStripMenuItem("DNS");
            var live4 = new WF.ToolStripMenuItem("...") { Enabled = false }; var live6 = new WF.ToolStripMenuItem("...") { Enabled = false };
            dns.DropDownItems.Add(live4); dns.DropDownItems.Add(live6); dns.DropDownItems.Add("-");
            foreach (var pr in DnsGuard.Presets)
            {
                var pp = pr;
                var it = new WF.ToolStripMenuItem(pr.Name + "   (" + pr.Note + ")") { Tag = pr.Name };
                it.Click += (s, e) => QuickSwitch(pp);
                dns.DropDownItems.Add(it);
            }
            dns.DropDownItems.Add("-");
            dns.DropDownItems.Add("DNS settings and DNS Guard...", null, (s, e) => Dialogs.DnsPanel());
            dns.DropDownItems.Add("Flush DNS cache", null, (s, e) => Balloon("DNS", FullTest.FlushDns()));
            dns.DropDownOpening += (s, e) =>
            {
                var l = DnsGuard.Live();
                live4.Text = l[0] + "  IPv4: " + l[1]; live6.Text = "IPv6: " + l[2];
                foreach (var x in dns.DropDownItems.OfType<WF.ToolStripMenuItem>()) if (x.Tag is string t) x.Checked = Engine.I.Cfg.DnsGuard && t == Engine.I.Cfg.DnsPreset;
            };
            m.Items.Add(dns);
            m.Items.Add("Speed test now", null, (s, e) => Engine.I.StartSpeedTest(true));
            m.Items.Add("Full Wi-Fi test", null, (s, e) => FullTestWindow.Open());
            m.Items.Add("History and tools", null, (s, e) => HistoryWindow.Open());
            m.Items.Add("Open data folder", null, (s, e) => Ui.Open(Paths.Root));
            m.Items.Add("About", null, (s, e) => Dialogs.About());
            m.Items.Add("-");
            m.Items.Add("Exit", null, (s, e) => App.Quit());
            icon.ContextMenuStrip = m;
            icon.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) App.Widget?.Toggle(); };
            icon.BalloonTipClicked += (s, e) => { App.Widget?.SetExpanded(true); };
        }

        /// <summary>Tray > DNS > preset: set it now (admin copy) or open the DNS panel to ask for permission.</summary>
        static async void QuickSwitch(DnsPreset p)
        {
            var c = Engine.I.Cfg;
            if (!DnsGuard.IsAdmin) { Dialogs.DnsPanel(); return; }
            c.DnsPreset = p.Name; c.DnsP4 = p.P4; c.DnsS4 = p.S4; c.DnsP6 = p.P6; c.DnsS6 = p.S6; c.DnsGuard = true; c.Save();
            await System.Threading.Tasks.Task.Run(() => DnsGuard.Apply(c, true));
            Engine.I.Book.WriteAlert("DNS changed", p.Name + " (DNS Guard on)");
            Engine.I.Dns = null; Engine.I.RequestDns();
            App.TrayIcon?.Balloon("DNS set", p.Name + ": " + p.P4 + ", " + p.S4);
        }

        public void Balloon(string title, string text)
        {
            try { icon.BalloonTipTitle = title; icon.BalloonTipText = text; icon.ShowBalloonTip(6000); } catch { }
        }

        public void Dispose() { try { icon.Visible = false; icon.Dispose(); } catch { } }
    }
}
