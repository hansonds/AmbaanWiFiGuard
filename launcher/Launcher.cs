// Small Windows launcher for the PowerShell app.
// Built twice by GitHub Actions (see .github/workflows/build.yml):
//   - Installed build: the scripts sit in an "app" folder next to the EXE (put there by the Setup).
//   - PORTABLE build:  the scripts are packed inside the EXE and unpacked on first run into a
//                      "<AppId>-data" folder next to the EXE, together with all saved data.
// Command line:  (none) = open the widget   --atlogin = widget, started at Windows sign-in
//                --analyzer = run the full Wi-Fi test in a console window
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

static class Launcher
{
    static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
    static string ExeDir { get { return Path.GetDirectoryName(ExePath); } }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            bool analyzer = false, atLogin = false;
            foreach (string a in args)
            {
                string x = a.ToLowerInvariant().TrimStart('-', '/');
                if (x == "analyzer") analyzer = true;
                else if (x == "atlogin") atLogin = true;
            }

            string dataDir, appDir;
#if PORTABLE
            dataDir = Path.Combine(ExeDir, AppInfo.Id + "-data");
            if (!CanWrite(dataDir)) dataDir = LocalData();
            appDir = Unpack(dataDir);
#else
            dataDir = LocalData();
            appDir = Path.Combine(ExeDir, "app");
#endif
            Directory.CreateDirectory(dataDir);
            string script = Path.Combine(appDir, analyzer ? "WiFiNetworkAnalyzerPro.ps1" : "WiFi-Floating-Widget.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("App files are missing. Please reinstall.", script);

            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(ps);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = appDir;
            psi.EnvironmentVariables["AMBAAN_DATA"] = dataDir;
            psi.EnvironmentVariables["AMBAAN_EXE"] = ExePath;
            if (analyzer)
            {
                psi.Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"";
                psi.CreateNoWindow = false;   // the full test shows its progress in a console window
            }
            else
            {
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"" + (atLogin ? " -AtLogin" : "");
                psi.CreateNoWindow = true;
            }
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static string LocalData()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Id);
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

#if PORTABLE
    // Unpack the scripts once per version into <data>\app-<version>. Old versions are removed when possible.
    static string Unpack(string dataDir)
    {
        string target = Path.Combine(dataDir, "app-" + AppInfo.Version);
        string marker = Path.Combine(target, ".complete");
        if (File.Exists(marker)) return target;

        string tmp = target + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        {
            if (s == null) throw new InvalidOperationException("This EXE is damaged (no app files inside). Download it again.");
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read)) zip.ExtractToDirectory(tmp);
        }
        Directory.Move(tmp, target);
        File.WriteAllText(marker, AppInfo.Version);

        foreach (string d in Directory.GetDirectories(dataDir, "app-*"))
        {
            if (!string.Equals(d, target, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(d, true); } catch { }   // still in use: leave it for next time
        }
        return target;
    }
#endif
}
