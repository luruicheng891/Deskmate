using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 预览栏在「列目录」与「看文件内容」之间切换的那段界面逻辑。
/// 这里最要紧的一条是：**单击只是看**，一次启动都不许发生 —— 用户报的原话就是「点一下怎么直接打开了」。
/// </summary>
public class FilePreviewPanelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly FakeDesktopFileService _desktop = new();
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;

    public FilePreviewPanelTests()
    {
        var config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(_folders, _launcher, _desktop, new FakeFileOperations(), NullLogger<FolderManagerViewModel>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private string MakeFolder(params string[] files)
    {
        var root = Path.Combine(_temp.Path, "作业");
        Directory.CreateDirectory(root);

        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(root, name), "第一行\n第二行\n");
        }

        return root;
    }

    private static async Task WaitIdle(FilePreviewViewModel preview)
    {
        for (var tick = 0; tick < 150 && preview.IsBusy; tick++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ComputedPanelTogglesReNotifyOnEveryPreviewChange()
    {
        var root = MakeFolder("笔记.md");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        Assert.False(_viewModel.IsBrowsing);

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        Assert.True(_viewModel.IsBrowsing);

        var changed = new List<string>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        _viewModel.PreviewFile(Path.Combine(root, "笔记.md"));

        // 计算属性必须把依赖方一起通知到。以前 IsPreviewOpen 翻了而不重发那些派生名，
        // 界面就停在第一次求值的结果上（列表一直收着、内容页盖满整栏，自检真就这么红过一次）。
        Assert.Contains(nameof(FolderManagerViewModel.IsFilePreviewOpen), changed);
        Assert.Contains(nameof(FolderManagerViewModel.Breadcrumbs), changed);
        Assert.Contains(nameof(FolderManagerViewModel.BrowseTitle), changed);
    }

    [Fact]
    public async Task PreviewingAFileFromTheListKeepsTheListAndOpensThePane()
    {
        var root = MakeFolder("笔记.md");
        File.WriteAllText(Path.Combine(root, "笔记.md"), "# 第一章\n\n正文一句。\n");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        Assert.True(_viewModel.IsBrowsing);

        _viewModel.PreviewFile(Path.Combine(root, "笔记.md"));

        // 从列表点文件：列表留在原地（两边同时可见），右边开窗格。
        Assert.True(_viewModel.IsBrowsing);
        Assert.True(_viewModel.IsFilePreviewOpen);

        var preview = _viewModel.FilePreview!;
        Assert.Equal(FilePreviewKind.Markdown, preview.Kind);
        await WaitIdle(preview);
        Assert.Equal("Markdown", preview.KindLabel);
        Assert.Contains(preview.Blocks, block => block.Kind == MarkdownBlockKind.Heading && block.Text == "第一章");
        Assert.Contains(_viewModel.PreviewItems, entry => entry.IsSelected);

        // 收起窗格只收窗格：列表与浏览状态都要在。
        _viewModel.CloseContent();
        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.True(_viewModel.IsBrowsing);
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "笔记.md");
    }

    [Fact]
    public async Task PreviewingAnythingNeverLaunchesAnything()
    {
        var root = MakeFolder("说明.txt", "报告.docx", "图.png");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        foreach (var file in Directory.GetFiles(root))
        {
            _viewModel.PreviewFile(file);
            await WaitIdle(_viewModel.FilePreview!);
        }

        Assert.Empty(_launcher.Launched);
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public void AdjacentFileButtonsWalkTheSameFolderOnly()
    {
        var root = MakeFolder("a.txt", "b.txt", "c.txt");
        var elsewhere = Path.Combine(_temp.Path, "别处.txt");
        File.WriteAllText(elsewhere, "不在这一层");

        _viewModel.PreviewFile(Path.Combine(root, "b.txt"));
        var preview = _viewModel.FilePreview!;

        Assert.True(preview.CanGoPreviousFile);
        Assert.True(preview.CanGoNextFile);

        preview.PreviousFileCommand.Execute(null);
        Assert.Equal(Path.Combine(root, "a.txt"), _viewModel.FilePreview!.FullPath);
        Assert.False(_viewModel.FilePreview.CanGoPreviousFile);

        _viewModel.PreviewFile(Path.Combine(root, "c.txt"));
        Assert.False(_viewModel.FilePreview!.CanGoNextFile);
    }

    [Fact]
    public void HiddenAndSystemFilesAreNotSteppedThrough()
    {
        var root = MakeFolder("a.txt", "b.txt");
        var hidden = Path.Combine(root, "秘密.txt");
        File.WriteAllText(hidden, "系统维护的");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        _viewModel.PreviewFile(Path.Combine(root, "a.txt"));
        _viewModel.FilePreview!.NextFileCommand.Execute(null);

        // 翻到一半跳出个 desktop.ini / 隐藏文件，手感就是「翻页会跳空」，所以它们根本不在序列里。
        Assert.Equal(Path.Combine(root, "b.txt"), _viewModel.FilePreview!.FullPath);
        Assert.False(_viewModel.FilePreview.CanGoNextFile);
    }

    [Fact]
    public async Task AFormatWeCannotReadSaysWhyInsteadOfPaintingNothing()
    {
        var root = MakeFolder("固件.bin");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewFile(Path.Combine(root, "固件.bin"));
        var preview = _viewModel.FilePreview!;
        await WaitIdle(preview);

        Assert.Equal(FilePreviewKind.Unsupported, preview.Kind);
        Assert.True(preview.ShowUnsupported);
        Assert.False(preview.ShowText);
        Assert.False(preview.ShowError);
        Assert.Contains("二进制", preview.UnsupportedHint);
    }

    [Fact]
    public void PreviewingAGoneFileSaysSoAndLeavesThePanelAlone()
    {
        string notices = string.Empty;
        _viewModel.UserNotice += (_, e) => notices = e.Message;

        _viewModel.PreviewFile(Path.Combine(_temp.Path, "已经不存在的.txt"));

        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.Contains("打不开", notices);
    }

    [Fact]
    public void PopoutAsksTheWindowForTheSharedViewModel()
    {
        FilePreviewViewModel? requested = null;
        _viewModel.FilePopoutRequested += (_, preview) => requested = preview;

        var root = MakeFolder("笔记.md");
        _viewModel.PreviewFile(Path.Combine(root, "笔记.md"));
        _viewModel.FilePreview!.PopoutCommand.Execute(null);

        // 放大窗和窄栏必须是同一个视图模型，否则一边翻页另一边不动。
        Assert.Same(_viewModel.FilePreview, requested);
    }

    [Fact]
    public void SiblingChipsListTheFolderAndClickingOneSwitches()
    {
        var root = MakeFolder("a.txt", "b.md", "c.pdf");

        _viewModel.PreviewFile(Path.Combine(root, "b.md"));
        var preview = _viewModel.FilePreview!;

        // 胶囊条就是「同层还有什么」的答案：不用先退回列表再找。
        Assert.Equal(["a.txt", "b.md", "c.pdf"], preview.Chips.Select(chip => chip.Name));
        Assert.Equal("b.md", Assert.Single(preview.Chips, chip => chip.IsCurrent).Name);

        preview.SelectChipCommand.Execute(preview.Chips[2]);

        Assert.Equal(Path.Combine(root, "c.pdf"), _viewModel.FilePreview!.FullPath);
        Assert.Equal("c.pdf", Assert.Single(_viewModel.FilePreview.Chips, chip => chip.IsCurrent).Name);
    }

    [Fact]
    public void OnlyOneFileMeansNoChipStrip()
    {
        var root = MakeFolder("独苗.txt");

        _viewModel.PreviewFile(Path.Combine(root, "独苗.txt"));

        // 一排只有一枚的胶囊条是噪声。
        Assert.False(_viewModel.FilePreview!.HasChips);
    }

    [Fact]
    public void GoingBackFromAFileLandsOnItsOwnFolderListFirst()
    {
        var root = MakeFolder("a.txt", "b.txt");

        _viewModel.PreviewFile(Path.Combine(root, "a.txt"));
        Assert.True(_viewModel.IsFilePreviewOpen);

        // 用户报的那条：点收藏 / 分类 / 搜索里的文件，左边不该被换成它所在的文件夹。
        Assert.False(_viewModel.IsBrowsing);
        Assert.Empty(_viewModel.PreviewItems);

        // ← 的第一下只收内容窗格，左边那一页原封不动。
        _viewModel.GoBackPreview();
        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.False(_viewModel.IsBrowsing);
    }

    [Fact]
    public async Task ReturningToTheSameFolderDoesNotRebuildTheList()
    {
        var root = MakeFolder("a.txt", "b.txt");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        var first = _viewModel.PreviewItems.ToList();
        Assert.NotEmpty(first);

        _viewModel.PreviewFile(Path.Combine(root, "a.txt"));
        _viewModel.CloseContent();

        // 同一批实例 = 没重新枚举 = ScrollViewer 的偏移不会被清回顶部。
        Assert.Equal(first, _viewModel.PreviewItems.ToList());
    }

    [Fact]
    public async Task ClosingThePaneDoesNotLeaveADuplicateBackStep()
    {
        var root = MakeFolder("a.txt");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        _viewModel.PreviewFile(Path.Combine(root, "a.txt"));
        _viewModel.CloseContent();

        Assert.True(_viewModel.IsBrowsing);

        // 窗格已经收了，← 就该直接退出浏览，而不是白按一次又列同一层。
        _viewModel.GoBackPreview();
        Assert.False(_viewModel.IsBrowsing);
    }

    [Fact]
    public async Task ClickingTheAlreadySelectedTabStillGoesBackToTheGrid()
    {
        var root = MakeFolder("a.txt");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        var all = _viewModel.Tabs.Single(tab => tab.Id == "all");

        _viewModel.PreviewCard(_viewModel.Cards.Single());

        // 点的是**已经选中**的那个 Tab：SelectedTabId 没变，属性变更通知不会响，
        // 所以退出浏览必须挂在命令上，不能只挂在 OnSelectedTabIdChanged 里。
        Assert.True(_viewModel.IsBrowsing);

        _viewModel.SelectTab(all);

        Assert.False(_viewModel.IsBrowsing);
        Assert.True(_viewModel.IsFolderView);
    }

    [Fact]
    public async Task BreadcrumbsWalkDownToTheCurrentFolder()
    {
        var root = Path.Combine(_temp.Path, "作业");
        var deep = Path.Combine(root, "第三章", "代码");
        Directory.CreateDirectory(deep);

        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        _viewModel.EnterFolder(_viewModel.PreviewItems.Single(entry => entry.Name == "第三章"));
        _viewModel.EnterFolder(_viewModel.PreviewItems.Single(entry => entry.Name == "代码"));

        var crumbs = _viewModel.Breadcrumbs;

        Assert.Equal(deep, _viewModel.BrowsePath);
        Assert.Equal("代码", crumbs[^1].Display);
        Assert.True(crumbs[^1].IsLast);
        Assert.False(crumbs[0].IsLast);
        Assert.Contains(crumbs, crumb => crumb.Display == "第三章");
        Assert.Equal(deep, crumbs[^1].Path);
    }

    [Fact]
    public async Task ClickingABreadcrumbJumpsThereAndBackKeepsWalkingUp()
    {
        var root = Path.Combine(_temp.Path, "作业");
        var deep = Path.Combine(root, "第三章", "代码");
        Directory.CreateDirectory(deep);

        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        _viewModel.EnterFolder(_viewModel.PreviewItems.Single(entry => entry.Name == "第三章"));
        _viewModel.EnterFolder(_viewModel.PreviewItems.Single(entry => entry.Name == "代码"));

        var middle = _viewModel.Breadcrumbs.Single(crumb => crumb.Display == "第三章");

        _viewModel.BrowseToCommand.Execute(middle);

        Assert.Equal(Path.Combine(root, "第三章"), _viewModel.BrowsePath);

        // 跳完再按 ←，应该是继续往上退（到「作业」），而不是被弹回刚才那个深角落。
        _viewModel.GoBackPreview();
        Assert.Equal(root, _viewModel.BrowsePath);

        _viewModel.GoBackPreview();
        Assert.Equal(_temp.Path, _viewModel.BrowsePath);
    }

    [Fact]
    public async Task ADeadBreadcrumbSaysSoInsteadOfPaintingAnEmptyList()
    {
        var root = Path.Combine(_temp.Path, "作业");
        Directory.CreateDirectory(root);

        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());

        string? notice = null;
        _viewModel.UserNotice += (_, e) => notice = e.Message;

        _viewModel.BrowseToCommand.Execute(new BreadcrumbItem("已经不在了", Path.Combine(_temp.Path, "已经不在了"), true));

        Assert.NotNull(notice);
        Assert.Equal(root, _viewModel.BrowsePath);
    }

    [Fact]
    public async Task PreviewingAClassifiedFileLeavesThatCategoryOnTheLeft()
    {
        // 用户说的「其他的也是一样（图片、文档、压缩包）」：换 Tab 也一样不该被顶掉。
        var shot = _temp.File("截图.png");
        File.WriteAllText(shot, "x");
        _desktop.Add(shot);
        await _viewModel.LoadAsync();

        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "desktop:image"));
        Assert.Single(_viewModel.DesktopFiles);

        _viewModel.PreviewFile(shot);

        Assert.True(_viewModel.IsFilePreviewOpen);
        Assert.False(_viewModel.IsBrowsing);
        Assert.Single(_viewModel.DesktopFiles);
        Assert.Empty(_viewModel.PreviewItems);
    }

    [Fact]
    public async Task SwitchingToAnotherCategoryTabDropsThePreview()
    {
        var photo = _temp.File("照片.png");
        File.WriteAllText(photo, "假图片");
        _desktop.Add(photo);
        await _viewModel.LoadAsync();

        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "desktop:image"));
        _viewModel.PreviewFile(photo);
        Assert.True(_viewModel.IsFilePreviewOpen);

        // 用户给的口径：预览是「随用随走」的。切到「压缩包」就已经不在原来那一页了，
        // 右边继续挂着上一张图，就得让人先去点 × —— 那一下不该存在。
        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "desktop:archive"));

        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.Null(_viewModel.FilePreview);
    }

    [Fact]
    public async Task StartingASearchDropsThePreview()
    {
        var root = MakeFolder("笔记.md");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewFile(Path.Combine(root, "笔记.md"));
        Assert.True(_viewModel.IsFilePreviewOpen);

        _viewModel.Keyword = "笔记";

        Assert.False(_viewModel.IsFilePreviewOpen);
    }

    [Fact]
    public async Task EnteringAnotherFolderDropsThePreview()
    {
        var first = MakeFolder("1.jpg");
        var second = Path.Combine(_temp.Path, "实验二");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "2.jpg"), "x");
        await _folders.AddAsync(first);
        await _folders.AddAsync(second);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single(card => card.RealPath == first));
        _viewModel.PreviewFile(Path.Combine(first, "1.jpg"));
        Assert.True(_viewModel.IsFilePreviewOpen);

        _viewModel.PreviewCard(_viewModel.Cards.Single(card => card.RealPath == second));

        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.True(_viewModel.IsBrowsing);
    }

    [Fact]
    public async Task DoubleClickingAFileHandsItToTheSystemAndClosesThePane()
    {
        var root = MakeFolder("1.jpg", "2.jpg");
        var shot = Path.Combine(root, "1.jpg");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        _viewModel.PreviewFile(shot);

        var row = _viewModel.PreviewItems.Single(
            item => item.HasPath && string.Equals(item.Path, shot, StringComparison.OrdinalIgnoreCase));

        _viewModel.OpenPreviewEntry(row);

        Assert.Equal(shot, Assert.Single(_launcher.Launched));

        // 真的交给源程序了 = 这一眼看完了；但左边那一层的列表要留着，用户回来还得接着翻。
        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.True(_viewModel.IsBrowsing);
        Assert.NotEmpty(_viewModel.PreviewItems);
    }

    [Fact]
    public async Task OpeningThePreviewedFileFromThePaneClosesThePane()
    {
        var root = MakeFolder("笔记.md");
        var note = Path.Combine(root, "笔记.md");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        _viewModel.PreviewFile(note);

        _viewModel.OpenPreviewedFile(_viewModel.FilePreview!);

        Assert.Equal(note, Assert.Single(_launcher.Launched));
        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.True(_viewModel.IsBrowsing);
    }

    [Fact]
    public async Task DoubleClickingAFileWithNoAssociationGoesToTheBuiltInViewer()
    {
        var root = MakeFolder("笔记.md");
        var note = Path.Combine(root, "笔记.md");
        File.WriteAllText(note, "# 第一章\n\n正文一句。\n");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        // 这台机器上没给 .md 配默认程序（本机实测：assoc .md 查无此人）。
        _launcher.Association = _ => false;

        var popped = new List<FilePreviewViewModel>();
        _viewModel.FilePopoutRequested += (_, preview) => popped.Add(preview);
        var notices = new List<string>();
        _viewModel.UserNotice += (_, args) => notices.Add(args.Message);

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        var row = _viewModel.PreviewItems.Single(item => item.HasPath && !item.IsDirectory);
        _viewModel.OpenPreviewEntry(row);

        // 交给 shell 只会起一个「你要用什么打开」的面板，用户读到的还是没打开；
        // DeskMate 自己能看，就该自己看给用户看。
        Assert.Empty(_launcher.Launched);
        Assert.True(_viewModel.IsFilePreviewOpen);
        Assert.Equal(note, Assert.Single(popped).FullPath);
        Assert.Contains("内置查看器", Assert.Single(notices));
    }

    [Fact]
    public async Task AnUnopenableFileSaysSoInsteadOfQuietlyDoingNothing()
    {
        var root = MakeFolder("固件.bin");
        var blob = Path.Combine(root, "固件.bin");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _launcher.Association = _ => false;
        var notices = new List<string>();
        _viewModel.UserNotice += (_, args) => notices.Add(args.Message);

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        var row = _viewModel.PreviewItems.Single(item => item.HasPath && !item.IsDirectory);
        _viewModel.OpenPreviewEntry(row);

        // 内置查看器也看不了的东西不装死：说清原因，并给出下一步（去 Windows 里选关联程序）。
        Assert.Empty(_launcher.Launched);
        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.Contains("默认程序", Assert.Single(notices));
    }

    [Fact]
    public async Task DoubleClickingAFileWithAnAssociationStillHandsItToTheSystem()
    {
        var root = MakeFolder("笔记.md");
        var note = Path.Combine(root, "笔记.md");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        var popped = 0;
        _viewModel.FilePopoutRequested += (_, _) => popped++;

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        _viewModel.PreviewFile(note);
        var row = _viewModel.PreviewItems.Single(item => item.HasPath && !item.IsDirectory);

        _viewModel.OpenPreviewEntry(row);

        // 有关联程序（本机 .md → Typora）时不许自作主张改走内置查看器。
        Assert.Equal(note, Assert.Single(_launcher.Launched));
        Assert.Equal(0, popped);
        Assert.False(_viewModel.IsFilePreviewOpen);
    }

    private sealed class RecordingLauncher : IAppLauncherService
    {
        public List<string> Launched { get; } = new();

        public List<string> Opened { get; } = new();

        public void Launch(string path) => Launched.Add(path);

        public void OpenInExplorer(string path) => Opened.Add(path);

        public void OpenUri(string uri)
        {
        }

        /// <summary>默认「有关联程序」；要验没关联时的兜底就改成 <c>_ => false</c>。</summary>
        public Func<string, bool> Association { get; set; } = _ => true;

        public bool HasDefaultProgram(string path) => Association(path);
    }
}
