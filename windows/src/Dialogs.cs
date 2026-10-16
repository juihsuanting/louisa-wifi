using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LouisaWiFi
{
    static class Ui
    {
        public static readonly Font Font = new Font("Microsoft JhengHei UI", 9f);
        public static readonly Font Bold = new Font("Microsoft JhengHei UI", 9f, FontStyle.Bold);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // Grey hint text in an empty text box (EM_SETCUEBANNER), kept while focused.
        public static void SetCue(TextBox box, string text) { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); }

        public static Form NewDialog(string title, int width, int height)
        {
            return new Form
            {
                Text = title,
                Font = Font,
                AutoScaleMode = AutoScaleMode.Dpi,
                ClientSize = new Size(width, height),
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                Icon = TrayIcons.AppIcon,
            };
        }
    }

    // 「輸入密碼…」: for when the password posted in the store isn't one of the candidates.
    static class PasswordDialog
    {
        public static string Ask(Store store)
        {
            using (var f = Ui.NewDialog(App.Name, 400, 140))
            {
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                var title = new Label { Text = "輸入" + store.name + "的 Wi-Fi 密碼", Font = Ui.Bold, Bounds = new Rectangle(12, 12, 376, 20) };
                var info = new Label { Text = "照店內張貼的密碼輸入。連上之後會記住，下次自動使用。", Bounds = new Rectangle(12, 36, 376, 20) };
                var box = new TextBox { Bounds = new Rectangle(12, 62, 376, 24), MaxLength = 63 };
                var ok = new Button { Text = "連線", Bounds = new Rectangle(232, 100, 75, 28) };
                var cancel = new Button { Text = "取消", Bounds = new Rectangle(313, 100, 75, 28), DialogResult = DialogResult.Cancel };
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                ok.Click += (s, e) =>
                {
                    string t = box.Text.Trim();
                    if (t.Length == 0) return;
                    if (t.Length < 8) { MessageBox.Show(f, "Wi-Fi 密碼至少要 8 個字元。", App.Name); return; }
                    f.DialogResult = DialogResult.OK;
                };
                f.Controls.AddRange(new Control[] { title, info, box, ok, cancel });
                f.Shown += (s, e) =>
                {
                    if (store.passwords.Count > 0) Ui.SetCue(box, store.passwords[0]);   // most likely password, like the Mac placeholder
                    box.Focus();
                };
                return f.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }

    // 「搜尋門市…」 (Windows only): find a store by name, address or phone number.
    static class SearchDialog
    {
        public class Choice
        {
            public Store Store;
            public bool EnterPassword;
        }

        class Row
        {
            public Store Store;
            public double Meters;
            public string Key;
        }

        static string Key(string s) { return (s ?? "").ToLowerInvariant().Replace("臺", "台").Replace(" ", "").Replace("-", ""); }

        public static Choice Show()
        {
            using (var f = Ui.NewDialog("搜尋門市", 560, 440))
            {
                f.MinimumSize = new Size(420, 300);
                var box = new TextBox { Bounds = new Rectangle(12, 12, 536, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                var list = new ListView
                {
                    View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true,
                    Bounds = new Rectangle(12, 44, 536, 344),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                };
                list.Columns.Add("門市", 170);
                list.Columns.Add("距離", 70);
                list.Columns.Add("地址", 270);
                var count = new Label { Bounds = new Rectangle(12, 402, 200, 20), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, ForeColor = SystemColors.GrayText };
                var go = new Button { Text = "連線", Bounds = new Rectangle(296, 398, 80, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
                var ask = new Button { Text = "輸入密碼…", Bounds = new Rectangle(382, 398, 90, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
                var cancel = new Button { Text = "取消", Bounds = new Rectangle(478, 398, 70, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
                f.CancelButton = cancel;
                f.Controls.AddRange(new Control[] { box, list, count, go, ask, cancel });

                // Nearest first when nothing is typed; by city without a location.
                var here = Locator.Current;
                var rows = Stores.All.Select(s => new Row
                {
                    Store = s,
                    Meters = here != null && s.HasLocation ? Stores.Distance(here.Lat, here.Lng, s.lat, s.lng) : double.MaxValue,
                    Key = Key(s.name + s.address + s.phone),
                }).OrderBy(r => r.Meters).ThenBy(r => Stores.CityRank(r.Store.City)).ThenBy(r => r.Store.name).ToList();

                Action fill = () =>
                {
                    // Every space-separated word must match, e.g. 「大安 復興」.
                    var terms = box.Text.Replace("臺", "台").Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries).Select(Key).ToList();
                    var hits = rows.Where(r => terms.All(t => r.Key.Contains(t))).ToList();
                    list.BeginUpdate();
                    list.Items.Clear();
                    list.Items.AddRange(hits.Select(r => new ListViewItem(new[] {
                        r.Store.name, r.Meters < double.MaxValue ? Fmt.Distance(r.Meters) : "", r.Store.address })
                        { Tag = r.Store, ToolTipText = r.Store.phone }).ToArray());
                    list.EndUpdate();
                    if (list.Items.Count > 0) { list.Items[0].Selected = true; list.Items[0].Focused = true; }
                    count.Text = list.Items.Count + " 家門市";
                };

                // Filter 0.15 s after typing stops instead of on every keystroke.
                var debounce = new Timer { Interval = 150 };
                debounce.Tick += (s, e) => { debounce.Stop(); fill(); };
                box.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };

                Choice result = null;
                Action<bool> accept = enterPassword =>
                {
                    if (list.SelectedItems.Count == 0) return;
                    result = new Choice { Store = (Store)list.SelectedItems[0].Tag, EnterPassword = enterPassword };
                    f.DialogResult = DialogResult.OK;
                };
                go.Click += (s, e) => accept(false);
                ask.Click += (s, e) => accept(true);
                list.DoubleClick += (s, e) => accept(false);
                list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; accept(false); } };
                box.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; debounce.Stop(); fill(); accept(false); }
                    else if (e.KeyCode == Keys.Down && list.Items.Count > 0) { e.Handled = true; list.Focus(); }
                };
                f.Shown += (s, e) => { Ui.SetCue(box, "輸入店名、地址或電話"); box.Focus(); };

                fill();
                f.ShowDialog();
                debounce.Dispose();
                return result;
            }
        }
    }
}
