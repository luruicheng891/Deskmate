using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DeskMate.Core.Interfaces;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 图标提取与内存缓存。所有 Win32/COM 调用排到 <see cref="StaScheduler"/> 上执行，
/// 返回前一律 Freeze，UI 线程即可直接使用而无需再编组。
/// </summary>
public sealed class IconService : IIconService
{
    /// <summary>
    /// 图标资源实际只有这几档尺寸，取图时向上取一档，让 WPF 做大→小的高质量降采样。
    /// 照渲染尺寸原样去要（比如 30px），Shell 只能给一张 32 的原图，悬停放大后就糊了。
    /// </summary>
    private static readonly int[] SizeBuckets = [16, 32, 48, 64, 96, 128, 256];

    private readonly ConcurrentDictionary<string, BitmapSource?> _cache = new();
    private readonly StaScheduler _sta;
    private readonly ILogger<IconService> _logger;

    /// <summary>构造图标服务。</summary>
    public IconService(StaScheduler sta, ILogger<IconService> logger)
    {
        _sta = sta;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<BitmapSource?> GetIconAsync(string path, int size = 64)
    {
        var normalized = Expand(path);
        if (normalized.Length == 0)
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        // 按换算后的物理档位建缓存键：30 和 41 都落在 48 这一档，共用同一张图。
        var pixelSize = ExtractionSizeFor(size);

        // 取不到图标也缓存 null，否则每次刷新界面都会重跑一遍昂贵的 COM 解析。
        var key = $"{pixelSize}|{normalized.ToUpperInvariant()}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        return ExtractAndCacheAsync(normalized, key, pixelSize);
    }

    /// <inheritdoc />
    public void ClearCache() => _cache.Clear();

    /// <summary>把「界面里多大」换算成「该去取哪一档」：先乘 DPI，再向上取整到最近的一档。</summary>
    private static int ExtractionSizeFor(int dipSize)
    {
        var needed = Math.Max(16, (int)Math.Ceiling(dipSize * DpiInterop.SystemScale));
        foreach (var bucket in SizeBuckets)
        {
            if (bucket >= needed)
            {
                return bucket;
            }
        }

        return SizeBuckets[^1];
    }

    private async Task<BitmapSource?> ExtractAndCacheAsync(string path, string key, int size)
    {
        var icon = await _sta.RunAsync(() => Extract(path, size));
        _cache[key] = icon;
        return icon;
    }

    private BitmapSource? Extract(string path, int size)
    {
        var source = ResolveIconSource(path);

        if (source is not null && source.Value.File.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            var fromFile = FromIconFile(source.Value.File, size);
            if (fromFile is not null)
            {
                return fromFile;
            }

            // .ico 读不动（文件损坏、被占用）时不能直接留空白格，继续问 Shell 要实际显示的那个。
        }

        // 先按 PE 资源提取：能拿到用户指定尺寸（56/64px），最清晰。
        var hicon = source is null ? IntPtr.Zero : ExtractFromExecutable(source.Value, size);

        // 上面失败的多半是 UWP/商店应用（exe 在 WindowsApps 下，普通权限读不到甚至 File.Exists 都返回 false）、
        // .url 网络快捷方式、以及目标本身是目录这三类。改问 Shell 要资源管理器实际显示的图标；
        // 尺寸通常只有 32px，但远比一格空白好认。路径真的不存在时 Shell 也取不到，正好留给 UI 画字母占位。
        if (hicon == IntPtr.Zero)
        {
            hicon = ShellIcon.ExtractIconHandle(path, size >= 24);
        }

        if (hicon == IntPtr.Zero)
        {
            _logger.LogDebug("图标提取失败：{Path}", path);
            return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                hicon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HICON 转 BitmapSource 失败：{Path}", path);
            return null;
        }
        finally
        {
            // HICON 是 GDI 句柄，不释放会在长时间运行后耗尽进程的句柄配额。
            IconExtractor.DestroyIconHandle(hicon);
        }
    }

    private IntPtr ExtractFromExecutable((string File, int Index) source, int size)
    {
        var hicon = IconExtractor.ExtractIconHandle(source.File, size, source.Index);
        return hicon != IntPtr.Zero
            ? hicon
            // 目标尺寸取不到时逐级降级，宁可小一点也不留空白格。
            : IconExtractor.ExtractIconHandleWithFallback(source.File, size);
    }

    // 快捷方式可能自定义了图标；没有自定义时退回目标 exe，目标也不存在时退回快捷方式本身。
    // 图标位置要拆成「路径 + 序号」两件事：ShellLink.GetIconLocation 已经把它们分开返回了
    // （IconPath = C:\Windows\system32\shell32.dll，IconIndex = -50）。以前只拿 IconPath 去找
    // 字符串里的逗号，路径里没有逗号，索引于是永远当 0 处理 —— 取到的是同一个 DLL 的第一个图标。
    private (string File, int Index)? ResolveIconSource(string path)
    {
        if (ShellLink.IsShortcut(path))
        {
            var resolved = ShellLink.Resolve(path);

            // 快捷方式显式设置了图标位置：直接用它 + 对应的 IconIndex。
            if (!string.IsNullOrWhiteSpace(resolved.IconPath))
            {
                var iconFile = Expand(resolved.IconPath);
                var index = resolved.IconIndex;

                // 负数索引表示倒数，需要换算成 0 基正索引。
                if (index < 0)
                {
                    var total = IconExtractor.CountIcons(iconFile);
                    index = total + index;
                    if (index < 0) index = 0;
                }

                return (iconFile, index);
            }

            // 没有自定义图标：退回目标 exe。
            if (!string.IsNullOrWhiteSpace(resolved.Target))
            {
                return (Expand(resolved.Target), 0);
            }

            return File.Exists(path) ? (path, 0) : null;
        }

        return File.Exists(path) ? (path, 0) : null;
    }

    /// <summary>
    /// 展开环境变量。快捷方式里的目标与图标位置常写成 <c>%SystemRoot%\system32\shell32.dll,-50</c>
    /// 这种未展开形式，不处理就注定取不到图标 —— 启动台部分格子空白正是这个原因。
    /// </summary>
    private static string Expand(string path)
        => Environment.ExpandEnvironmentVariables(path).Trim().Trim('"');

    private BitmapSource? FromIconFile(string iconFile, int size)
    {
        try
        {
            // 先把字节整个读进内存再解码。两个原因：
            // 1) 必须当场解码完（PreservePixelFormat + OnLoad）。以前用 DelayCreation + None，返回的是
            //    「还没解码的帧」，它背后的 decoder 属于这条 STA 线程，UI 线程拿去用时补解码就直接抛
            //    「调用线程无法访问此对象」—— Qoder、STM32CubeProgrammer 这些把图标单独指到 .ico 文件的
            //    快捷方式，格子因此整块空白。
            // 2) 不能让 decoder 去开文件。它会把句柄一直占着，等于 DeskMate 锁住了别人的图标文件，
            //    程序自己更新图标会失败，跑久了还白攒几百个句柄。
            using (var file = File.OpenRead(iconFile))
            {
                var buffered = new MemoryStream();
                file.CopyTo(buffered);
                buffered.Position = 0;

                return DecodeIcon(buffered, iconFile, size);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, ".ico 文件解码失败：{File}", iconFile);
            return null;
        }
    }

    private BitmapSource? DecodeIcon(Stream source, string iconFile, int size)
    {
        var decoder = BitmapDecoder.Create(
            source,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count == 0)
        {
            _logger.LogDebug(".ico 里没有可用帧：{File}", iconFile);
            return null;
        }

        // 一个 .ico 里通常塞了 16/32/48/256 好几档，而文件里的第一张常常是最小的那张。
        // 取「不小于目标尺寸里最小的一档」，全都不够大时退到最大的一档，交给 WPF 降采样。
        var frame = decoder.Frames
            .Where(f => f.PixelWidth >= size)
            .OrderBy(f => f.PixelWidth)
            .FirstOrDefault()
            ?? decoder.Frames
                .OrderByDescending(f => f.PixelWidth)
                .First();

        frame.Freeze();
        return frame;
    }
}
