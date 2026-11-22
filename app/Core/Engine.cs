using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Ambaan
{
    public class SpeedState
    {
        public double? Last; public DateTime? LastAt; public double? Usual;
        public List<double> Hist = new List<double>();
        public bool Busy, Manual, PingBusy, WasSlow;
        public DateTime NextTest = DateTime.Now.AddMinutes(2), NextPing = DateTime.Now.AddSeconds(30), LastAlert = DateTime.MinValue;
        public int SlowCount;
        public int? PingAvg, PingLoss;
        public string Diag = "", Via = "";
    }

    /// <summary>Everything that runs in the background. One 1-second tick on the UI thread; slow work goes to the thread pool.</summary>
    public class Engine
    {
        public static Engine I;
        public Settings Cfg;
        public DeviceBook Book;
        public readonly DeviceWatcher Watch = new DeviceWatcher();
        public OutageTracker Outages;
        public UsageTracker Usage;
        public WifiState Wifi = new WifiState();
        public Primary Primary;
        public double Down, Up;
        public readonly List<double> DownHist = new List<double>(), UpHist = new List<double>();
        Dictionary<string, long[]> lastCounters; DateTime lastCountersAt;
        public int? RouterMs, NetMs;
        bool routerBusy, netBusy, wifiBusy, netChanged;
        public DnsResult Dns; public bool DnsBusy; DateTime dnsDue = DateTime.MinValue;
        public bool Scanning; DateTime scanDue = DateTime.MinValue;
        public readonly SpeedState Speed = new SpeedState();
        public DateTime QuietRejoinUntil = DateTime.MinValue;
        DispatcherTimer timer; int tick;
        public bool Running;

        public event Action Changed;
        public event Action<string, string> AlertRaised;   // kind, mac
        public event Action<string, string> BalloonRaised; // title, text

        public void Init()
        {
            Cfg = Settings.Load();
            Book = new DeviceBook(this);
            Outages = new OutageTracker();
            Usage = new UsageTracker();
            AutoStart.Apply(Cfg.AutoStart);
            Primary = NetUtil.GetPrimary();
            if (Primary != null) Watch.Prefix = Primary.Prefix24;
            SystemEvents.PowerModeChanged += OnPower;
            NetworkChange.NetworkAddressChanged += (s, e) => netChanged = true;
        }

        public void Start()
        {
            if (Running) return;
            Running = true;
            Watch.Start();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Log.Write("engine started");
        }

        public void Stop()
        {
            try { timer?.Stop(); Watch.Stop(); Outages.Save(); Usage.Save(); } catch { }
        }

        void OnPower(object s, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.Suspend) { Outages.Suspend(); Usage.Save(); }
                else if (e.Mode == PowerModes.Resume)
                {
                    Log.Write("resume from sleep");
                    lastCounters = null; Usage.Reset();
                    QuietRejoinUntil = DateTime.Now.AddMinutes(2);   // devices look "back" after sleep: don't shout about them
                    Speed.NextTest = DateTime.Now.AddMinutes(1); Speed.SlowCount = 0;
                    netChanged = true;
                }
            }
            catch (Exception ex) { Log.Error("power", ex); }
        }

        public void RaiseAlert(string kind, string mac) { try { AlertRaised?.Invoke(kind, mac); } catch (Exception ex) { Log.Error("alert", ex); } }
        public void RaiseBalloon(string title, string text) { try { BalloonRaised?.Invoke(title, text); } catch (Exception ex) { Log.Error("balloon", ex); } }
        public void AskName(string mac, string ip) { Watch.AskName(mac, ip); }
        public void ScanSoon() { scanDue = DateTime.Now.AddSeconds(5); }

        void Safe(string what, Action a) { try { a(); } catch (Exception ex) { Log.Error(what, ex); } }

        void Tick()
        {
            tick++;
            var now = DateTime.Now;
            Safe("drain", () => Book.Drain(Watch));
            Safe("speed", UpdateSlowWatch);
            Safe("traffic", UpdateTraffic);
            if (tick % 2 == 1) { UpdateWifi(); PingRouter(); }
            if (tick % 10 == 1) PingInternet();
            if (tick % 30 == 1 || netChanged)
            {
                netChanged = false;
                Safe("primary", () =>
                {
                    var p = NetUtil.GetPrimary();
                    if (p != null) { Primary = p; Watch.Prefix = p.Prefix24; }
                });
            }
            if (tick % 20 == 0) Safe("build", Book.Build);   // ages online -> offline
            if (now >= scanDue) { scanDue = now.AddSeconds(20); RequestScan(); }
            if (now >= dnsDue) { dnsDue = now.AddMinutes(10); RequestDns(); }
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Error("ui refresh", ex); }
        }

        async void UpdateWifi()
        {
            if (wifiBusy) return; wifiBusy = true;
            try { Wifi = await Task.Run(() => WlanApi.Current()); }
            catch (Exception ex) { Log.Error("wifi", ex); }
            finally { wifiBusy = false; }
        }

        void UpdateTraffic()
        {
            var now = DateTime.Now;
            var c = NetUtil.Counters();
            if (lastCounters != null)
            {
                double dt = (now - lastCountersAt).TotalSeconds;
                long rx = 0, tx = 0;
                foreach (var kv in c)
                {
                    long[] p;
                    if (!lastCounters.TryGetValue(kv.Key, out p)) continue;
                    rx += Math.Max(0, kv.Value[0] - p[0]); tx += Math.Max(0, kv.Value[1] - p[1]);
                }
                if (dt > 0.2)
                {
                    Down = Math.Round(rx * 8 / 1e6 / dt, 1); Up = Math.Round(tx * 8 / 1e6 / dt, 1);
                }
            }
            lastCounters = c; lastCountersAt = now;
            DownHist.Add(Down); UpHist.Add(Up);
            while (DownHist.Count > 120) { DownHist.RemoveAt(0); UpHist.RemoveAt(0); }
            if (tick % 5 == 0) Usage.Sample(c);
        }

        async void PingRouter()
        {
            if (routerBusy) return; routerBusy = true;
            try { RouterMs = await NetUtil.PingMs(Primary?.Gateway?.ToString(), 400); }
            finally { routerBusy = false; }
        }

        async void PingInternet()
        {
            if (netBusy) return; netBusy = true;
            try
            {
                var ms = await NetUtil.PingMs("1.1.1.1", 800);
                if (ms == null) ms = await NetUtil.PingMs("8.8.8.8", 800);
                NetMs = ms;
                bool linkUp = Wifi.Connected || Primary != null;
                string msg = Outages.Check(ms != null, RouterMs != null, linkUp);
                if (msg != null)
                {
                    Book.WriteAlert(ms == null ? "Internet down" : "Internet back", msg);
                    if (Cfg.OutageAlerts) RaiseBalloon(ms == null ? "Internet is down" : "Internet is back", msg);
                }
            }
            catch (Exception ex) { Log.Error("ping net", ex); }
            finally { netBusy = false; }
        }

        public async void RequestScan()
        {
            if (Scanning) return; Scanning = true;
            try
            {
                var pr = Primary; var own = Book.Own;
                var r = await Task.Run(() => DeviceWatcher.Sweep(pr, own));
                Book.MergeScan(r);
            }
            catch (Exception ex) { Log.Error("scan", ex); }
            finally { Scanning = false; }
        }

        public async void RequestDns()
        {
            if (DnsBusy) return; DnsBusy = true;
            try
            {
                var r = await Task.Run(() => DnsShield.Check());
                var prev = Dns; Dns = r;
                if (r.Status != "Good" && (prev == null || prev.Status == "Good")) RaiseBalloon("DNS Shield", r.Headline);
            }
            catch (Exception ex) { Log.Error("dns", ex); }
            finally { DnsBusy = false; }
        }

        // ---------------- slow / fast Wi-Fi watch ----------------
        // Every 60 s: 4 pings (tiny). Every SpeedEvery min, or when pings look bad: a 5 MB download over Wi-Fi.
        // Slow = below SlowMbps twice in a row (re-test after 20 s). Popup at most once per 15 min.

        public int SpeedEvery => Math.Max(10, Cfg.SpeedEvery);

        public async void StartSpeedTest(bool manual)
        {
            var sp = Speed;
            if (sp.Busy) { if (manual) sp.Manual = true; return; }
            sp.Busy = true; sp.Manual = manual;
            IPAddress bind = Wifi.Connected ? NetUtil.WifiIp(Wifi.Name) : null;
            sp.Via = bind != null ? "Wi-Fi" : "cable";
            SpeedResult r = null;
            try { r = await Task.Run(() => SpeedTest.Download(bind, manual ? 10_000_000 : 5_000_000)); }
            catch (Exception ex) { Log.Error("speedtest", ex); }
            sp.Busy = false;
            try { OnSpeedResult(r); } catch (Exception ex) { Log.Error("speed result", ex); }
        }

        async void StartPingProbe()
        {
            var sp = Speed;
            if (sp.PingBusy) return; sp.PingBusy = true;
            try
            {
                int ok = 0; long sum = 0;
                for (int i = 0; i < 4; i++)
                {
                    var ms = await NetUtil.PingMs("1.1.1.1", 1000);
                    if (ms != null) { ok++; sum += ms.Value; }
                }
                sp.PingLoss = (4 - ok) * 25; sp.PingAvg = ok > 0 ? (int)(sum / ok) : (int?)null;
                if ((sp.PingLoss >= 50 || (sp.PingAvg ?? 0) > 250) && DateTime.Now < sp.NextTest) sp.NextTest = DateTime.Now;   // looks bad: test now
            }
            catch (Exception ex) { Log.Error("ping probe", ex); }
            finally { sp.PingBusy = false; }
        }

        void UpdateSlowWatch()
        {
            var sp = Speed; var now = DateTime.Now;
            if (!sp.PingBusy && now >= sp.NextPing) { sp.NextPing = now.AddSeconds(60); StartPingProbe(); }
            if (!sp.Busy && now >= sp.NextTest) { sp.NextTest = now.AddMinutes(SpeedEvery); StartSpeedTest(false); }
        }

        string SlowDiagnosis()
        {
            var sig = Wifi.Signal;
            if (Wifi.Connected && sig != null && sig < 40) return "Weak Wi-Fi signal (" + sig + "%). Move closer to the router or switch to 5 GHz.";
            if (RouterMs != null && RouterMs > 50) return "The router is slow to answer (" + RouterMs + " ms). Too many devices busy, or restart the router.";
            if (sig != null) return "Signal is fine (" + sig + "%) and the router answers quickly, so the internet line itself is slow. If it lasts, contact your internet provider.";
            return "The internet line itself looks slow.";
        }

        string FastDetail()
        {
            var bits = new List<string>();
            if (Wifi.Connected && Wifi.Signal != null) bits.Add("Signal " + Wifi.Signal + "%");
            if (RouterMs != null) bits.Add("router " + RouterMs + " ms");
            if (Speed.PingAvg != null) bits.Add("internet " + Speed.PingAvg + " ms");
            bits.Add("tested over " + Speed.Via);
            return string.Join("  ·  ", bits);
        }

        void OnSpeedResult(SpeedResult r)
        {
            var sp = Speed; var now = DateTime.Now;
            if (r != null && r.Refused)
            {
                if (sp.Manual) { sp.Manual = false; RaiseBalloon("Speed test", "The test server is busy. Try again in a minute."); }
                sp.NextTest = now.AddMinutes(3);
                return;
            }
            double? mbps = r != null && r.Ok ? r.Mbps : null;
            sp.Last = mbps; sp.LastAt = now;
            SpeedHistory.Add(mbps, sp.PingAvg ?? NetMs, sp.Via);
            double limit = Cfg.SlowMbps;
            bool isSlow = mbps == null || mbps < limit;
            if (!isSlow)
            {
                sp.Hist.Add(mbps.Value); while (sp.Hist.Count > 12) sp.Hist.RemoveAt(0);
                var sorted = sp.Hist.OrderBy(x => x).ToList(); sp.Usual = Math.Round(sorted[sorted.Count / 2]);
            }
            sp.Diag = isSlow ? SlowDiagnosis() : FastDetail();
            if (sp.Manual)
            {
                sp.Manual = false;   // you pressed Speed test: always answer
                Book.WriteAlert("Speed test", mbps + " Mbps via " + sp.Via);
                if (isSlow) { sp.WasSlow = true; RaiseAlert("Slow", null); }
                else if (mbps >= Cfg.FastMbps) { sp.WasSlow = false; sp.SlowCount = 0; RaiseAlert("Fast", null); }
                else { sp.WasSlow = false; sp.SlowCount = 0; RaiseAlert("Ok", null); }
                sp.NextTest = now.AddMinutes(SpeedEvery);
            }
            else if (isSlow)
            {
                sp.SlowCount++;
                if (sp.SlowCount == 1) sp.NextTest = now.AddSeconds(20);   // confirm before shouting
                else if ((now - sp.LastAlert).TotalMinutes >= 15)
                {
                    sp.LastAlert = now; sp.WasSlow = true;
                    Book.WriteAlert("Slow Wi-Fi", mbps + " Mbps via " + sp.Via + "; signal " + Wifi.Signal + "%; router " + RouterMs + " ms");
                    RaiseAlert("Slow", null);
                    sp.NextTest = now.AddMinutes(5);
                }
                else sp.NextTest = now.AddMinutes(5);
            }
            else
            {
                sp.SlowCount = 0;
                string today = now.ToString("yyyy-MM-dd");
                if (mbps >= Cfg.FastMbps && Cfg.FastMode != "Off")
                {
                    if (Cfg.FastMode == "Every" || sp.WasSlow || Cfg.LastFastDay != today)
                    {
                        sp.WasSlow = false; Cfg.LastFastDay = today; Cfg.Save();
                        RaiseAlert("Fast", null);
                    }
                }
                sp.NextTest = now.AddMinutes(SpeedEvery);
            }
        }

        public void SettingsChanged(int oldEvery)
        {
            if (SpeedEvery != oldEvery && !Speed.Busy && Speed.SlowCount == 0)
            {
                var b = Speed.LastAt ?? DateTime.Now;
                var next = b.AddMinutes(SpeedEvery); var soon = DateTime.Now.AddMinutes(1);
                Speed.NextTest = next < soon ? soon : next;
            }
        }
    }
}
