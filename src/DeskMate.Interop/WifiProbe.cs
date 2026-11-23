using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// Wi-Fi 连接探测（wlanapi）：拿当前连接的 SSID 与信号强度（0~100 的百分比）。
/// <para>
/// 只负责 Wi-Fi 这一半：没有 Wi-Fi 接口、没在连接或查询失败都返回 <c>null</c>，
/// 由调用方用「有线已连接 / 网络未连接」兜底。一次性查询，不开通知。
/// </para>
/// </summary>
public static class WifiProbe
{
    /// <summary>当前连接。Ssid 已经是解码后的明文（非隐藏网络）。</summary>
    public sealed record WifiSignal(string Ssid, int StrengthPercent);

    /// <summary>wlan_intf_opcode_current_connection（autoconf 段第 3 个）。</summary>
    private const uint WlanIntfOpcodeCurrentConnection = 0x00000002;

    /// <summary>wlan_interface_state_connected。</summary>
    private const uint WlanInterfaceStateConnected = 1;

    /// <summary>CLIENT_VERSION_WINDOWS_VISTA：Win10/11 上请求版本 2。</summary>
    private const uint ClientVersionVista = 2;

    [DllImport("Wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("Wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("Wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("Wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("Wlanapi.dll")]
    private static extern int WlanQueryInterface(
        IntPtr clientHandle, ref Guid interfaceGuid, uint opcode, IntPtr reserved, uint flags,
        out IntPtr responseData, out uint dataSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanInterfaceInfoList
    {
        public uint NumberOfItems;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        public uint State;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    /// <summary>
    /// 字段顺序与 wlanapi.h 的 <c>WLAN_CONNECTION_ATTRIBUTES</c> 完全一致
    /// （isState/mode/profile/关联属性/安全属性）。只取其中 SSID 与信号质量。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public uint State;
        public uint ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public Dot11Ssid Ssid;
        public uint BssType;
        public uint PhyType;
        public uint SignalQuality;
        public uint SecondaryPhyType;
        public int SecurityEnabled;
        public int OneXEnabled;
        public uint AuthAlgorithm;
        public uint CipherAlgorithm;
    }

    public static WifiSignal? GetConnectedSignal()
    {
        if (WlanOpenHandle(ClientVersionVista, IntPtr.Zero, out _, out var client) != 0)
        {
            return null;
        }

        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0)
            {
                return null;
            }

            try
            {
                var header = Marshal.PtrToStructure<WlanInterfaceInfoList>(list);
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();

                for (var i = 0; i < header.NumberOfItems; i++)
                {
                    var item = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(list, 8 + (int)(i * itemSize)));
                    if (item.State != WlanInterfaceStateConnected)
                    {
                        continue;
                    }

                    var guid = item.InterfaceGuid;
                    if (WlanQueryInterface(client, ref guid, WlanIntfOpcodeCurrentConnection, IntPtr.Zero, 0, out var data, out _) != 0)
                    {
                        continue;
                    }

                    try
                    {
                        var attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
                        if (attributes.Ssid.Length == 0)
                        {
                            continue;
                        }

                        var ssid = System.Text.Encoding.UTF8.GetString(attributes.Ssid.Ssid, 0, (int)attributes.Ssid.Length);
                        return new WifiSignal(ssid, (int)Math.Clamp(attributes.SignalQuality, 0, 100));
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }

                return null;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            _ = WlanCloseHandle(client, IntPtr.Zero);
        }
    }
}
