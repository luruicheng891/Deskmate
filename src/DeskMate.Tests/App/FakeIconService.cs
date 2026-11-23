using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskMate.Core.Interfaces;

namespace DeskMate.Tests.App;

/// <summary>
/// 图标替身：不碰 Shell，一律回一张 1×1 的已冻结位图。
/// 面板的行与卡片在建出来时会异步问类型图标，替身让这条链在单测里也能走完并断言到 <c>Icon</c>。
/// </summary>
public sealed class FakeIconService : IIconService
{
    private static readonly BitmapSource Pixel = CreatePixel();

    public Task<BitmapSource?> GetIconAsync(string path, int size = 64) => Task.FromResult<BitmapSource?>(Pixel);

    public Task<BitmapSource?> GetTypeIconAsync(string path, bool isDirectory, int size = 32)
        => Task.FromResult<BitmapSource?>(Pixel);

    public void ClearCache()
    {
    }

    private static BitmapSource CreatePixel()
    {
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 4);
        pixel.Freeze();
        return pixel;
    }
}
