using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ambaan
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr h, ref WCAData d);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        [StructLayout(LayoutKind.Sequential)] public struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
        [StructLayout(LayoutKind.Sequential)] public struct WCAData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("iphlpapi.dll")] public static extern int GetIpNetTable2(ushort family, out IntPtr table);
        [DllImport("iphlpapi.dll")] public static extern void FreeMibTable(IntPtr p);
        [DllImport("iphlpapi.dll")] public static extern int GetBestInterface(uint dest, out uint idx);
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")] public static extern bool DnsFlushResolverCache();

        [DllImport("wlanapi.dll")] public static extern int WlanOpenHandle(uint ver, IntPtr res, out uint neg, out IntPtr h);
        [DllImport("wlanapi.dll")] public static extern int WlanEnumInterfaces(IntPtr h, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] public static extern int WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr res, out int size, out IntPtr data, out int valType);
        [DllImport("wlanapi.dll")] public static extern int WlanGetNetworkBssList(IntPtr h, ref Guid g, IntPtr ssid, int bssType, bool secOnly, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] public static extern int WlanScan(IntPtr h, ref Guid g, IntPtr ssid, IntPtr ie, IntPtr res);
        [DllImport("wlanapi.dll")] public static extern void WlanFreeMemory(IntPtr p);

        public static int WindowsBuild
        {
            get
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                        return int.Parse((string)k.GetValue("CurrentBuildNumber"));
                }
                catch { return 0; }
            }
        }
    }

    public class WifiState
    {
        public bool HasAdapter, Connected, LocationBlocked;
        public string Ssid = "", Band = "", Name = "", Bssid = "", Gen = "";
        public int? Signal, Rssi;
        public int Channel;
        public double Rx, Tx;
    }

    public class BssInfo
    {
        public string Ssid = "", Bssid = "", Band = "";
        public int Rssi, Quality, FreqMHz, Channel;
    }

    /// <summary>Windows Native Wi-Fi API: same numbers as "netsh wlan", but works in every Windows language.</summary>
    public static class WlanApi
    {
        static IntPtr handle = IntPtr.Zero;
        static readonly object L = new object();

        static IntPtr H
        {
            get
            {
                if (handle == IntPtr.Zero)
                {
                    uint neg; IntPtr h;
                    if (Native.WlanOpenHandle(2, IntPtr.Zero, out neg, out h) == 0) handle = h;
                }
                return handle;
            }
        }

        class Iface { public Guid Guid; public string Desc; public int State; }

        static List<Iface> Interfaces()
        {
            var r = new List<Iface>();
            IntPtr list;
            if (H == IntPtr.Zero || Native.WlanEnumInterfaces(H, IntPtr.Zero, out list) != 0) return r;
            try
            {
                int n = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = list + 8 + i * 532;
                    r.Add(new Iface { Guid = Marshal.PtrToStructure<Guid>(p), Desc = Marshal.PtrToStringUni(p + 16), State = Marshal.ReadInt32(p, 16 + 512) });
                }
            }
            finally { Native.WlanFreeMemory(list); }
            return r;
        }

        static string NameOf(Guid g)
        {
            try
            {
                string id = g.ToString("B");
                var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                return ni != null ? ni.Name : "Wi-Fi";
            }
            catch { return "Wi-Fi"; }
        }

        public static WifiState Current()
        {
            var w = new WifiState { Ssid = "Wired" };
            lock (L)
            {
                try
                {
                    var ifs = Interfaces();
                    w.HasAdapter = ifs.Count > 0;
                    var c = ifs.FirstOrDefault(i => i.State == 1);   // wlan_interface_state_connected
                    if (c == null) { if (ifs.Count > 0) w.Name = NameOf(ifs[0].Guid); return w; }
                    w.Connected = true; w.Name = NameOf(c.Guid); w.Ssid = "Wi-Fi";
                    var g = c.Guid;
                    int size, vt; IntPtr data;
                    int rc = Native.WlanQueryInterface(H, ref g, 7, IntPtr.Zero, out size, out data, out vt);   // current_connection
                    if (rc == 5) { w.LocationBlocked = true; }
                    else if (rc == 0)
                    {
                        try
                        {
                            int len = Marshal.ReadInt32(data, 520);
                            if (len > 0 && len <= 32) { var b = new byte[len]; Marshal.Copy(data + 524, b, 0, len); w.Ssid = Encoding.UTF8.GetString(b); }
                            var mac = new byte[6]; Marshal.Copy(data + 560, mac, 0, 6);
                            w.Bssid = string.Join(":", mac.Select(x => x.ToString("X2")));
                            int phy = Marshal.ReadInt32(data, 568);
                            w.Gen = phy == 7 ? "Wi-Fi 4" : phy == 8 ? "Wi-Fi 5" : phy == 10 ? "Wi-Fi 6" : phy == 11 ? "Wi-Fi 7" : "";
                            w.Signal = Marshal.ReadInt32(data, 576);
                            w.Rx = Marshal.ReadInt32(data, 580) / 1000.0;
                            w.Tx = Marshal.ReadInt32(data, 584) / 1000.0;
                        }
                        finally { Native.WlanFreeMemory(data); }
                    }
                    if (Native.WlanQueryInterface(H, ref g, 8, IntPtr.Zero, out size, out data, out vt) == 0)   // channel_number
                    {
                        w.Channel = Marshal.ReadInt32(data); Native.WlanFreeMemory(data);
                    }
                    if (Native.WlanQueryInterface(H, ref g, 0x10000102, IntPtr.Zero, out size, out data, out vt) == 0)   // rssi
                    {
                        w.Rssi = Marshal.ReadInt32(data); Native.WlanFreeMemory(data);
                    }
                    if (w.Signal == null && w.Rssi != null) w.Signal = Math.Max(0, Math.Min(100, 2 * (w.Rssi.Value + 100)));
                    w.Band = w.Channel > 14 ? "5 GHz" : w.Channel > 0 ? "2.4 GHz" : "";
                    if (w.Bssid != "")
                    {
                        var me = BssList(g).FirstOrDefault(b => b.Bssid == w.Bssid);
                        if (me != null && me.Band != "") w.Band = me.Band;
                    }
                }
                catch (Exception ex) { Log.Error("wlan", ex); }
            }
            return w;
        }

        static List<BssInfo> BssList(Guid g)
        {
            var r = new List<BssInfo>();
            IntPtr list;
            if (Native.WlanGetNetworkBssList(H, ref g, IntPtr.Zero, 3, false, IntPtr.Zero, out list) != 0) return r;
            try
            {
                int n = Marshal.ReadInt32(list, 4);
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = list + 8 + i * 360;
                    var b = new BssInfo();
                    int len = Marshal.ReadInt32(e, 0);
                    if (len > 0 && len <= 32) { var s = new byte[len]; Marshal.Copy(e + 4, s, 0, len); b.Ssid = Encoding.UTF8.GetString(s); }
                    var mac = new byte[6]; Marshal.Copy(e + 40, mac, 0, 6);
                    b.Bssid = string.Join(":", mac.Select(x => x.ToString("X2")));
                    b.Rssi = Marshal.ReadInt32(e, 56);
                    b.Quality = Marshal.ReadInt32(e, 60);
                    b.FreqMHz = Marshal.ReadInt32(e, 92) / 1000;
                    int f = b.FreqMHz;
                    if (f >= 2400 && f < 2500) { b.Band = "2.4 GHz"; b.Channel = f == 2484 ? 14 : (f - 2407) / 5; }
                    else if (f >= 5900 && f < 7200) { b.Band = "6 GHz"; b.Channel = (f - 5950) / 5; }
                    else if (f >= 5000 && f < 5900) { b.Band = "5 GHz"; b.Channel = (f - 5000) / 5; }
                    r.Add(b);
                }
            }
            finally { Native.WlanFreeMemory(list); }
            return r;
        }

        /// <summary>Nearby networks. With rescan=true it asks the adapter for a fresh scan (takes ~4 s).</summary>
        public static List<BssInfo> Scan(bool rescan, out bool blocked)
        {
            blocked = false;
            var all = new List<BssInfo>();
            List<Iface> ifs;
            lock (L) ifs = Interfaces();
            foreach (var i in ifs)
            {
                var g = i.Guid;
                if (rescan) { lock (L) Native.WlanScan(H, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
            }
            if (rescan && ifs.Count > 0) System.Threading.Thread.Sleep(4000);
            foreach (var i in ifs)
            {
                var g = i.Guid; IntPtr list;
                lock (L)
                {
                    int rc = Native.WlanGetNetworkBssList(H, ref g, IntPtr.Zero, 3, false, IntPtr.Zero, out list);
                    if (rc == 5) { blocked = true; continue; }
                    if (rc != 0) continue;
                    Native.WlanFreeMemory(list);
                    all.AddRange(BssList(g));
                }
            }
            return all;
        }
    }

    public class Primary
    {
        public NetworkInterface Ni;
        public IPAddress Local, Mask, Gateway;
        public int Index;
        public bool IsWifi;
        public string Prefix24 { get { var b = Local.GetAddressBytes(); return b[0] + "." + b[1] + "." + b[2] + "."; } }
    }

    public class Neighbor { public string Ip, Mac; public int State; }

    public static class NetUtil
    {
        public static string Mac(byte[] b) { return string.Join("-", b.Take(6).Select(x => x.ToString("X2"))); }

        public static uint ToUint(IPAddress a) { return BitConverter.ToUInt32(a.GetAddressBytes(), 0); }

        public static Primary GetPrimary()
        {
            try
            {
                uint best = 0;
                Native.GetBestInterface(ToUint(IPAddress.Parse("1.1.1.1")), out best);
                Primary fallback = null;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var p = ni.GetIPProperties();
                    IPv4InterfaceProperties v4 = null;
                    try { v4 = p.GetIPv4Properties(); } catch { }
                    if (v4 == null) continue;
                    var gw = p.GatewayAddresses.Select(x => x.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                    if (gw == null) continue;
                    var ua = p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (ua == null) continue;
                    var pr = new Primary { Ni = ni, Local = ua.Address, Mask = ua.IPv4Mask, Gateway = gw, Index = v4.Index, IsWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 };
                    if (v4.Index == best) return pr;
                    if (fallback == null) fallback = pr;
                }
                return fallback;
            }
            catch (Exception ex) { Log.Error("primary", ex); return null; }
        }

        public static IPAddress WifiIp(string ifaceName)
        {
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == ifaceName && n.OperationalStatus == OperationalStatus.Up);
                return ni?.GetIPProperties().UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch { return null; }
        }

        public static async Task<int?> PingMs(string host, int timeout)
        {
            if (string.IsNullOrEmpty(host)) return null;
            try
            {
                using (var p = new Ping())
                {
                    var r = await p.SendPingAsync(host, timeout).ConfigureAwait(false);
                    if (r.Status == IPStatus.Success) return (int)r.RoundtripTime;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Windows neighbour (ARP) table for IPv4.</summary>
        public static List<Neighbor> Neighbors()
        {
            var list = new List<Neighbor>();
            IntPtr t;
            try
            {
                if (Native.GetIpNetTable2(2, out t) != 0) return list;
            }
            catch { return list; }
            try
            {
                int n = Marshal.ReadInt32(t);
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = t + 8 + i * 88;
                    if ((ushort)Marshal.ReadInt16(r, 0) != 2) continue;
                    int len = Marshal.ReadInt32(r, 72);
                    if (len != 6) continue;
                    var a = new byte[4]; Marshal.Copy(r + 4, a, 0, 4);
                    var m = new byte[6]; Marshal.Copy(r + 40, m, 0, 6);
                    list.Add(new Neighbor { Ip = new IPAddress(a).ToString(), Mac = Mac(m), State = Marshal.ReadInt32(r, 76) });
                }
            }
            catch (Exception ex) { Log.Error("arp", ex); }
            finally { Native.FreeMibTable(t); }
            return list;
        }

        public static Dictionary<string, string> OwnMacs()
        {
            var d = new Dictionary<string, string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet && ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    var b = ni.GetPhysicalAddress().GetAddressBytes();
                    if (b.Length == 6) d[Mac(b)] = ni.Name;
                }
            }
            catch { }
            return d;
        }

        /// <summary>Locally administered bit set = private/random address (phones, tablets, laptops in privacy mode).</summary>
        public static bool IsRandomMac(string mac)
        {
            if (mac == null || mac.Length < 2) return false;
            char c = char.ToUpperInvariant(mac[1]);
            return c == '2' || c == '6' || c == 'A' || c == 'E';
        }

        /// <summary>Bytes received/sent per adapter (Wi-Fi and Ethernet that are up).</summary>
        public static Dictionary<string, long[]> Counters()
        {
            var d = new Dictionary<string, long[]>();
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType != NetworkInterfaceType.Ethernet && n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    try { var s = n.GetIPStatistics(); d[n.Id] = new[] { s.BytesReceived, s.BytesSent }; } catch { }
                }
            }
            catch { }
            return d;
        }

        /// <summary>Wake-on-LAN magic packet: 6 x FF then the MAC 16 times, broadcast on UDP 9 and 7.</summary>
        public static bool WakeOnLan(string mac, Primary pr)
        {
            try
            {
                var m = mac.Split('-', ':').Select(x => Convert.ToByte(x, 16)).ToArray();
                if (m.Length != 6) return false;
                var pkt = new byte[102];
                for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
                for (int i = 0; i < 16; i++) Array.Copy(m, 0, pkt, 6 + i * 6, 6);
                using (var u = new UdpClient())
                {
                    u.EnableBroadcast = true;
                    var targets = new List<IPAddress> { IPAddress.Broadcast };
                    if (pr != null) targets.Add(IPAddress.Parse(pr.Prefix24 + "255"));
                    foreach (var t in targets) { u.Send(pkt, pkt.Length, new IPEndPoint(t, 9)); u.Send(pkt, pkt.Length, new IPEndPoint(t, 7)); }
                }
                return true;
            }
            catch (Exception ex) { Log.Error("wol", ex); return false; }
        }

        public static string Size(double bytes)
        {
            if (bytes >= 1e9) return (bytes / 1e9).ToString("0.00") + " GB";
            if (bytes >= 1e6) return (bytes / 1e6).ToString("0") + " MB";
            return (bytes / 1e3).ToString("0") + " KB";
        }

        public static string Minutes(double m)
        {
            if (m < 1) return (int)Math.Max(1, m * 60) + " s";
            if (m < 60) return (int)Math.Round(m) + " min";
            return ((int)(m / 60)) + " h " + ((int)(m % 60)) + " min";
        }
    }
}
