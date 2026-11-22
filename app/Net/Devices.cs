using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Ambaan
{
    public class Obs
    {
        public string Source, Mac, Ip, Host, Vendor;
        public bool Alive = true;
    }

    public class SweepResult
    {
        public string Gateway, GatewayMac;
        public List<Obs> Rows = new List<Obs>();
    }

    /// <summary>Background watchers. They only report what they see; DeviceBook decides what it means (on the UI thread).
    ///  1. DHCP listener: every phone/laptop broadcasts a DHCP request the moment it joins the Wi-Fi.
    ///  2. ARP table every 3 s: catches devices that talk without DHCP.
    ///  3. Ping sweep of the /24 every 20 s (run by the engine).
    ///  4. Name finder: asks the router's DNS and NetBIOS what each device is called.</summary>
    public class DeviceWatcher
    {
        public readonly ConcurrentQueue<Obs> Queue = new ConcurrentQueue<Obs>();
        public readonly ConcurrentQueue<string> ForgetQueue = new ConcurrentQueue<string>();
        readonly BlockingCollection<string[]> nameJobs = new BlockingCollection<string[]>();
        readonly ConcurrentDictionary<string, bool> nameAsked = new ConcurrentDictionary<string, bool>();
        public volatile string Prefix;
        public volatile string DhcpState = "starting";
        volatile bool stop;

        public void Start()
        {
            Run("dhcp", DhcpLoop);
            Run("arp", ArpLoop);
            Run("names", NameLoop);
        }

        public void Stop() { stop = true; nameJobs.CompleteAdding(); }

        void Run(string name, Action body)
        {
            var t = new Thread(() =>
            {
                while (!stop)
                {
                    try { body(); }
                    catch (Exception ex) { Log.Error("watcher " + name, ex); }
                    if (!stop) Thread.Sleep(5000);   // crashed: restart the loop
                }
            }) { IsBackground = true, Name = name };
            t.Start();
        }

        void DhcpLoop()
        {
            while (!stop)
            {
                UdpClient udp = null;
                try
                {
                    udp = new UdpClient();
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, 67));
                    udp.Client.ReceiveTimeout = 1000;
                    DhcpState = "listening";
                    Log.Write("DHCP listener started on UDP 67");
                    while (!stop)
                    {
                        byte[] b;
                        try { var ep = new IPEndPoint(IPAddress.Any, 0); b = udp.Receive(ref ep); }
                        catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut) { continue; }
                        ParseDhcp(b);
                    }
                }
                catch (Exception ex)
                {
                    DhcpState = "off";
                    Log.Write("DHCP listener unavailable: " + ex.Message);
                    for (int i = 0; i < 60 && !stop; i++) Thread.Sleep(1000);
                }
                finally { try { udp?.Close(); } catch { } }
            }
        }

        void ParseDhcp(byte[] b)
        {
            if (b.Length < 240 || b[0] != 1 || b[236] != 99 || b[237] != 130 || b[238] != 83 || b[239] != 99) return;
            string mac = NetUtil.Mac(b.Skip(28).Take(6).ToArray());
            string host = null, ip = null, vendor = null;
            string ci = string.Join(".", b.Skip(12).Take(4));
            if (ci != "0.0.0.0") ip = ci;
            int i = 240;
            while (i < b.Length)
            {
                int code = b[i];
                if (code == 255) break;
                if (code == 0) { i++; continue; }
                if (i + 1 >= b.Length) break;
                int len = b[i + 1];
                if (len > 0 && i + 1 + len < b.Length)
                {
                    var val = new byte[len]; Array.Copy(b, i + 2, val, 0, len);
                    if (code == 12) host = Encoding.ASCII.GetString(val).Trim('\0');
                    else if (code == 50 && len == 4) ip = string.Join(".", val);
                    else if (code == 60) vendor = Encoding.ASCII.GetString(val).Trim('\0');
                }
                i += 2 + len;
            }
            Queue.Enqueue(new Obs { Source = "dhcp", Mac = mac, Ip = ip, Host = host, Vendor = vendor });
        }

        void ArpLoop()
        {
            var seen = new HashSet<string>();
            while (!stop)
            {
                string f;
                while (ForgetQueue.TryDequeue(out f)) { seen.Remove(f); nameAsked.TryRemove(f, out _); }
                var prefix = Prefix;
                if (prefix != null)
                {
                    foreach (var n in NetUtil.Neighbors())
                    {
                        if (!n.Ip.StartsWith(prefix) || n.State == 0 || n.State == 1 || n.State == 6) continue;   // unreachable, incomplete, permanent
                        if (n.Mac == "00-00-00-00-00-00" || n.Mac == "FF-FF-FF-FF-FF-FF") continue;
                        if (seen.Add(n.Mac)) Queue.Enqueue(new Obs { Source = "arp", Mac = n.Mac, Ip = n.Ip });
                    }
                }
                Thread.Sleep(3000);
            }
        }

        /// <summary>Ping every address in the /24, then read the ARP table. Best effort: sleeping phones can be missed.</summary>
        public static SweepResult Sweep(Primary pr, Dictionary<string, string> own)
        {
            var res = new SweepResult();
            if (pr == null) return res;
            res.Gateway = pr.Gateway.ToString();
            string prefix = pr.Prefix24;
            var replied = new ConcurrentDictionary<string, bool>();
            var tasks = new List<Task>();
            var pings = new List<Ping>();
            for (int i = 1; i < 255; i++)
            {
                string ip = prefix + i;
                try
                {
                    var p = new Ping(); pings.Add(p);
                    tasks.Add(p.SendPingAsync(ip, 400).ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result.Status == IPStatus.Success) replied[ip] = true;
                    }));
                }
                catch { }
            }
            try { Task.WaitAll(tasks.ToArray(), 3000); } catch { }
            foreach (var p in pings) { try { p.Dispose(); } catch { } }

            var rows = new Dictionary<string, Obs>();
            foreach (var n in NetUtil.Neighbors())
            {
                if (!n.Ip.StartsWith(prefix) || n.State == 0 || n.State == 1 || n.Ip.EndsWith(".255")) continue;
                if (n.Mac == "00-00-00-00-00-00" || n.Mac == "FF-FF-FF-FF-FF-FF" || n.Mac.StartsWith("01-00-5E") || n.Mac.StartsWith("33-33")) continue;
                if (n.Ip == res.Gateway) res.GatewayMac = n.Mac;
                bool alive = replied.ContainsKey(n.Ip) || n.State == 5;   // reachable
                Obs o;
                if (!rows.TryGetValue(n.Mac, out o)) rows[n.Mac] = new Obs { Source = "sweep", Mac = n.Mac, Ip = n.Ip, Alive = alive };
                else if (alive) { o.Alive = true; o.Ip = n.Ip; }
            }
            // this PC (Windows does not list itself in ARP)
            try
            {
                var b = pr.Ni.GetPhysicalAddress().GetAddressBytes();
                if (b.Length == 6)
                {
                    string m = NetUtil.Mac(b);
                    if (!rows.ContainsKey(m)) rows[m] = new Obs { Source = "sweep", Mac = m, Ip = pr.Local.ToString(), Alive = true };
                }
            }
            catch { }
            res.Rows = rows.Values.ToList();
            return res;
        }

        public void AskName(string mac, string ip)
        {
            if (string.IsNullOrEmpty(ip) || !ip.Contains(".") || !nameAsked.TryAdd(mac, true)) return;
            try { nameJobs.Add(new[] { mac, ip }); } catch { }
        }

        void NameLoop()
        {
            foreach (var job in nameJobs.GetConsumingEnumerable())
            {
                if (stop) return;
                string name = null;
                try
                {
                    var t = Dns.GetHostEntryAsync(job[1]);
                    t.ContinueWith(x => { var ignored = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    if (t.Wait(2000) && t.Result != null && !string.IsNullOrEmpty(t.Result.HostName) && t.Result.HostName != job[1])
                    {
                        name = t.Result.HostName;
                        int dot = name.IndexOf('.'); if (dot > 0) name = name.Substring(0, dot);
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(name)) name = NetBiosName(job[1]);
                if (!string.IsNullOrEmpty(name) && !IPAddress.TryParse(name, out _))
                    Queue.Enqueue(new Obs { Source = "name", Mac = job[0], Ip = job[1], Host = name, Alive = false });
            }
        }

        /// <summary>Windows PCs (and some NAS/TVs) answer a NetBIOS "who are you" query on UDP 137.</summary>
        static string NetBiosName(string ip)
        {
            try
            {
                var q = new byte[50];
                q[0] = 0xA2; q[1] = 0x48; q[5] = 1;
                q[12] = 0x20; q[13] = (byte)'C'; q[14] = (byte)'K';
                for (int i = 15; i < 45; i++) q[i] = (byte)'A';
                q[45] = 0; q[47] = 0x21; q[49] = 1;
                using (var u = new UdpClient())
                {
                    u.Client.ReceiveTimeout = 800;
                    u.Send(q, q.Length, new IPEndPoint(IPAddress.Parse(ip), 137));
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var r = u.Receive(ref ep);
                    if (r.Length < 57) return null;
                    int count = r[56];
                    for (int i = 0; i < count; i++)
                    {
                        int o = 57 + i * 18;
                        if (o + 18 > r.Length) break;
                        bool group = (r[o + 16] & 0x80) != 0;
                        if (r[o + 15] == 0 && !group) return Encoding.ASCII.GetString(r, o, 15).Trim();
                    }
                }
            }
            catch { }
            return null;
        }
    }

    public class DevEntry { public string Ip; public DateTime LastSeen, LastAlive; }

    public class DeviceRow
    {
        public string Mac { get; set; }
        public string Ip { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string Sub { get; set; }
        public string Tag { get; set; }
        public bool Named { get; set; }
        public bool Online { get; set; }
        public bool New { get; set; }
        public Brush Dot { get; set; }
        public Brush TagBrush { get; set; }
        public double RowOpacity { get; set; }
        public string Vendor { get; set; }
        public string HostName { get; set; }
    }

    /// <summary>The device list and its alert rules. Runs on the UI thread.</summary>
    public class DeviceBook
    {
        public const string Unnamed = "Unknown device";
        readonly Engine E;
        public Dictionary<string, string> Known, HostNames;
        readonly HashSet<string> hostFromVendorGuess = new HashSet<string>();
        public readonly Dictionary<string, DevEntry> Map = new Dictionary<string, DevEntry>();
        public readonly HashSet<string> SessionNew = new HashSet<string>();
        readonly Dictionary<string, DateTime> lastJoin = new Dictionary<string, DateTime>();
        public Dictionary<string, string> Own;
        public string GatewayMac;
        public List<DeviceRow> Devices = new List<DeviceRow>();
        public bool FirstRun, BaselineDone;
        public int Version;   // bumps when the list changes

        string KnownFile => Path.Combine(Paths.Data, "KnownDevices.json");
        string HintFile => Path.Combine(Paths.Data, "DeviceHints.json");
        string AlertCsv => Path.Combine(Paths.Data, "Alerts.csv");
        string DeviceCsv => Path.Combine(Paths.Data, "DeviceHistory.csv");

        public DeviceBook(Engine e)
        {
            E = e;
            FirstRun = !File.Exists(KnownFile);
            BaselineDone = !FirstRun;
            Known = Json.LoadMap(KnownFile);
            Known.Remove("00-00-00-00-00-00");
            HostNames = Json.LoadMap(HintFile);
            Own = NetUtil.OwnMacs();
        }

        public void SaveKnown() { Json.Save(KnownFile, Known); }
        void SaveHints() { Json.Save(HintFile, HostNames); }

        public void WriteAlert(string kind, string detail)
        {
            Csv.Append(AlertCsv, new[] { "Timestamp", "Event", "Detail" }, DateTime.Now.ToString("s"), kind, detail);
        }

        string AutoName(string mac)
        {
            string a;
            if (Own.TryGetValue(mac, out a)) return "This PC - " + a;
            if (mac == GatewayMac) return "Router (gateway)";
            return null;
        }

        public string Vendor(string mac)
        {
            if (NetUtil.IsRandomMac(mac)) return null;
            return Oui.Lookup(mac);
        }

        public string Hint(string mac)
        {
            if (Own.ContainsKey(mac)) return "this computer";
            if (mac == GatewayMac) { var v = Vendor(mac); return "main router" + (v != null ? " · " + v : ""); }
            var parts = new List<string>();
            string h; if (HostNames.TryGetValue(mac, out h) && !string.IsNullOrEmpty(h)) parts.Add("calls itself " + h);
            var ven = Vendor(mac);
            if (ven != null) parts.Add(ven);
            else if (NetUtil.IsRandomMac(mac)) parts.Add("private address · phone/laptop");
            if (parts.Count == 0) parts.Add("fixed address");
            return string.Join(" · ", parts);
        }

        public void Register(string mac, string ip, string source, bool alive)
        {
            mac = mac.ToUpperInvariant();
            if (mac.StartsWith("FF-FF") || mac.StartsWith("01-00-5E") || mac.StartsWith("33-33") || mac == "00-00-00-00-00-00") return;
            var now = DateTime.Now;
            DevEntry prev;
            Map.TryGetValue(mac, out prev);
            double? offlineMin = (prev != null && prev.LastAlive != DateTime.MinValue) ? (now - prev.LastAlive).TotalMinutes : (double?)null;
            if (prev != null)
            {
                prev.LastSeen = now;
                if (alive) { prev.LastAlive = now; if (!string.IsNullOrEmpty(ip)) prev.Ip = ip; }
            }
            else
            {
                Map[mac] = new DevEntry { Ip = string.IsNullOrEmpty(ip) ? "connecting..." : ip, LastSeen = now, LastAlive = alive ? now : DateTime.MinValue };
                Version++;
            }
            string auto = AutoName(mac);
            if (!Known.ContainsKey(mac))
            {
                Known[mac] = auto ?? Unnamed; SaveKnown(); Version++;
                if (auto == null)
                {
                    string shownIp = Map[mac].Ip;
                    E.AskName(mac, shownIp);
                    if (!BaselineDone) return;   // very first run: learn what is already at home quietly
                    SessionNew.Add(mac);
                    WriteAlert("New device detected", "IP " + shownIp + "; MAC " + mac + "; via " + source);
                    Csv.Append(DeviceCsv, new[] { "Timestamp", "IP", "MAC", "DeviceName" }, now.ToString("s"), shownIp, mac, Unnamed);
                    lastJoin[mac] = now;
                    E.RaiseAlert("New", mac);
                    E.ScanSoon();
                }
            }
            else
            {
                if (auto != null && Known[mac] == Unnamed) { Known[mac] = auto; SaveKnown(); }
                if (!BaselineDone) return;
                // DHCP = joining right now. From the sweep, only count it if it was gone 5+ min (phones nap and stop answering pings).
                bool joined = alive && (source == "dhcp" || (offlineMin != null && offlineMin >= 5 && now >= E.QuietRejoinUntil));
                string name = Known[mac];
                bool trusted = name != Unnamed;
                DateTime last;
                bool cool = !lastJoin.TryGetValue(mac, out last) || (now - last).TotalSeconds >= 10;
                if (joined && auto == null && cool && (!trusted || E.Cfg.AlertAll))
                {
                    lastJoin[mac] = now;
                    string shownIp = Map[mac].Ip;
                    WriteAlert("Device reconnected", name + "; IP " + shownIp + "; MAC " + mac + "; via " + source);
                    if (trusted) E.RaiseBalloon("Device reconnected", "'" + name + "' is back on Wi-Fi (" + shownIp + ").");
                    else E.RaiseAlert("Unnamed", mac);
                }
            }
        }

        public void Drain(DeviceWatcher w)
        {
            Obs o; bool any = false;
            while (w.Queue.TryDequeue(out o))
            {
                any = true;
                string mac = o.Mac.ToUpperInvariant();
                if (o.Source == "name")
                {
                    string cur;
                    bool has = HostNames.TryGetValue(mac, out cur) && !string.IsNullOrEmpty(cur);
                    if ((!has || hostFromVendorGuess.Contains(mac)) && cur != o.Host)
                    {
                        HostNames[mac] = o.Host; hostFromVendorGuess.Remove(mac); SaveHints(); Version++;
                    }
                    continue;
                }
                string label = null; bool guess = false;
                if (!string.IsNullOrEmpty(o.Host)) label = o.Host;
                else if (o.Vendor != null && o.Vendor.StartsWith("android", StringComparison.OrdinalIgnoreCase)) { label = "Android device"; guess = true; }
                else if (o.Vendor != null && o.Vendor.StartsWith("MSFT")) { label = "Windows PC"; guess = true; }
                string old;
                bool better = !string.IsNullOrEmpty(o.Host) || !HostNames.ContainsKey(mac);
                if (label != null && better && (!HostNames.TryGetValue(mac, out old) || old != label))
                {
                    HostNames[mac] = label; if (guess) hostFromVendorGuess.Add(mac); else hostFromVendorGuess.Remove(mac);
                    SaveHints(); Version++;
                }
                bool alive = o.Source == "dhcp" || !Map.ContainsKey(mac);
                Register(mac, o.Ip, o.Source, alive);
            }
            if (any) Build();
        }

        public void MergeScan(SweepResult r)
        {
            if (r == null) return;
            if (r.GatewayMac != null) GatewayMac = r.GatewayMac;
            foreach (var row in r.Rows) { Register(row.Mac, row.Ip, "sweep", row.Alive); if (row.Alive) E.AskName(row.Mac, row.Ip); }
            if (!BaselineDone)
            {
                BaselineDone = true;
                int n = Known.Count(k => k.Value == Unnamed);
                if (n > 0) E.RaiseBalloon("Ambaan is watching your Wi-Fi", "Found " + n + " devices already at home. Open the widget and name the ones you know.");
            }
            Build();
        }

        public void Build()
        {
            var now = DateTime.Now;
            var list = new List<DeviceRow>();
            foreach (var kv in Map)
            {
                var e = kv.Value; string mac = kv.Key;
                bool online = (now - e.LastAlive).TotalSeconds <= 50;
                if (!online && (e.LastAlive == DateTime.MinValue || (now - e.LastAlive).TotalMinutes > 30)) continue;
                string name; Known.TryGetValue(mac, out name); name = name ?? Unnamed;
                bool named = name != Unnamed;
                string host; HostNames.TryGetValue(mac, out host);
                string ven = Vendor(mac);
                int ago = (int)Math.Max(1, (now - e.LastAlive).TotalMinutes);
                var r = new DeviceRow
                {
                    Mac = mac, Ip = e.Ip, Name = name, Named = named, Online = online, New = SessionNew.Contains(mac),
                    Vendor = ven, HostName = host,
                    Title = named ? name : !string.IsNullOrEmpty(host) ? host : ven != null ? ven + " device" : "Unnamed device",
                    Sub = e.Ip + "  ·  " + (online ? Hint(mac) : "offline · last seen " + ago + " min ago"),
                };
                r.Dot = !online ? Ui.B("#6EA9A3D9") : r.New ? Ui.B("#FF9C9C") : !named ? Ui.B("#FFD27A") : (name.StartsWith("This PC") || name.StartsWith("Router")) ? Ui.B("#8EC5FF") : Ui.B("#7CE8B8");
                r.Tag = r.New ? "NEW" : !named ? "name it" : "";
                r.TagBrush = r.New ? Ui.B("#FF9C9C") : Ui.B("#8EC5FF");
                r.RowOpacity = online ? 1.0 : 0.55;
                list.Add(r);
            }
            Devices = list.OrderBy(d => d.Online ? 0 : 1).ThenBy(d =>
            {
                var p = (d.Ip ?? "").Split('.'); int x; return p.Length == 4 && int.TryParse(p[3], out x) ? x : 999;
            }).ToList();
        }

        public DeviceRow Row(string mac)
        {
            var r = Devices.FirstOrDefault(d => d.Mac == mac);
            if (r != null) return r;
            DevEntry e; Map.TryGetValue(mac, out e);
            string name; Known.TryGetValue(mac, out name); name = name ?? Unnamed;
            string host; HostNames.TryGetValue(mac, out host);
            return new DeviceRow { Mac = mac, Ip = e != null ? e.Ip : "?", Name = name, Named = name != Unnamed, Title = name, Sub = Hint(mac), HostName = host, Vendor = Vendor(mac) };
        }

        public void Rename(string mac, string name)
        {
            Known[mac] = string.IsNullOrWhiteSpace(name) ? Unnamed : name.Trim();
            SessionNew.Remove(mac); SaveKnown(); Version++; Build();
        }

        public void Forget(string mac, DeviceWatcher w)
        {
            Known.Remove(mac); Map.Remove(mac); HostNames.Remove(mac); SessionNew.Remove(mac);
            SaveKnown(); SaveHints(); w.ForgetQueue.Enqueue(mac); Version++; Build();
        }
    }
}
