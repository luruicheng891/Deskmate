using System.Text;
using DeskMate.Core.Models;

namespace DeskMate.Core.Services;

/// <summary>
/// 读文本 / 代码内容。两条硬要求都写在实现里：
/// <list type="number">
/// <item><description>不能锁文件：用户的 .c 正开在 Keil 里、log 正在被写的时候也要能预览，所以一律 <see cref="FileShare.ReadWrite"/>。</description></item>
/// <item><description>不能猜编码猜到乱码：BOM → 严格 UTF-8 → GB18030（中文 Windows 上老工程最常见的就是它）→ Latin-1 兜底，
/// 并且把实际用的编码名带回界面 —— 注释是乱码时用户得知道我们按什么解的。</description></item>
/// </list>
/// </summary>
public static class TextPreviewReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>读文件开头一段作为文本预览。</summary>
    public static async Task<TextPreview> ReadAsync(string path, CancellationToken token = default)
    {
        // CodePages 提供程序只为拿 GB18030；注册是幂等的，放静态构造里最合适。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var total = stream.Length;
        var take = (int)Math.Min(total, FilePreviewRules.MaxTextBytes);
        var buffer = new byte[take];

        await stream.ReadExactlyAsync(buffer, 0, take, token);

        var (text, encodingName) = Decode(buffer);
        var truncated = total > take;

        if (truncated)
        {
            // 截在半个汉字 / 半行上是很难看的：只保留到最后一条完整行。
            var cut = text.LastIndexOf('\n');
            if (cut >= 0)
            {
                text = text[..cut];
            }
        }

        return new TextPreview(text.TrimEnd('\r', '\n'), encodingName, total, truncated);
    }

    /// <summary>
    /// 按 BOM / UTF-8 / GB18030 / Latin-1 的顺序试。Latin-1 永远不失败（每字节一个字符），
    /// 所以这一步之后不存在「解不出来」，只存在「解出来不是人话」，那由界面提示编码名来交代。
    /// </summary>
    private static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (StrictUtf8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes), "UTF-16 LE");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes), "UTF-16 BE");
        }

        try
        {
            return (StrictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // 落到 GB18030：GBK 是它的子集，所以一个编码能盖住两种老工程。
        }

        try
        {
            return (Encoding.GetEncoding("GB18030").GetString(bytes), "GB18030");
        }
        catch (ArgumentException)
        {
            // 提供程序没注册上（理论上不会），至少给一版能看全 ASCII 的结果。
        }

        return (Encoding.Latin1.GetString(bytes), "Latin-1");
    }
}
