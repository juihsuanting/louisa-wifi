using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace LouisaWiFi
{
    // Counts minutes on Louisa Wi-Fi per day and store, like the Mac Tracker.swift.
    // usage.json: "yyyy-MM-dd" → store → seconds. Timers don't fire while asleep, so a closed lid isn't counted.
    class Tracker
    {
        public const string UnknownStore = "未知門市";
        readonly Dictionary<string, Dictionary<string, double>> usage;
        readonly Joiner joiner;

        static string FilePath { get { return Paths.Data("usage.json"); } }

        public Tracker(Joiner joiner)
        {
            this.joiner = joiner;
            usage = Json.Read<Dictionary<string, Dictionary<string, double>>>(FilePath)
                    ?? new Dictionary<string, Dictionary<string, double>>();
        }

        // Every minute (UI thread; the Wi-Fi query takes milliseconds).
        public void Tick()
        {
            var w = Wlan.Current();
            if (!w.Connected || !App.IsLouisa(w.Ssid)) return;
            string store = joiner.CurrentStore(w) ?? UnknownStore;
            string day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Dictionary<string, double> stores;
            if (!usage.TryGetValue(day, out stores)) usage[day] = stores = new Dictionary<string, double>();
            double seconds;
            stores.TryGetValue(store, out seconds);
            stores[store] = seconds + 60;
            Json.Write(FilePath, usage);
        }

        // Fill the stats page template with the data and open it in the default browser.
        public void ShowStats()
        {
            string template;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LouisaWiFi.stats.html"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                template = r.ReadToEnd();
            string json = Json.Serialize(usage).Replace("</", "<\\/");
            string page = template.Replace("/*__USAGE__*/{}", json);
            string path = Paths.Data("stats.html");
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(path, page, new UTF8Encoding(false));
            Process.Start(path);
        }
    }
}
