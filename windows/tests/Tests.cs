// Offline checks for the Windows app: store data, parsing, data-file compatibility, menu sizing.
// No Wi-Fi, location or tray needed, so they also run on GitHub Actions. Run: windows\test.ps1
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LouisaWiFi
{
    static class Tests
    {
        static int failures;

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
            if (!ok) failures++;
        }

        static int Main(string[] args)   // args[0]: path to data/stores.json
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("Store data (built-in copy of data/stores.json)");
            Stores.Load();
            Check(Stores.All.Count >= 100, Stores.All.Count + " stores");
            Check(Stores.All.All(s => s.passwords.Count > 0), "every store has password candidates");
            Check(Stores.All.Count(s => s.HasLocation) > Stores.All.Count / 2, "most stores have coordinates");

            Console.WriteLine("Address parsing");
            Func<string, Store> at = a => new Store { address = a };
            Check(at("臺中市 西屯區 台灣大道三段").City == "台中市", "臺 → 台 in city names");
            Check(at("台北市 大安區 羅斯福路四段1號").District == "大安區", "district");
            Check(at("台北市 中山區中山北路二段48巷7號").District == "中山區", "district without a space after it");
            Check(at("桃園市 平鎮區 環南路").District == "平鎮區", "平鎮區 (鎮 inside the name) is kept whole");
            Check(at("高雄市 前鎮區 中華五路").District == "前鎮區", "前鎮區 is kept whole");
            Check(at("").City == "其他" && at("").District == "其他", "empty address");

            Console.WriteLine("stores.json validation (a broken download must not replace good data)");
            var renamed = Json.Parse<List<Store>>(File.ReadAllText(args[0], System.Text.Encoding.UTF8)
                .Replace("\"passwords\"", "\"wifi_passwords\""));
            Check(Stores.Valid(renamed) == null, "rejects a list whose passwords field was renamed");
            Check(Stores.Valid(Stores.All.Take(50).ToList()) == null, "rejects a list with fewer than 100 stores");
            Check(Stores.Valid(Stores.All) != null, "accepts the real list");

            Console.WriteLine("Distances");
            double d = Stores.Distance(25.0478, 121.5170, 25.0339, 121.5645);   // Taipei Main Station → Taipei 101
            Check(d > 4500 && d < 5200, "Taipei Main Station → Taipei 101 ≈ 5 km (" + (int)d + " m)");
            Check(Fmt.Distance(215) == "210 m" && Fmt.Distance(1530) == "1.5 km", "distance labels");
            var near = Stores.Nearest(new Location { Lat = 25.0123, Lng = 121.5378, Time = DateTime.Now }, 3, null);
            Check(near.Count == 3 && near[0].Meters <= near[1].Meters && near[1].Meters <= near[2].Meters, "nearest stores, closest first");

            Console.WriteLine("Data files written by the PowerShell version / Mac format (UTF-8 BOM, pretty-printed)");
            var learned = Json.Parse<Dictionary<string, LearnedEntry>>(
                "﻿{\r\n    \"9a:23:b1:05:2c:88\":  {\r\n        \"store\":  \"台大卓越門市\",\r\n        \"password\":  \"23676613\"\r\n    }\r\n}".TrimStart('﻿'));
            Check(learned["9a:23:b1:05:2c:88"].store == "台大卓越門市" && learned["9a:23:b1:05:2c:88"].password == "23676613", "learned.json");
            var usage = Json.Parse<Dictionary<string, Dictionary<string, double>>>("{\"2026-10-14\":{\"台大卓越門市\":120,\"未知門市\":60}}");
            Check(usage["2026-10-14"]["台大卓越門市"] == 120, "usage.json (same format as the Mac app)");
            Check(Json.Serialize(usage).Contains("\"台大卓越門市\":120"), "usage.json round-trips");

            Console.WriteLine("Menu sizing (Windows menus can't scroll with the wheel)");
            foreach (var city in Stores.All.GroupBy(s => s.City).Where(g => g.Count() > 30))
            {
                int longest = city.GroupBy(s => s.District).Max(g => g.Count());
                Check(longest <= 30, city.Key + " (" + city.Count() + " stores): biggest district has " + longest);
            }

            Console.WriteLine(failures == 0 ? "All checks passed." : failures + " check(s) failed.");
            return failures == 0 ? 0 : 1;
        }
    }
}
