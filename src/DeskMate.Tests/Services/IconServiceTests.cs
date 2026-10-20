using System.Windows;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 图标链路集成测试：PrivateExtractIcons → HICON → BitmapSource，
/// 同时验证 STA 调度与 GDI 句柄释放路径能跑通。
/// </summary>
public class IconServiceTests : IDisposable
{
    private readonly StaScheduler _sta = new();

    private IconService Create() => new(_sta, NullLogger<IconService>.Instance);

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    public async Task GetIconAsync_ReturnsFrozenBitmap(string exe)
    {
        if (!File.Exists(exe))
        {
            return;
        }

        var icon = await Create().GetIconAsync(exe, 56);

        Assert.NotNull(icon);
        Assert.True(icon!.IsFrozen, "跨线程使用的 BitmapSource 必须 Freeze");
        Assert.True(icon.PixelWidth > 0 && icon.PixelHeight > 0);
    }

    [Fact]
    public async Task GetIconAsync_ReturnsNull_AndCachesFailure_ForMissingFile()
    {
        var service = Create();
        var path = Path.Combine(Path.GetTempPath(), $"不存在的程序-{Guid.NewGuid():N}.exe");

        Assert.Null(await service.GetIconAsync(path));
        Assert.Null(await service.GetIconAsync(path));
    }

    [Fact]
    public async Task GetIconAsync_IsServedFromCache_OnSecondCall()
    {
        var service = Create();

        var first = await service.GetIconAsync(@"C:\Windows\System32\cmd.exe", 64);
        var second = await service.GetIconAsync(@"C:\Windows\System32\cmd.exe", 64);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task ClearCache_ForcesReextraction()
    {
        var service = Create();

        var first = await service.GetIconAsync(@"C:\Windows\System32\cmd.exe", 64);
        service.ClearCache();
        var second = await service.GetIconAsync(@"C:\Windows\System32\cmd.exe", 64);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task GetIconAsync_ResolvesShortcutTarget()
    {
        // 开始菜单里几乎人人都有一个「命令提示符」，用它验证 .lnk → 目标 → 图标 的解析链。
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        var lnk = Directory.Exists(startMenu)
            ? Directory.GetFiles(startMenu, "*.lnk", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (lnk is null)
        {
            return;
        }

        var icon = await Create().GetIconAsync(lnk, 48);

        Assert.NotNull(icon);
    }

    [Fact]
    public async Task GetIconAsync_FallsBackToShellIcon_ForDirectory()
    {
        // 目录没有 PE 资源，必须由 SHGetFileInfo 兜底，否则启动台/面板里这类条目就是一片空白。
        var directory = Path.Combine(Path.GetTempPath(), $"DeskMate.IconTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var icon = await Create().GetIconAsync(directory, 64);

            Assert.NotNull(icon);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task GetIconAsync_FallsBackToShellIcon_ForUrlShortcut()
    {
        var file = Path.Combine(Path.GetTempPath(), $"DeskMate.{Guid.NewGuid():N}.url");
        await File.WriteAllTextAsync(file, "[InternetShortcut]\r\nURL=https://example.com/\r\n");

        try
        {
            var icon = await Create().GetIconAsync(file, 48);

            Assert.NotNull(icon);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task GetIconAsync_LeavesPlaceholder_ForMissingPath()
    {
        // 失效条目必须返回 null 让 UI 画字母占位，而不是给一个看起来很正常的通用图标。
        var icon = await Create().GetIconAsync($@"C:\Totally\Missing\{Guid.NewGuid():N}.exe", 48);

        Assert.Null(icon);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 64)]
    [InlineData(48, 64)]
    [InlineData(64, 64)]
    [InlineData(256, 64)]
    public async Task GetIconAsync_PicksUsableFrame_AndStaysCrossThreadReadable(int requested, int expected)
    {
        // 快捷方式把图标单独指到一个 .ico 文件是很常见的（Qoder、STM32CubeProgrammer 就是这样）。
        // 这类文件一次解码就要在现场做完：以前用 DelayCreation，返回的是「还没解码的帧」，
        // 它背后的 decoder 属于图标线程，UI 线程拿去用时补解码直接抛「调用线程无法访问此对象」，
        // 格子于是整块空白。下面这句 PixelWidth 就是在图标线程之外的线程上读它，出问题先炸在这里。
        // 造的文件里第一档是 16px（真实 .ico 也常这样），所以「照第一张给」会立刻被这组数据抓到。
        var file = WriteIconFile([16, 64]);

        try
        {
            var icon = await Create().GetIconAsync(file, requested);

            Assert.NotNull(icon);
            Assert.True(icon!.IsFrozen);
            Assert.Equal(expected, icon.PixelWidth);
        }
        finally
        {
            // 能删掉才说明解码没把用户的图标文件锁住（程序自己更新图标时不该被 DeskMate 挡住）。
            File.Delete(file);
        }
    }

    /// <summary>
    /// 造一个多尺寸 .ico（每项一档，蓝色通道的值等于该档边长，方便认出取到了哪一档）。
    /// </summary>
    private static string WriteIconFile(int[] sizes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DeskMate.{Guid.NewGuid():N}.ico");

        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            var images = sizes.Select(BuildIconImage).ToArray();

            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);

            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                // 256 在目录项里写成 0。
                var edge = sizes[i] == 256 ? (byte)0 : (byte)sizes[i];
                writer.Write(edge);
                writer.Write(edge);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)images[i].Length);
                writer.Write((uint)offset);

                offset += images[i].Length;
            }

            foreach (var image in images)
            {
                writer.Write(image);
            }
        }

        return path;
    }

    private static byte[] BuildIconImage(int size)
    {
        var maskRowBytes = ((size + 31) / 32) * 4;
        var pixels = size * size * 4;
        var mask = maskRowBytes * size;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((uint)40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)0);
        writer.Write((uint)(pixels + mask));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var i = 0; i < size * size; i++)
        {
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)255);
        }

        for (var i = 0; i < mask; i++)
        {
            writer.Write((byte)0);
        }

        return stream.ToArray();
    }

    public void Dispose() => _sta.Dispose();
}
