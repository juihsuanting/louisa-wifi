using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using FormsTimer = System.Windows.Forms.Timer;

namespace LouisaWiFi
{
    // Tray icons: full colour when connected, grey with a blue dot while connecting, grey otherwise
    // (the Mac version swaps the menu bar symbol the same way).
    static class TrayIcons
    {
        public static Icon AppIcon, Connected, Connecting, Idle;

        public static void Load()
        {
            try { AppIcon = Icon.ExtractAssociatedIcon(Paths.CurrentExe); } catch { AppIcon = SystemIcons.Application; }
            Bitmap src;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LouisaWiFi.icon.png"))
                src = new Bitmap(s);
            var color = new Bitmap(src, 32, 32);
            var rect = new Rectangle(0, 0, 32, 32);

            var gray = new Bitmap(32, 32);
            using (var attrs = new ImageAttributes())
            using (var g = Graphics.FromImage(gray))
            {
                attrs.SetColorMatrix(new ColorMatrix(new[] {
                    new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                    new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                    new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                    new[] { 0f, 0, 0, 0.8f, 0 },
                    new[] { 0f, 0, 0, 0, 1 } }));
                g.DrawImage(color, rect, 0, 0, 32, 32, GraphicsUnit.Pixel, attrs);
            }

            var busy = new Bitmap(gray);
            using (var g = Graphics.FromImage(busy))
            using (var blue = new SolidBrush(Color.FromArgb(42, 120, 214)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(Brushes.White, 17, 17, 15, 15);
                g.FillEllipse(blue, 19, 19, 11, 11);
            }

            Connected = Icon.FromHandle(color.GetHicon());
            Connecting = Icon.FromHandle(busy.GetHicon());
            Idle = Icon.FromHandle(gray.GetHicon());
        }
    }

    // Make the native menu follow Windows' light/dark app setting. Windows has no public API for dark
    // Win32 menus; apps like File Explorer use these uxtheme.dll exports (Windows 10 1903 and later).
    static class MenuTheme
    {
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
        static bool? applied;

        public static void Apply()
        {
            if (Environment.OSVersion.Version.Build < 18362) return;
            bool dark = false;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                dark = v is int && (int)v == 0;
            }
            if (applied == dark) return;
            applied = dark;
            try { SetPreferredAppMode(dark ? 2 /* force dark */ : 3 /* force light */); FlushMenuThemes(); }
            catch (Exception e) { Log.Write("menu theme: " + e.Message); }
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        // ContextMenu (not ContextMenuStrip) is a real Win32 menu, so Windows draws it: Windows 11 style,
        // dark mode, and mouse-wheel scrolling of long lists, the way the Mac version uses the system menu.
        readonly ContextMenu menu = new ContextMenu();
        readonly Control invoker = new Control();   // marshals worker-thread events onto the UI thread
        readonly Joiner joiner = new Joiner();
        readonly Tracker tracker;
        readonly EventWaitHandle showEvent, quitEvent;
        readonly FormsTimer slowTimer = new FormsTimer { Interval = 15000 };
        readonly FormsTimer fastTimer = new FormsTimer { Interval = 1000 };
        readonly FormsTimer usageTimer = new FormsTimer { Interval = 60000 };

        string status = "啟動中…";
        StatusKind kind = StatusKind.Info;
        string failText;          // a failure stays on the menu until we're actually connected (like the Mac version)
        int wifiChanged;
        bool kick = true;         // check right away at launch
        DateTime lastAuto = DateTime.MinValue;
        DateTime? hideAt;         // the icon was shown only to carry a notification

        Config Config { get { return Config.Current; } }

        public TrayApp(EventWaitHandle showEvent, EventWaitHandle quitEvent)
        {
            this.showEvent = showEvent;
            this.quitEvent = quitEvent;
            invoker.CreateControl();
            var unused = invoker.Handle;
            tracker = new Tracker(joiner);

            Stores.Load();
            Log.Write("started " + App.Version + ", " + Stores.All.Count + " stores");
            Locator.Start();
            Wlan.Changed += () => Interlocked.Exchange(ref wifiChanged, 1);
            Wlan.StartNotifications();
            Wlan.MakeProfilesManual();
            joiner.StatusChanged += (text, k, notify) => invoker.BeginInvoke(new Action(() => OnStatus(text, k, notify)));

            tray.Text = App.Name;
            tray.ContextMenu = menu;
            menu.MenuItems.Add(Disabled(status));
            menu.Popup += (s, e) =>
            {
                try { BuildMenu(); } catch (Exception ex) { Log.Write("menu error: " + ex); }
            };
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (show != null) show.Invoke(tray, null);
            };
            RefreshStatus();
            tray.Visible = !Config.hideIcon;

            slowTimer.Tick += (s, e) => joiner.AutoConnect();
            fastTimer.Tick += (s, e) => FastTick();
            usageTimer.Tick += (s, e) => { try { tracker.Tick(); } catch (Exception ex) { Log.Write("usage error: " + ex.Message); } };
            slowTimer.Start();
            fastTimer.Start();
            usageTimer.Start();

            FirstRun();
        }

        // ---------- Status ----------

        void OnStatus(string text, StatusKind k, bool notify)
        {
            if (k == StatusKind.Failed) failText = text;
            else if (k == StatusKind.Connecting || k == StatusKind.Connected) failText = null;
            SetStatus(text, k);
            if (notify) Notify(text);
        }

        void RefreshStatus()
        {
            if (kind == StatusKind.Connecting && joiner.Busy) return;   // the worker is reporting progress
            var w = Wlan.Current();
            if (w.Connected && App.IsLouisa(w.Ssid)) { failText = null; SetStatus(joiner.ConnectedText(w), StatusKind.Connected); }
            else if (failText != null) SetStatus(failText, StatusKind.Failed);
            else if (w.NoAdapter) SetStatus("找不到 Wi-Fi 網卡", StatusKind.Info);
            else if (w.RadioOff) SetStatus("Wi-Fi 已關閉", StatusKind.Info);
            else if (w.Connected) SetStatus("目前連線：" + w.Ssid, StatusKind.Info);
            else SetStatus("尚未連線", StatusKind.Info);
        }

        void SetStatus(string text, StatusKind k)
        {
            status = text;
            kind = k;
            string tip = App.Name + "：" + text;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            tray.Icon = k == StatusKind.Connected ? TrayIcons.Connected : k == StatusKind.Connecting ? TrayIcons.Connecting : TrayIcons.Idle;
        }

        // Works with the icon hidden too: show it briefly to carry the notification.
        void Notify(string text)
        {
            if (!tray.Visible) { tray.Visible = true; hideAt = DateTime.Now.AddSeconds(8); }
            tray.ShowBalloonTip(4000, App.Name, text, ToolTipIcon.None);
        }

        // ---------- Timers ----------

        void FastTick()
        {
            try
            {
                if (quitEvent.WaitOne(0)) { Quit(); return; }   // the installer is replacing us
                if (showEvent.WaitOne(0))                       // opened again while running: bring the icon back
                {
                    Config.hideIcon = false;
                    Config.Save();
                    hideAt = null;
                    tray.Visible = true;
                    Notify(App.Name + " 正在背景執行");
                }
                if (hideAt != null && DateTime.Now > hideAt.Value)
                {
                    hideAt = null;
                    if (Config.hideIcon) tray.Visible = false;
                }
                if (Locator.Poll()) kick = true;   // moved more than 100 m
                // React to Wi-Fi changes and moves right away, at most every 3 seconds.
                bool changed = Thread.VolatileRead(ref wifiChanged) == 1;
                if ((kick || changed) && DateTime.Now - lastAuto > TimeSpan.FromSeconds(3))
                {
                    kick = false;
                    Interlocked.Exchange(ref wifiChanged, 0);
                    lastAuto = DateTime.Now;
                    joiner.AutoConnect();
                }
            }
            catch (Exception e) { Log.Write("tick error: " + e.Message); }
        }

        void FirstRun()
        {
            if (Config.welcomed) return;
            Config.welcomed = true;
            Config.Save();
            if (tray.Visible)
                tray.ShowBalloonTip(6000, App.Name + " 已在背景執行",
                    "到了路易莎會自動連上 Wi-Fi。點右下角的路易莎圖示可以手動選門市（可能收在 ^ 裡面）。", ToolTipIcon.None);
            if (!Locator.Allowed && !Config.locationAsked)
            {
                Config.locationAsked = true;
                Config.Save();
                var answer = MessageBox.Show(
                    "建議開啟 Windows 的「定位」，" + App.Name + " 才能自動找出你在哪一家門市。\n\n" +
                    "請在接下來的設定頁面打開：\n・定位服務\n・讓桌面應用程式存取您的位置\n\n" +
                    "不開也可以用，只是第一次到某家店時需要從選單手動選門市。\n\n現在開啟設定嗎？",
                    App.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) OpenLocationSettings();
            }
        }

        // ---------- Menu (rebuilt each time it opens, like the Mac menuNeedsUpdate) ----------

        // How many items fit in one menu column on the screen the menu opens on. Measured on Windows 11:
        // items are about 35 px when the menu bar height (SM_CYMENU) is 30 px, at any scaling.
        // 1920×1200 at 150% gives 30; a 1080p screen at 100% gives about 42.
        static int MenuCapacity()
        {
            int screen = Screen.FromPoint(Cursor.Position).WorkingArea.Height;
            double item = SystemInformation.MenuHeight * 35.0 / 30;
            int fit = (int)((screen - SystemInformation.MenuHeight) / item) - 1;   // menu padding + one spare row
            return Math.Max(15, fit);
        }

        void BuildMenu()
        {
            int capacity = MenuCapacity();
            MenuTheme.Apply();
            RefreshStatus();
            var items = new List<MenuItem> { Disabled(status) };
            var pw = PasswordMenu();
            if (pw != null) items.Add(pw);
            items.Add(Sep());

            bool onlyKnown = Config.onlyKnown;
            var known = joiner.KnownStores();
            var list = onlyKnown ? Stores.All.Where(s => known.Contains(s.name)).ToList() : Stores.All;
            var here = Locator.Current;

            if (onlyKnown && list.Count == 0) items.Add(Disabled("還沒有連過任何門市"));
            else if (here != null)
            {
                items.Add(Disabled(onlyKnown ? "連過的門市" : "附近門市"));
                foreach (var n in Stores.Nearest(here, 6, list))
                {
                    var s = n.Store;
                    // A tab puts the distance in the right-hand column, the way Windows menus show shortcuts.
                    items.Add(Item(s.name, () => joiner.Connect(s, null), Fmt.Distance(n.Meters)));
                }
            }
            else items.Add(Item("連線到最近的門市", joiner.ConnectNearest));

            var all = new MenuItem(onlyKnown ? "所有連過的門市" : "所有門市") { Enabled = list.Count > 0 };
            foreach (var g in list.GroupBy(s => s.City).OrderBy(g => Stores.CityRank(g.Key)).ThenBy(g => g.Key))
            {
                var stores = g.OrderBy(s => s.name).ToList();
                var city = new MenuItem(Esc(g.Key + "（" + stores.Count + "）"));
                // Windows menus can't scroll with the mouse wheel, so a city too long for the screen gets
                // a district level instead (台北市 → 大安區 → stores); every list then fits.
                if (stores.Count > capacity)
                    Lazy(city, () => stores.GroupBy(s => s.District).OrderBy(d => d.Key).Select(d =>
                    {
                        var inDistrict = d.ToList();
                        var district = new MenuItem(Esc(d.Key + "（" + inDistrict.Count + "）"));
                        Lazy(district, () => inDistrict.Select(StoreMenu).ToArray());
                        return district;
                    }).ToArray());
                else
                    Lazy(city, () => stores.Select(StoreMenu).ToArray());
                all.MenuItems.Add(city);
            }
            items.Add(all);
            items.Add(Item("搜尋門市…", Search));
            items.Add(Sep());
            items.Add(Item("連線統計…", () => { try { tracker.ShowStats(); } catch (Exception ex) { Log.Write("stats error: " + ex.Message); } }));
            items.Add(Sep());

            if (!Locator.Allowed) items.Add(Item("開啟定位權限…", OpenLocationSettings));
            items.Add(Check("自動連線", Config.autoConnect, () =>
            {
                Config.autoConnect = !Config.autoConnect;
                Config.Save();
                if (Config.autoConnect) { joiner.ClearSnoozes(); joiner.AutoConnect(); }
            }));
            items.Add(Check("只顯示連過的門市", onlyKnown, () => { Config.onlyKnown = !Config.onlyKnown; Config.Save(); }));
            items.Add(Item("不在系統匣顯示…", HideIcon));
            items.Add(Check("開機時自動執行", Installer.StartupEnabled, () => Installer.SetStartup(!Installer.StartupEnabled)));
            items.Add(Item("開啟資料夾（紀錄與設定）", () => Process.Start("explorer.exe", Paths.DataDir)));
            items.Add(Sep());
            items.Add(Disabled("版本 " + App.Version));
            items.Add(Item("結束", Quit));

            // Free last time's items (with their submenus), or every open leaks a batch.
            var old = menu.MenuItems.Cast<MenuItem>().ToList();
            menu.MenuItems.Clear();
            foreach (var o in old) o.Dispose();
            menu.MenuItems.AddRange(items.ToArray());
        }

        // 「用密碼連線」: the nearest store's candidates, for when the password is posted on the wall.
        MenuItem PasswordMenu()
        {
            var n = Stores.Nearest(Locator.Current, 1, null).FirstOrDefault();
            if (n == null) return null;
            var item = new MenuItem("用密碼連線");
            item.MenuItems.Add(Disabled(n.Store.name));
            item.MenuItems.AddRange(PasswordItems(n.Store, "　"));
            return item;
        }

        // 所有門市 → city → store: 自動嘗試, each candidate, 輸入密碼…
        MenuItem StoreMenu(Store s)
        {
            var item = new MenuItem(Esc(s.name));
            Lazy(item, () => new[] { Item("自動嘗試", () => joiner.Connect(s, null)), Sep() }.Concat(PasswordItems(s, "")).ToArray());
            return item;
        }

        MenuItem[] PasswordItems(Store s, string indent)
        {
            var items = s.passwords.Select(p => Item(indent + p, () => joiner.Connect(s, p))).ToList();
            items.Add(Item(indent + "輸入密碼…", () => AskPassword(s)));
            return items.ToArray();
        }

        // Submenus are filled when they open: 500+ stores × 5 items would make every open slow.
        static void Lazy(MenuItem item, Func<MenuItem[]> fill)
        {
            item.MenuItems.Add(Disabled("…"));
            bool filled = false;
            item.Popup += (s, e) =>
            {
                if (filled) return;
                filled = true;
                var placeholder = item.MenuItems[0];
                item.MenuItems.Clear();
                placeholder.Dispose();
                item.MenuItems.AddRange(fill());
            };
        }

        // "&" marks a keyboard shortcut in Windows menus; double it to show it as text.
        static string Esc(string text) { return text.Replace("&", "&&"); }

        static MenuItem Item(string text, Action onClick, string right = null)
        {
            var it = new MenuItem(Esc(text) + (right == null ? "" : "\t" + right));
            it.Click += (s, e) => onClick();
            return it;
        }

        static MenuItem Check(string text, bool on, Action onClick)
        {
            var it = Item(text, onClick);
            it.Checked = on;
            return it;
        }

        static MenuItem Disabled(string text) { return new MenuItem(Esc(text)) { Enabled = false }; }

        static MenuItem Sep() { return new MenuItem("-"); }

        // ---------- Actions ----------

        void AskPassword(Store s)
        {
            string pw = PasswordDialog.Ask(s);
            if (pw != null) joiner.Connect(s, pw);
        }

        void Search()
        {
            var choice = SearchDialog.Show();
            if (choice == null) return;
            if (choice.EnterPassword) AskPassword(choice.Store);
            else joiner.Connect(choice.Store, null);
        }

        void HideIcon()
        {
            var answer = MessageBox.Show(
                App.Name + " 會繼續在背景自動連線、記錄時間。\n\n想再顯示時，從「開始」功能表再打開一次 Louisa Wi-Fi 就會出現。",
                "不在系統匣顯示？", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            Config.hideIcon = true;
            Config.Save();
            tray.Visible = false;
        }

        static void OpenLocationSettings()
        {
            try { Process.Start("ms-settings:privacy-location"); } catch { }
        }

        void Quit()
        {
            slowTimer.Stop();
            fastTimer.Stop();
            usageTimer.Stop();
            tray.Visible = false;
            Locator.Stop();
            ExitThread();
        }
    }
}
