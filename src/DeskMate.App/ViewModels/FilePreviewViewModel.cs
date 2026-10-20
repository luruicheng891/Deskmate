using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 一个文件的内容预览。
/// <para>
/// 分派完全由 <see cref="FilePreviewRules.Classify"/> 决定，界面只看 <see cref="Kind"/> 挑一块画：
/// 文本 / Markdown / OOXML 走文字，图片走位图，PDF 逐页位图，压缩包列条目，认不出的写清楚为什么看不了。
/// </para>
/// <para>
/// 所有读盘都在后台线程做，解码完的 <see cref="BitmapSource"/> 一律 <c>Freeze()</c> 再交回界面 ——
/// 不冻的话它是线程亲和的，跨线程一碰就抛（图标服务踩过同一条）。
/// </para>
/// </summary>
public sealed partial class FilePreviewViewModel : ObservableObject
{
    private readonly FolderManagerViewModel _owner;
    private readonly ILogger _logger;
    private readonly string _directory;

    private CancellationTokenSource? _load;
    private PdfPreviewSession? _pdf;
    private IReadOnlyList<string> _siblings = [];
    private int _siblingIndex = -1;

    // PDF 的渲染尺寸要跟着实际显示宽度走：界面在布局好之后报进来。
    private double _pdfViewportDip;
    private double _pdfPixelsPerDip = 1d;
    private bool _pdfRendering;
    private bool _pdfQueued;

    /// <summary>构造内容预览；调用之后要再 <see cref="Load"/> 才会真去读。</summary>
    public FilePreviewViewModel(FolderManagerViewModel owner, ILogger logger, string path)
    {
        _owner = owner;
        _logger = logger;
        FullPath = path;
        Name = Path.GetFileName(path.TrimEnd('\\', '/'));
        _directory = Path.GetDirectoryName(path) ?? string.Empty;
        Kind = FilePreviewRules.Classify(path);

        var info = new FileInfo(path);
        SizeBytes = info.Exists ? info.Length : 0;
        MetaLine = info.Exists
            ? $"{FileText.Size(info.Length)} · {FileText.Modified(info.LastWriteTime)} · {KindLabel}"
            : "文件已经不在了";
    }

    /// <summary>完整路径。</summary>
    public string FullPath { get; }

    /// <summary>文件名。</summary>
    public string Name { get; }

    /// <summary>所在目录，「上一个 / 下一个」就在这层里翻。</summary>
    public string DirectoryPath => _directory;

    /// <summary>该画哪一块。</summary>
    public FilePreviewKind Kind { get; }

    /// <summary>标题行下面那句：大小 · 日期 · 这类文件叫什么。</summary>
    public string MetaLine { get; }

    /// <summary>给标题行用的类型说法。界面上不出现「Unsupported」这种词。</summary>
    public string KindLabel => Kind switch
    {
        FilePreviewKind.Markdown => "Markdown",
        FilePreviewKind.Image => "图片",
        FilePreviewKind.Pdf => "PDF",
        FilePreviewKind.Archive => "压缩包",
        FilePreviewKind.Office => "Office 文档",
        FilePreviewKind.Text => "文本",
        _ => "看不了的格式",
    };

    /// <summary>文件字节数（判「空文件」用）。</summary>
    public long SizeBytes { get; }

    /// <summary>认不出格式时给的那句解释。</summary>
    public string UnsupportedHint => FilePreviewRules.UnsupportedHint(FullPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    [NotifyPropertyChangedFor(nameof(ShowMarkdown))]
    [NotifyPropertyChangedFor(nameof(ShowImage))]
    [NotifyPropertyChangedFor(nameof(ShowPdf))]
    [NotifyPropertyChangedFor(nameof(ShowArchive))]
    [NotifyPropertyChangedFor(nameof(ShowUnsupported))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    [NotifyPropertyChangedFor(nameof(ShowMarkdown))]
    [NotifyPropertyChangedFor(nameof(ShowImage))]
    [NotifyPropertyChangedFor(nameof(ShowPdf))]
    [NotifyPropertyChangedFor(nameof(ShowArchive))]
    [NotifyPropertyChangedFor(nameof(ShowUnsupported))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private string _error = string.Empty;

    /// <summary>文本 / Markdown / OOXML 的正文（Markdown 与 OOXML 也留一份原文，供「看源码」用）。</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Markdown 分好块的段落。</summary>
    [ObservableProperty]
    private IReadOnlyList<MarkdownBlock> _blocks = [];

    /// <summary>图片。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImage))]
    private BitmapSource? _image;

    /// <summary>压缩包条目。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchiveEntries))]
    [NotifyPropertyChangedFor(nameof(ShowArchive))]
    private IReadOnlyList<ArchiveEntryInfo> _entries = [];

    /// <summary>压缩包条目的显示行（把字节数换成「12.3 KB」这种写法）。视图模型算好，模板里就不写转换器了。</summary>
    [ObservableProperty]
    private IReadOnlyList<ArchiveEntryRow> _rows = [];

    /// <summary>PDF 当前页的位图。</summary>
    [ObservableProperty]
    private BitmapSource? _pdfPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    [NotifyPropertyChangedFor(nameof(CanGoPreviousPage))]
    [NotifyPropertyChangedFor(nameof(CanGoNextPage))]
    private int _pageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    [NotifyPropertyChangedFor(nameof(HasPageNav))]
    private int _pageCount;

    /// <summary>实际按哪种编码解的；截断时那句提示也要用它。</summary>
    [ObservableProperty]
    private string _encodingName = string.Empty;

    /// <summary>正文只读了开头一段。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTruncationNote))]
    private bool _isTruncated;

    /// <summary>正在读（界面显示占位话）。</summary>
    public bool IsBusy => IsLoading;

    /// <summary>读失败了：界面显示这句话而不是空白。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>压缩包有没有列出来东西。</summary>
    public bool HasArchiveEntries => Entries.Count > 0;

    // 界面只认这七块开关：每块什么时候露脸集中在这里判，XAML 里就不必再写一遍「Kind 等于什么、
    // 而且没在加载、而且没出错」—— 那种条件写在 XAML 里改一处漏三处。

    /// <summary>画纯文本 / 代码。</summary>
    public bool ShowText => Kind == FilePreviewKind.Text && !IsBusy && !HasError;

    /// <summary>画按块渲染过的文字：Markdown 与 OOXML 共用同一套块渲染（Word 的标题/加粗/表格都落成块）。</summary>
    public bool ShowMarkdown => Kind is FilePreviewKind.Markdown or FilePreviewKind.Office && !IsBusy && !HasError;

    /// <summary>画图片。</summary>
    public bool ShowImage => Kind == FilePreviewKind.Image && !IsBusy && !HasError && Image is not null;

    /// <summary>画 PDF 当前页。</summary>
    public bool ShowPdf => Kind == FilePreviewKind.Pdf && !IsBusy && !HasError;

    /// <summary>画压缩包条目表。</summary>
    public bool ShowArchive => Kind == FilePreviewKind.Archive && !IsBusy && !HasError;

    /// <summary>画「这类看不了，为什么 + 下一步点哪」。</summary>
    public bool ShowUnsupported => Kind == FilePreviewKind.Unsupported && !IsBusy && !HasError;

    /// <summary>画那句读失败的说明。</summary>
    public bool ShowError => HasError && !IsBusy;

    /// <summary>底部那句「只读了开头一段」：只有真的在显示文字、而且确实截断了才出现。</summary>
    public bool ShowsTruncationNote => IsTruncated && ShowText;

    /// <summary>PDF 需不需要给翻页条。</summary>
    public bool HasPageNav => Kind == FilePreviewKind.Pdf && PageCount > 1;

    /// <summary>「第 3 / 41 页」。</summary>
    public string PageLabel => PageCount > 0 ? $"第 {PageIndex + 1} / {PageCount} 页" : "没有可看的页";

    /// <summary>还有上一页可翻。</summary>
    public bool CanGoPreviousPage => Kind == FilePreviewKind.Pdf && PageIndex > 0;

    /// <summary>还有下一页可翻。超过能翻上限时也算到底，按钮不会一直亮着点不动。</summary>
    public bool CanGoNextPage => Kind == FilePreviewKind.Pdf && _pdf is not null && PageIndex < _pdf.LastRenderableIndex;

    /// <summary>同目录里还有前一个文件可翻。</summary>
    public bool CanGoPreviousFile => _siblingIndex > 0;

    /// <summary>同目录里还有后一个文件可翻。</summary>
    public bool CanGoNextFile => _siblingIndex >= 0 && _siblingIndex < _siblings.Count - 1;

    /// <summary>
    /// 交给它同目录的文件列表，「上一个 / 下一个」按这个顺序翻。
    /// 由外面传进来而不是自己再枚举一次：预览栏本来就刚列过这一层，不该为翻一个文件再读一遍盘。
    /// </summary>
    public void SetSiblings(IReadOnlyList<string> files)
    {
        _siblings = files;
        _siblingIndex = files.ToList().FindIndex(candidate => SamePath(candidate, FullPath));

        // 胶囊条解决的就是「看了这个文件，就不知道同层还有什么」：把同层文件摊在内容页顶上，点一下就切。
        // 只画前 200 枚，再多就靠 ◀ ▶ 或回列表翻 —— 一排几百枚胶囊本身也没法看。
        Chips = [.. files.Take(MaxChips).Select(candidate => new FileChipViewModel(
            Path.GetFileName(candidate),
            candidate,
            SamePath(candidate, FullPath)))];

        RaiseNavigationChanged();
    }

    /// <summary>同层文件胶囊（最多 <see cref="MaxChips"/> 枚）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChips))]
    private IReadOnlyList<FileChipViewModel> _chips = [];

    /// <summary>两个以上才值得画胶囊条（只有一枚时它是纯噪声）。</summary>
    public bool HasChips => Chips.Count > 1;

    /// <summary>胶囊条的上限。</summary>
    private const int MaxChips = 200;

    /// <summary>点某枚胶囊 = 切到那个文件的内容。</summary>
    [RelayCommand]
    private void SelectChip(FileChipViewModel? chip)
    {
        if (chip is not null && !SamePath(chip.Path, FullPath))
        {
            _owner.PreviewFile(chip.Path);
        }
    }

    /// <summary>开始读内容。重复调用会取消上一次未完成的读取。</summary>
    public async void Load()
    {
        _load?.Cancel();
        _load = new CancellationTokenSource();
        var token = _load.Token;

        IsLoading = true;
        Error = string.Empty;

        try
        {
            switch (Kind)
            {
                case FilePreviewKind.Text:
                    await LoadTextAsync(token);
                    break;

                case FilePreviewKind.Markdown:
                    await LoadTextAsync(token);
                    Blocks = MarkdownBlocks.Split(Text);
                    break;

                case FilePreviewKind.Office:
                    await LoadOfficeAsync(token);
                    break;

                case FilePreviewKind.Image:
                    await LoadImageAsync(token);
                    break;

                case FilePreviewKind.Pdf:
                    await LoadPdfAsync(token);
                    break;

                case FilePreviewKind.Archive:
                    await LoadArchiveAsync(token);
                    break;

                default:
                    // 认不出格式：不报错，界面走「为什么看不了 + 下一步点哪」那块。
                    Text = string.Empty;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // 用户已经翻到别的文件了，这一版结果丢掉就是正确行为。
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "预览文件失败 {Path}", FullPath);
            Error = ex is IOException or UnauthorizedAccessException
                ? $"读不到这个文件：{ex.Message}"
                : $"这个格式我解不开（{ex.GetType().Name}）。双击它，交给系统默认的程序看。";
            Text = string.Empty;
            Image = null;
            PdfPage = null;
            Entries = [];
            Rows = [];
            Blocks = [];
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// 翻到 PDF 的某一页（或按当前缩放重渲本页）。
    /// <para>
    /// 渲染宽度按「显示宽度 × 系统缩放」算，出来的位图正好 1:1 落在设备像素上。
    /// 固定倍率（以前是 2×）在窄栏里会被 WPF 再缩一次，两次缩放叠在字上就是「糊」。
    /// </para>
    /// </summary>
    public async Task ShowPageAsync(int index)
    {
        if (_pdf is null || index < 0 || index > _pdf.LastRenderableIndex)
        {
            return;
        }

        var pixels = PdfTargetPixels;

        if (pixels <= 0)
        {
            // 还没量到显示宽度（第一次布局之前）：等 SizeChanged 报上来再渲，不要先渲一张糊的。
            return;
        }

        if (_pdfRendering)
        {
            // 拖动窗口的过程中每一帧都会重渲，PDF 渲一页是几十毫秒级的活，会一路排队卡成幻灯片。
            // 只记一笔「最后一次请求要渲什么」，当前这次渲完补一次。
            _pdfQueued = true;
            return;
        }

        _pdfRendering = true;
        var token = _load?.Token ?? CancellationToken.None;
        var pending = index;

        try
        {
            do
            {
                _pdfQueued = false;
                var png = await _pdf.RenderPageAsync(pending, pixels, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                PageIndex = pending;
                PdfPage = Decode(png);
            }
            while (_pdfQueued);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "渲染 PDF 第 {Page} 页失败 {Path}", pending + 1, FullPath);
            Error = $"第 {pending + 1} 页渲染不出来：{ex.Message}";
        }
        finally
        {
            _pdfRendering = false;
        }
    }

    /// <summary>界面报上来：这一页现在实际有多少 DIP 可显示、系统缩放是多少。</summary>
    public void SetPdfViewport(double widthDip, double pixelsPerDip)
    {
        if (Kind != FilePreviewKind.Pdf || widthDip <= 0)
        {
            return;
        }

        if (Math.Abs(widthDip - _pdfViewportDip) < 1 && Math.Abs(pixelsPerDip - _pdfPixelsPerDip) < 0.01)
        {
            return;
        }

        _pdfViewportDip = widthDip;
        _pdfPixelsPerDip = Math.Max(1d, pixelsPerDip);

        OnPropertyChanged(nameof(PdfDisplayWidth));
        _ = ShowPageAsync(PageIndex);
    }

    /// <summary>
    /// 这一页该占多少 DIP 宽。必须显式给界面：横向可滚的 ScrollViewer 会拿无限宽度去量子元素，
    /// 那时 <c>Stretch=Uniform</c> 等于按位图原始像素排，150% 系统缩放下会宽出近一倍、
    /// 刚渲好的 1:1 像素又被 WPF 缩一次 —— 糊回原点。
    /// </summary>
    public double PdfDisplayWidth => Math.Round(_pdfViewportDip * PdfZoom, 1);

    /// <summary>这一页要渲成多少设备像素宽。</summary>
    private double PdfTargetPixels => _pdfViewportDip <= 0 ? 0 : _pdfViewportDip * PdfZoom * _pdfPixelsPerDip;

    /// <summary>缩放：1.0 = 适配栏宽，大于 1 就是放大后横向滚动看细节。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PdfZoomLabel))]
    private double _pdfZoom = 1d;

    /// <summary>给界面显示的那句「150%」。</summary>
    public string PdfZoomLabel => $"{(int)Math.Round(PdfZoom * 100)}%";

    partial void OnPdfZoomChanged(double value)
    {
        OnPropertyChanged(nameof(PdfDisplayWidth));
        _ = ShowPageAsync(PageIndex);
    }

    [RelayCommand]
    private void ZoomIn() => PdfZoom = Math.Min(6d, Math.Round(PdfZoom * 1.25, 3));

    [RelayCommand]
    private void ZoomOut() => PdfZoom = Math.Max(0.5d, Math.Round(PdfZoom / 1.25, 3));

    [RelayCommand]
    private void ZoomFit() => PdfZoom = 1d;

    /// <summary>关掉这份 PDF（换文件或面板收起时调，别让 WinRT 对象一直挂着）。</summary>
    public void ReleasePdf()
    {
        _pdf = null;
    }

    [RelayCommand]
    private void NextPage() => _ = ShowPageAsync(PageIndex + 1);

    [RelayCommand]
    private void PreviousPage() => _ = ShowPageAsync(PageIndex - 1);

    [RelayCommand]
    private void NextFile()
    {
        if (CanGoNextFile)
        {
            _owner.PreviewFile(_siblings[_siblingIndex + 1]);
        }
    }

    [RelayCommand]
    private void PreviousFile()
    {
        if (CanGoPreviousFile)
        {
            _owner.PreviewFile(_siblings[_siblingIndex - 1]);
        }
    }

    /// <summary>重新读一遍（文件被别的程序改完之后点一下就是新的）。</summary>
    [RelayCommand]
    private void Reload() => Load();

    /// <summary>放大成大窗看。</summary>
    [RelayCommand]
    private void Popout() => _owner.RequestFilePopout(this);

    /// <summary>双击 / 「打开」：交给系统关联程序。</summary>
    [RelayCommand]
    private void Open() => _owner.OpenPreviewedFile(this);

    /// <summary>在资源管理器里选中它。</summary>
    [RelayCommand]
    private void Reveal() => _owner.RevealPreviewedFile(this);

    /// <summary>收藏这一条内容对应的路径。</summary>
    [RelayCommand]
    public Task ToggleFavoriteAsync() => _owner.ToggleFavoriteAsync(FullPath, Name);

    private async Task LoadTextAsync(CancellationToken token)
    {
        var preview = await TextPreviewReader.ReadAsync(FullPath, token);

        Text = preview.Text;
        EncodingName = preview.EncodingName;
        IsTruncated = preview.Truncated;
    }

    private async Task LoadOfficeAsync(CancellationToken token)
    {
        var blocks = await OfficeDocumentReader.ReadAsync(FullPath, token);

        if (blocks.Count == 0)
        {
            // 包是开着的但抽不到字：多半是扫描件（图片拼的 docx）或者包结构不合常规。
            Error = "这个文档里没抽到文字（可能是扫描件或纯图片）。双击它用 Office 看。";
            return;
        }

        Blocks = blocks;
        Text = string.Join('\n', blocks.Select(block => block.Text));
        EncodingName = "OOXML";
    }

    private async Task LoadImageAsync(CancellationToken token)
    {
        if (SizeBytes > FilePreviewRules.MaxImageBytes)
        {
            Error = $"这张图有 {FileText.Size(SizeBytes)}，超出预览上限，双击用「照片」看。";
            return;
        }

        var bytes = await ReadAllBytesAsync(FullPath, token);
        Image = Decode(bytes);
    }

    private async Task LoadArchiveAsync(CancellationToken token)
    {
        var entries = await ArchiveLister.ListAsync(FullPath, token);

        Entries = entries;
        Rows = entries.Select(entry => new ArchiveEntryRow(
            entry.FullName,
            entry.IsDirectory ? "目录" : FileText.Size(entry.SizeBytes))).ToList();
    }

    private async Task LoadPdfAsync(CancellationToken token)
    {
        var session = await PdfPreviewSession.OpenAsync(FullPath, token);

        _pdf = session;
        PageCount = session.PageCount;
        OnPropertyChanged(nameof(CanGoNextPage));
        await ShowPageAsync(0);
    }

    private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, 0, bytes.Length, token);
        return bytes;
    }

    /// <summary>
    /// 在后台线程解码并冻住。图片/PNG 的解码放在 UI 线程上，翻一张大图就会明显卡一下；
    /// 冻完之后它就成了跨线程可用的只读对象。
    /// </summary>
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private void RaiseNavigationChanged()
    {
        OnPropertyChanged(nameof(CanGoPreviousFile));
        OnPropertyChanged(nameof(CanGoNextFile));
    }
}

/// <summary>压缩包里的一条条目，已经换成能直接画的文本。</summary>
/// <param name="FullName">包内路径。</param>
/// <param name="SizeText">「12.3 KB」或「目录」。</param>
public sealed record ArchiveEntryRow(string FullName, string SizeText);

/// <summary>
/// 内容页顶部的一枚同层文件胶囊。
/// </summary>
/// <param name="Name">文件名（含扩展名）。</param>
/// <param name="Path">完整路径。</param>
/// <param name="IsCurrent">是不是正在看的那一个（高亮它，用户才知道自己在哪）。</param>
public sealed record FileChipViewModel(string Name, string Path, bool IsCurrent);
