// Louisa Wi-Fi for Windows
// 走進任何一家路易莎，Windows 自動連上 Wi-Fi。
// 移植自 https://github.com/madeyexz/louisa-wifi （MIT）
//
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.x csc.exe),
// so anyone can build it without installing anything. See build.ps1.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("Louisa Wi-Fi")]
[assembly: AssemblyDescription("Louisa Wi-Fi")]
[assembly: AssemblyProduct("Louisa Wi-Fi")]
[assembly: AssemblyCompany("louisa-wifi")]
[assembly: AssemblyCopyright("MIT")]
// Version attributes come from build.ps1 (build\Version.cs), from the git tag like the Mac app.
// Without this, an exe built by plain csc.exe runs in .NET 4.0 compatibility mode
// (old TLS defaults, so downloads from GitHub fail).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace LouisaWiFi
{
    static class App
    {
        public const string Name = "路易莎 Wi-Fi";
        // Stores don't all use the same network name: "LouisaCoffee" at most, "LOUISA COFFEE" at some
        // (seen at 新店佳瑪門市). Compare ignoring case and spaces.
        public static bool IsLouisa(string ssid)
        {
            if (ssid == null) return false;
            return ssid.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant() == "louisacoffee";
        }
        public const string MutexName = @"Local\LouisaWiFi";
        public const string ShowEventName = @"Local\LouisaWiFi.Show";
        public const string QuitEventName = @"Local\LouisaWiFi.Quit";
        public const string StoresUrl = "https://raw.githubusercontent.com/madeyexz/louisa-wifi/main/data/stores.json";
        public const string ProjectUrl = "https://github.com/madeyexz/louisa-wifi";

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }
    }

    static class Paths
    {
        // Same folder as the PowerShell version, so settings, learned routers and stats carry over.
        // LOUISAWIFI_DATA points it elsewhere (tests).
        public static readonly string DataDir = Environment.GetEnvironmentVariable("LOUISAWIFI_DATA") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LouisaWiFi");

        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LouisaWiFi");

        public static string InstalledExe { get { return Path.Combine(InstallDir, "LouisaWiFi.exe"); } }
        public static string CurrentExe { get { return Assembly.GetExecutingAssembly().Location; } }

        public static string Data(string name) { return Path.Combine(DataDir, name); }
    }

    static class Log
    {
        static readonly object gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Paths.DataDir);
                    string path = Paths.Data("LouisaWiFi.log");
                    // Keep the log small: past 1 MB, the old one becomes LouisaWiFi.old.log.
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string old = Paths.Data("LouisaWiFi.old.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + " " + message + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    static class Json
    {
        static JavaScriptSerializer Serializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }

        public static T Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Serializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception e)
            {
                Log.Write("read " + Path.GetFileName(path) + " failed: " + e.Message);
                return null;
            }
        }

        public static T Parse<T>(string text) { return Serializer().Deserialize<T>(text); }

        public static string Serialize(object value) { return Serializer().Serialize(value); }

        // Write to a temp file first, so a crash mid-write can't leave a half-written file.
        public static void Write(string path, object value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Log.Write("save " + Path.GetFileName(path) + " failed: " + e.Message);
            }
        }
    }

    // config.json. Field names match the PowerShell version.
    class Config
    {
        public bool autoConnect = true;
        public bool onlyKnown = false;
        public bool hideIcon = false;
        public bool welcomed = false;
        public bool locationAsked = false;

        static Config current;
        public static Config Current
        {
            get
            {
                if (current == null) current = Json.Read<Config>(Paths.Data("config.json")) ?? new Config();
                return current;
            }
        }

        public void Save() { Json.Write(Paths.Data("config.json"), this); }
    }

    // learned.json: BSSID → the store and password that worked there.
    class LearnedEntry
    {
        public string store;
        public string password;
    }

    static class Fmt
    {
        public static string Distance(double meters)
        {
            if (meters < 1000) return ((int)(meters / 10) * 10) + " m";
            return (meters / 1000).ToString("0.0") + " km";
        }

    }
}
