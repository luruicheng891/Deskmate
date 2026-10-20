using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 从 .exe / .dll 提取指定尺寸图标句柄。上层负责用
/// <c>System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon</c> 转成 BitmapSource，
/// 并在用完后调用 <see cref="DestroyIcon"/> 释放 GDI 对象。
/// </summary>
public static class IconExtractor
{
    private const uint LR_DEFAULTCOLOR = 0x00000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int PrivateExtractIconsW(
        string lpszFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        int[]? piconid,
        int nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 提取指定边长的图标句柄。
    /// <para>
    /// 优先用 <c>PrivateExtractIcons</c>：它是唯一能按任意尺寸（Dock 的 56px、启动台的 64px）
    /// 取图的 API。<c>ExtractIconEx</c> 只给系统大/小图标两种尺寸，缩放后发虚，因此只当兜底。
    /// </para>
    /// </summary>
    /// <param name="path">exe、dll 或已解析出目标的 lnk 路径。</param>
    /// <param name="size">期望边长（设备无关像素）。</param>
    /// <param name="iconIndex">同一文件里的第几个图标，通常为 0。</param>
    /// <returns>成功返回非零 HICON；文件无图标或不存在时返回 <see cref="IntPtr.Zero"/>。</returns>
    public static IntPtr ExtractIconHandle(string path, int size = 32, int iconIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return IntPtr.Zero;
        }

        var handles = new IntPtr[1];
        var extracted = PrivateExtractIconsW(path, iconIndex, size, size, handles, null, 1, LR_DEFAULTCOLOR);
        if (extracted > 0 && handles[0] != IntPtr.Zero)
        {
            return handles[0];
        }

        // 兜底：某些老程序集 PrivateExtractIcons 取不到，退回系统大图标尺寸。
        var large = new IntPtr[1];
        if (ExtractIconExW(path, iconIndex, large, null, 1) >= 1 && large[0] != IntPtr.Zero)
        {
            return large[0];
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 取不到目标尺寸时，按 48 → 32 → 16 依次降级，避免 Dock 直接没有图标。
    /// </summary>
    public static IntPtr ExtractIconHandleWithFallback(string path, int preferredSize)
    {
        foreach (var size in new[] { preferredSize, 48, 32, 16 })
        {
            var handle = ExtractIconHandle(path, size);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>文件内嵌图标总数。</summary>
    public static int CountIcons(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        // 传 null 数组只查询数量，不会产生需要释放的句柄。
        return (int)ExtractIconExW(path, -1, null, null, 0);
    }

    /// <summary>释放 HICON。上层转换完 BitmapSource 后必须调用，否则 GDI 句柄泄漏。</summary>
    public static void DestroyIconHandle(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero)
        {
            DestroyIcon(hIcon);
        }
    }
}
