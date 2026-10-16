using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LouisaWiFi
{
    static class Program
    {
        // LouisaWiFi.exe            downloaded copy: offer to install (or update), then start the installed copy
        // LouisaWiFi.exe            installed copy: run in the tray (a second launch brings the hidden icon back)
        // LouisaWiFi.exe --portable run in the tray from wherever it is, without installing
        // LouisaWiFi.exe --uninstall
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // GitHub only accepts TLS 1.2+; make sure it's on even on older .NET defaults.
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            Application.ThreadException += (s, e) => Log.Write("ui error: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);

            var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (flags.Contains("--uninstall")) { Installer.Uninstall(); return; }
                if (!Installer.IsInstalledCopy && !flags.Contains("--portable")) { Installer.Install(); return; }

                bool created;
                using (var mutex = new Mutex(true, App.MutexName, out created))
                using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, App.ShowEventName))
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, App.QuitEventName))
                {
                    if (!created) { show.Set(); return; }   // already running: show its icon instead
                    TrayIcons.Load();
                    Application.Run(new TrayApp(show, quit));
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception e)
            {
                Log.Write("fatal: " + e);
                MessageBox.Show("發生問題：\n\n" + e.Message, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // Per-user install, no admin rights needed: %LOCALAPPDATA%\Programs\LouisaWiFi,
    // a Start menu shortcut, start at sign-in, and an entry in Settings → Apps to remove it.
    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LouisaWiFi";
        const string RunValue = "LouisaWiFi";

        static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Louisa Wi-Fi.lnk"); }
        }

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(Paths.CurrentExe), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        public static bool StartupEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
        }

        public static void SetStartup(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(RunValue, "\"" + Paths.CurrentExe + "\"");
                else key.DeleteValue(RunValue, false);
            }
        }

        public static void Install()
        {
            bool updating = File.Exists(Paths.InstalledExe);
            string question = updating
                ? "要把 Louisa Wi-Fi 更新到 " + App.Version + " 嗎？\n\n設定和連線紀錄都會保留。"
                : "Louisa Wi-Fi 會安裝到這台電腦，並在開機時自動在背景執行。\n\n之後走進任何一家路易莎咖啡，就會自動連上 Wi-Fi。\n\n要安裝嗎？";
            if (MessageBox.Show(question, "Louisa Wi-Fi", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            try
            {
                StopLegacy();
                StopRunning();
                Directory.CreateDirectory(Paths.InstallDir);
                CopyWithRetry(Paths.CurrentExe, Paths.InstalledExe);
                DeleteFile(Paths.InstalledExe + ":Zone.Identifier");   // "downloaded from the internet" mark, so it starts without a warning

                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                    key.SetValue(RunValue, "\"" + Paths.InstalledExe + "\"");
                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", "Louisa Wi-Fi");
                    key.SetValue("DisplayVersion", App.Version);
                    key.SetValue("DisplayIcon", Paths.InstalledExe + ",0");
                    key.SetValue("Publisher", "louisa-wifi");
                    key.SetValue("InstallLocation", Paths.InstallDir);
                    key.SetValue("UninstallString", "\"" + Paths.InstalledExe + "\" --uninstall");
                    key.SetValue("URLInfoAbout", App.ProjectUrl);
                    key.SetValue("EstimatedSize", (int)(new FileInfo(Paths.InstalledExe).Length / 1024), RegistryValueKind.DWord);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                CreateShortcut(StartMenuLink, Paths.InstalledExe, "自動連上路易莎咖啡的 Wi-Fi");
                Log.Write((updating ? "updated to " : "installed ") + App.Version);
                Process.Start(Paths.InstalledExe);
            }
            catch (Exception e)
            {
                Log.Write("install error: " + e);
                MessageBox.Show("安裝時發生問題：\n\n" + e.Message, "Louisa Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void Uninstall()
        {
            if (MessageBox.Show("要移除 Louisa Wi-Fi 嗎？", "Louisa Wi-Fi", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            StopRunning();
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (key != null) key.DeleteValue(RunValue, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            try { File.Delete(StartMenuLink); } catch { }

            if (Directory.Exists(Paths.DataDir) &&
                MessageBox.Show("也要刪除連線紀錄和統計嗎？\n\n（記住的門市密碼、每天的連線時間）\n\nWindows 記住的路易莎 Wi-Fi 會保留。",
                    "Louisa Wi-Fi", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try { Directory.Delete(Paths.DataDir, true); } catch { }
            }

            // The exe can't delete itself while running; let cmd remove the folder after we exit.
            if (Directory.Exists(Paths.InstallDir))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"")
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                });
            }
            MessageBox.Show("Louisa Wi-Fi 已移除。", "Louisa Wi-Fi");
        }

        // Ask a running copy to quit and wait until it has.
        static void StopRunning()
        {
            try { using (var quit = EventWaitHandle.OpenExisting(App.QuitEventName)) quit.Set(); }
            catch { return; }   // nothing running
            for (int i = 0; i < 50; i++)
            {
                try { using (Mutex.OpenExisting(App.MutexName)) { } }
                catch (WaitHandleCannotBeOpenedException) { return; }
                Thread.Sleep(100);
            }
        }

        // The earlier PowerShell version: stop it and remove its startup shortcut.
        static void StopLegacy()
        {
            try
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LouisaWiFi.lnk");
                if (File.Exists(lnk)) { File.Delete(lnk); Log.Write("removed PowerShell version startup shortcut"); }
                using (var search = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe'"))
                {
                    foreach (ManagementObject p in search.Get())
                    {
                        string cmd = p["CommandLine"] as string;
                        if (cmd == null || cmd.IndexOf("LouisaWiFi.ps1", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try { Process.GetProcessById(Convert.ToInt32(p["ProcessId"])).Kill(); Log.Write("stopped PowerShell version"); } catch { }
                    }
                }
            }
            catch (Exception e) { Log.Write("legacy cleanup: " + e.Message); }
        }

        static void CopyWithRetry(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (i >= 20) throw; Thread.Sleep(250); }   // the old copy may still be exiting
            }
        }

        static void CreateShortcut(string path, string target, string description)
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            try
            {
                object lnk = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFile(string path);
    }
}
