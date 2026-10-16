using System;
using System.Collections.Generic;
using System.Device.Location;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace LouisaWiFi
{
    // One store from stores.json (same file as the Mac version). Fields are filled in by the JSON reader.
#pragma warning disable 0649
    class Store
    {
        public string name;
        public string address;
        public string phone;
        public double lat;
        public double lng;
        public List<string> passwords;   // Wi-Fi password candidates, most likely first
#pragma warning restore 0649

        public string City
        {
            get
            {
                string first = (address ?? "").Split(' ')[0];
                return first.Length == 0 ? "其他" : first.Replace("臺", "台");
            }
        }

        // "台北市 大安區 ..." → "大安區". Some addresses have no space after the district
        // ("中山區中山北路二段…"), so cut at the first 區; only if there is none, at 鄉/鎮/市
        // (平鎮區 and 前鎮區 have a 鎮 inside the name).
        public string District
        {
            get
            {
                var parts = (address ?? "").Split(' ');
                if (parts.Length < 2) return "其他";
                var m = System.Text.RegularExpressions.Regex.Match(parts[1], "^.{1,4}?區");
                if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(parts[1], "^.{1,4}?[鄉鎮市]");
                return m.Success ? m.Value : "其他";
            }
        }

        public bool HasLocation { get { return lat != 0; } }
    }

    class NearStore
    {
        public Store Store;
        public double Meters;
    }

    static class Stores
    {
        public static readonly string[] CityOrder = {
            "台北市", "新北市", "基隆市", "桃園市", "新竹市", "新竹縣", "苗栗縣", "台中市", "彰化縣", "南投縣", "雲林縣",
            "嘉義市", "嘉義縣", "台南市", "高雄市", "屏東縣", "宜蘭縣", "花蓮縣", "台東縣", "澎湖縣", "金門縣", "連江縣" };

        static List<Store> all = new List<Store>();
        public static List<Store> All { get { return all; } }

        static string LocalFile { get { return Paths.Data("stores.json"); } }

        // The weekly-updated copy in %APPDATA% if it looks right, else the copy built into the exe.
        public static void Load()
        {
            List<Store> list = null;
            try { if (File.Exists(LocalFile)) list = Valid(Json.Parse<List<Store>>(File.ReadAllText(LocalFile, Encoding.UTF8))); }
            catch (Exception e) { Log.Write("stores.json unreadable: " + e.Message); }
            if (list == null)
            {
                if (File.Exists(LocalFile)) Log.Write("stores.json doesn't look right, using the built-in store list");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LouisaWiFi.stores.json"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                    list = Valid(Json.Parse<List<Store>>(r.ReadToEnd())) ?? new List<Store>();
            }
            all = list;
        }

        // Stores with a name and at least one password, or null when that leaves fewer than 100
        // (a broken download, or the upstream format changed): then keep using what we have.
        internal static List<Store> Valid(List<Store> list)
        {
            if (list == null) return null;
            var ok = list.Where(s => s != null && !string.IsNullOrEmpty(s.name) && s.passwords != null &&
                                     s.passwords.Count > 0 && s.passwords.All(p => !string.IsNullOrEmpty(p))).ToList();
            return ok.Count >= 100 ? ok : null;
        }

        public static int CityRank(string city)
        {
            int i = Array.IndexOf(CityOrder, city);
            return i < 0 ? CityOrder.Length : i;
        }

        public static double Distance(double lat1, double lng1, double lat2, double lng2)
        {
            const double r = 6371000, d = Math.PI / 180;
            double dLat = (lat2 - lat1) * d, dLng = (lng2 - lng1) * d;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * d) * Math.Cos(lat2 * d) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return 2 * r * Math.Asin(Math.Sqrt(a));
        }

        public static List<NearStore> Nearest(Location here, int limit, IEnumerable<Store> list)
        {
            if (here == null) return new List<NearStore>();
            return (list ?? all).Where(s => s.HasLocation)
                .Select(s => new NearStore { Store = s, Meters = Distance(here.Lat, here.Lng, s.lat, s.lng) })
                .OrderBy(n => n.Meters).Take(limit).ToList();
        }

        // Refresh %APPDATA%\stores.json from the upstream repo once a week (worker thread).
        static DateTime nextTry = DateTime.MinValue;
        public static void UpdateIfDue()
        {
            DateTime age = File.Exists(LocalFile) ? File.GetLastWriteTime(LocalFile) : DateTime.MinValue;
            if (DateTime.Now - age < TimeSpan.FromDays(7) || DateTime.Now < nextTry) return;
            nextTry = DateTime.Now.AddHours(1);
            if (!Net.Online()) { nextTry = DateTime.Now.AddMinutes(5); return; }
            try
            {
                string text;
                using (var web = new WebClient())
                {
                    web.Encoding = Encoding.UTF8;
                    text = web.DownloadString(App.StoresUrl);
                }
                var parsed = Valid(Json.Parse<List<Store>>(text));
                if (parsed == null)
                {
                    Log.Write("downloaded stores.json doesn't look right, keeping the current list");
                    return;
                }
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(LocalFile, text, new UTF8Encoding(false));
                all = parsed;
                Log.Write("stores updated: " + parsed.Count);
            }
            catch (Exception e) { Log.Write("store update failed: " + e.Message); }
        }
    }

    static class Net
    {
        // Same check Windows itself uses to decide whether a network has internet.
        public static bool Online()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://www.msftconnecttest.com/connecttest.txt");
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                    return r.ReadToEnd().Contains("Microsoft Connect Test");
            }
            catch { return false; }
        }
    }

    class Location
    {
        public double Lat, Lng;
        public DateTime Time;
    }

    // Windows location service. Laptops locate themselves from nearby Wi-Fi, so the fix is often
    // unknown right after disconnecting; keep the last one for 2 hours.
    static class Locator
    {
        static GeoCoordinateWatcher watcher;
        static volatile Location last;
        static DateTime nextCheck = DateTime.Now.AddSeconds(30);
        static int wait = 30;
        static Location moveAnchor;

        public static void Start()
        {
            try
            {
                watcher = new GeoCoordinateWatcher(GeoPositionAccuracy.Default);
                watcher.MovementThreshold = 100;
                watcher.TryStart(true, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception e) { Log.Write("location unavailable: " + e.Message); watcher = null; }
            Log.Write("location permission: " + (watcher == null ? "unavailable" : watcher.Permission.ToString()));
        }

        public static bool Allowed { get { return watcher != null && watcher.Permission == GeoPositionPermission.Granted; } }

        // Last known location from the past 2 hours, or null. Safe from any thread.
        public static Location Current
        {
            get
            {
                var l = last;
                return l != null && DateTime.Now - l.Time < TimeSpan.FromHours(2) ? l : null;
            }
        }

        // Called every second on the UI thread. Returns true when we moved more than 100 m,
        // which triggers a check like the Mac version does on location updates.
        public static bool Poll()
        {
            if (watcher == null) return false;
            var c = watcher.Position.Location;
            if (c != null && !c.IsUnknown)
            {
                if (last == null) Log.Write("location ready");   // never log coordinates: people paste logs into bug reports
                last = new Location { Lat = c.Latitude, Lng = c.Longitude, Time = DateTime.Now };
                wait = 30;
                nextCheck = DateTime.Now.AddSeconds(30);   // only restart after 30 s without a fix
            }
            else if (Allowed && DateTime.Now > nextCheck)
            {
                // The watcher sometimes gets stuck with no data (seen right after start). Restart it,
                // backing off from 30 s up to 10 min so a long outage doesn't spin or flood the log.
                if (wait <= 60) Log.Write("location unknown (" + watcher.Status + "), restarting");
                nextCheck = DateTime.Now.AddSeconds(wait);
                wait = Math.Min(wait * 2, 600);
                try { watcher.Stop(); watcher.TryStart(true, TimeSpan.FromMilliseconds(100)); } catch { }
            }

            var now = Current;
            if (now == null) return false;
            if (moveAnchor == null) { moveAnchor = now; return false; }
            if (Stores.Distance(moveAnchor.Lat, moveAnchor.Lng, now.Lat, now.Lng) <= 100) return false;
            moveAnchor = now;
            return true;
        }

        public static void Stop() { if (watcher != null) watcher.Stop(); }
    }
}
