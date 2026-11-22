using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Ambaan
{
    public class SpeedResult { public double? Mbps; public bool Ok, Refused; }

    public static class SpeedTest
    {
        /// <summary>Download test from Cloudflare. Bound to the Wi-Fi address when given, so it measures Wi-Fi.
        /// A 403/429 from Cloudflare is a refusal (tests too close together), not slow internet: retry smaller.</summary>
        public static SpeedResult Download(IPAddress bind, long bytes, int maxSeconds = 20)
        {
            var sizes = new List<long> { bytes };
            if (bytes > 5_000_000) sizes.Add(5_000_000);
            sizes.Add(2_000_000);
            foreach (var size in sizes)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("https://speed.cloudflare.com/__down?bytes=" + size + "&r=" + Guid.NewGuid().ToString("N"));
                    req.Timeout = maxSeconds * 1000; req.ReadWriteTimeout = maxSeconds * 1000;
                    req.UserAgent = "AmbaanWiFiGuard/" + Paths.Version; req.KeepAlive = false;
                    req.ServicePoint.BindIPEndPointDelegate = (sp, remote, retry) =>
                        bind != null && remote.AddressFamily == AddressFamily.InterNetwork && retry < 2 ? new IPEndPoint(bind, 0) : null;
                    var sw = Stopwatch.StartNew();
                    long total = 0;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    {
                        var buf = new byte[65536]; int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            if (sw.Elapsed.TotalSeconds > maxSeconds) break;
                        }
                    }
                    double sec = Math.Max(0.05, sw.Elapsed.TotalSeconds);
                    return new SpeedResult { Ok = true, Mbps = Math.Round(total * 8 / 1e6 / sec, 1) };
                }
                catch (WebException ex)
                {
                    var hr = ex.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        int code = (int)hr.StatusCode; hr.Close();
                        if (code == 403 || code == 429 || code >= 500) { Thread.Sleep(2000); continue; }
                    }
                    return new SpeedResult { Ok = false };
                }
                catch (Exception) { return new SpeedResult { Ok = false }; }
            }
            return new SpeedResult { Refused = true };
        }

        public static double? Upload(int bytes = 5_000_000)
        {
            try
            {
                var payload = new byte[bytes]; new Random().NextBytes(payload);
                var req = (HttpWebRequest)WebRequest.Create("https://speed.cloudflare.com/__up");
                req.Method = "POST"; req.ContentType = "application/octet-stream"; req.Timeout = 60000; req.ReadWriteTimeout = 60000;
                req.UserAgent = "AmbaanWiFiGuard/" + Paths.Version; req.ContentLength = payload.Length;
                var sw = Stopwatch.StartNew();
                using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                using (var resp = req.GetResponse()) { }
                return Math.Round(bytes * 8 / 1e6 / Math.Max(0.05, sw.Elapsed.TotalSeconds), 1);
            }
            catch { return null; }
        }
    }

    public class DnsServer
    {
        public string Address, Kind, Label, Typo;
        public bool Responds, Unused;
        public int? Ms;
    }

    public class DnsResult
    {
        public string Status, Shield, Headline, Detail;
        public List<string> Issues = new List<string>();
        public List<DnsServer> Servers = new List<DnsServer>();
        public bool StaticDns;
        public DateTime CheckedAt = DateTime.Now;
    }

    /// <summary>Is DNS working, is there a dead/typo server, is scam-link filtering on? Read-only.</summary>
    public static class DnsShield
    {
        static readonly Dictionary<string, string> Filter = new Dictionary<string, string>
        {
            ["1.1.1.2"] = "Cloudflare scam & malware filter", ["1.0.0.2"] = "Cloudflare scam & malware filter",
            ["2606:4700:4700::1112"] = "Cloudflare scam & malware filter", ["2606:4700:4700::1002"] = "Cloudflare scam & malware filter",
            ["1.1.1.3"] = "Cloudflare family filter", ["1.0.0.3"] = "Cloudflare family filter",
            ["2606:4700:4700::1113"] = "Cloudflare family filter", ["2606:4700:4700::1003"] = "Cloudflare family filter",
            ["9.9.9.9"] = "Quad9 malware filter", ["149.112.112.112"] = "Quad9 malware filter",
            ["2620:fe::fe"] = "Quad9 malware filter", ["2620:fe::9"] = "Quad9 malware filter",
            ["94.140.14.14"] = "AdGuard ad & malware filter", ["94.140.15.15"] = "AdGuard ad & malware filter",
            ["2a10:50c0::ad1:ff"] = "AdGuard ad & malware filter", ["2a10:50c0::ad2:ff"] = "AdGuard ad & malware filter",
            ["94.140.14.15"] = "AdGuard family filter", ["94.140.15.16"] = "AdGuard family filter",
            ["185.228.168.9"] = "CleanBrowsing security filter", ["185.228.169.9"] = "CleanBrowsing security filter",
            ["208.67.222.123"] = "OpenDNS FamilyShield", ["208.67.220.123"] = "OpenDNS FamilyShield",
        };
        static readonly Dictionary<string, string> Plain = new Dictionary<string, string>
        {
            ["8.8.8.8"] = "Google DNS (no scam filter)", ["8.8.4.4"] = "Google DNS (no scam filter)",
            ["2001:4860:4860::8888"] = "Google DNS (no scam filter)", ["2001:4860:4860::8844"] = "Google DNS (no scam filter)",
            ["1.1.1.1"] = "Cloudflare DNS (no scam filter)", ["1.0.0.1"] = "Cloudflare DNS (no scam filter)",
            ["2606:4700:4700::1111"] = "Cloudflare DNS (no scam filter)", ["2606:4700:4700::1001"] = "Cloudflare DNS (no scam filter)",
            ["208.67.222.222"] = "OpenDNS", ["208.67.220.220"] = "OpenDNS",
        };
        static readonly Dictionary<string, string> Typos = new Dictionary<string, string>
        { ["8.8.8.4"] = "8.8.4.4", ["8.8.4.8"] = "8.8.8.8", ["8.8.8.0"] = "8.8.8.8", ["1.1.1.0"] = "1.0.0.1", ["1.0.0.0"] = "1.0.0.1", ["1.1.1.12"] = "1.1.1.2" };

        static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC || IPAddress.IPv6Loopback.Equals(a);
        }

        static string Prefix64(IPAddress a) { return BitConverter.ToString(a.GetAddressBytes(), 0, 8); }

        /// <summary>Send one DNS question (example.com, type A) and time the answer.</summary>
        public static int? Query(IPAddress server, string name = "example.com", int timeoutMs = 1500)
        {
            try
            {
                var pkt = new List<byte>();
                var id = (ushort)new Random().Next(1, 65535);
                pkt.AddRange(new byte[] { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                foreach (var label in name.Split('.')) { pkt.Add((byte)label.Length); pkt.AddRange(Encoding.ASCII.GetBytes(label)); }
                pkt.AddRange(new byte[] { 0, 0, 1, 0, 1 });
                using (var u = new UdpClient(server.AddressFamily))
                {
                    u.Client.ReceiveTimeout = timeoutMs;
                    var sw = Stopwatch.StartNew();
                    u.Send(pkt.ToArray(), pkt.Count, new IPEndPoint(server, 53));
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        var ep = new IPEndPoint(server.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
                        var r = u.Receive(ref ep);
                        if (r.Length >= 12 && r[0] == (byte)(id >> 8) && r[1] == (byte)id) return (int)sw.ElapsedMilliseconds;
                    }
                }
            }
            catch { }
            return null;
        }

        public static DnsResult Check()
        {
            var res = new DnsResult();
            var order = new List<IPAddress>();
            bool hasV6 = false;
            var localV6 = new HashSet<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet && ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    var p = ni.GetIPProperties();
                    foreach (var ua in p.UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily == AddressFamily.InterNetworkV6 && !IsPrivate(a))
                        {
                            localV6.Add(Prefix64(a));
                            if (ua.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred) hasV6 = true;
                        }
                    }
                    try
                    {
                        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + ni.Id))
                            if (k != null && !string.IsNullOrWhiteSpace(k.GetValue("NameServer") as string)) res.StaticDns = true;
                    }
                    catch { }
                    foreach (var d in p.DnsAddresses)
                    {
                        string s = d.ToString().Split('%')[0].ToLowerInvariant();
                        if (s.StartsWith("fec0:0:0:ffff::")) continue;   // Windows' placeholder IPv6 DNS: never answers, not a real problem
                        if (!order.Any(x => x.ToString().Split('%')[0].ToLowerInvariant() == s)) order.Add(d);
                    }
                }
            }
            catch (Exception ex) { Log.Error("dns list", ex); }

            foreach (var a in order)
            {
                string s = a.ToString().Split('%')[0].ToLowerInvariant();
                bool v6 = a.AddressFamily == AddressFamily.InterNetworkV6;
                var sv = new DnsServer { Address = s };
                string l;
                if (Filter.TryGetValue(s, out l)) { sv.Kind = "Filter"; sv.Label = l; }
                else if (Plain.TryGetValue(s, out l)) { sv.Kind = "Plain"; sv.Label = l; }
                else if (IsPrivate(a) || (v6 && localV6.Contains(Prefix64(a)))) { sv.Kind = "Router"; sv.Label = "Router DNS (filtering unknown)"; }
                else { sv.Kind = "Other"; sv.Label = "Unknown DNS provider"; }
                sv.Unused = v6 && !hasV6;
                if (!sv.Unused) { sv.Ms = Query(a); sv.Responds = sv.Ms != null; }
                Typos.TryGetValue(s, out sv.Typo);
                res.Servers.Add(sv);
            }

            var live = res.Servers.Where(x => x.Responds).ToList();
            var dead = res.Servers.Where(x => !x.Responds && !x.Unused).ToList();
            var unfiltered = live.Where(x => x.Kind != "Filter").ToList();
            foreach (var d in dead) res.Issues.Add(d.Typo != null ? d.Address + " doesn't answer. Typo? Did you mean " + d.Typo + "?" : d.Address + " doesn't answer.");

            if (res.Servers.Count == 0) { res.Status = "Bad"; res.Shield = "Unknown"; res.Headline = "No DNS servers found"; res.Detail = "No connected adapter reported a DNS server."; }
            else if (live.Count == 0) { res.Status = "Bad"; res.Shield = "Off"; res.Headline = "DNS is not answering"; res.Detail = "None of the DNS servers replied. Websites will not open by name."; }
            else
            {
                if (unfiltered.Count == 0)
                {
                    res.Shield = "On"; res.Status = "Good";
                    res.Headline = "Scam filter ON - " + string.Join(", ", live.Select(x => x.Label).Distinct());
                    res.Detail = "Known phishing and malware sites are blocked on this PC.";
                }
                else if (unfiltered.Count < live.Count)
                {
                    res.Shield = "Partial"; res.Status = "Warn"; res.Headline = "Scam filter only partly on";
                    res.Detail = "Some DNS servers filter and some do not. Windows may use either, so scam links can still open.";
                }
                else
                {
                    res.Shield = "Off"; res.Status = "Warn"; res.Headline = "Scam filter OFF";
                    res.Detail = "Using " + string.Join(" + ", unfiltered.Select(x => x.Label).Distinct()) + ". Scam and phishing links are not blocked.";
                }
                if (dead.Count > 0) { if (res.Status == "Good") res.Status = "Warn"; res.Headline += "  |  " + dead.Count + " dead server"; }
            }
            return res;
        }

        /// <summary>Change DNS on this PC (asks for administrator permission). 1 = scam filter, 2 = family filter, 3 = automatic.</summary>
        public static bool Apply(int choice, out string error)
        {
            error = null;
            string set = choice == 1 ? "-ServerAddresses '1.1.1.2','1.0.0.2','2606:4700:4700::1112','2606:4700:4700::1002'"
                       : choice == 2 ? "-ServerAddresses '1.1.1.3','1.0.0.3','2606:4700:4700::1113','2606:4700:4700::1003'"
                       : "-ResetServerAddresses";
            string cmd = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex " + set + " }; Clear-DnsClientCache";
            return Elevated.RunPowerShell(cmd, out error);
        }
    }

    public static class Elevated
    {
        public static bool RunPowerShell(string command, out string error)
        {
            error = null;
            try
            {
                string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -WindowStyle Hidden -EncodedCommand " + enc) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) { p.WaitForExit(60000); return p.ExitCode == 0; }
            }
            catch (System.ComponentModel.Win32Exception) { error = "No change made (administrator permission was not given)."; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }

    public class OutageRec { public DateTime Start, End; public double Minutes; public string Cause; }

    /// <summary>Internet outage log + uptime per day. Fed by the internet ping every 10 s.</summary>
    public class OutageTracker
    {
        string File => Path.Combine(Paths.Data, "Outages.csv");
        string UpFile => Path.Combine(Paths.Data, "Uptime.json");
        int fails;
        public DateTime? DownSince;
        public string Cause = "";
        DateTime lastCheck = DateTime.MinValue, lastSave = DateTime.Now;
        readonly Dictionary<string, double[]> days = new Dictionary<string, double[]>();

        public OutageTracker()
        {
            foreach (var kv in Json.LoadObject(UpFile))
            {
                var arr = Json.Nums(kv.Value);
                if (arr != null && arr.Length == 2) days[kv.Key] = arr;
            }
        }

        double[] Day(DateTime t)
        {
            string k = t.ToString("yyyy-MM-dd"); double[] d;
            if (!days.TryGetValue(k, out d)) { d = new double[2]; days[k] = d; }
            return d;
        }

        /// <summary>Returns a message to show when the internet goes down or comes back, else null.</summary>
        public string Check(bool netOk, bool routerOk, bool linkUp)
        {
            var now = DateTime.Now;
            double dt = (now - lastCheck).TotalSeconds;
            lastCheck = now;
            if (dt > 0 && dt < 60) { var d = Day(now); d[0] += dt; if (DownSince != null) d[1] += dt; }
            if ((now - lastSave).TotalMinutes >= 5) Save();
            if (netOk)
            {
                fails = 0;
                if (DownSince != null)
                {
                    double mins = (now - DownSince.Value).TotalMinutes;
                    Record(DownSince.Value, now, Cause);
                    DownSince = null;
                    return "Internet is back after " + NetUtil.Minutes(mins) + ".";
                }
                return null;
            }
            fails++;
            if (fails == 3 && DownSince == null)
            {
                DownSince = now.AddSeconds(-20);
                Cause = !linkUp ? "Wi-Fi disconnected" : !routerOk ? "Router not answering" : "Internet line (ISP) down";
                return "Internet is down: " + Cause.ToLowerInvariant() + ".";
            }
            return null;
        }

        void Record(DateTime s, DateTime e, string cause)
        {
            Csv.Append(File, new[] { "Start", "End", "Minutes", "Cause" }, s.ToString("s"), e.ToString("s"), Math.Round((e - s).TotalMinutes, 1), cause);
        }

        /// <summary>PC went to sleep: close an open outage at that moment.</summary>
        public void Suspend()
        {
            if (DownSince != null) { Record(DownSince.Value, DateTime.Now, Cause); DownSince = null; }
            fails = 0; lastCheck = DateTime.MinValue; Save();
        }

        public void Save()
        {
            lastSave = DateTime.Now;
            var cut = DateTime.Today.AddDays(-60).ToString("yyyy-MM-dd");
            foreach (var k in days.Keys.Where(k => string.CompareOrdinal(k, cut) < 0).ToList()) days.Remove(k);
            Json.Save(UpFile, days.ToDictionary(k => k.Key, k => (object)new object[] { Math.Round(k.Value[0]), Math.Round(k.Value[1]) }));
        }

        public double? Uptime(int lastDays)
        {
            double mon = 0, down = 0;
            for (int i = 0; i < lastDays; i++)
            {
                double[] d;
                if (days.TryGetValue(DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"), out d)) { mon += d[0]; down += d[1]; }
            }
            if (mon < 60) return null;
            return Math.Max(0, 100.0 * (1 - down / mon));
        }

        public List<OutageRec> Recent(int n = 50)
        {
            var r = new List<OutageRec>();
            foreach (var row in Csv.Read(File, n))
            {
                try
                {
                    r.Add(new OutageRec
                    {
                        Start = DateTime.Parse(row["Start"], CultureInfo.InvariantCulture),
                        End = DateTime.Parse(row["End"], CultureInfo.InvariantCulture),
                        Minutes = double.Parse(row["Minutes"], CultureInfo.InvariantCulture),
                        Cause = row["Cause"]
                    });
                }
                catch { }
            }
            r.Reverse();
            return r;
        }

        public int CountToday() { return Recent(200).Count(o => o.Start.Date == DateTime.Today); }
    }

    /// <summary>Data used by this PC per day (Wi-Fi + Ethernet). Per-adapter counters, so a reconnect never double counts.</summary>
    public class UsageTracker
    {
        string File => Path.Combine(Paths.Data, "Usage.json");
        readonly Dictionary<string, double[]> days = new Dictionary<string, double[]>();
        Dictionary<string, long[]> last = new Dictionary<string, long[]>();
        DateTime lastSave = DateTime.Now;

        public UsageTracker()
        {
            foreach (var kv in Json.LoadObject(File))
            {
                var arr = Json.Nums(kv.Value);
                if (arr != null && arr.Length == 2) days[kv.Key] = arr;
            }
        }

        public void Sample(Dictionary<string, long[]> now)
        {
            string k = DateTime.Today.ToString("yyyy-MM-dd"); double[] d;
            if (!days.TryGetValue(k, out d)) { d = new double[2]; days[k] = d; }
            foreach (var kv in now)
            {
                long[] p;
                if (!last.TryGetValue(kv.Key, out p)) continue;
                long dr = kv.Value[0] - p[0], dtx = kv.Value[1] - p[1];
                if (dr > 0 && dr < 20_000_000_000) d[0] += dr;
                if (dtx > 0 && dtx < 20_000_000_000) d[1] += dtx;
            }
            last = now;
            if ((DateTime.Now - lastSave).TotalMinutes >= 1) Save();
        }

        public void Reset() { last = new Dictionary<string, long[]>(); }

        public void Save()
        {
            lastSave = DateTime.Now;
            var cut = DateTime.Today.AddDays(-400).ToString("yyyy-MM-dd");
            foreach (var key in days.Keys.Where(x => string.CompareOrdinal(x, cut) < 0).ToList()) days.Remove(key);
            Json.Save(File, days.ToDictionary(x => x.Key, x => (object)new object[] { Math.Round(x.Value[0]), Math.Round(x.Value[1]) }));
        }

        public double[] Day(DateTime day)
        {
            double[] d; return days.TryGetValue(day.ToString("yyyy-MM-dd"), out d) ? d : new double[2];
        }

        public double[] Month()
        {
            var r = new double[2]; string pre = DateTime.Today.ToString("yyyy-MM-");
            foreach (var kv in days) if (kv.Key.StartsWith(pre)) { r[0] += kv.Value[0]; r[1] += kv.Value[1]; }
            return r;
        }
    }

    public class SpeedPoint { public DateTime Time; public double? Down; public int? Ping; public string Via; }

    public static class SpeedHistory
    {
        static string File => Path.Combine(Paths.Data, "SpeedHistory.csv");
        public static void Add(double? down, int? ping, string via)
        {
            Csv.Append(File, new[] { "Timestamp", "DownloadMbps", "PingMs", "Via" }, DateTime.Now.ToString("s"),
                down?.ToString(CultureInfo.InvariantCulture) ?? "", ping?.ToString() ?? "", via);
        }
        public static List<SpeedPoint> Load(int days)
        {
            var cut = DateTime.Now.AddDays(-days);
            var r = new List<SpeedPoint>();
            foreach (var row in Csv.Read(File, 5000))
            {
                DateTime t;
                if (!DateTime.TryParse(row["Timestamp"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) || t < cut) continue;
                double d; int p;
                r.Add(new SpeedPoint
                {
                    Time = t,
                    Down = double.TryParse(row["DownloadMbps"], NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null,
                    Ping = int.TryParse(row["PingMs"], out p) ? p : (int?)null,
                    Via = row["Via"]
                });
            }
            return r;
        }
    }

    public class ChannelAdvice
    {
        public string Summary = "", Detail = "";
        public int MyChannel; public string MyBand = "";
        public Dictionary<int, double> Busy24 = new Dictionary<int, double>(), Busy5 = new Dictionary<int, double>();
        public Dictionary<int, int> Count24 = new Dictionary<int, int>(), Count5 = new Dictionary<int, int>();
        public List<BssInfo> Networks = new List<BssInfo>();
        public bool Blocked;
    }

    /// <summary>Which Wi-Fi channel is least crowded? Uses the nearby networks Windows can see.</summary>
    public static class ChannelAdvisor
    {
        static readonly int[] Ch5 = { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 };
        static bool Dfs(int c) { return c >= 52 && c <= 144; }

        public static ChannelAdvice Analyze(WifiState me)
        {
            var a = new ChannelAdvice();
            bool blocked;
            var all = WlanApi.Scan(true, out blocked);
            a.Blocked = blocked;
            a.Networks = all.GroupBy(b => b.Bssid).Select(g => g.First()).OrderByDescending(b => b.Rssi).ToList();
            a.MyChannel = me.Channel; a.MyBand = me.Band;
            var others = a.Networks.Where(b => b.Bssid != me.Bssid).ToList();
            Func<BssInfo, double> strength = b => Math.Max(0.1, Math.Min(1.0, (b.Rssi + 100) / 60.0));

            for (int c = 1; c <= 13; c++) { a.Count24[c] = 0; }
            foreach (var b in others.Where(x => x.Band == "2.4 GHz")) if (a.Count24.ContainsKey(b.Channel)) a.Count24[b.Channel]++;
            foreach (int c in new[] { 1, 6, 11 })
                a.Busy24[c] = others.Where(x => x.Band == "2.4 GHz" && Math.Abs(x.Channel - c) < 5).Sum(x => (1 - Math.Abs(x.Channel - c) / 5.0) * strength(x));
            foreach (int c in Ch5) { a.Count5[c] = 0; a.Busy5[c] = 0; }
            foreach (var b in others.Where(x => x.Band == "5 GHz"))
            {
                int c = Ch5.OrderBy(x => Math.Abs(x - b.Channel)).First();
                a.Count5[c]++; a.Busy5[c] += strength(b);
            }

            if (a.Networks.Count == 0)
            {
                a.Summary = blocked ? "Windows blocked the Wi-Fi scan." : "No nearby networks found.";
                a.Detail = blocked ? "Turn on Location for desktop apps (Settings > Privacy > Location), then scan again." : "Try again in a minute.";
                return a;
            }

            int best24 = a.Busy24.OrderBy(x => x.Value).First().Key;
            int best5 = a.Busy5.Where(x => !Dfs(x.Key)).OrderBy(x => x.Value).First().Key;
            var lines = new List<string>();
            lines.Add("2.4 GHz: least crowded of 1 / 6 / 11 is channel " + best24 + ".");
            lines.Add("5 GHz: least crowded common channel is " + best5 + " (channels 52-144 also work but may pause for radar).");
            if (me.Connected && me.Channel > 0)
            {
                if (me.Band == "2.4 GHz")
                {
                    int nearest = new[] { 1, 6, 11 }.OrderBy(x => Math.Abs(x - me.Channel)).First();
                    double mine = others.Where(x => x.Band == "2.4 GHz" && Math.Abs(x.Channel - me.Channel) < 5).Sum(x => (1 - Math.Abs(x.Channel - me.Channel) / 5.0) * strength(x));
                    bool off = me.Channel != 1 && me.Channel != 6 && me.Channel != 11;
                    if (off || a.Busy24[best24] < mine * 0.6)
                        a.Summary = "Change your router to channel " + best24 + " (2.4 GHz). You are on " + me.Channel + ", which is busier.";
                    else a.Summary = "Your channel " + me.Channel + " is a good choice. Nothing to change.";
                    lines.Add("Tip: if your router and devices support 5 GHz, use it. It is much faster and less crowded.");
                }
                else if (me.Band == "5 GHz")
                {
                    int mineKey = Ch5.OrderBy(x => Math.Abs(x - me.Channel)).First();
                    double mine = a.Busy5[mineKey];
                    if (a.Busy5[best5] < mine * 0.6 && mine > 0.5)
                        a.Summary = "Change your router's 5 GHz channel to " + best5 + ". You are on " + me.Channel + ", which is busier.";
                    else a.Summary = "Your 5 GHz channel " + me.Channel + " is fine. Nothing to change.";
                }
                else a.Summary = "You are on " + me.Band + " channel " + me.Channel + ".";
            }
            else a.Summary = "Best channels: " + best24 + " (2.4 GHz) and " + best5 + " (5 GHz).";
            lines.Add("Change the channel on your router's admin page or app (Wi-Fi / Wireless settings).");
            a.Detail = string.Join("\n", lines);
            return a;
        }
    }
}
