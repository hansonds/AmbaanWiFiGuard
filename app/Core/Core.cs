using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Ambaan
{
    /// <summary>Where everything lives. Installed: %LOCALAPPDATA%\AmbaanWiFiGuard. Portable: a folder next to the EXE.</summary>
    public static class Paths
    {
        public const string AppId = "AmbaanWiFiGuard";
        public const string AppName = "Ambaan WiFi Guard";
        public static string Exe => Assembly.GetExecutingAssembly().Location;
        public static string ExeDir => Path.GetDirectoryName(Exe);
        public static string Version
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }
        public static string Root;
        public static bool Portable;
        public static string Data => Path.Combine(Root, "MonitorData");
        public static string Reports => Path.Combine(Root, "Reports");
        public static string History => Path.Combine(Root, "History");
        public static string UserAssets => Path.Combine(Root, "assets");
        public static string Cache => Path.Combine(Root, "cache");

        public static void Init()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);
            if (File.Exists(Path.Combine(ExeDir, "installed.flag"))) { Root = local; Portable = false; }
            else
            {
                string p = Path.Combine(ExeDir, AppId + "-data");
                if (CanWrite(p)) { Root = p; Portable = true; } else { Root = local; Portable = false; }
            }
            foreach (var d in new[] { Root, Data, Reports, History, Cache }) Directory.CreateDirectory(d);
        }

        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string t = Path.Combine(dir, ".write-test");
                File.WriteAllText(t, "ok"); File.Delete(t);
                return true;
            }
            catch { return false; }
        }
    }

    public static class Log
    {
        static readonly object L = new object();
        static string FilePath => Path.Combine(Paths.Data, "App.log");
        public static void Write(string msg)
        {
            try
            {
                lock (L)
                {
                    var f = FilePath;
                    if (File.Exists(f) && new FileInfo(f).Length > 1_000_000)
                    {
                        var old = f + ".old"; if (File.Exists(old)) File.Delete(old); File.Move(f, old);
                    }
                    File.AppendAllText(f, DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
        public static void Error(string where, Exception ex) { Write("ERROR " + where + ": " + ex); }
    }

    public static class Json
    {
        static JavaScriptSerializer S() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        public static string Serialize(object o) { return S().Serialize(o); }
        public static object Parse(string text) { return S().DeserializeObject(text); }

        public static Dictionary<string, object> LoadObject(string path)
        {
            try
            {
                if (!File.Exists(path)) return new Dictionary<string, object>();
                var o = Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿')) as Dictionary<string, object>;
                return o ?? new Dictionary<string, object>();
            }
            catch (Exception ex) { Log.Error("json load " + path, ex); return new Dictionary<string, object>(); }
        }

        public static Dictionary<string, string> LoadMap(string path)
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in LoadObject(path)) d[kv.Key.ToUpperInvariant()] = kv.Value == null ? "" : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return d;
        }

        /// <summary>A JSON array (object[] or ArrayList) as numbers, or null.</summary>
        public static double[] Nums(object o)
        {
            try
            {
                if (o is object[] arr) return arr.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                if (o is ArrayList al) return al.Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
            }
            catch { }
            return null;
        }

        public static double[] LoadNumbers(string path)
        {
            try
            {
                var o = Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                if (o is object[] arr) return arr.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                if (o is ArrayList al) return al.Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
            }
            catch { }
            return null;
        }

        /// <summary>Write to a temp file then swap it in, so a crash never leaves a half-written file.</summary>
        public static void SaveAtomic(string path, string text)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch (Exception ex) { Log.Error("save " + path, ex); }
        }
        public static void Save(string path, object o) { SaveAtomic(path, Serialize(o)); }

        public static double Num(Dictionary<string, object> d, string k, double def)
        {
            object v; if (d.TryGetValue(k, out v) && v != null) { try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { } }
            return def;
        }
        public static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v; if (d.TryGetValue(k, out v) && v != null) { try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { } }
            return def;
        }
        public static string Str(Dictionary<string, object> d, string k, string def)
        {
            object v; if (d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return def;
        }
    }

    public static class Csv
    {
        static readonly object L = new object();
        static string Q(string s) { s = s ?? ""; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        public static void Append(string path, string[] header, params object[] values)
        {
            try
            {
                lock (L)
                {
                    bool isNew = !File.Exists(path);
                    var sb = new StringBuilder();
                    if (isNew) sb.AppendLine(string.Join(",", header.Select(Q)));
                    sb.AppendLine(string.Join(",", values.Select(v => Q(Convert.ToString(v, CultureInfo.InvariantCulture)))));
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception ex) { Log.Error("csv " + path, ex); }
        }

        public static List<Dictionary<string, string>> Read(string path, int lastN = 0)
        {
            var rows = new List<Dictionary<string, string>>();
            try
            {
                if (!File.Exists(path)) return rows;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 2) return rows;
                var head = Split(lines[0].TrimStart('﻿'));
                int start = 1;
                if (lastN > 0 && lines.Length - 1 > lastN) start = lines.Length - lastN;
                for (int i = start; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var f = Split(lines[i]);
                    var d = new Dictionary<string, string>();
                    for (int j = 0; j < head.Count; j++) d[head[j]] = j < f.Count ? f[j] : "";
                    rows.Add(d);
                }
            }
            catch (Exception ex) { Log.Error("csv read " + path, ex); }
            return rows;
        }

        static List<string> Split(string line)
        {
            var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { r.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            r.Add(sb.ToString());
            return r;
        }
    }

    public class Settings
    {
        public bool AutoStart = true;
        public double SlowMbps = 10, FastMbps = 50;
        public string FastMode = "Daily";      // Daily | Every | Off
        public int SpeedEvery = 30;            // minutes
        public bool AlertAll = false;          // alert when named devices reconnect too
        public bool Voice = true;
        public int Volume = 100;
        public string Character = "Ambaan";    // Ambaan | Dhamu
        public bool QuietHours = false;
        public int QuietFrom = 23, QuietTo = 7;
        public bool Glass = true;
        public bool OutageAlerts = true;
        public string Corner = "BottomRight";
        public bool Expanded = false;
        public double? ScreenX, ScreenY;
        public string LastFastDay = "";
        public bool AutoStartAsked = false;
        // DNS Guard (from SilentDNS)
        public bool DnsGuard = false;
        public string DnsPreset = "Cloudflare Security";
        public string DnsP4 = "1.1.1.2", DnsS4 = "1.0.0.2", DnsP6 = "2606:4700:4700::1112", DnsS6 = "2606:4700:4700::1002";
        public bool DnsEncrypted = true;
        // data limits (GB, 0 = off)
        public double MonthCapGB = 0, DayCapGB = 0;
        public string CapAlerts = "";
        public bool TrayHintShown = false;

        static string FilePath => Path.Combine(Paths.Data, "WidgetSettings.json");

        public static Settings Load()
        {
            var s = new Settings();
            var d = Json.LoadObject(FilePath);
            s.AutoStart = Json.Bool(d, "AutoStart", s.AutoStart);
            s.SlowMbps = Json.Num(d, "SlowMbps", s.SlowMbps);
            s.FastMbps = Json.Num(d, "FastMbps", s.FastMbps);
            var fm = Json.Str(d, "FastMode", s.FastMode); if (fm == "Daily" || fm == "Every" || fm == "Off") s.FastMode = fm;
            s.SpeedEvery = (int)Json.Num(d, "SpeedEvery", s.SpeedEvery);
            s.AlertAll = Json.Bool(d, "AlertAll", s.AlertAll);
            s.Voice = Json.Bool(d, "Voice", s.Voice);
            s.Volume = (int)Json.Num(d, "Volume", s.Volume);
            var ch = Json.Str(d, "Character", s.Character); if (ch == "Ambaan" || ch == "Dhamu") s.Character = ch;
            s.QuietHours = Json.Bool(d, "QuietHours", s.QuietHours);
            s.QuietFrom = (int)Json.Num(d, "QuietFrom", s.QuietFrom);
            s.QuietTo = (int)Json.Num(d, "QuietTo", s.QuietTo);
            s.Glass = Json.Bool(d, "Glass", s.Glass);
            s.OutageAlerts = Json.Bool(d, "OutageAlerts", s.OutageAlerts);
            s.Corner = Json.Str(d, "Corner", s.Corner);
            s.Expanded = Json.Bool(d, "Expanded", s.Expanded);
            if (d.ContainsKey("ScreenX") && d["ScreenX"] != null) { s.ScreenX = Json.Num(d, "ScreenX", 0); s.ScreenY = Json.Num(d, "ScreenY", 0); }
            s.LastFastDay = Json.Str(d, "LastFastDay", "");
            s.AutoStartAsked = Json.Bool(d, "AutoStartAsked", false);
            s.DnsGuard = Json.Bool(d, "DnsGuard", false);
            s.DnsPreset = Json.Str(d, "DnsPreset", s.DnsPreset);
            s.DnsP4 = Json.Str(d, "DnsP4", s.DnsP4); s.DnsS4 = Json.Str(d, "DnsS4", s.DnsS4);
            s.DnsP6 = Json.Str(d, "DnsP6", s.DnsP6); s.DnsS6 = Json.Str(d, "DnsS6", s.DnsS6);
            s.DnsEncrypted = Json.Bool(d, "DnsEncrypted", true);
            s.MonthCapGB = Json.Num(d, "MonthCapGB", 0); s.DayCapGB = Json.Num(d, "DayCapGB", 0);
            s.CapAlerts = Json.Str(d, "CapAlerts", "");
            s.TrayHintShown = Json.Bool(d, "TrayHintShown", false);
            s.Fix();
            return s;
        }

        public void Fix()
        {
            if (SlowMbps < 1) SlowMbps = 1; if (SlowMbps > 2000) SlowMbps = 2000;
            if (FastMbps <= SlowMbps) FastMbps = SlowMbps + 10;
            if (SpeedEvery < 10) SpeedEvery = 10; if (SpeedEvery > 240) SpeedEvery = 240;
            if (Volume < 0) Volume = 0; if (Volume > 100) Volume = 100;
            QuietFrom = ((QuietFrom % 24) + 24) % 24; QuietTo = ((QuietTo % 24) + 24) % 24;
            if (Corner != "TopLeft" && Corner != "TopRight" && Corner != "BottomLeft" && Corner != "BottomRight") Corner = "BottomRight";
        }

        public void Save()
        {
            var d = new Dictionary<string, object>
            {
                ["AutoStart"] = AutoStart, ["SlowMbps"] = SlowMbps, ["FastMbps"] = FastMbps, ["FastMode"] = FastMode,
                ["SpeedEvery"] = SpeedEvery, ["AlertAll"] = AlertAll, ["Voice"] = Voice, ["Volume"] = Volume,
                ["Character"] = Character, ["QuietHours"] = QuietHours, ["QuietFrom"] = QuietFrom, ["QuietTo"] = QuietTo,
                ["Glass"] = Glass, ["OutageAlerts"] = OutageAlerts, ["Corner"] = Corner, ["Expanded"] = Expanded,
                ["ScreenX"] = ScreenX, ["ScreenY"] = ScreenY, ["LastFastDay"] = LastFastDay, ["AutoStartAsked"] = AutoStartAsked,
                ["DnsGuard"] = DnsGuard, ["DnsPreset"] = DnsPreset, ["DnsP4"] = DnsP4, ["DnsS4"] = DnsS4, ["DnsP6"] = DnsP6, ["DnsS6"] = DnsS6,
                ["DnsEncrypted"] = DnsEncrypted, ["MonthCapGB"] = MonthCapGB, ["DayCapGB"] = DayCapGB, ["CapAlerts"] = CapAlerts, ["TrayHintShown"] = TrayHintShown
            };
            Json.Save(FilePath, d);
        }

        public bool InQuietHours()
        {
            if (!QuietHours || QuietFrom == QuietTo) return false;
            int h = DateTime.Now.Hour;
            return QuietFrom < QuietTo ? (h >= QuietFrom && h < QuietTo) : (h >= QuietFrom || h < QuietTo);
        }
    }

    /// <summary>Built-in pictures and sounds. They are unpacked once into cache\assets; files with the same
    /// name in the data folder's "assets" folder replace them (make your own character).</summary>
    public static class Assets
    {
        public static string CacheDir => Path.Combine(Paths.Cache, "assets");

        public static void Extract()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var asm = Assembly.GetExecutingAssembly();
                foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("assets/")))
                {
                    string file = Path.Combine(CacheDir, res.Substring(7));
                    using (var s = asm.GetManifestResourceStream(res))
                    {
                        if (File.Exists(file) && new FileInfo(file).Length == s.Length) continue;
                        using (var f = File.Create(file)) s.CopyTo(f);
                    }
                }
            }
            catch (Exception ex) { Log.Error("assets", ex); }
        }

        public static string Find(string name)
        {
            string u = Path.Combine(Paths.UserAssets, name);
            if (File.Exists(u)) return u;
            string c = Path.Combine(CacheDir, name);
            return File.Exists(c) ? c : null;
        }

        public static Stream Open(string logicalName)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
        }

        public static string ReadText(string logicalName)
        {
            using (var s = Open(logicalName)) { if (s == null) return null; using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd(); }
        }
    }

    /// <summary>Start with Windows: HKCU Run key (no admin). Removes the shortcut older versions put in the Startup folder.</summary>
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        /// <summary>With DNS Guard on, Ambaan must start with admin rights: a Task Scheduler entry does that without a UAC prompt.
        /// Otherwise a normal HKCU Run entry. Call with admin rights to switch between the two.</summary>
        public static void Sync(Settings c)
        {
            try
            {
                bool task = c.AutoStart && c.DnsGuard;
                if (DnsGuard.IsAdmin)
                {
                    if (task) DnsGuard.TaskEnable(); else if (DnsGuard.TaskExists()) DnsGuard.TaskDisable();
                }
                Apply(c.AutoStart && !(task && (DnsGuard.IsAdmin || DnsGuard.TaskExists())));
            }
            catch (Exception ex) { Log.Error("autostart sync", ex); }
        }

        public static void Apply(bool on)
        {
            try
            {
                string oldLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Ambaan WiFi Guard.lnk");
                if (File.Exists(oldLnk)) File.Delete(oldLnk);
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(Paths.AppId, "\"" + Paths.Exe + "\" --atlogin");
                    else if (k.GetValue(Paths.AppId) != null) k.DeleteValue(Paths.AppId, false);
                }
            }
            catch (Exception ex) { Log.Error("autostart", ex); }
        }
    }

    /// <summary>MAC address → maker name (Samsung, Apple, TP-Link ...), from the Wireshark list packed in at build time.</summary>
    public static class Oui
    {
        static Dictionary<string, string> map;
        static readonly object L = new object();
        static readonly string[] Junk = { "co", "co.", "ltd", "ltd.", "inc", "inc.", "corp", "corp.", "corporation", "limited", "llc", "gmbh", "ag", "sa", "s.a.", "plc", "company", "technology", "technologies", "tech", "electronics", "electronic", "communication", "communications", "international", "group", "holdings", "industrial", "mobile", "telecommunication", "telecommunications", "network", "networks", "systems", "computer", "digital", "devices", "device", "intelligent", "information", "software", "products", "manufacturing", "co.,ltd", "co.,ltd.", "(china)", "china", "shenzhen", "guangdong", "beijing", "hangzhou", "zhejiang", "dongguan", "corporate", "&" };

        public static string Lookup(string mac)
        {
            if (string.IsNullOrEmpty(mac) || mac.Length < 8) return null;
            lock (L)
            {
                if (map == null) LoadMap();
            }
            string key = mac.Replace("-", "").Replace(":", "").ToUpperInvariant();
            if (key.Length < 6) return null;
            string v; return map.TryGetValue(key.Substring(0, 6), out v) ? v : null;
        }

        static void LoadMap()
        {
            map = new Dictionary<string, string>();
            try
            {
                using (var s = Assets.Open("oui.txt.gz"))
                {
                    if (s == null) return;
                    using (var gz = new GZipStream(s, CompressionMode.Decompress))
                    using (var r = new StreamReader(gz, Encoding.UTF8))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            int t = line.IndexOf('\t');
                            if (t != 6) continue;
                            map[line.Substring(0, 6)] = Clean(line.Substring(7));
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("oui", ex); }
        }

        static string Clean(string name)
        {
            int c = name.IndexOf(','); if (c > 0) name = name.Substring(0, c);
            var words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(w => !Junk.Contains(w.ToLowerInvariant())).Take(2).ToList();
            if (words.Count == 0) return name.Trim();
            string r = string.Join(" ", words);
            if (r == r.ToUpperInvariant() && r.Length > 4) r = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(r.ToLowerInvariant());
            return r;
        }
    }
}
