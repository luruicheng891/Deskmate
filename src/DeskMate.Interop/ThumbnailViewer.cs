using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 用 DWM 把别的窗口的内容实时贴到我们的窗口上 —— 这就是任务栏悬停预览那种会动的缩略图，
/// 不是截图：由 DWM 在合成阶段直接搬运源窗口的画面，所以不占我们的 CPU，源窗口一动它就跟着动。
/// </summary>
/// <remarks>
/// 一个实例对应一个源窗口。必须 Dispose 注销，否则 DWM 侧的缩略图引用会一直挂着。
/// </remarks>
public sealed class ThumbnailViewer : IDisposable
{
    private const int FmtVisible = 0x00000002;
    private const int FmtDestination = 0x00000001;
    private const int FmtSource = 0x00000004;

    /// <summary>源窗口的原始尺寸（物理像素）。</summary>
    public int SourceWidth { get; private set; }

    /// <summary>源窗口的原始高度（物理像素）。</summary>
    public int SourceHeight { get; private set; }

    /// <summary>是否成功建立了缩略图通道。</summary>
    public bool IsAvailable { get; private set; }

    private IntPtr _thumbnail;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // 字段顺序必须与 dwmapi.h 的 DWM_THUMBNAIL_PROPERTIES 完全一致：
    // dwFlags 在最前，漏掉它会让后面所有字段错位。
    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public int dwFlags;
        public DwmRect rcDestination;
        public DwmRect rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmSize
    {
        public int cx;
        public int cy;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr pThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out DwmSize pSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DwmThumbnailProperties ptsProperties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// 建立目标窗口与源窗口之间的缩略图通道，并读出源窗口的原始尺寸（用来算等比缩放）。
    /// </summary>
    /// <param name="targetHwnd">承载缩略图的窗口（我们的预览浮层）。</param>
    /// <param name="sourceHwnd">被预览的窗口。</param>
    public ThumbnailViewer(IntPtr targetHwnd, IntPtr sourceHwnd)
    {
        if (targetHwnd == IntPtr.Zero || sourceHwnd == IntPtr.Zero)
        {
            return;
        }

        if (DwmRegisterThumbnail(targetHwnd, sourceHwnd, out var id) != 0 || id == IntPtr.Zero)
        {
            return;
        }

        _thumbnail = id;

        if (DwmQueryThumbnailSourceSize(id, out var size) == 0)
        {
            SourceWidth = size.cx;
            SourceHeight = size.cy;
        }

        IsAvailable = SourceWidth > 0 && SourceHeight > 0;

        if (!IsAvailable)
        {
            Dispose();
        }
    }

    /// <summary>
    /// 指定缩略图画在目标窗口的哪块区域（物理像素、相对目标窗口客户区）。
    /// </summary>
    public void UpdateDestination(int x, int y, int width, int height)
    {
        if (!IsAvailable)
        {
            return;
        }

        var properties = new DwmThumbnailProperties
        {
            dwFlags = FmtDestination | FmtVisible | FmtSource,
            rcDestination = new DwmRect { Left = x, Top = y, Right = x + width, Bottom = y + height },
            rcSource = new DwmRect { Left = 0, Top = 0, Right = SourceWidth, Bottom = SourceHeight },
            opacity = 255,
            fVisible = 1,
            fSourceClientAreaOnly = 0,
        };

        _ = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
    }

    /// <summary>
    /// Windows 11 的圆角与描边（对非分层窗口生效，正好适合预览浮层）。
    /// 老系统上这个调用会返回错误码，直接忽略即可。
    /// </summary>
    /// <param name="hwnd">目标窗口。</param>
    /// <param name="borderColorBgr">描边颜色，0x00BBGGRR；传 -1 用系统默认。</param>
    public static void ApplyModernFrame(IntPtr hwnd, int borderColorBgr = -1)
    {
        var corner = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));

        if (borderColorBgr != -1)
        {
            var border = borderColorBgr;
            _ = DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
    }

    /// <summary>注销缩略图。必须调用，否则 DWM 会一直持有源窗口的引用。</summary>
    public void Dispose()
    {
        if (_thumbnail == IntPtr.Zero)
        {
            return;
        }

        // 先把区域清空，避免窗口还在时留下最后一帧。
        var hidden = new DwmThumbnailProperties { dwFlags = FmtVisible, fVisible = 0 };
        _ = DwmUpdateThumbnailProperties(_thumbnail, ref hidden);

        _ = DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
        IsAvailable = false;
    }
}
