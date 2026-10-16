using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LouisaWiFi
{
    enum StatusKind { Info, Connecting, Connected, Failed }

    // Finds the right password for the Louisa you're at and joins it. Port of the Mac Joiner.swift.
    // Scanning and joining block, so all of it runs on one background thread, in order.
    class Joiner
    {
        public const string LocationBlockedText = "Windows 需要開啟定位才能掃描 Wi-Fi，請到「設定 → 隱私權與安全性 → 定位」開啟";

        // Raised on the worker thread: text, kind, show a notification.
        public event Action<string, StatusKind, bool> StatusChanged;

        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        readonly object learnedLock = new object();
        readonly Dictionary<string, LearnedEntry> learned;
        readonly Dictionary<string, DateTime> snoozed = new Dictionary<string, DateTime>();   // BSSIDs we recently failed on
        DateTime quietUntil = DateTime.MinValue;   // our own joins fire Wi-Fi events; ignore them for a bit
        DateTime? offlineSince;
        DateTime lastScan = DateTime.Now;
        int epoch;                                 // bumped by user actions to stop a running attempt
        int autoQueued;
        volatile bool busy;
        string lastText;

        enum Outcome
        {
            Joined,
            Failed,
            Interrupted,   // a newer user action in this app
            UserTookOver,  // the user connected to another network themselves
            Gone,          // LouisaCoffee isn't there any more (Windows' network list was stale)
        }

        class Attempt
        {
            public string Store, Password;
            public Attempt(string store, string password) { Store = store; Password = password; }
        }

        public Joiner()
        {
            learned = Json.Read<Dictionary<string, LearnedEntry>>(Paths.Data("learned.json")) ?? new Dictionary<string, LearnedEntry>();
            var t = new Thread(Run) { IsBackground = true, Name = "joiner" };
            t.Start();
        }

        void Run()
        {
            foreach (var action in queue.GetConsumingEnumerable())
            {
                busy = true;
                try { action(); }
                catch (Exception e) { Log.Write("error: " + e); }
                finally { busy = false; }
            }
        }

        // ---------- Entry points (any thread) ----------

        // Wi-Fi changed, location changed, timer. Coalesces bursts into one pending run.
        public void AutoConnect()
        {
            if (Interlocked.Exchange(ref autoQueued, 1) == 1) return;
            int e = Thread.VolatileRead(ref epoch);
            queue.Add(() =>
            {
                Interlocked.Exchange(ref autoQueued, 0);
                RunAuto(false, e);
                Maintenance();
            });
        }

        // 連線到最近的門市: ignores the auto-connect setting and snoozes.
        public void ConnectNearest()
        {
            int e = Interlocked.Increment(ref epoch);
            if (busy) Report("稍候，改連最近的門市…", StatusKind.Connecting, false);
            queue.Add(() => { snoozed.Clear(); RunAuto(true, e); });
        }

        // User picked a store, optionally with the exact password.
        public void Connect(Store store, string password)
        {
            int e = Interlocked.Increment(ref epoch);
            if (busy) Report("稍候，改連：" + store.name + "…", StatusKind.Connecting, false);
            queue.Add(() => ConnectStore(store, password, e));
        }

        public void ClearSnoozes() { queue.Add(() => snoozed.Clear()); }

        // True while the worker is scanning or trying passwords.
        public bool Busy { get { return busy; } }

        bool Current(int e) { return Thread.VolatileRead(ref epoch) == e; }

        // ---------- Learned routers ----------

        public string CurrentStore(WifiStatus w)
        {
            if (w == null || !w.Connected || !App.IsLouisa(w.Ssid) || w.Bssid == null) return null;
            lock (learnedLock)
            {
                LearnedEntry l;
                return learned.TryGetValue(w.Bssid, out l) ? l.store : null;
            }
        }

        public HashSet<string> KnownStores()
        {
            lock (learnedLock) return new HashSet<string>(learned.Values.Select(l => l.store));
        }

        public string ConnectedText(WifiStatus w)
        {
            string store = CurrentStore(w);
            return store != null ? "已連上：" + store : "已連上路易莎";
        }

        List<Attempt> LearnedAttempts(ScanResult scan, string onlyStore)
        {
            var list = new List<Attempt>();
            lock (learnedLock)
            {
                foreach (var ap in scan.Aps)
                {
                    LearnedEntry l;
                    if (learned.TryGetValue(ap.Bssid, out l) && (onlyStore == null || l.store == onlyStore))
                        list.Add(new Attempt(l.store, l.password));
                }
            }
            return list;
        }

        // Remember every strong LouisaCoffee router here (and the strongest one), like the Mac version.
        void Remember(ScanResult scan, string store, string password)
        {
            Dictionary<string, LearnedEntry> copy;
            lock (learnedLock)
            {
                var keys = scan.Aps.Where(a => a.Rssi > -75).Select(a => a.Bssid).ToList();
                keys.Add(scan.Best.Bssid);
                foreach (var b in keys.Distinct()) learned[b] = new LearnedEntry { store = store, password = password };
                copy = new Dictionary<string, LearnedEntry>(learned);
            }
            Json.Write(Paths.Data("learned.json"), copy);
        }

        // ---------- Auto ----------

        void RunAuto(bool manual, int e)
        {
            var w = Wlan.Current();
            if (w.NoAdapter) { if (manual) Report("找不到 Wi-Fi 網卡", StatusKind.Failed, true); return; }
            if (w.RadioOff) { if (manual) Report("請先開啟 Wi-Fi", StatusKind.Failed, true); return; }
            if (!manual && !Config.Current.autoConnect) return;
            if (!manual && DateTime.Now < quietUntil) return;
            if (w.Connected && App.IsLouisa(w.Ssid))
            {
                if (manual) Report(ConnectedText(w), StatusKind.Connected, true);
                return;
            }

            if (manual) Wlan.ScanAndWait();
            bool blocked;
            var scan = Wlan.FindLouisa(out blocked);   // auto: Windows' cached list, cheap, can be a minute stale
            if (blocked) { Report(LocationBlockedText, StatusKind.Failed, manual); return; }
            if (scan == null)
            {
                offlineSince = null;
                if (manual) Report("附近沒有路易莎 Wi-Fi", StatusKind.Info, true);
                return;
            }
            if (!manual && IsSnoozed(scan)) return;

            // On another Wi-Fi: keep it if it has internet. Windows version only: if it has had
            // no internet for 30 s and Louisa is here, switch.
            if (w.Connected)
            {
                if (Net.Online())
                {
                    offlineSince = null;
                    if (manual) Report("目前已可上網，保留現有連線", StatusKind.Failed, true);
                    return;
                }
                if (!manual)
                {
                    if (offlineSince == null) { offlineSince = DateTime.Now; Log.Write(w.Ssid + " 無法上網"); return; }
                    if (DateTime.Now - offlineSince.Value < TimeSpan.FromSeconds(30)) return;
                }
            }

            // Windows keeps networks in its list for about a minute after they disappear (e.g. walking out
            // of the store). Confirm with a fresh scan before trying passwords, like the Mac version does.
            if (!manual)
            {
                scan = FreshScan();
                if (scan == null) { Log.Write("LouisaCoffee no longer nearby (stale network list)"); return; }
            }

            // 1. Routers we've joined before. 2. The 3 nearest stores within 5 km, best guess of each first.
            var tries = LearnedAttempts(scan, null);
            var near = Stores.Nearest(Locator.Current, 3, null).Where(n => n.Meters < 5000).Select(n => n.Store).ToList();
            for (int n = 0; n < 3; n++)
                foreach (var s in near)
                    if (n < s.passwords.Count) tries.Add(new Attempt(s.name, s.passwords[n]));

            if (tries.Count == 0)
            {
                Log.Write("no candidates: location known=" + (Locator.Current != null) + " allowed=" + Locator.Allowed);
                Snooze(scan);
                Report(Locator.Allowed ? "無法辨識門市，請從選單選擇" : "請開啟 Windows 定位，以自動尋找附近門市", StatusKind.Failed, true);
                return;
            }
            if (w.Connected)
            {
                Log.Write(w.Ssid + " 無法上網，切換到路易莎");
                Wlan.Disconnect();
                Thread.Sleep(1500);
            }
            offlineSince = null;
            var outcome = TryAll(scan, tries, e, false);
            if (outcome == Outcome.Failed)
            {
                Snooze(scan);
                Report("自動連線失敗，請從選單選擇門市", StatusKind.Failed, true);
            }
            else if (outcome == Outcome.UserTookOver || outcome == Outcome.Gone)
            {
                Snooze(scan);   // leave the user alone for a while
                Report("已停止自動連線", StatusKind.Info, false);
            }
        }

        // Snooze the whole store, not just its strongest router: stores often have several, and the
        // strongest one changes as you move, which would start the whole round of passwords again.
        void Snooze(ScanResult scan)
        {
            var until = DateTime.Now.AddMinutes(10);
            foreach (var ap in scan.Aps) snoozed[ap.Bssid] = until;
        }

        bool IsSnoozed(ScanResult scan)
        {
            DateTime until;
            return scan.Aps.Any(ap => snoozed.TryGetValue(ap.Bssid, out until) && until > DateTime.Now);
        }

        // ---------- Manual ----------

        void ConnectStore(Store store, string password, int e)
        {
            var w = Wlan.Current();
            if (w.NoAdapter) { Report("找不到 Wi-Fi 網卡", StatusKind.Failed, true); return; }
            if (w.RadioOff) { Report("請先開啟 Wi-Fi", StatusKind.Failed, true); return; }
            // Already on Louisa: leave it (Mac does the same). On another Wi-Fi: the user picked
            // a store on purpose, so go ahead.
            if (w.Connected && App.IsLouisa(w.Ssid)) { Report(ConnectedText(w), StatusKind.Connected, true); return; }

            Report("連線中：" + store.name + "…", StatusKind.Connecting, false);
            Wlan.ScanAndWait();
            bool blocked;
            var scan = Wlan.FindLouisa(out blocked);
            if (blocked) { Report(LocationBlockedText, StatusKind.Failed, true); return; }
            if (scan == null) { Report("附近找不到路易莎 Wi-Fi", StatusKind.Failed, true); return; }

            List<Attempt> tries;
            if (password != null) tries = new List<Attempt> { new Attempt(store.name, password) };
            else
            {
                tries = LearnedAttempts(scan, store.name);
                tries.AddRange(store.passwords.Select(p => new Attempt(store.name, p)));
            }
            var outcome = TryAll(scan, tries, e, true);
            if (outcome == Outcome.Failed)
                Report(password != null ? "密碼 " + password + " 無法連上" + store.name : "無法連上" + store.name + "，請確認門市與密碼",
                    StatusKind.Failed, true);
            else if (outcome == Outcome.Gone)
                Report("附近找不到路易莎 Wi-Fi", StatusKind.Failed, true);
            else if (outcome == Outcome.UserTookOver)
                Report("已停止連線", StatusKind.Info, false);
        }

        // ---------- Joining ----------

        // Each attempt can take 15-20 s, and every attempt pulls the computer onto LouisaCoffee.
        // So before each password after the first, make sure that's still what the user wants:
        // they haven't connected somewhere else themselves, and LouisaCoffee is really still here.
        Outcome TryAll(ScanResult scan, List<Attempt> tries, int e, bool manual)
        {
            var seen = new HashSet<string>();
            bool first = true;
            foreach (var t in tries)
            {
                if (!Current(e)) return Outcome.Interrupted;   // a user action came in; stop before the next password
                if (!seen.Add(t.Password)) continue;
                if (!first)
                {
                    var w = Wlan.Current();
                    if (w.Connected && !App.IsLouisa(w.Ssid))
                    {
                        // Connected by the user: always stop. Connected by Windows on its own: stop for
                        // auto-connect if that network has internet (manual picks keep going).
                        if (!w.AutoConnected || (!manual && Net.Online()))
                        {
                            Log.Write("使用者已連上 " + w.Ssid + "，停止嘗試");
                            return Outcome.UserTookOver;
                        }
                    }
                    var fresh = FreshScan();
                    if (fresh == null) { Log.Write("附近已沒有 LouisaCoffee，停止嘗試"); return Outcome.Gone; }
                    scan = fresh;
                }
                first = false;
                Report("連線中：" + t.Store + "…", StatusKind.Connecting, false);
                var r = Join(scan, t.Password);
                if (r == Outcome.Joined)
                {
                    Remember(scan, t.Store, t.Password);
                    Report("已連上：" + t.Store, StatusKind.Connected, true);
                    return Outcome.Joined;
                }
                if (r == Outcome.UserTookOver) return r;
            }
            return Current(e) ? Outcome.Failed : Outcome.Interrupted;
        }

        Outcome Join(ScanResult scan, string password)
        {
            var start = DateTime.Now;
            var before = Wlan.Current();
            string previous = before.Connected ? before.Ssid : null;   // a manual pick may start from another network
            bool left = previous == null;
            if (!Wlan.SetProfile(scan.Ssid, password, scan.Auth)) return Outcome.Failed;
            Wlan.Connect(scan.Ssid);
            quietUntil = DateTime.Now.AddSeconds(15);
            for (int i = 0; i < 30; i++)   // up to 15 s; a wrong password usually fails in 15-20 s
            {
                Thread.Sleep(500);
                var w = Wlan.Current();
                if (!w.Connected || w.Ssid != previous) left = true;
                if (w.Connected && App.IsLouisa(w.Ssid))
                {
                    Thread.Sleep(1000);
                    w = Wlan.Current();
                    if (w.Connected && App.IsLouisa(w.Ssid))
                    {
                        Log.Write("joined in " + (DateTime.Now - start).TotalSeconds.ToString("0.0") + "s");
                        quietUntil = DateTime.Now.AddSeconds(15);
                        return Outcome.Joined;
                    }
                }
                // The user picked another network in Windows while we were trying: let them have it.
                if (left && w.Connected && !App.IsLouisa(w.Ssid) && !w.AutoConnected)
                {
                    Log.Write("使用者已連上 " + w.Ssid + "，停止嘗試");
                    return Outcome.UserTookOver;
                }
            }
            // Cancel our attempt, but never disconnect a network the computer is already on.
            var now = Wlan.Current();
            if (!(now.Connected && !App.IsLouisa(now.Ssid))) Wlan.Disconnect();
            Log.Write("failed in " + (DateTime.Now - start).TotalSeconds.ToString("0.0") + "s");
            return Outcome.Failed;
        }

        // Fresh scan (about 4 s): Windows' cached list can still show a network that's gone.
        static ScanResult FreshScan()
        {
            Wlan.ScanAndWait();
            bool blocked;
            return Wlan.FindLouisa(out blocked);
        }

        // ---------- Housekeeping ----------

        void Maintenance()
        {
            // Windows rarely rescans while connected, so ask once a minute; the next check sees the results.
            if (DateTime.Now - lastScan > TimeSpan.FromSeconds(60)) { Wlan.Scan(); lastScan = DateTime.Now; }
            Stores.UpdateIfDue();
        }

        void Report(string text, StatusKind kind, bool notify)
        {
            if (text != lastText || notify) Log.Write(text);
            lastText = text;
            var h = StatusChanged;
            if (h != null) h(text, kind, notify);
        }
    }
}
