using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 「桌面文件归类」在文件夹面板里的呈现：常用类型标签紧跟「全部」、只列文件不列文件夹、
/// 双击等于在桌面上双击（交给关联程序），并且全程不移动桌面上的文件。
/// </summary>
public class DesktopCategoryTabTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly FakeDesktopFileService _desktop = new();
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;

    public DesktopCategoryTabTests()
    {
        var config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(_folders, _launcher, _desktop, new FakeFileOperations(), NullLogger<FolderManagerViewModel>.Instance);
    }

    [Fact]
    public async Task FrequentTypeTabsSitRightAfterAll_AndCarryTheirCounts()
    {
        _desktop.Add(_temp.File("截图.png")).Add(_temp.File("笔记.pdf")).Add(_temp.File("笔记2.docx"));
        await _folders.AddGroupAsync("课程资料");

        await _viewModel.LoadAsync();

        Assert.Equal(
            ["all", "fav", "desktop:image", "desktop:document", "desktop:archive", "desktop:other", "none"],
            _viewModel.Tabs.Select(tab => tab.Id).Take(7));

        // 数量直接写在标签上，0 个也保留位置，桌面一变空标签就不会突然少一个。
        // 分组也一样带数量（和桌面分类同一套写法），空组一眼就能看出来。
        Assert.Equal(["全部 0", "★ 收藏 0", "图片 1", "文档 2", "压缩包 0"], _viewModel.Tabs.Select(tab => tab.Name).Take(5));
        Assert.Equal("课程资料 0", _viewModel.Tabs.Last().Name);
    }

    [Fact]
    public async Task FavoritesTabCollectsStarredCards_AndShowsThemAsItsOwnView()
    {
        var dir = _temp.File("作业");
        Directory.CreateDirectory(dir);
        var card = (await _folders.AddAsync(dir)).Id;
        await _viewModel.LoadAsync();

        var folderCard = _viewModel.Cards.Single(entry => entry.Id == card);
        Assert.False(folderCard.IsFavorite);

        await _viewModel.ToggleFavoriteAsync(folderCard);
        Assert.True(folderCard.IsFavorite);
        Assert.Contains("★ 收藏 1", _viewModel.Tabs.Select(tab => tab.Name));

        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "fav"));
        Assert.True(_viewModel.IsFavoritesView);
        Assert.False(_viewModel.IsFolderView);
        Assert.Equal(dir, Assert.Single(_viewModel.FavoriteCards).FullPath);
    }

    [Fact]
    public async Task UnfavoritingRestoresTheFolderViewAndEmptiesTheFavoritesTab()
    {
        var dir = _temp.File("作业");
        Directory.CreateDirectory(dir);
        await _folders.AddAsync(dir);
        await _viewModel.LoadAsync();

        var folderCard = _viewModel.Cards.Single();
        await _viewModel.ToggleFavoriteAsync(folderCard);
        await _viewModel.ToggleFavoriteAsync(folderCard);

        Assert.False(folderCard.IsFavorite);
        Assert.Contains("★ 收藏 0", _viewModel.Tabs.Select(tab => tab.Name));
    }

    [Fact]
    public async Task ClickingAFavoriteCardOnlyPreviewsIt_ItTakesADoubleClickToOpen()
    {
        var dir = _temp.File("作业");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "笔记.txt"), "内容");
        await _folders.AddAsync(dir);
        await _viewModel.LoadAsync();
        await _viewModel.ToggleFavoriteAsync(_viewModel.Cards.Single());

        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "fav"));
        var favorite = _viewModel.FavoriteCards.Single();

        _viewModel.PreviewFavorite(favorite);

        Assert.True(_viewModel.IsBrowsing);
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "笔记.txt");
        // 单击只是看：绝不能顺手把资源管理器拉起来
        Assert.Empty(_launcher.Opened);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task ClickingAFavoritedFileShowsItsOwnContent()
    {
        var dir = _temp.File("作业");
        Directory.CreateDirectory(dir);
        var note = Path.Combine(dir, "笔记.txt");
        File.WriteAllText(note, "第一条\n第二条\n");
        await _folders.AddFavoritesAsync([note]);
        await _viewModel.LoadAsync();

        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "fav"));
        _viewModel.PreviewFavorite(_viewModel.FavoriteCards.Single());

        // 单击文件 = 右边看它的内容，**左边还是收藏那一页**（把主区换成它所在的桌面就是用户报的 bug）。
        Assert.True(_viewModel.IsFilePreviewOpen);
        Assert.False(_viewModel.IsBrowsing);
        Assert.Single(_viewModel.FavoriteCards);
        Assert.Empty(_viewModel.PreviewItems);

        var preview = _viewModel.FilePreview!;
        Assert.Equal(note, preview.FullPath);

        for (var tick = 0; tick < 100 && preview.IsBusy; tick++)
        {
            await Task.Delay(20);
        }

        Assert.Contains("第二条", preview.Text);
        Assert.Equal("UTF-8", preview.EncodingName);
    }

    [Fact]
    public async Task PreviewRowsCarryTheirFavoriteState_AndStarringOneStarsEveryOtherCopy()
    {
        var parent = _temp.File("作业");
        var sub = Path.Combine(parent, "第三章");
        Directory.CreateDirectory(sub);
        await _folders.AddAsync(parent);
        await _folders.AddAsync(sub);
        await _viewModel.LoadAsync();

        // 「第三章」既是一张受管文件夹卡片，也是父目录预览里的一行。
        var subCard = _viewModel.Cards.Single(card => card.RealPath == sub);
        _viewModel.PreviewCard(_viewModel.Cards.Single(card => card.RealPath == parent));

        var row = _viewModel.PreviewItems.Single(entry => entry.Path == sub);
        Assert.False(subCard.IsFavorite);
        Assert.False(row.IsFavorite);

        await row.ToggleFavoriteAsync();

        Assert.True(row.IsFavorite);
        Assert.True(subCard.IsFavorite);
        Assert.Contains("★ 收藏 1", _viewModel.Tabs.Select(tab => tab.Name));

        // 再点一下收回：星星状态跟着回去，收藏列表也少一条。
        await row.ToggleFavoriteAsync();
        Assert.False(row.IsFavorite);
        Assert.False(subCard.IsFavorite);
        Assert.Contains("★ 收藏 0", _viewModel.Tabs.Select(tab => tab.Name));
    }

    [Fact]
    public async Task PreviewRowsKnowWhatWasAlreadyFavoritedBeforeThePanelOpened()
    {
        var dir = _temp.File("作业");
        Directory.CreateDirectory(dir);
        var note = Path.Combine(dir, "笔记.txt");
        File.WriteAllText(note, "内容");
        await _folders.AddAsync(dir);
        await _folders.AddFavoritesAsync([note]);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());

        // 星星的初值要在建行时就带上，不能先画成空心再等异步回填。
        Assert.True(_viewModel.PreviewItems.Single(entry => entry.Name == "笔记.txt").IsFavorite);
    }

    [Fact]
    public async Task AFavoritedFileIsNotPaintedAsABrokenFolder()
    {
        var dir = _temp.File("作业");
        Directory.CreateDirectory(dir);
        var note = Path.Combine(dir, "笔记.txt");
        File.WriteAllText(note, "abc");
        await _folders.AddFavoritesAsync([note]);
        await _viewModel.LoadAsync();

        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "fav"));
        var card = _viewModel.FavoriteCards.Single();

        // 详情是后台填的，等到它填完为止（超时就是没填上）。
        for (var tick = 0; tick < 100 && card.MetaText == "读取中…"; tick++)
        {
            await Task.Delay(20);
        }

        Assert.False(card.IsDirectory);
        Assert.True(card.IsValid);
        Assert.Equal("TXT", card.Badge);
        Assert.StartsWith("3 B", card.MetaText);
    }

    [Fact]
    public async Task CreateEntryOverlayValidatesTheNameBeforeLettingItThrough()
    {
        var dir = _temp.File("作业");
        Directory.CreateDirectory(dir);
        await _folders.AddAsync(dir);
        await _viewModel.LoadAsync();
        var card = _viewModel.Cards.Single();

        _viewModel.BeginCreateEntry(card);
        Assert.True(_viewModel.IsCreateEntryOpen);

        _viewModel.CreateEntryText = "a/b";
        Assert.NotEmpty(_viewModel.CreateEntryError);
        Assert.False(_viewModel.CommitCreateEntryCommand.CanExecute(null));

        _viewModel.CreateEntryText = "第三章";
        Assert.Empty(_viewModel.CreateEntryError);
        Assert.Contains("将新建文件夹", _viewModel.CreateEntryPreview);
        Assert.True(_viewModel.CommitCreateEntryCommand.CanExecute(null));

        _viewModel.CreateEntryText = "main.c";
        Assert.Contains("将新建文件", _viewModel.CreateEntryPreview);

        // 自检不能替用户往他目录里放东西，这里只验到「可以建」为止。
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "作业", "第三章")));
        _viewModel.CancelCreateEntry();
        Assert.False(_viewModel.IsCreateEntryOpen);
    }

    [Fact]
    public async Task SelectingACategoryShowsOnlyItsFilesAndHidesFolderCards()
    {
        await _folders.AddAsync(_temp.Path);
        _desktop.Add(_temp.File("a.png")).Add(_temp.File("b.zip")).Add(_temp.File("c.exe"));

        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(Tab("desktop:image"));

        Assert.True(_viewModel.IsDesktopView);
        Assert.False(_viewModel.IsFolderView);
        Assert.Empty(_viewModel.Cards);
        Assert.Equal(["a.png"], _viewModel.DesktopFiles.Select(file => file.Name));
    }

    [Fact]
    public async Task KeywordSearchIsCrossCategory_NotLimitedToTheCurrentTab()
    {
        _desktop.Add(_temp.File("报告 2024.pdf")).Add(_temp.File("报告 2025.pdf")).Add(_temp.File("照片.png"));

        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(Tab("desktop:document"));

        // 搜索词有 150ms 防抖，测试里没有消息循环，直接走防抖到期后的那一步。
        _viewModel.Keyword = "2025";
        _viewModel.RefreshView();

        Assert.True(_viewModel.IsSearchView);
        Assert.Equal(["报告 2025.pdf"], SearchHits(_viewModel).Select(file => file.Name));

        // 打字就是要找东西，不该先问「你现在停在哪个分类上」：停在文档分类里也能搜到图片。
        _viewModel.Keyword = "照片";
        _viewModel.RefreshView();
        Assert.Equal(["照片.png"], SearchHits(_viewModel).Select(file => file.Name));
    }

    private static IEnumerable<DesktopFileItemViewModel> SearchHits(FolderManagerViewModel viewModel)
        => viewModel.SearchHits.OfType<DesktopFileItemViewModel>();

    [Fact]
    public async Task OpeningAFile_HandsItToTheAssociatedProgramAndLeavesItOnTheDesktop()
    {
        var path = _temp.File("课程表.xlsx");
        File.WriteAllText(path, "内容");
        _desktop.Add(path);

        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(Tab("desktop:document"));
        var item = _viewModel.DesktopFiles.Single();

        Assert.Equal("XLSX", item.Badge);
        item.OpenCommand.Execute(null);

        // Launch = 直接打开文件本身；OpenInExplorer 只会跳到所在目录再让人点一次。
        Assert.Equal([path], _launcher.Launched);
        Assert.Empty(_launcher.Opened);
        Assert.Equal("内容", File.ReadAllText(path));
    }

    [Fact]
    public async Task Reveal_OnlyJumpsToTheFileInExplorer()
    {
        _desktop.Add(_temp.File("素材.zip"));

        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(Tab("desktop:archive"));

        _viewModel.DesktopFiles.Single().RevealCommand.Execute(null);

        Assert.Empty(_launcher.Launched);
        Assert.Equal([_temp.File("素材.zip")], _launcher.Opened);
    }

    [Fact]
    public async Task DesktopChange_RefreshesBothCountsAndTheOpenCategory()
    {
        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(Tab("desktop:image"));
        Assert.Empty(_viewModel.DesktopFiles);

        // 监听器在线程池线程上回调，视图模型会把它转回 UI 线程；单测没有消息循环，直接调刷新入口。
        _desktop.Add(_temp.File("新增.png"));
        _viewModel.RefreshView();

        Assert.Equal(["新增.png"], _viewModel.DesktopFiles.Select(file => file.Name));
        Assert.Equal("图片 1", _viewModel.Tabs.Single(tab => tab.Id == "desktop:image").Name);
    }

    [Fact]
    public async Task CategoryTabs_CannotBeDeletedLikeGroups()
    {
        await _folders.AddGroupAsync("课程资料");
        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(Tab("desktop:image"));

        string? notice = null;
        _viewModel.UserNotice += (_, args) => notice = args.Message;
        _viewModel.RequestDeleteGroupCommand.Execute(null);

        Assert.Equal("桌面文件分类是内置视图，不能删除。", notice);
        Assert.Single(_folders.GetGroups());
    }

    [Fact]
    public void DesktopEntryOpensTheOverlayPointedAtTheRealDesktopFolder()
    {
        // 桌面常被重定向（这台机器上就是 E:\Desktop），所以判的是 SpecialFolder 给的那个，不是硬编码 C:\Users\...\Desktop。
        _viewModel.BeginCreateOnDesktopCommand.Execute(null);

        Assert.True(_viewModel.IsCreateEntryOpen);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            _viewModel.CreateEntryDirectory);

        _viewModel.CreateEntryText = "第三章";
        Assert.Contains("将新建文件夹", _viewModel.CreateEntryPreview);
        Assert.Contains(_viewModel.CreateEntryDirectory, _viewModel.CreateEntryPreview);
    }

    [Fact]
    public async Task PreviewEntryPointsAtTheFolderCurrentlyBeingViewed()
    {
        var parent = _temp.File("作业");
        var sub = Path.Combine(parent, "第三章");
        Directory.CreateDirectory(sub);
        await _folders.AddAsync(parent);
        await _viewModel.LoadAsync();

        // 预览没打开时不该凭空弹出一个不知道要建哪的浮层。
        _viewModel.BeginCreateInPreviewCommand.Execute(null);
        Assert.False(_viewModel.IsCreateEntryOpen);

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        _viewModel.EnterFolder(new PreviewEntry(_viewModel, "第三章", sub, true));
        _viewModel.BeginCreateInPreviewCommand.Execute(null);

        Assert.True(_viewModel.IsCreateEntryOpen);
        Assert.Equal(sub, _viewModel.CreateEntryDirectory);
    }

    [Fact]
    public void ChangingTheLocationKeepsTheOverlayOpenAndRewritesThePreview()
    {
        var other = _temp.File("别处");
        Directory.CreateDirectory(other);

        _viewModel.BeginCreateOnDesktop();
        _viewModel.CreateEntryText = "notes";
        _viewModel.SelectCreateEntryType(_viewModel.CreateEntryTypes.First(entry => entry.Option.Key == "txt").Option);
        _viewModel.SetCreateEntryDirectory(other);

        Assert.True(_viewModel.IsCreateEntryOpen);
        Assert.Equal(other, _viewModel.CreateEntryDirectory);
        Assert.Contains("将新建文本文件", _viewModel.CreateEntryPreview);
        Assert.Contains(Path.Combine(other, "notes.txt"), _viewModel.CreateEntryPreview);
    }

    [Fact]
    public void ChoosingATypeSelectsExactlyOnePillAndRewritesThePreview()
    {
        _viewModel.BeginCreateOnDesktop();

        Assert.Equal("folder", _viewModel.SelectedCreateEntryType.Key);
        Assert.Single(_viewModel.CreateEntryTypes, option => option.IsSelected);

        var word = _viewModel.CreateEntryTypes.First(option => option.Option.Key == "docx");
        word.IsSelected = true;
        _viewModel.CreateEntryText = "报告";

        Assert.Equal("docx", _viewModel.SelectedCreateEntryType.Key);
        Assert.Single(_viewModel.CreateEntryTypes, option => option.IsSelected);
        Assert.Same(word, _viewModel.CreateEntryTypes.Single(option => option.IsSelected));
        Assert.Contains("将新建Word 文档：", _viewModel.CreateEntryPreview);
        Assert.EndsWith(Path.Combine(_viewModel.CreateEntryDirectory, "报告.docx"), _viewModel.CreateEntryPreview);

        // 重开浮层要回到默认项，不然上次选的 Word 会悄悄跟着下一次新建。
        _viewModel.CancelCreateEntry();
        _viewModel.BeginCreateOnDesktop();
        Assert.Equal("folder", _viewModel.SelectedCreateEntryType.Key);
    }

    [Fact]
    public void PickingAMissingLocationIsRefusedAndSaysSo()
    {
        _viewModel.BeginCreateOnDesktop();
        var before = _viewModel.CreateEntryDirectory;

        _viewModel.SetCreateEntryDirectory(Path.Combine(_temp.Path, "这个目录不存在"));

        Assert.Equal(before, _viewModel.CreateEntryDirectory);
    }

    [Fact]
    public async Task KeywordSearchCoversFolderCardsAndDesktopFilesTogether()
    {
        var dir = _temp.File("课程资料");
        Directory.CreateDirectory(dir);
        await _folders.AddAsync(dir);
        _desktop.Add(_temp.File("课程表.pdf"));
        await _viewModel.LoadAsync();

        Assert.True(_viewModel.IsFolderView);

        _viewModel.Keyword = "课程";
        _viewModel.RefreshView();

        // 搜索跨两类来源：只筛受管文件夹的话，「我记得桌面上有个课程表」就永远搜不到。
        Assert.True(_viewModel.IsSearchView);
        Assert.False(_viewModel.IsFolderView);
        Assert.Contains(_viewModel.SearchHits, hit => hit is FolderCardViewModel);
        Assert.Contains(_viewModel.SearchHits, hit => hit is DesktopFileItemViewModel);
        Assert.Empty(_viewModel.Cards);

        _viewModel.Keyword = string.Empty;
        _viewModel.RefreshView();
        Assert.False(_viewModel.IsSearchView);
        Assert.Empty(_viewModel.SearchHits);
        Assert.Single(_viewModel.Cards);

        // 清空之后状态栏不能还报着刚才那次搜索的结果。
        Assert.DoesNotContain("搜索「", _viewModel.StatusText);
    }

    [Fact]
    public async Task KeywordSearchSaysSoWhenNothingMatches()
    {
        _desktop.Add(_temp.File("a.png"));
        await _viewModel.LoadAsync();

        _viewModel.Keyword = "绝对不存在的东西xyz";
        _viewModel.RefreshView();

        Assert.True(_viewModel.IsSearchEmpty);
        Assert.Contains("没找到", _viewModel.StatusText);
    }

    [Fact]
    public async Task FavoritesTabKeepsFilteringItsOwnListInsteadOfJumpingToSearch()
    {
        var dir = _temp.File("课程资料");
        Directory.CreateDirectory(dir);
        await _folders.AddAsync(dir);
        await _viewModel.LoadAsync();
        await _viewModel.ToggleFavoriteAsync(_viewModel.Cards.Single());

        _viewModel.SelectTab(_viewModel.Tabs.Single(tab => tab.Id == "fav"));
        _viewModel.Keyword = "课程";
        _viewModel.RefreshView();

        // 收藏页里打字是「在这几条里再筛一下」，不该把人甩到全局搜索结果页。
        Assert.True(_viewModel.IsFavoritesView);
        Assert.False(_viewModel.IsSearchView);
        Assert.Single(_viewModel.FavoriteCards);
    }

    public void Dispose() => _temp.Dispose();

    private GroupTabViewModel Tab(string id) => _viewModel.Tabs.Single(tab => tab.Id == id);

    private sealed class RecordingLauncher : IAppLauncherService
    {
        public List<string> Launched { get; } = new();

        public List<string> Opened { get; } = new();

        public void Launch(string path) => Launched.Add(path);

        public void OpenInExplorer(string path) => Opened.Add(path);

        public void OpenUri(string uri)
        {
        }

        /// <summary>这些替身不关心关联查询，一律回答「有」，让「双击交给系统」那条主路照旧走通。</summary>
        public bool HasDefaultProgram(string path) => true;
    }
}
