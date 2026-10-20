using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskMate.Interop;
using Xunit;

namespace DeskMate.Tests.Interop;

/// <summary>
/// 遮罩取帧用到的那条带子：像素是不是「什么都没画进来」只能靠这几个字节判，
/// 判错就是把一块黑糊在用户的桌面上，所以钉死在这里。
/// </summary>
public class ScreenCaptureBandTests
{
    [Fact]
    public void AnAllZeroBandIsTreatedAsNothingGrabbed()
    {
        var blank = new BandPixels(new byte[8 * 2], 2, 2, 8);
        Assert.True(blank.IsBlank);

        // 只要有一个像素有颜色，就不能算「没画进来」—— 那是壁纸，不是空画布。
        var withContent = new BandPixels(
            new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            2,
            2,
            8);
        Assert.False(withContent.IsBlank);
    }

    [Fact]
    public void TheCroppedBandBecomesAnOpaqueBitmapOfTheSameSize()
    {
        // Bgr32 而不是 Bgra32：GDI 写的第四个字节全是 0，按预乘 alpha 读进去就成了一张全透明的图。
        var pixels = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0, 70, 80, 90, 0, 100, 110, 120, 0 };
        var band = new BandPixels(pixels, 2, 2, 8);

        var bitmap = band.ToBitmapSource();

        Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        Assert.Equal(2, bitmap.PixelWidth);
        Assert.Equal(2, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen, "冻结了才能从取帧线程交给 UI 线程用");

        var copied = new byte[pixels.Length];
        bitmap.CopyPixels(copied, 8, 0);
        Assert.Equal(pixels, copied);
    }

    [Fact]
    public void AnImpossibleCropReturnsNothingInsteadOfThrowing()
    {
        Assert.Null(ScreenCapture.GrabBandFromWindow(IntPtr.Zero, 0, 0, 100, 48));

        // 句柄存在但压根不是个全屏桌面层（这里是个不存在的窗口）：裁不出来越界的那一块，只能返回 null。
        Assert.Null(ScreenCapture.GrabBandFromWindow(new IntPtr(0x123456), -8, -8, 4000, 4000));
    }
}
