using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Ambaan
{
    public class DnsPreset
    {
        public string Name, Note, P4, S4, P6, S6, DohP, DohS;
        public DnsPreset(string n, string note, string p4, string s4, string p6, string s6, string dohP, string dohS)
        { Name = n; Note = note; P4 = p4; S4 = s4; P6 = p6; S6 = s6; DohP = dohP; DohS = dohS; }
    }

    /// <summary>DNS Guard (from SilentDNS): sets your DNS on every connected adapter, IPv4 and IPv6, optional encrypted DNS (DoH),
    /// and keeps it set when you change Wi-Fi, plug in a cable or wake the PC. Needs administrator rights.</summary>
    public static class DnsGuard
    {
        const string CF = "https://cloudflare-dns.com/dns-query";
        const string CFS = "https://security.cloudflare-dns.com/dns-query";
        const string CFF = "https://family.cloudflare-dns.com/dns-query";
        const string GG = "https://dns.google/dns-query";
        const string Q9 = "https://dns.quad9.net/dns-query";
        const string AG = "https://dns.adguard-dns.com/dns-query";

        public static readonly DnsPreset[] Presets =
        {
            new DnsPreset("Cloudflare Security", "blocks scam + malware sites", "1.1.1.2", "1.0.0.2", "2606:4700:4700::1112", "2606:4700:4700::1002", CFS, CFS),
            new DnsPreset("Cloudflare + Google", "fastest, no filtering", "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111", "2001:4860:4860::8888", CF, GG),
            new DnsPreset("Cloudflare", "fast, private", "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001", CF, CF),
            new DnsPreset("Google", "reliable", "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844", GG, GG),
            new DnsPreset("Quad9", "blocks malware", "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9", Q9, Q9),
            new DnsPreset("AdGuard", "blocks ads + trackers", "94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff", AG, AG),
            new DnsPreset("Cloudflare Family", "also blocks adult sites", "1.1.1.3", "1.0.0.3", "2606:4700:4700::1113", "2606:4700:4700::1003", CFF, CFF),
        };

        public static DnsPreset Find(string name) { return Presets.FirstOrDefault(p => p.Name == name); }

        static string TemplateFor(string ip)
        {
            foreach (var p in Presets)
            {
                if (ip == p.P4 || ip == p.P6) return p.DohP;
                if (ip == p.S4 || ip == p.S6) return p.DohS;
            }
            return null;
        }

        public static bool IsAdmin
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        public static string Run(string exe, string args, int waitMs = 15000)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(waitMs);
                    return o;
                }
            }
            catch (Exception e) { return e.Message; }
        }

        static string Netsh => Path.Combine(Environment.SystemDirectory, "netsh.exe");

        /// <summary>Connected adapters with an internet route (Wi-Fi, Ethernet, USB tethering). Skips loopback, VPN tunnels, hotspot host side.</summary>
        public static List<NetworkInterface> ActiveAdapters()
        {
            var list = new List<NetworkInterface>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (n.GetIPProperties().GatewayAddresses.Count == 0) continue;
                    list.Add(n);
                }
                catch { }
            }
            return list;
        }

        static bool Valid(string s, AddressFamily fam)
        {
            IPAddress a;
            return !string.IsNullOrEmpty(s) && IPAddress.TryParse(s, out a) && a.AddressFamily == fam;
        }

        /// <summary>Set the DNS. force=false only touches adapters whose DNS drifted. Returns how many were changed.</summary>
        public static int Apply(Settings c, bool force)
        {
            int changed = 0;
            var want4 = new List<string>(); if (Valid(c.DnsP4, AddressFamily.InterNetwork)) want4.Add(c.DnsP4); if (Valid(c.DnsS4, AddressFamily.InterNetwork)) want4.Add(c.DnsS4);
            var want6 = new List<string>(); if (Valid(c.DnsP6, AddressFamily.InterNetworkV6)) want6.Add(c.DnsP6); if (Valid(c.DnsS6, AddressFamily.InterNetworkV6)) want6.Add(c.DnsS6);
            foreach (var n in ActiveAdapters())
            {
                IPInterfaceProperties ip;
                try { ip = n.GetIPProperties(); } catch { continue; }
                var cur = ip.DnsAddresses.Select(a => a.ToString().Split('%')[0].ToLowerInvariant()).ToList();
                try
                {
                    int idx = ip.GetIPv4Properties().Index;
                    var cur4 = cur.Where(x => !x.Contains(":")).ToList();
                    if (want4.Count > 0 && (force || !cur4.SequenceEqual(want4)))
                    {
                        Run(Netsh, "interface ipv4 set dnsservers name=" + idx + " source=static address=" + want4[0] + " register=primary validate=no");
                        for (int i = 1; i < want4.Count; i++) Run(Netsh, "interface ipv4 add dnsservers name=" + idx + " address=" + want4[i] + " index=" + (i + 1) + " validate=no");
                        changed++;
                    }
                }
                catch { }
                // IPv6 too, so Windows can't quietly fall back to the ISP's IPv6 DNS (Jio, Airtel...)
                try
                {
                    var v6 = ip.GetIPv6Properties();
                    if (v6 == null) continue;
                    var cur6 = cur.Where(x => x.Contains(":") && !x.StartsWith("fec0")).ToList();
                    if (want6.Count > 0 && (force || !cur6.SequenceEqual(want6.Select(x => x.ToLowerInvariant()))))
                    {
                        Run(Netsh, "interface ipv6 set dnsservers name=" + v6.Index + " source=static address=" + want6[0] + " register=primary validate=no");
                        for (int i = 1; i < want6.Count; i++) Run(Netsh, "interface ipv6 add dnsservers name=" + v6.Index + " address=" + want6[i] + " index=" + (i + 1) + " validate=no");
                        changed++;
                    }
                }
                catch { }
            }
            if (c.DnsEncrypted) EnableDoh(want4.Concat(want6));
            if (changed > 0 || force) { try { Native.DnsFlushResolverCache(); } catch { } }
            if (changed > 0) Log.Write("DNS Guard applied " + c.DnsPreset + " on " + changed + " adapter setting(s)");
            return changed;
        }

        /// <summary>Back to normal automatic DNS (from the router) on every adapter.</summary>
        public static void Reset()
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var ip = n.GetIPProperties();
                    try { Run(Netsh, "interface ipv4 set dnsservers name=" + ip.GetIPv4Properties().Index + " source=dhcp"); } catch { }
                    try { var v6 = ip.GetIPv6Properties(); if (v6 != null) Run(Netsh, "interface ipv6 set dnsservers name=" + v6.Index + " source=dhcp"); } catch { }
                }
                catch { }
            }
            try { Native.DnsFlushResolverCache(); } catch { }
            Log.Write("DNS reset to automatic");
        }

        /// <summary>Windows 11 built-in DNS-over-HTTPS: register the template and turn on auto-upgrade.</summary>
        static void EnableDoh(IEnumerable<string> servers)
        {
            foreach (var s in servers)
            {
                string t = TemplateFor(s);
                if (t == null) continue;
                string args = " encryption server=" + s + " dohtemplate=" + t + " autoupgrade=yes udpfallback=no";
                string o = Run(Netsh, "dns add" + args);
                if (o.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0 || o.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0)
                    Run(Netsh, "dns set" + args);
            }
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters"))
                    k.SetValue("EnableAutoDoh", 2, RegistryValueKind.DWord);
            }
            catch { }
        }

        /// <summary>The DNS servers Windows is using right now: [adapter, IPv4 list, IPv6 list].</summary>
        public static string[] Live()
        {
            var a = ActiveAdapters();
            if (a.Count == 0) return new[] { "No network", "-", "-" };
            try
            {
                var all = a[0].GetIPProperties().DnsAddresses;
                var d4 = all.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()).ToList();
                var d6 = all.Where(x => x.AddressFamily == AddressFamily.InterNetworkV6 && !x.IsIPv6SiteLocal).Select(x => x.ToString().Split('%')[0]).ToList();
                return new[] { a[0].Name, d4.Count == 0 ? "-" : string.Join(", ", d4), d6.Count == 0 ? "not used on this network" : string.Join(", ", d6) };
            }
            catch { return new[] { a[0].Name, "-", "-" }; }
        }

        /// <summary>Is the wanted DNS what Windows is using now?</summary>
        public static bool InPlace(Settings c)
        {
            try
            {
                var a = ActiveAdapters();
                if (a.Count == 0) return true;
                var cur = a[0].GetIPProperties().DnsAddresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()).ToList();
                return cur.Count > 0 && cur[0] == c.DnsP4;
            }
            catch { return true; }
        }

        // ---------- start at sign-in WITH admin rights, no UAC prompt (Task Scheduler) ----------
        const string TaskName = "AmbaanWiFiGuard";

        public static bool TaskExists() { return Run("schtasks.exe", "/Query /TN " + TaskName).IndexOf(TaskName, StringComparison.OrdinalIgnoreCase) >= 0; }

        public static void TaskEnable()
        {
            string user = WindowsIdentity.GetCurrent().Name;
            Func<string, string> esc = s => System.Security.SecurityElement.Escape(s);
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Ambaan WiFi Guard (with DNS Guard)</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + esc(user) + "</UserId></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
"<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>" +
"<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
"<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + esc(Paths.Exe) + "</Command><Arguments>--atlogin</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "ambaan_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            Log.Write("task: " + Run("schtasks.exe", "/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F").Trim());
            try { File.Delete(tmp); } catch { }
        }

        public static void TaskDisable() { Run("schtasks.exe", "/Delete /TN " + TaskName + " /F"); }

        /// <summary>Start the admin copy through the task (no UAC prompt). True if the task exists and was started.</summary>
        public static bool TaskRun() { return Run("schtasks.exe", "/Run /TN " + TaskName).IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Run this EXE with admin rights (UAC prompt). Returns false if the user said no.</summary>
        public static bool RunElevated(string args, bool wait)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Paths.Exe, args) { Verb = "runas", UseShellExecute = true });
                if (wait && p != null) p.WaitForExit(60000);
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (Exception ex) { Log.Error("elevate", ex); return false; }
        }

        /// <summary>Median answer time of a DNS server (5 lookups), or null if it never answered.</summary>
        public static int? Benchmark(string server)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(server, out ip)) return null;
            var times = new List<int>();
            foreach (var name in new[] { "example.com", "google.com", "wikipedia.org", "cloudflare.com", "microsoft.com" })
            {
                var t = DnsShield.Query(ip, name, 1500);
                if (t != null) times.Add(t.Value);
            }
            if (times.Count == 0) return null;
            times.Sort();
            return times[times.Count / 2];
        }
    }
}
