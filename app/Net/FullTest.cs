using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace Ambaan
{
    public class Check { public string Area, Name, Value, Status, Explanation; }

    /// <summary>The full Wi-Fi test: signal, router, internet, DNS, DNS safety, route, nearby networks, speed. Writes an HTML dashboard.</summary>
    public class FullTest
    {
        public readonly List<Check> Results = new List<Check>();
        public string Overall = "", Likely = "", ReportPath, RepairSummary = "No network-disruptive repair was applied.";
        public readonly List<string> Recommendations = new List<string>();
        public readonly List<string> RepairLog = new List<string>();
        public double? DownMbps, UpMbps;
        public PingStats Router, Cloud, Google;
        public double? DnsMs;
        public WifiState Wifi;
        public bool LocalIssue, DnsFailed;
        readonly Action<string> say;

        public FullTest(Action<string> progress) { say = progress ?? (s => { }); }

        public class PingStats { public int Sent, Received; public double Loss; public double? Avg, Min, Max; }

        void Add(string area, string check, string value, string status, string expl = "")
        {
            Results.Add(new Check { Area = area, Name = check, Value = value, Status = status, Explanation = expl });
        }

        static PingStats PingMany(string target, int count)
        {
            var times = new List<double>(); int lost = 0;
            using (var p = new Ping())
            {
                for (int i = 0; i < count; i++)
                {
                    try { var r = p.Send(target, 1800); if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else lost++; }
                    catch { lost++; }
                    Thread.Sleep(100);
                }
            }
            return new PingStats
            {
                Sent = count, Received = times.Count, Loss = Math.Round(100.0 * lost / count, 1),
                Avg = times.Count > 0 ? Math.Round(times.Average(), 1) : (double?)null,
                Min = times.Count > 0 ? times.Min() : (double?)null, Max = times.Count > 0 ? times.Max() : (double?)null
            };
        }

        public void Run()
        {
            var now = DateTime.Now;
            say("Reading Wi-Fi details...");
            Wifi = WlanApi.Current();
            var w = Wifi;
            Add("Wi-Fi", "Connection", w.Connected ? "Connected" : (w.HasAdapter ? "Disconnected" : "No Wi-Fi adapter (cable?)"), w.Connected ? "PASS" : (w.HasAdapter ? "FAIL" : "INFO"));
            if (w.LocationBlocked) Add("Wi-Fi", "Wi-Fi details", "Hidden by Windows", "INFO", "Turn on Location for desktop apps to see the network name and signal.");
            if (w.Signal != null) Add("Wi-Fi", "Signal strength", w.Signal + "%", w.Signal >= 80 ? "PASS" : w.Signal >= 50 ? "WARNING" : "FAIL");
            if (w.Rssi != null) Add("Wi-Fi", "RSSI", w.Rssi + " dBm", w.Rssi >= -60 ? "PASS" : w.Rssi >= -70 ? "WARNING" : "FAIL", "Closer to zero is stronger.");
            if (w.Connected) Add("Wi-Fi", "Band / channel", w.Band + " / " + w.Channel, "INFO", w.Gen);
            if (w.Connected) Add("Wi-Fi", "Link rate", "RX " + w.Rx + " / TX " + w.Tx + " Mbps", "INFO", "PHY link rate is not real internet throughput.");

            var pr = NetUtil.GetPrimary();
            if (pr != null)
            {
                say("Testing your router...");
                Router = PingMany(pr.Gateway.ToString(), 20);
                string st = Router.Loss > 5 || (Router.Avg ?? 0) > 20 ? "FAIL" : Router.Loss > 0 || (Router.Avg ?? 0) > 5 ? "WARNING" : "PASS";
                Add("Local network", "Router latency", Router.Avg != null ? Router.Avg + " ms avg" : "No replies", st, Router.Loss + "% packet loss; router address hidden from report.");
            }
            else Add("Local network", "Default gateway", "Not found", "FAIL", "No active IPv4 gateway was found.");

            say("Testing internet (Cloudflare and Google)...");
            Cloud = PingMany("1.1.1.1", 20);
            Google = PingMany("8.8.8.8", 20);
            foreach (var e in new[] { Tuple.Create("Cloudflare 1.1.1.1", Cloud), Tuple.Create("Google 8.8.8.8", Google) })
            {
                var p = e.Item2;
                string st = p.Loss > 5 || (p.Avg ?? 0) > 150 ? "FAIL" : p.Loss > 0 || (p.Avg ?? 0) > 80 ? "WARNING" : p.Avg == null ? "FAIL" : "PASS";
                Add("Internet", e.Item1, p.Avg != null ? p.Avg + " ms avg" : "No replies", st, p.Loss + "% loss; min " + p.Min + " / max " + p.Max + " ms.");
            }

            say("Testing DNS...");
            var dnsServer = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => { try { return n.GetIPProperties().DnsAddresses.ToList(); } catch { return new List<IPAddress>(); } })
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            int? q = dnsServer != null ? DnsShield.Query(dnsServer, "www.microsoft.com", 3000) : null;
            if (q == null)
            {
                DnsFailed = true;
                Add("DNS", "Resolution", "Failed", "FAIL", "Could not resolve www.microsoft.com.");
                say("DNS failed: flushing the DNS cache (safe repair)...");
                try { Native.DnsFlushResolverCache(); } catch { }
                Thread.Sleep(2000);
                q = dnsServer != null ? DnsShield.Query(dnsServer, "www.microsoft.com", 3000) : null;
                if (q != null)
                {
                    DnsFailed = false;
                    RepairLog.Add("Flush DNS cache|Initial DNS lookup failed|Completed|DNS lookup succeeded after repair");
                    Add("Auto repair", "Flush DNS cache", "Completed", "PASS", "DNS lookup succeeded on post-repair verification.");
                    RepairSummary = "Repair action(s) applied; post-repair verification performed.";
                }
                else
                {
                    RepairLog.Add("Flush DNS cache|Initial DNS lookup failed|Completed|DNS still failing");
                    Add("Auto repair", "Flush DNS cache", "Applied; issue persists", "WARNING", "DNS still fails. This may be an upstream resolver or connectivity issue.");
                }
            }
            else
            {
                DnsMs = q;
                Add("DNS", "Lookup time", q + " ms", q < 100 ? "PASS" : q < 300 ? "WARNING" : "FAIL", "One lookup to your first DNS server.");
                RepairLog.Add("No safe repair needed|No DNS failure detected|Skipped|Not applicable");
            }

            say("Checking DNS safety...");
            var ds = DnsShield.Check();
            foreach (var s in ds.Servers)
            {
                string shown = s.Kind == "Router" ? "Router (local)" : s.Address;
                string val = s.Unused ? "Unused (no IPv6)" : s.Responds ? "Answers in " + s.Ms + " ms" : "NOT ANSWERING";
                string st = s.Unused ? "INFO" : !s.Responds ? "FAIL" : s.Kind == "Filter" ? "PASS" : "INFO";
                Add("DNS safety", "Server " + shown, val, st, s.Label + (s.Typo != null ? ". Looks like a typo: did you mean " + s.Typo + "?" : ""));
            }
            Add("DNS safety", "Scam-link filter", ds.Shield, ds.Shield == "On" ? "PASS" : ds.Shield == "Unknown" ? "INFO" : "WARNING",
                ds.Detail + " To protect every device at home, set 1.1.1.2 / 1.0.0.2 as DNS on the router.");

            say("Tracing the route to 1.1.1.1...");
            var route = Trace("1.1.1.1", 12);
            Add("Routing", "Traceroute", route.Count(l => l.Contains(".")) + " responding hops", "INFO", "Non-responding hops are common; this alone does not prove packet loss.");

            say("Scanning nearby Wi-Fi networks...");
            bool blocked;
            var nets = WlanApi.Scan(true, out blocked).GroupBy(b => b.Bssid).Select(g => g.First()).ToList();
            var busy = nets.GroupBy(b => b.Channel).OrderByDescending(g => g.Count()).Take(3).Select(g => "CH " + g.Key + ": " + g.Count());
            Add("Wi-Fi environment", "Nearby networks", nets.Count + " detected", "INFO",
                blocked ? "Windows blocked the scan (Location is off for desktop apps)." : "Channel counts: " + (nets.Count > 0 ? string.Join(", ", busy) : "Not available") + ". Scan is a snapshot, not a full RF spectrum analysis.");

            say("Speed test: download (~20 MB)...");
            var d = SpeedTest.Download(null, 20_000_000, 40);
            DownMbps = d.Ok ? d.Mbps : null;
            say("Speed test: upload (~5 MB)...");
            UpMbps = SpeedTest.Upload();
            if (DownMbps != null) Add("Throughput", "Download", DownMbps + " Mbps", DownMbps >= 50 ? "PASS" : DownMbps >= 10 ? "WARNING" : "FAIL", "Compare against your internet plan; server conditions affect results.");
            else Add("Throughput", "Download", "Unavailable", "WARNING", d.Refused ? "Test server busy." : "No connection to the test server.");
            if (UpMbps != null) Add("Throughput", "Upload", UpMbps + " Mbps", UpMbps >= 10 ? "PASS" : UpMbps >= 2 ? "WARNING" : "FAIL");
            else Add("Throughput", "Upload", "Unavailable", "WARNING");

            LocalIssue = Router != null && (Router.Loss > 5 || (Router.Avg ?? 0) > 20);
            if (!LocalIssue) RepairLog.Add("Risky repair assessment|No strong local gateway fault detected|Not offered|No disruptive action recommended");
            else RepairLog.Add("Risky repair assessment|Elevated router latency/loss|Offered in the app|Use Renew IP or Restart Wi-Fi if needed");

            int fail = Results.Count(r => r.Status == "FAIL"), warn = Results.Count(r => r.Status == "WARNING");
            Overall = fail > 0 ? "ATTENTION REQUIRED" : warn > 0 ? "DEGRADED / REVIEW" : "NO ISSUES DETECTED";
            Likely = "Insufficient evidence; compare wired and Wi-Fi tests.";
            int sig = w.Signal ?? 0;
            if (Router != null && (Router.Loss > 0 || (Router.Avg ?? 0) > 10)) Likely = "Likely local Wi-Fi/router segment: gateway latency or packet loss is elevated.";
            else if (Router != null && Router.Loss == 0 && Cloud.Loss > 0 && Google.Loss > 0) Likely = "Likely upstream of the router or ISP path; verify with Ethernet and another time window.";
            else if (Router != null && Router.Loss == 0 && (Router.Avg ?? 99) < 5 && ((Cloud.Avg ?? 0) > 80 || (Google.Avg ?? 0) > 80)) Likely = "Local link looks responsive; likely WAN/ISP routing, congestion, or destination path.";
            else if (DownMbps != null && DownMbps < 10 && sig >= 80) Likely = "Wi-Fi signal is strong but measured throughput is low; compare Ethernet and your plan to isolate router/ISP/server.";
            else if (w.Connected && sig < 50) Likely = "Weak Wi-Fi signal is a likely contributor; test closer to the router.";

            if (w.Connected && sig < 70) Recommendations.Add("Move closer to the router and compare signal/RSSI; avoid placing the router behind metal or inside a cabinet.");
            if (Router != null && (Router.Loss > 0 || (Router.Avg ?? 0) > 10)) Recommendations.Add("Local gateway response is elevated. Test near the router, pause heavy local transfers, and compare a wired Ethernet test.");
            if (Cloud.Loss > 0 || Google.Loss > 0) Recommendations.Add("External ping loss observed. Repeat at another time and compare multiple destinations; ICMP may be deprioritized.");
            if (DownMbps != null && DownMbps < 10) Recommendations.Add("Throughput is low in this sample. Compare against your internet plan, test via Ethernet, and repeat without VPN or large downloads.");
            if (DnsFailed || (DnsMs ?? 0) > 300) Recommendations.Add("DNS resolution is failing or slow. Try another resolver only after confirming the issue is repeatable.");
            if (ds.Shield != "On") Recommendations.Add("Turn on the DNS scam filter (widget > DNS Shield > Fix it) to block known phishing and malware sites.");
            if (Recommendations.Count == 0) Recommendations.Add("No strong fault was isolated in this run. Repeat during a slowdown and compare the historical graphs.");

            say("Writing the report...");
            WriteReport(now, route);
            say("Done.");
        }

        static List<string> Trace(string target, int maxHops)
        {
            var lines = new List<string>();
            using (var p = new Ping())
            {
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var r = p.Send(target, 900, new byte[32], new PingOptions(ttl, true));
                        sw.Stop();
                        if (r.Status == IPStatus.TtlExpired || r.Status == IPStatus.Success)
                        {
                            lines.Add(ttl + "    " + sw.ElapsedMilliseconds + " ms    " + r.Address);
                            if (r.Status == IPStatus.Success) break;
                        }
                        else lines.Add(ttl + "    *    Request timed out.");
                    }
                    catch { lines.Add(ttl + "    *    error"); }
                }
            }
            return lines;
        }

        static string H(object x) { return WebUtility.HtmlEncode(Convert.ToString(x, CultureInfo.InvariantCulture) ?? ""); }
        static string N(double? x) { return x?.ToString(CultureInfo.InvariantCulture) ?? ""; }

        void WriteReport(DateTime now, List<string> route)
        {
            string csv = Path.Combine(Paths.History, "NetworkHistory.csv");
            var w = Wifi;
            Csv.Append(csv, new[] { "Timestamp", "SignalPercent", "RSSIdBm", "Band", "Channel", "LinkRxMbps", "LinkTxMbps", "RouterAvgMs", "RouterLossPct", "CloudflareAvgMs", "CloudflareLossPct", "GoogleAvgMs", "GoogleLossPct", "DnsMs", "DownloadMbps", "UploadMbps", "Overall" },
                now.ToString("s"), w.Signal?.ToString() ?? "", w.Rssi?.ToString() ?? "", w.Band, w.Channel, N(w.Rx), N(w.Tx), N(Router?.Avg), N(Router?.Loss),
                N(Cloud.Avg), N(Cloud.Loss), N(Google.Avg), N(Google.Loss), N(DnsMs), N(DownMbps), N(UpMbps), Overall);
            foreach (var e in RepairLog)
            {
                var p = e.Split('|');
                Csv.Append(Path.Combine(Paths.Reports, "RepairLog.csv"), new[] { "Timestamp", "Action", "Reason", "Result", "Verification" }, now.ToString("s"), p[0], p[1], p[2], p[3]);
            }
            var hist = Csv.Read(csv, 200);
            string historyJson = Json.Serialize(hist);

            string tpl = Assets.ReadText("report.html") ?? "<html><body>__CHECKS__</body></html>";
            var checks = string.Join("\n", Results.Select(r =>
            {
                string cls = r.Status == "PASS" ? "pass" : r.Status == "WARNING" ? "warn" : r.Status == "FAIL" ? "fail" : "info";
                return "<tr><td>" + H(r.Area) + "</td><td>" + H(r.Name) + "</td><td>" + H(r.Value) + "</td><td><span class='pill " + cls + "'>" + H(r.Status) + "</span></td><td>" + H(r.Explanation) + "</td></tr>";
            }));
            var repairs = string.Join("\n", RepairLog.Select(e => { var p = e.Split('|'); return "<tr><td>" + H(now.ToString("s")) + "</td><td>" + H(p[0]) + "</td><td>" + H(p[2]) + "</td><td>" + H(p[3]) + "</td></tr>"; }));
            var map = new Dictionary<string, string>
            {
                ["__OVERALLCLASS__"] = Overall == "NO ISSUES DETECTED" ? "pass" : "warn",
                ["__OVERALL__"] = H(Overall),
                ["__RUNDATE__"] = H(now.ToString("yyyy-MM-dd HH:mm:ss")),
                ["__DOWN__"] = DownMbps != null ? DownMbps + " Mbps" : "N/A",
                ["__UP__"] = UpMbps != null ? UpMbps + " Mbps" : "N/A",
                ["__PING__"] = Cloud.Avg != null ? Cloud.Avg + " ms" : "N/A",
                ["__SIGNAL__"] = w.Signal != null ? w.Signal + "%" : "N/A",
                ["__RSSI__"] = w.Rssi != null ? w.Rssi + " dBm" : "N/A",
                ["__LIKELY__"] = H(Likely),
                ["__RECOMMENDATIONS__"] = string.Join("\n", Recommendations.Select(r => "<li>" + H(r) + "</li>")),
                ["__ROUTE__"] = string.Join("\n", route.Select(r => "<li>" + H(r) + "</li>")),
                ["__CHECKS__"] = checks,
                ["__HISTORY__"] = historyJson,
                ["__RESULTS__"] = "[]",
                ["__REPAIRSUMMARY__"] = H(RepairSummary),
                ["__REPAIRLOG__"] = repairs,
            };
            foreach (var kv in map) tpl = tpl.Replace(kv.Key, kv.Value);
            ReportPath = Path.Combine(Paths.Reports, "WiFiDashboard-" + now.ToString("yyyyMMdd-HHmmss") + ".html");
            File.WriteAllText(ReportPath, tpl, new UTF8Encoding(true));
        }

        public static string LatestReport()
        {
            try
            {
                return new DirectoryInfo(Paths.Reports).GetFiles("WiFiDashboard-*.html").OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).FirstOrDefault();
            }
            catch { return null; }
        }

        // ---------- repairs (asked for in the app, never automatic) ----------
        public static string RenewIp()
        {
            string err;
            bool ok = Elevated.RunPowerShell("ipconfig /release | Out-Null; Start-Sleep 2; ipconfig /renew | Out-Null", out err);
            Csv.Append(Path.Combine(Paths.Reports, "RepairLog.csv"), new[] { "Timestamp", "Action", "Reason", "Result", "Verification" }, DateTime.Now.ToString("s"), "Renew DHCP lease", "Asked by user", ok ? "Completed" : (err ?? "Failed"), "");
            return ok ? "IP address renewed." : (err ?? "Renew failed.");
        }

        public static string RestartWifi(string adapter)
        {
            if (string.IsNullOrEmpty(adapter)) return "No Wi-Fi adapter found.";
            string err;
            string a = adapter.Replace("'", "''");
            bool ok = Elevated.RunPowerShell("Restart-NetAdapter -Name '" + a + "' -Confirm:$false", out err);
            Csv.Append(Path.Combine(Paths.Reports, "RepairLog.csv"), new[] { "Timestamp", "Action", "Reason", "Result", "Verification" }, DateTime.Now.ToString("s"), "Restart Wi-Fi adapter", "Asked by user", ok ? "Completed" : (err ?? "Failed"), "");
            return ok ? "Wi-Fi adapter restarted. It reconnects in a few seconds." : (err ?? "Restart failed.");
        }

        public static string FlushDns()
        {
            bool ok = false;
            try { ok = Native.DnsFlushResolverCache(); } catch { }
            return ok ? "DNS cache cleared." : "Could not clear the DNS cache.";
        }
    }
}
