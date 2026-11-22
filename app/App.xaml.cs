using System;
using System.Linq;
using System.Net;
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

            // one copy only: a second start just shows the running one
            bool created;
            mutex = new Mutex(true, @"Local\AmbaanWiFiGuard.App", out created);
            if (!created)
            {
                try { EventWaitHandle.OpenExisting(analyzer ? @"Local\AmbaanWiFiGuard.Test" : @"Local\AmbaanWiFiGuard.Show").Set(); } catch { }
                Shutdown(); return;
            }
            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmbaanWiFiGuard.Show");
            var testEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmbaanWiFiGuard.Test");
            new Thread(() =>
            {
                var hs = new WaitHandle[] { showEvt, testEvt };
                while (true)
                {
                    int i = WaitHandle.WaitAny(hs);
                    Dispatcher.BeginInvoke(new Action(() => { if (i == 0) Widget?.ShowWidget(); else FullTestWindow.Open(); }));
                }
            }) { IsBackground = true }.Start();

            Paths.Init();
            Log.Write("start " + Paths.Version + (Paths.Portable ? " (portable)" : "") + (atLogin ? " at login" : ""));
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

            if (analyzer) FullTestWindow.Open();
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

        public void Balloon(string title, string text)
        {
            try { icon.BalloonTipTitle = title; icon.BalloonTipText = text; icon.ShowBalloonTip(6000); } catch { }
        }

        public void Dispose() { try { icon.Visible = false; icon.Dispose(); } catch { } }
    }
}
