using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace DeskMate.Core.Services;

/// <summary>
/// 打开一份 PDF，按页渲染成 PNG 字节。
/// <para>
/// 用的是系统自带的 <c>Windows.Data.Pdf</c>（Win10/11 内置组件，.NET 8 的 WinRT 投影直接能调），
/// 所以既不用装 Acrobat，也不用引 PdfSharp/Pdfium 这类原生 DLL —— 少一个要跟着发布的非托管依赖。
/// </para>
/// <para>
/// 加载走「自己按 <see cref="FileShare.ReadWrite"/> 读成字节 → <see cref="PdfDocument.LoadFromStreamAsync"/>」，
/// 而不是 <c>LoadFromFileAsync</c>：后者会拿着文件句柄不放（投影里 <c>PdfDocument</c> 连 Dispose 都没有），
/// 于是预览开着的期间 Acrobat / 编辑器打不开同一份文件，这跟 DeskMate「只读引用、不锁用户文件」的底线冲突。
/// </para>
/// </summary>
public sealed class PdfPreviewSession
{
    private readonly PdfDocument _document;

    private PdfPreviewSession(PdfDocument document) => _document = document;

    /// <summary>页数（照实报，哪怕超过能翻的上限）。</summary>
    public int PageCount => (int)_document.PageCount;

    /// <summary>能翻到的最后一页（0 基）。页数超过上限时，界面要说明只翻到这里。</summary>
    public int LastRenderableIndex => Math.Min(PageCount, FilePreviewRules.MaxPdfPages) - 1;

    /// <summary>页面是横的还是竖的（界面据此决定初始缩放）。</summary>
    public (double Width, double Height) FirstPageSize { get; private set; }

    /// <summary>打开一份 PDF。文件读不到、不是合法 PDF、或大得离谱时抛，由调用方决定怎么交代。</summary>
    public static async Task<PdfPreviewSession> OpenAsync(string path, CancellationToken token = default)
    {
        var bytes = await ReadWholeFileAsync(path, token);
        var document = await LoadFromBytesAsync(bytes, token);

        var session = new PdfPreviewSession(document);
        using var first = document.GetPage(0);
        session.FirstPageSize = (first.Size.Width, first.Size.Height);

        return session;
    }

    /// <summary>
    /// 渲染一页成 PNG。
    /// </summary>
    /// <param name="index">0 基页号。</param>
    /// <param name="destinationPixels">
    /// 目标宽度（**设备像素**）。调用方给多少就渲多少，这里不再猜倍率 ——
    /// 之前固定 2× 的做法，在 386 DIP 的窄栏里会被 WPF 再缩一次，两次缩放叠在字上就是「糊」。
    /// 界面按「显示宽度 × 系统缩放」算，渲完正好 1:1 落像素。
    /// </param>
    public async Task<byte[]> RenderPageAsync(int index, double destinationPixels, CancellationToken token = default)
    {
        if (index < 0 || index > LastRenderableIndex)
        {
            return [];
        }

        using var page = _document.GetPage((uint)index);
        using var stream = new InMemoryRandomAccessStream();

        // 只给宽，高按页面比例自动算：PDF 有横有竖，两个都定死会把图画歪。
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Clamp(destinationPixels, 1d, 24_000d),
        };

        await page.RenderToStreamAsync(stream, options).AsTask(token);

        return await ReadBackAsync(stream, token);
    }

    private static async Task<byte[]> ReadWholeFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > FilePreviewRules.MaxPdfBytes)
        {
            throw new IOException($"这份 PDF 有 {stream.Length / (1024 * 1024)} MB，超出预览上限，还是用阅读器打开吧。");
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, 0, bytes.Length, token);
        return bytes;
    }

    private static async Task<PdfDocument> LoadFromBytesAsync(byte[] bytes, CancellationToken token)
    {
        using var stream = new InMemoryRandomAccessStream();

        // 用 DataWriter 写：byte[] → IBuffer 的隐式转换在这个投影版本里没开放，别绕。
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(token);
            await writer.FlushAsync().AsTask(token);

            // 不 Detach 的话流还归 writer 管，后面读会拿到空内容。
            writer.DetachStream();
        }

        stream.Seek(0);

        return await PdfDocument.LoadFromStreamAsync(stream).AsTask(token)
            ?? throw new InvalidDataException("这份文件不是合法的 PDF（系统解析器读不出页）。");
    }

    private static async Task<byte[]> ReadBackAsync(IRandomAccessStream stream, CancellationToken token)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = (uint)stream.Size;

        await reader.LoadAsync(size).AsTask(token);

        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
