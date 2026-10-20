using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskMate.Interop;

/// <summary>
/// 抓一块屏幕区域变成 BitmapSource，给「毛玻璃」当背景素材。
/// 分层窗口（AllowsTransparency=True）不接受 DWM 的背景采样，所以只能自己把面板后面那块
/// 屏幕取下来、模糊之后再贴回去。
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int width, int height);

    /// <summary>建一块「像素直接可读写的内存位图」，返回位图句柄，像素地址从 ppvBits 出来。</summary>
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hDC, ref BitmapInfo info, uint usage, out IntPtr ppvBits, IntPtr section, uint offset);

    /// <summary>DIB 的颜色表用 RGB 三元组，不做调色板转换。</summary>
    private const uint DibRgbColors = 0;

    /// <summary>32 位 DIB 的掩码式压缩值。</summary>
    private const uint BiBitfields = 3;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hDC, int x, int y, int width, int height, IntPtr srcDC, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hDC, int x, int y, int width, int height, IntPtr srcDC, int srcX, int srcY, int srcWidth, int srcHeight, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hDC, int mode);

    /// <summary>HALFTONE：降采样时质量最好的模式，正适合「抓下来就是要糊掉」的毛玻璃。</summary>
    private const int Halftone = 4;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetDIBits(IntPtr hDC, IntPtr hBitmap, uint startLine, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hDC, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 lpRect);

    /// <summary>PrintWindow 的 PW_RENDERFULLCONTENT：能把 DComp 渲染的内容（比如桌面壁纸）也取出来。</summary>
    private const uint PwRenderFullContent = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint Srccopy = 0x00CC0020;

    /// <summary>
    /// 抓取屏幕上的一块矩形（坐标与尺寸都是物理像素）。失败返回 null，调用方退回纯色半透明。
    /// 受 DRM 保护的内容可能本来就是黑的，那是系统行为，不是这里的错误。
    /// </summary>
    /// <param name="downscale">
    /// 降采样倍数：>1 时用 <c>StretchBlt</c> 直接把目标矩形画小，于是 <c>GetDIBits</c> 要搬的字节数
    /// 按平方减少（1344x756 全屏抓一次约 23 ms，降到 1/2 只要约 6 ms）。毛玻璃本来就要糊掉，
    /// 少这点分辨率完全看不出来。
    /// </param>
    public static BitmapSource? Grab(int x, int y, int width, int height, int downscale = 1)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var factor = Math.Max(1, downscale);
        var targetWidth = Math.Max(1, width / factor);
        var targetHeight = Math.Max(1, height / factor);

        var screenDC = GetDC(IntPtr.Zero);
        if (screenDC == IntPtr.Zero)
        {
            return null;
        }

        var memoryDC = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memoryDC = CreateCompatibleDC(screenDC);
            bitmap = CreateCompatibleBitmap(screenDC, targetWidth, targetHeight);
            if (memoryDC == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return null;
            }

            previous = SelectObject(memoryDC, bitmap);

            var copied = factor == 1
                ? BitBlt(memoryDC, 0, 0, targetWidth, targetHeight, screenDC, x, y, Srccopy)
                : CopiedScaled(memoryDC, screenDC, x, y, width, height, targetWidth, targetHeight);

            if (!copied)
            {
                return null;
            }

            return ToBitmapSource(memoryDC, bitmap, targetWidth, targetHeight);
        }
        catch
        {
            // 抓背景只是锦上添花，不值得把启动台的打开流程带崩。
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                _ = SelectObject(memoryDC, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memoryDC != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDC);
            }

            _ = ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    private static bool CopiedScaled(IntPtr memoryDC, IntPtr screenDC, int x, int y, int width, int height, int targetWidth, int targetHeight)
    {
        _ = SetStretchBltMode(memoryDC, Halftone);
        return StretchBlt(memoryDC, 0, 0, targetWidth, targetHeight, screenDC, x, y, width, height, Srccopy);
    }

    /// <summary>
    /// 抓某个窗口<strong>自己渲染</strong>的内容里的一块，直接拿原始 BGRA 像素（不建 WPF 位图）。
    /// 给「每几百毫秒重抓一次、大多数时候画面没变」的用法用：位图留到真变了再建，
    /// 省掉每轮一张 80 KB 的 BitmapSource 和它带来的绘制缓存。失败或裁不出返回 null。
    /// <para>
    /// 目标画布必须是 <c>CreateDIBSection</c> 建的 32 位 DIB，<strong>不能</strong>用
    /// <c>CreateCompatibleBitmap(screenDC, …)</c>：动态壁纸那一层是 GPU 合成的，
    /// PrintWindow 往 DDB 里画会「返回成功但一个像素都没写」，屏幕上就是一块纯黑（实测踩过）。
    /// 同一份内容换成 DIB 段就正常。
    /// </para>
    /// </summary>
    public static BandPixels? GrabBandFromWindow(IntPtr hwnd, int x, int y, int width, int height)
    {
        if (hwnd == IntPtr.Zero || width <= 0 || height <= 0 || !GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var sourceWidth = rect.Right - rect.Left;
        var sourceHeight = rect.Bottom - rect.Top;
        var offsetX = x - rect.Left;
        var offsetY = y - rect.Top;
        if (sourceWidth <= 0 || sourceHeight <= 0 || offsetX < 0 || offsetY < 0
            || offsetX + width > sourceWidth || offsetY + height > sourceHeight)
        {
            return null;
        }

        var screenDC = GetDC(IntPtr.Zero);
        if (screenDC == IntPtr.Zero)
        {
            return null;
        }

        var memoryDC = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = sourceWidth,
                    Height = -sourceHeight, // 负高度 = 自上而下的行序，和 WPF 一致
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiBitfields,
                },
                RedMask = 0x00FF0000,
                GreenMask = 0x0000FF00,
                BlueMask = 0x000000FF,
            };

            memoryDC = CreateCompatibleDC(screenDC);
            bitmap = CreateDIBSection(memoryDC, ref info, DibRgbColors, out var pixels, IntPtr.Zero, 0);
            if (memoryDC == IntPtr.Zero || bitmap == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                return null;
            }

            previous = SelectObject(memoryDC, bitmap);
            if (!PrintWindow(hwnd, memoryDC, PwRenderFullContent))
            {
                return null;
            }

            // 裁一块直接在内存里搬行，省掉第二张画布和一次 BitBlt。
            var sourceStride = sourceWidth * 4;
            var targetStride = width * 4;
            var bytes = new byte[targetStride * height];
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(pixels + ((offsetY + row) * sourceStride) + (offsetX * 4), bytes, row * targetStride, targetStride);
            }

            return new BandPixels(bytes, width, height, targetStride);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                _ = SelectObject(memoryDC, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }

            if (memoryDC != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDC);
            }

            _ = ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    /// <summary>
    /// 抓某个窗口<strong>自己渲染</strong>的内容（不看它上面压了谁），再从中裁一块变成位图。
    /// 一次性取一块用这个；要反复取同一条带去比帧差，用 <see cref="GrabBandFromWindow"/>。
    /// </summary>
    public static BitmapSource? GrabFromWindow(IntPtr hwnd, int x, int y, int width, int height)
        => GrabBandFromWindow(hwnd, x, y, width, height)?.ToBitmapSource();

    private static BitmapSource? ToBitmapSource(IntPtr memoryDC, IntPtr bitmap, int width, int height)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // 负高度 = 自上而下的行序，和 WPF 一致
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        var bytes = new byte[width * height * 4];
        if (GetDIBits(memoryDC, bitmap, 0, (uint)height, bytes, ref header, 0) == 0)
        {
            return null;
        }

        // 必须是 Bgr32 而不是 Bgra32：GDI 填的那第四个字节全是 0，WPF 把 Bgra32 当预乘 alpha 读，
        // 整张图会被当成「完全透明」——毛玻璃抓到了却一点都看不见。Bgr32 直接忽略这一字节。
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bytes, width * 4);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// BITMAPINFO：头 + 32 位 BI_BITFIELDS 的三个掩码。
    /// 32 位 DIB 用 BI_RGB 时，DWM 的重定向绘制会「返回成功但一个像素都不写」，
    /// 掩码格式（GDI+ 内部用的就是这个）才画得进去。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
    }

    /// <summary>BITMAPINFOHEADER。布局必须逐字节对齐，所以显式声明 Pack = 1。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }
}

/// <summary>
/// 从某个窗口自己渲染的内容里裁出来的一条带子，还是原始 BGRA 像素。
/// 留着像素而不是直接变成位图，是为了「先比一比，真变了才建位图」——
/// 每几百毫秒就 alloc 一张 BitmapSource，GC 和渲染缓存都不该白吃这份。
/// </summary>
/// <param name="Pixels">逐行像素，长度 = <see cref="Stride"/> × <see cref="Height"/>。</param>
/// <param name="Stride">一行的字节数（32 位像素 = 宽 × 4，没有额外填充）。</param>
public sealed record BandPixels(byte[] Pixels, int Width, int Height, int Stride)
{
    /// <summary>
    /// 整条都是纯黑 = 什么都没画进来（往错的画布上 PrintWindow 一个 GPU 合成的层就会这样）。
    /// 真的纯黑壁纸会被误判，但那种时候兜底的 <c>Progman</c> 同样是黑的，观感没差别 ——
    /// 宁可判错，也不能把没写进来的画布当成壁纸糊在屏幕上。
    /// </summary>
    public bool IsBlank
    {
        get
        {
            for (var i = 0; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 0 || Pixels[i + 1] != 0 || Pixels[i + 2] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 冻结成能跨线程用的位图：取帧那条线程建好，再交给 UI 线程画。
    /// 格式必须是 Bgr32 而不是 Bgra32 —— GDI 填的第四个字节全是 0，WPF 把 Bgra32 当预乘 alpha 读，
    /// 整张图会被当成「完全透明」，像素明明抓到了却一点都看不见。
    /// </summary>
    public BitmapSource ToBitmapSource()
    {
        var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Pixels, Stride);
        source.Freeze();
        return source;
    }
}
