using DeskMate.Core.Models;

namespace DeskMate.Core.Services;

/// <summary>
/// 文件内容预览的判定规则：哪种扩展名用哪种方式画、各自的上限是多少。
/// <para>
/// 规则集中在这里是为了能单测，也为了让「为什么这个文件看不了」有一句确定的回答。
/// 只列 <c>DeskMate</c> 自己解得动的：PDF 用系统自带的 Windows.Data.Pdf，图片只列 WPF 有解码器的那些，
/// 压缩包只列 zip（rar / 7z 需要第三方解码库，宁可写明看不了也不引依赖）。
/// </para>
/// </summary>
public static class FilePreviewRules
{
    /// <summary>文本一次最多读这么多字节。再多不是读不起，是画出来也没人看。</summary>
    public const long MaxTextBytes = 512 * 1024;

    /// <summary>超过这个大小的图片不解（几万像素的 tif 解码会吃掉几十兆内存）。</summary>
    public const long MaxImageBytes = 64L * 1024 * 1024;

    /// <summary>压缩包最多列这么多条，超了写「还有 N 项」。</summary>
    public const int MaxArchiveEntries = 300;

    /// <summary>PDF 最多允许翻到第几页（页数本身会照实显示）。</summary>
    public const int MaxPdfPages = 2000;

    /// <summary>PDF 整份读进内存的上限。再大就不是「预览」了，该交给阅读器。</summary>
    public const long MaxPdfBytes = 128L * 1024 * 1024;

    /// <summary>Markdown 只渲染前这么多块：一份几万行的 md 全渲染会把 UI 线程钉住。</summary>
    public const int MaxMarkdownBlocks = 800;

    private static readonly string[] TextExtensions =
    [
        "txt", "log", "csv", "tsv", "json", "jsonc", "xml", "config", "ini", "cfg", "conf", "properties",
        "yml", "yaml", "toml", "env", "editorconfig", "md", "markdown",
        "c", "h", "cpp", "cxx", "cc", "hpp", "hh", "cs", "java", "kt", "kts", "gradle", "py", "js", "ts",
        "jsx", "tsx", "vue", "go", "rs", "rb", "php", "lua", "pl", "swift", "m", "mm",
        "s", "asm", "ld", "icf", "sct", "lin", "map", "lst", "def", "inc", "dts", "dtsi",
        "html", "htm", "css", "scss", "less", "svg",
        "bat", "cmd", "ps1", "psm1", "sh", "bash", "zsh", "fish", "vbs", "ahk",
        "sql", "mdx", "tex", "bib", "st", "step", "iges", "igs",
        "uvprojx", "uvoptx", "uvproj", "uvopt", "ioc", "ioclock", "prd", "xcl",
        "cmake", "mk", "make", "patch", "diff", "lock", "bun", "nuspec", "resx", "settings",
    ];

    private static readonly string[] ImageExtensions =
    [
        // 只列 WPF 自带解码器认得的：webp / heic / avif 交给系统看，我们解不动。
        "png", "jpg", "jpeg", "gif", "bmp", "dib", "ico", "tif", "tiff", "jxl",
    ];

    private static readonly string[] MarkdownExtensions = ["md", "markdown", "mdown", "mkd"];

    private static readonly string[] PdfExtensions = ["pdf"];

    /// <summary>OOXML：本质是 zip + XML，不用装 Office 就能把正文抽出来。</summary>
    private static readonly string[] OfficeExtensions = ["docx", "xlsx", "pptx", "docm", "xlsm", "pptm"];

    private static readonly string[] ZipExtensions = ["zip", "jar", "war", "apk", "nupkg", "vsix"];

    /// <summary>
    /// 没有扩展名、但基本可以断定是文本的那些名字（Makefile、LICENSE 这类）。
    /// 代码目录里它们出现得很频繁，按「认不出」处理会显得工具不会用。
    /// </summary>
    private static readonly string[] TextFileNames =
    [
        "makefile", "dockerfile", "readme", "license", "licence", "notice", "changelog", "authors",
        "contributing", "install", "copying", "todo", "notes", "cmakelists", "gemfile", "rakefile",
        "procfile", "vagrantfile", "justfile", "doxygen", "dotfile", "headers", "sources", "cflags",
    ];

    /// <summary>按路径判定该用哪种方式画。</summary>
    public static FilePreviewKind Classify(string path)
    {
        var name = Path.GetFileName(path ?? string.Empty);
        var extension = DesktopFileRules.ExtensionOf(name);

        if (extension.Length == 0)
        {
            return TextFileNames.Contains(name.ToLowerInvariant()) ? FilePreviewKind.Text : FilePreviewKind.Unsupported;
        }

        if (MarkdownExtensions.Contains(extension))
        {
            return FilePreviewKind.Markdown;
        }

        if (PdfExtensions.Contains(extension))
        {
            return FilePreviewKind.Pdf;
        }

        if (ImageExtensions.Contains(extension))
        {
            return FilePreviewKind.Image;
        }

        if (ZipExtensions.Contains(extension))
        {
            return FilePreviewKind.Archive;
        }

        if (OfficeExtensions.Contains(extension))
        {
            return FilePreviewKind.Office;
        }

        return TextExtensions.Contains(extension) ? FilePreviewKind.Text : FilePreviewKind.Unsupported;
    }

    /// <summary>
    /// 「看不了」时要说的那句话。必须具体到「为什么」和「下一步点哪」，
    /// 只写「不支持此文件」用户只能猜是工具坏了还是文件坏了。
    /// </summary>
    public static string UnsupportedHint(string path)
    {
        var extension = DesktopFileRules.ExtensionOf(Path.GetFileName(path));

        return extension switch
        {
            "rar" or "7z" or "zst" or "xz" or "iso" => "这种压缩包要 WinRAR / 7-Zip 才解得动，DeskMate 不引第三方解码库。双击它就用默认程序打开。",
            "doc" or "xls" or "ppt" or "rtf" or "odt" or "odp" or "epub" =>
                "老式 Office 文档（.doc/.xls/.ppt）是二进制复合文档，DeskMate 不装 Office 也读不出来 —— 存成 .docx/.xlsx/.pptx 就能在这里直接看正文。双击它用 Word / Excel / PPT 打开。",
            "webp" or "heic" or "avif" or "raw" or "cr2" or "nef" or "arw" =>
                "这种图片 WPF 没有解码器（系统有，但要走 COM 组件）。双击它用「照片」看最快。",
            "exe" or "dll" or "sys" or "msi" or "bin" or "hex" or "elf" or "o" or "obj" or "lib" or "axf" =>
                "这是二进制文件，没有能给人看的内容。要看里面的东西请用反汇编 / 烧录工具。",
            _ => extension.Length == 0
                ? "这个文件没有扩展名，判断不出该按哪种方式显示。双击它，交给系统关联的程序。"
                : $"暂不支持 .{extension} 的内容预览。双击它，用系统默认程序打开。",
        };
    }

    /// <summary>这个路径能不能按「有内容可看」的方式预览（决定单击要不要走内容页）。</summary>
    public static bool CanPreview(string path) => Classify(path) != FilePreviewKind.Unsupported;
}
