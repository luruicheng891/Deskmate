using System.Text;
using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// PDF 预览（系统自带的 Windows.Data.Pdf）。这里同时盯两件事：能不能渲出页图，
/// 以及**打开一份 PDF 之后文件还能不能被别人独占写** —— 后者是产品底线，
/// 用 LoadFromFileAsync 那条路就会踩到（所以实现里是自己读字节再 LoadFromStreamAsync）。
/// </summary>
public class PdfPreviewTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task PdfPreview_RendersTheFirstPageToPng()
    {
        var path = WriteMinimalPdf("一页.pdf");

        var session = await PdfPreviewSession.OpenAsync(path);
        var png = await session.RenderPageAsync(0, 900);

        Assert.Equal(1, session.PageCount);
        Assert.True(png.Length > 100);

        // PNG 签名 + IHDR：只验「非空」的话，一张全白图也算过。
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.True(session.FirstPageSize.Width > 0);
    }

    [Fact]
    public async Task PdfPreview_LeavesTheFileFreeForOtherPrograms()
    {
        var path = WriteMinimalPdf("不锁.pdf");

        var session = await PdfPreviewSession.OpenAsync(path);
        Assert.Equal(1, session.PageCount);

        // 预览还开着的时候，Acrobat / 编辑器必须照样能独占打开它。
        await using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task PdfPreview_RefusesToRenderAPageThatIsNotThere()
    {
        var path = WriteMinimalPdf("越界.pdf");

        var session = await PdfPreviewSession.OpenAsync(path);

        Assert.Empty(await session.RenderPageAsync(7, 900));
        Assert.Empty(await session.RenderPageAsync(-1, 900));
    }

    /// <summary>
    /// 手写一份最小的合法 PDF（一页、画几个字）。偏移量按真实字节位置算，
    /// 因为 xref 表错了系统解析器就直接拒载 —— 那正好也是这条测试要防的。
    /// </summary>
    private string WriteMinimalPdf(string name)
    {
        var content = "BT /F1 24 Tf 20 60 Td (DeskMate) Tj ET";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];

        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var start = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n");

        var path = Path.Combine(_temp.Path, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(builder.ToString()));
        return path;
    }
}
