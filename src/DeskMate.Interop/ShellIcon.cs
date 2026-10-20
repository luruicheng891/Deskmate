using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 通过 Shell 取图标：<c>SHGetFileInfo</c> 返回的是资源管理器实际显示的那个图标，
/// 因此能覆盖 <see cref="IconExtractor"/> 直接读 PE 资源覆盖不到的三类情况：
/// UWP/商店应用（exe 在 WindowsApps 下，普通权限读不到）、.url 网络快捷方式、以及目标本身就是目录的文件。
/// </summary>
public static class ShellIcon
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiSmallIcon = 0x000000001;

    private const int MaxPath = 260;
    private const int MaxTypeName = 80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;

        [MarshalAs(UnmanagedType.U4)]
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxTypeName)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShellFileInfo psfi, uint cbFileInfo, uint uFlags);

    /// <summary>
    /// 取 Shell 为路径准备的图标。路径不存在时返回 <see cref="IntPtr.Zero"/>
    /// —— 这正是调用方想要的：失效条目该画字母占位，而不是套一个看起来很正常的通用图标。
    /// </summary>
    /// <param name="path">.lnk / .exe / .url / 目录，或任何 Explorer 里能看到图标的东西。</param>
    /// <param name="large">true 取大图标（一般为 32px），false 取小图标（16px）。</param>
    /// <returns>HICON；取不到时返回 <see cref="IntPtr.Zero"/>。用完必须交给 <see cref="IconExtractor.DestroyIconHandle"/>。</returns>
    public static IntPtr ExtractIconHandle(string path, bool large = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return IntPtr.Zero;
        }

        var info = new ShellFileInfo();
        var flags = ShgfiIcon | (large ? ShgfiLargeIcon : ShgfiSmallIcon);

        _ = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags);

        return info.hIcon;
    }
}
