using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace ClawLibrary.Data;

internal static class QamWireless
{
    internal sealed record Entry(string Id, string Name, string Detail, string Icon, bool Saved, bool Connected,
        WiFiAdapter? Adapter = null, WiFiAvailableNetwork? Network = null, string? Profile = null,
        DeviceInformation? Device = null, int Signal = 0, bool Secured = false);

    internal static string? ConnectedWifiName()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile?.IsWlanConnectionProfile != true)
            profile = NetworkInformation.GetConnectionProfiles().FirstOrDefault(p =>
                p.IsWlanConnectionProfile && p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
        return WifiName(profile);
    }

    private static string? WifiName(ConnectionProfile? profile)
    {
        if (profile?.IsWlanConnectionProfile != true) return null;
        try
        {
            var ssid = profile.WlanConnectionProfileDetails.GetConnectedSsid();
            if (!string.IsNullOrEmpty(ssid)) return ssid;
        }
        catch (Exception ex) { Log.Info($"Wi-Fi SSID unavailable: {ex.Message}"); }
        return profile.ProfileName;
    }

    internal static Entry ConnectedWifiEntry(string ssid, WiFiAdapter? adapter = null, string? profile = null) =>
        new($"connected:{adapter?.NetworkAdapter.NetworkAdapterId}:{ssid}", ssid, "已连接", "qam.wifi",
            profile != null, true, adapter, Profile: profile);

    internal static async Task<List<Entry>> Wifi()
    {
        if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed)
            throw new InvalidOperationException("Wi-Fi 扫描需要位置权限");
        var adapters = await WiFiAdapter.FindAllAdaptersAsync();
        var result = await Task.WhenAll(adapters.Select(async adapter =>
        {
            await adapter.ScanAsync();
            var id = adapter.NetworkAdapter.NetworkAdapterId;
            var profiles = await Task.Run(() => WlanProfiles(id));
            var connected = WifiName(await adapter.NetworkAdapter.GetConnectedProfileAsync());
            var entries = adapter.NetworkReport.AvailableNetworks.Where(n => n.Ssid.Length > 0).Select(n =>
            {
                bool on = n.Ssid == connected;
                bool secure = n.SecuritySettings.NetworkEncryptionType != NetworkEncryptionType.None;
                profiles.TryGetValue(n.Ssid, out string? profile);
                int frequency = n.ChannelCenterFrequencyInKilohertz;
                string band = frequency >= 5925000 ? "6 GHz" : frequency >= 4900000 ? "5 GHz" : frequency > 0 ? "2.4 GHz" : "";
                string detail = on ? (band.Length == 0 ? "已连接" : $"已连接 · {band}") :
                    profile != null ? "已保存" : !secure ? "开放网络" : "";
                return new Entry($"{id}:{n.Ssid}", n.Ssid, detail, "qam.wifi", profile != null, on,
                    adapter, n, profile, Signal: n.SignalBars, Secured: secure);
            }).ToList();
            if (connected != null && !entries.Any(e => e.Connected))
                entries.Add(ConnectedWifiEntry(connected, adapter, profiles.GetValueOrDefault(connected)));
            return entries;
        }));
        return result.SelectMany(rows => rows).OrderByDescending(e => e.Connected).ThenByDescending(e => e.Signal)
            .DistinctBy(e => e.Name).ToList();
    }

    internal static async Task<WiFiConnectionStatus> Connect(Entry entry, string? password)
    {
        var adapter = entry.Adapter!;
        if (entry.Profile != null)
        {
            await Task.Run(() => WithWlan(h =>
            {
                var id = adapter.NetworkAdapter.NetworkAdapterId;
                var args = new ConnectParameters { Profile = entry.Profile, BssType = 3 };
                Check(WlanConnect(h, ref id, ref args, 0));
            }));
            for (int i = 0; i < 40; i++)
            {
                var connected = await adapter.NetworkAdapter.GetConnectedProfileAsync();
                if (WifiName(connected) == entry.Name)
                    return WiFiConnectionStatus.Success;
                await Task.Delay(250);
            }
            return WiFiConnectionStatus.Timeout;
        }

        var result = password == null
            ? await adapter.ConnectAsync(entry.Network!, WiFiReconnectionKind.Automatic)
            : await adapter.ConnectAsync(entry.Network!, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
        return result.ConnectionStatus;
    }

    internal static Task Forget(Entry entry) => Task.Run(() => WithWlan(h =>
    {
        var id = entry.Adapter!.NetworkAdapter.NetworkAdapterId;
        Check(WlanDeleteProfile(h, ref id, entry.Profile!, 0));
    }));

    private static readonly string[] BtProperties = ["System.Devices.Aep.IsConnected", "System.Devices.Aep.Bluetooth.Cod.Major", "System.Devices.Aep.Bluetooth.Cod.Minor"];
    internal static DeviceWatcher WatchBluetooth() => DeviceInformation.CreateWatcher(
        "(" + BluetoothDevice.GetDeviceSelectorFromPairingState(true) + ") OR (" +
        BluetoothDevice.GetDeviceSelectorFromPairingState(false) + ") OR (" +
        BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) + ") OR (" +
        BluetoothLEDevice.GetDeviceSelectorFromPairingState(false) + ")", BtProperties, DeviceInformationKind.AssociationEndpoint);

    internal static Entry Bluetooth(DeviceInformation device)
    {
        bool connected = device.Properties.TryGetValue(BtProperties[0], out var value) && value is true;
        bool saved = device.Pairing.IsPaired;
        string name = device.Name;
        string type = name.ToLowerInvariant();
        uint major = device.Properties.TryGetValue(BtProperties[1], out var majorValue) && majorValue is uint m ? m : 0;
        uint minor = device.Properties.TryGetValue(BtProperties[2], out var minorValue) && minorValue is uint n ? n : 0;
        string icon = type.Contains("keyboard") || type.Contains("键盘") || major == 5 && minor == 16 ? "keyboard" :
            type.Contains("mouse") || type.Contains("鼠标") || major == 5 && minor == 32 ? "mouse" :
            type.Contains("speaker") || major == 4 && minor == 5 ? "speaker" :
            type.Contains("head") || type.Contains("buds") || type.Contains("airpods") || major == 4 ? "headphones" :
            type.Contains("controller") || type.Contains("xbox") || type.Contains("dualsense") || major == 5 ? "controller" : "qam.bt";
        return new Entry(device.Id, string.IsNullOrWhiteSpace(name) ? "未命名设备" : name,
            saved ? connected ? "已连接" : "未连接" : "", icon, saved, connected, Device: device);
    }

    internal static bool IsLowEnergy(Entry entry) => entry.Device?.Id.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase) == true;

    internal static async Task<int?> Battery(Entry entry)
    {
        if (!IsLowEnergy(entry) || !entry.Connected) return null;
        using var device = await BluetoothLEDevice.FromIdAsync(entry.Id);
        if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
        int? level = null;
        foreach (var service in services.Services)
        {
            using (service)
            {
                if (level.HasValue) continue;
                var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached);
                var characteristic = chars.Characteristics.FirstOrDefault();
                if (characteristic == null || !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) continue;
                var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Cached);
                if (result.Status != GattCommunicationStatus.Success || result.Value.Length == 0) continue;
                using var reader = DataReader.FromBuffer(result.Value);
                level = reader.ReadByte();
            }
        }
        return level is >= 0 and <= 100 ? level : null;
    }

    internal static async Task<GattSession> ConnectBluetooth(Entry entry)
    {
        using var device = await BluetoothLEDevice.FromIdAsync(entry.Id);
        var session = device == null ? null : await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        if (session == null || !session.CanMaintainConnection)
        {
            session?.Dispose();
            throw new InvalidOperationException("设备无法连接");
        }
        try
        {
            session.MaintainConnection = true;
            for (int i = 0; i < 40; i++)
            {
                if (session.SessionStatus == GattSessionStatus.Active) return session;
                await Task.Delay(250);
            }
            throw new InvalidOperationException("设备连接超时");
        }
        catch { session.Dispose(); throw; }
    }

    private static Dictionary<string, string> WlanProfiles(Guid id)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        WithWlan(h =>
        {
            Check(WlanGetProfileList(h, ref id, 0, out nint list));
            try
            {
                int count = Marshal.ReadInt32(list);
                for (int i = 0; i < count; i++)
                {
                    string profile = Marshal.PtrToStringUni(list + 8 + 516 * i)!;
                    uint flags = 0;
                    if (WlanGetProfile(h, ref id, profile, 0, out nint xml, ref flags, out _) != 0) continue;
                    try
                    {
                        var root = XElement.Parse(Marshal.PtrToStringUni(xml)!);
                        var ns = root.Name.Namespace;
                        string? ssid = root.Element(ns + "SSIDConfig")?.Element(ns + "SSID")?.Element(ns + "name")?.Value;
                        if (!string.IsNullOrEmpty(ssid)) result.TryAdd(ssid, profile);
                    }
                    finally { WlanFreeMemory(xml); }
                }
            }
            finally { WlanFreeMemory(list); }
        });
        return result;
    }

    private static void Check(uint error) { if (error != 0) throw new Win32Exception((int)error); }
    private static void WithWlan(Action<nint> action)
    {
        Check(WlanOpenHandle(2, 0, out _, out nint handle));
        try { action(handle); }
        finally { WlanCloseHandle(handle, 0); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConnectParameters
    {
        public int Mode;
        public string Profile;
        public nint Ssid, BssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(nint handle, ref Guid id, nint reserved, out nint list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanGetProfile(nint handle, ref Guid id, string name, nint reserved, out nint xml, ref uint flags, out uint access);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanDeleteProfile(nint handle, ref Guid id, string name, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanConnect(nint handle, ref Guid id, ref ConnectParameters args, nint reserved);
}
