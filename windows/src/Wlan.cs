using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LouisaWiFi
{
    class WifiStatus
    {
        public bool NoAdapter;
        public bool RadioOff;
        public bool Connected;
        public string Ssid;     // null when not connected
        public string Bssid;    // "aa:bb:cc:dd:ee:ff"
        public bool AutoConnected;   // Windows joined it on its own (wlan_connection_mode_auto), not the user or us
    }

    class AccessPoint
    {
        public string Bssid;
        public int Rssi;        // dBm
    }

    class ScanResult
    {
        public string Ssid;                                        // this store's network name, as broadcast
        public List<AccessPoint> Aps = new List<AccessPoint>();   // strongest first
        public string Auth = "WPA2PSK";                           // WLAN profile authentication value
        public AccessPoint Best { get { return Aps[0]; } }
    }

    // Windows Native Wifi API (wlanapi.dll). Talking to it directly instead of parsing `netsh`
    // output means it works on any Windows display language, and each call takes milliseconds.
    static class Wlan
    {
        const int ERROR_ACCESS_DENIED = 5;
        const int WLAN_INTERFACE_INFO_SIZE = 532;      // GUID + WCHAR[256] + state
        const int WLAN_BSS_ENTRY_SIZE = 360;
        const int WLAN_AVAILABLE_NETWORK_SIZE = 628;

        static readonly object gate = new object();
        static IntPtr handle = IntPtr.Zero;
        static NotifyCallback callback;                // kept alive for the lifetime of the app
        static readonly AutoResetEvent scanDone = new AutoResetEvent(false);

        // Raised (on a system thread) when Wi-Fi changes: scan done, connected, failed,
        // network appeared, disconnected. Used to react right away instead of waiting.
        public static event Action Changed;

        static IntPtr Handle
        {
            get
            {
                if (handle == IntPtr.Zero)
                {
                    uint negotiated;
                    IntPtr h;
                    if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out h) == 0) handle = h;
                }
                return handle;
            }
        }

        public static void StartNotifications()
        {
            lock (gate)
            {
                if (Handle == IntPtr.Zero) { Log.Write("wifi: no WLAN service"); return; }
                callback = OnNotify;
                int prev;
                int rc = WlanRegisterNotification(Handle, 0x8 /* ACM */, true, callback, IntPtr.Zero, IntPtr.Zero, out prev);
                Log.Write("wifi events: " + (rc == 0));
            }
        }

        static void OnNotify(IntPtr data, IntPtr context)
        {
            int code = Marshal.ReadInt32(data, 4);
            if (code == 7 || code == 8) scanDone.Set();   // scan_complete / scan_fail
            // 7 scan_complete, 10 connection_complete, 11 connection_attempt_fail, 19 network_available, 21 disconnected
            if (code == 7 || code == 10 || code == 11 || code == 19 || code == 21)
            {
                var h = Changed;
                if (h != null) h();
            }
        }

        // First Wi-Fi adapter, or null when there is none.
        static Guid? Interface()
        {
            IntPtr list;
            if (Handle == IntPtr.Zero || WlanEnumInterfaces(Handle, IntPtr.Zero, out list) != 0) return null;
            try
            {
                if (Marshal.ReadInt32(list) == 0) return null;
                return (Guid)Marshal.PtrToStructure(new IntPtr(list.ToInt64() + 8), typeof(Guid));
            }
            finally { WlanFreeMemory(list); }
        }

        public static WifiStatus Current()
        {
            lock (gate)
            {
                var status = new WifiStatus();
                Guid? g = Interface();
                if (g == null) { status.NoAdapter = true; return status; }
                Guid id = g.Value;

                // Radio switched off (airplane mode, Wi-Fi toggle)?
                int size, type;
                IntPtr data;
                if (WlanQueryInterface(Handle, ref id, 4 /* radio_state */, IntPtr.Zero, out size, out data, out type) == 0)
                {
                    try
                    {
                        if (Marshal.ReadInt32(data) > 0)
                        {
                            int software = Marshal.ReadInt32(data, 8), hardware = Marshal.ReadInt32(data, 12);
                            status.RadioOff = software == 2 || hardware == 2;   // dot11_radio_state_off
                        }
                    }
                    finally { WlanFreeMemory(data); }
                }

                // Current connection. Fails with ERROR_INVALID_STATE when not connected.
                if (WlanQueryInterface(Handle, ref id, 7 /* current_connection */, IntPtr.Zero, out size, out data, out type) == 0)
                {
                    try
                    {
                        int state = Marshal.ReadInt32(data, 0);
                        status.Connected = state == 1;   // wlan_interface_state_connected; not "associating" or "authenticating"
                        if (status.Connected)
                        {
                            status.AutoConnected = Marshal.ReadInt32(data, 4) == 4;   // wlanConnectionMode
                            status.Ssid = ReadSsid(data, 520);
                            status.Bssid = ReadBssid(data, 560);
                        }
                    }
                    finally { WlanFreeMemory(data); }
                }
                return status;
            }
        }

        // Ask the adapter for a fresh scan. Results arrive a few seconds later.
        public static void Scan()
        {
            lock (gate)
            {
                Guid? g = Interface();
                if (g == null) return;
                Guid id = g.Value;
                WlanScan(Handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
        }

        // Scan and wait for the results (worker thread only).
        public static void ScanAndWait()
        {
            scanDone.Reset();
            Scan();
            scanDone.WaitOne(4000);
        }

        // Nearby Louisa access points from the last scan, or null when there are none.
        // locationBlocked: Windows 11 24H2+ refuses to list networks to apps without location access.
        public static ScanResult FindLouisa(out bool locationBlocked) { return Find(App.IsLouisa, out locationBlocked); }

        public static ScanResult Find(Func<string, bool> match, out bool locationBlocked)
        {
            locationBlocked = false;
            lock (gate)
            {
                Guid? g = Interface();
                if (g == null) return null;
                Guid id = g.Value;

                IntPtr list;
                int rc = WlanGetNetworkBssList(Handle, ref id, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out list);
                if (rc == ERROR_ACCESS_DENIED) { locationBlocked = true; return null; }
                if (rc != 0) return null;

                var found = new List<KeyValuePair<string, AccessPoint>>();
                try
                {
                    int count = Marshal.ReadInt32(list, 4);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr entry = new IntPtr(list.ToInt64() + 8 + i * WLAN_BSS_ENTRY_SIZE);
                        string ssid = ReadSsid(entry, 0);
                        if (!match(ssid)) continue;
                        found.Add(new KeyValuePair<string, AccessPoint>(ssid,
                            new AccessPoint { Bssid = ReadBssid(entry, 40), Rssi = Marshal.ReadInt32(entry, 56) }));
                    }
                }
                finally { WlanFreeMemory(list); }
                if (found.Count == 0) return null;
                found.Sort((a, b) => b.Value.Rssi.CompareTo(a.Value.Rssi));
                // Use the strongest network's exact name (and only its routers) for the profile.
                var result = new ScanResult { Ssid = found[0].Key };
                foreach (var f in found) if (f.Key == result.Ssid) result.Aps.Add(f.Value);
                result.Auth = Auth(id, result.Ssid);
                return result;
            }
        }

        // Security type of the network, for the WLAN profile.
        static string Auth(Guid id, string ssid)
        {
            IntPtr list;
            if (WlanGetAvailableNetworkList(Handle, ref id, 0, IntPtr.Zero, out list) != 0) return "WPA2PSK";
            try
            {
                int count = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = new IntPtr(list.ToInt64() + 8 + i * WLAN_AVAILABLE_NETWORK_SIZE);
                    if (ReadSsid(entry, 512) != ssid) continue;
                    switch (Marshal.ReadInt32(entry, 612))   // dot11DefaultAuthAlgorithm
                    {
                        case 1: return "open";
                        case 4: return "WPAPSK";
                        case 9: return "WPA3SAE";
                        default: return "WPA2PSK";
                    }
                }
            }
            finally { WlanFreeMemory(list); }
            return "WPA2PSK";
        }

        // Save the profile for this store's network with this password. All-user first, because a
        // network joined earlier from Windows Settings is all-user and blocks a per-user one.
        public static bool SetProfile(string ssid, string password, string auth)
        {
            string key = auth == "open" ? "" :
                "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" +
                System.Security.SecurityElement.Escape(password) + "</keyMaterial></sharedKey>";
            string name = System.Security.SecurityElement.Escape(ssid);
            string xml =
                "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>" + name + "</name><SSIDConfig><SSID><name>" + name + "</name></SSID></SSIDConfig>" +
                // manual: Windows never joins LouisaCoffee on its own (with the last store's password, or from
                // a stale network list after you've left). This app decides when; roaming inside a store still works.
                "<connectionType>ESS</connectionType><connectionMode>manual</connectionMode><MSM><security>" +
                "<authEncryption><authentication>" + auth + "</authentication><encryption>" + (auth == "open" ? "none" : "AES") +
                "</encryption><useOneX>false</useOneX></authEncryption>" + key + "</security></MSM></WLANProfile>";
            lock (gate)
            {
                Guid? g = Interface();
                if (g == null) return false;
                Guid id = g.Value;
                int reason;
                int rc = WlanSetProfile(Handle, ref id, 0 /* all users */, xml, null, true, IntPtr.Zero, out reason);
                if (rc != 0) rc = WlanSetProfile(Handle, ref id, 2 /* WLAN_PROFILE_USER */, xml, null, true, IntPtr.Zero, out reason);
                if (rc != 0) Log.Write("set profile failed: " + rc + " reason " + reason);
                return rc == 0;
            }
        }

        // Louisa profiles saved by earlier versions (or by joining in Windows Settings) are "connect
        // automatically". Switch them to manual. Reads each saved profile as-is (password stays encrypted),
        // so no password is needed.
        public static void MakeProfilesManual()
        {
            lock (gate)
            {
                Guid? g = Interface();
                if (g == null) return;
                Guid id = g.Value;
                IntPtr list;
                if (WlanGetProfileList(Handle, ref id, IntPtr.Zero, out list) != 0) return;
                var names = new List<string>();
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < count; i++)   // WLAN_PROFILE_INFO = WCHAR[256] + flags = 516 bytes
                        names.Add(Marshal.PtrToStringUni(new IntPtr(list.ToInt64() + 8 + i * 516)));
                }
                finally { WlanFreeMemory(list); }

                foreach (var name in names.Where(App.IsLouisa))
                {
                    IntPtr xmlPtr;
                    int flags = 0, access;
                    if (WlanGetProfile(Handle, ref id, name, IntPtr.Zero, out xmlPtr, ref flags, out access) != 0) continue;
                    string xml;
                    try { xml = Marshal.PtrToStringUni(xmlPtr); }
                    finally { WlanFreeMemory(xmlPtr); }
                    if (xml == null || !xml.Contains("<connectionMode>auto</connectionMode>")) continue;
                    xml = xml.Replace("<connectionMode>auto</connectionMode>", "<connectionMode>manual</connectionMode>");
                    int reason;
                    int rc = WlanSetProfile(Handle, ref id, flags & 2 /* keep per-user / all-user */, xml, null, true, IntPtr.Zero, out reason);
                    Log.Write(rc == 0 ? name + " profile set to connect manually" : "could not update " + name + " profile: " + rc + " reason " + reason);
                }
            }
        }

        public static bool Connect(string ssid)
        {
            lock (gate)
            {
                Guid? g = Interface();
                if (g == null) return false;
                Guid id = g.Value;
                var p = new ConnectionParameters { mode = 0 /* profile */, profile = ssid, bssType = 1 /* infrastructure */ };
                return WlanConnect(Handle, ref id, ref p, IntPtr.Zero) == 0;
            }
        }

        public static void Disconnect()
        {
            lock (gate)
            {
                Guid? g = Interface();
                if (g == null) return;
                Guid id = g.Value;
                WlanDisconnect(Handle, ref id, IntPtr.Zero);
            }
        }

        // DOT11_SSID: ULONG length + UCHAR[32]
        static string ReadSsid(IntPtr p, int offset)
        {
            int len = Math.Min(Marshal.ReadInt32(p, offset), 32);
            if (len <= 0) return "";
            var bytes = new byte[len];
            Marshal.Copy(new IntPtr(p.ToInt64() + offset + 4), bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }

        static string ReadBssid(IntPtr p, int offset)
        {
            var parts = new string[6];
            for (int i = 0; i < 6; i++) parts[i] = Marshal.ReadByte(p, offset + i).ToString("x2");
            return string.Join(":", parts);
        }

        delegate void NotifyCallback(IntPtr data, IntPtr context);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ConnectionParameters
        {
            public int mode;
            [MarshalAs(UnmanagedType.LPWStr)] public string profile;
            public IntPtr ssid;
            public IntPtr desiredBssids;
            public int bssType;
            public int flags;
        }

        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, out int valueType);
        [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool securityEnabled, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, int flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanSetProfile(IntPtr handle, ref Guid iface, int flags, string xml, string security, bool overwrite, IntPtr reserved, out int reason);
        [DllImport("wlanapi.dll")] static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanGetProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved, out IntPtr xml, ref int flags, out int access);
        [DllImport("wlanapi.dll")] static extern int WlanConnect(IntPtr handle, ref Guid iface, ref ConnectionParameters parameters, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanRegisterNotification(IntPtr handle, int source, bool ignoreDuplicate, NotifyCallback callback, IntPtr context, IntPtr reserved, out int previous);
    }
}
