using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DeskMate.App.Controls;
using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.App.Views;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace DeskMate.App;

/// <summary>
/// 应用入口：单实例守卫 → 日志 → DI 容器 → 配置载入 → 托盘隐藏窗口 → 窗口协调器。
/// 常驻后台，关闭所有窗口不退出进程（ShutdownMode = OnExplicitShutdown）。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "DeskMate.SingleInstance.v1";
    private const string SmokeTestFlag = "--smoke-test";
    private const long LogFileLimitBytes = 5L * 1024 * 1024;

    private Mutex? _instanceLock;
    private ServiceProvider? _container;

    // 自检模式下的异常要变成退出码，不能只留在日志里等人翻。
    private bool _smokeTest;
    private int _smokeTestFailures;

    /// <summary>全局服务定位器，供无法走构造注入的 XAML 生成代码使用。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    // 启动流程里有若干必须等待的 IO，OnStartup 是事件入口，async void 在这里是正当用法。
    protected override async void OnStartup(StartupEventArgs e)
    {
        _instanceLock = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("DeskMate 已在运行，请在系统托盘图标上操作。", "DeskMate",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        WellKnownPaths.EnsureDirectories();
        ConfigureLogging();
        AttachGlobalExceptionHandlers();

        // 常驻托盘：关掉设置一类的窗口不能带走整个进程。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _container = BuildContainer();
        Services = _container;

        var config = _container.GetRequiredService<IConfigService>();
        await config.LoadAsync();
        await _container.GetRequiredService<IUsageService>().LoadAsync();
        _container.GetRequiredService<ThemeService>().ApplyCurrent();

        // 先建隐藏窗口：热键与 WinEvent 钩子都需要一个有消息泵的 HWND。
        _container.GetRequiredService<TrayWindow>().Show();
        var shell = _container.GetRequiredService<AppShell>();
        shell.Initialize();

        _smokeTest = e.Args.Contains(SmokeTestFlag);
        if (_smokeTest)
        {
            await RunSmokeTestAsync();
            // 这个计数把「自检断言失败」和「UI 线程未处理异常」记在一起，文案别再写成「未处理异常」——
            // 上次就是它把人带去查一个根本不存在的异常（那次纯粹是滚轮断言自己写错了）。
            Log.Information("冒烟自检结束，失败项 {Count} 个（断言失败与未处理异常合计）", _smokeTestFailures);
            Shutdown(_smokeTestFailures == 0 ? 0 : 1);
            return;
        }

        await shell.ShowWelcomeAsync(auto: true);

        Log.Information("DeskMate 启动完成，数据目录 {Root}", WellKnownPaths.Root);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_container is not null)
        {
            // 逆序收尾：先收起界面并解绑事件，再释放钩子，最后让容器处置 STA 线程与统计落盘。
            try
            {
                _container.GetRequiredService<AppShell>().Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "退出时收起界面失败");
            }
        }

        HotkeyManager.Detach();
        Log.Information("DeskMate 退出");

        _container?.Dispose();
        Log.CloseAndFlush();
        _instanceLock?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// 自检：逐个创建并显示每个窗口，确认 XAML 解析、控件模板与绑定不会抛异常。
    /// 界面部分没法用单元测试覆盖，实跑一遍比读代码可靠得多。
    /// </summary>
    private async Task RunSmokeTestAsync()
    {
        var services = _container ?? throw new InvalidOperationException("自检必须在容器建好之后运行。");

        Log.Information("冒烟自检开始");


        // Dock 先验：后面的窗口会盖住它，菜单要趁窗口还在前台时打开。
        var dock = services.GetRequiredService<DockWindow>();
        await ShowAndRecord("Dock", () => { });
        VerifyTileMenus(dock, "Dock 图标");
        VerifyCommandBindings(dock, "Dock");
        VerifyMenuStyle(dock, "Dock 图标", "smoke-menu-dock.png");
        await VerifyPreviewCloseAsync(services.GetRequiredService<WindowPreviewWindow>(), services.GetRequiredService<DockViewModel>());
        await VerifyDockDragFeedbackAsync(dock);

        // 遮罩没有按钮，但它同样是分层置顶窗口：确认它能显示、能定位、能收回去。
        // 填充改成后台线程按帧差取了，所以要等一拍再问「画上壁纸了没有」，并看一眼实际节奏。
        var cover = services.GetRequiredService<TaskbarCoverWindow>();
        await ShowAndRecord("任务栏遮罩", () => cover.SetVisibility(true));
        for (var tick = 0; tick < 30 && !cover.IsFilledWithWallpaper; tick++)
        {
            await Task.Delay(50);
        }

        Log.Information(
            "任务栏遮罩：{Fill}，{Timing}",
            cover.IsFilledWithWallpaper ? "填的是壁纸原图" : "退回主题底色",
            cover.BackdropTimingText);
        if (!cover.IsFilledWithWallpaper)
        {
            SmokeFail("任务栏遮罩没填上壁纸原图（屏幕睡着或锁屏时也会这样，先重跑一遍再怀疑代码）");
        }

        cover.SetVisibility(false);

        var launcher = services.GetRequiredService<LauncherWindow>();
        await ShowAndRecord("启动台", () => launcher.ShowLauncher());
        VerifyTileMenus(launcher, "启动台磁贴");
        VerifyCommandBindings(launcher, "启动台");
        VerifyMenuStyle(launcher, "启动台磁贴", "smoke-menu-launcher.png");
        await VerifyLauncherScrollAsync(launcher, services.GetRequiredService<LauncherViewModel>());

        var folders = services.GetRequiredService<FolderManagerWindow>();
        await ShowAndRecord("文件夹管理", () => folders.ShowManager());
        VerifyTileMenus(folders, "文件夹卡片");
        VerifyCommandBindings(folders, "文件夹管理");
        VerifyMenuStyle(folders, "文件夹卡片", "smoke-menu-folder.png");

        // 桌面分类的条目要切过去才会生成容器；绑定路径写错既不编译失败也不抛异常，只能主动验。
        // 四个分类逐个过：某个分类恰好是空的（比如桌面上没有图片）时，模板里的错误也必须被暴露出来。
        var folderVm = services.GetRequiredService<FolderManagerViewModel>();

        // 收藏与新建：星星是这条链唯一的入口，它点不动的话「★ 收藏」页就是永远空的。
        await VerifyFavoritesAndCreateAsync(folders, folderVm);
        await VerifyFavoriteStarsAndPreviewAsync(folders, folderVm);
        await VerifyPanelSearchAsync(folders, folderVm);
        await VerifyBrowseAndContentAsync(folders, folderVm);
        await VerifyFileContentAsync(folders, folderVm, services.GetRequiredService<FilePreviewWindow>());
        await VerifyPreviewFollowsContextAsync(folders, folderVm);
        await VerifyCardColumnsAsync(folders, folderVm);

        foreach (var tab in folderVm.Tabs.Where(tab => DesktopFileRules.IsCategoryTab(tab.Id)).ToList())
        {
            folderVm.SelectTabCommand.Execute(tab);
            folders.UpdateLayout();
            VerifyTileMenus(folders, $"桌面文件 · {tab.Name}");
            VerifyCommandBindings(folders, $"桌面文件归类 · {tab.Name}");
        }

        folderVm.SelectTabCommand.Execute(folderVm.Tabs[0]);

        // 单例窗口被 Close 之后再 Show 会抛 InvalidOperationException（用户点 Dock 的文件夹按钮就踩到了）。
        // 现在关闭改成隐藏，这一步把「打开→关闭→再打开」固化成回归检查。
        await ShowAndRecord("文件夹管理重复开关", () =>
        {
            folders.ShowManager();
            folders.Close();
            folders.ShowManager();
            folders.Hide();
        });

        await VerifyOpenCloseAsync(folders);
        await VerifyOpenCloseRaceAsync(folders);

        var searchWindow = services.GetRequiredService<SearchWindow>();
        await ShowAndRecord("搜索", () => searchWindow.Present());

        // 结果条目的模板只有在真有结果时才会实例化，它上面的右键菜单必须带着结果来验：
        // 绑定路径写错既不编译失败也不抛异常，只会右键点了没反应。
        var searchVm = services.GetRequiredService<SearchViewModel>();
        searchVm.Keyword = "a";
        await searchVm.RunQueryAsync();
        await Task.Delay(400);
        searchWindow.UpdateLayout();
        VerifyTileMenus(searchWindow, "搜索结果");
        VerifyCommandBindings(searchWindow, "搜索");

        // 结果条目真有几类来源、类型标签对不对，只有截图看得出来。
        SaveWindowProof(searchWindow, "smoke-search.png");
        searchVm.Keyword = string.Empty;
        await ShowAndRecord("设置", () => services.GetRequiredService<SettingsWindow>().ShowSettings());

        // 托盘菜单现在背着「搜索」这个兜底入口，菜单项的命令必须逐条验：
        // 绑定路径写错既不编译失败也不抛异常，只会点下去毫无反应。
        VerifyTileMenus(services.GetRequiredService<TrayWindow>(), "托盘菜单");

        // 快捷键注册成功不等于按下去会开出窗口：回调链断在协调器里，用户看到的就是「按了没反应」。
        // 合成按键在这台机器上送不到全局热键（连原有的 Alt+Space 都收不到），所以直接验动作。
        var shell = services.GetRequiredService<AppShell>();
        await VerifyHotkeyActionAsync("搜索", shell.ToggleSearch, services.GetRequiredService<SearchWindow>());
        await VerifyHotkeyActionAsync("启动台", shell.ToggleLauncher, services.GetRequiredService<LauncherWindow>());
        await VerifyHotkeyActionAsync("文件夹面板", shell.ToggleFolderManager, services.GetRequiredService<FolderManagerWindow>());

        // 非模态提示是这个工具唯一的失败/回执出口，它「不显示」和「收不掉」都等于界面坏了。
        var toast = services.GetRequiredService<ToastService>();
        await ShowAndRecord("提示浮层", () => toast.Show("自检：这是一条测试提示"));
        if (!toast.IsShowing)
        {
            SmokeFail("提示浮层推入后没有显示出来");
        }

        SaveToastProof(toast);

        toast.DismissAll();
        if (toast.IsShowing)
        {
            SmokeFail("提示浮层收不掉：DismissAll 之后仍然有内容");
        }


        Log.Information("冒烟自检结束");
    }

    /// <summary>
    /// 把某个窗口连四周 24px 一起截一帧存到日志目录。
    /// 上面的断言只能证明「状态对」，截图才能证明它真的画在屏幕上、内容没画错。
    /// </summary>
    private static void SaveWindowProof(Window window, string fileName)
    {
        if (!window.IsVisible || window.ActualWidth < 1 || window.ActualHeight < 1)
        {
            Log.Warning("自检截图跳过：{Name} 此刻不可见", fileName);
            return;
        }

        // 抓的是屏幕，而 UpdateLayout() 只保证布局走完、不代表已经合成上去：
        // 刚把浮层设为可见就立刻抓，拿到的还是上一帧（自检里就出现过「断言说浮层在，截图里没有」）。
        // 排一次 Render 优先级的空操作，等这一帧真画出来。
        // 抓的是屏幕。注意：在 UI 线程上 Dispatcher.Invoke(..., Render) 是就地执行的，等于没等 ——
        // 必须真的 await 让出一次，那一帧才会画出来。调用方负责在改完可见性之后先让一拍。

        var scale = VisualTreeHelper.GetDpi(window).PixelsPerDip;
        const int margin = 24;

        SaveScreenProof(
            (int)Math.Floor((window.Left - margin) * scale),
            (int)Math.Floor((window.Top - margin) * scale),
            (int)Math.Ceiling((window.ActualWidth + margin * 2) * scale),
            (int)Math.Ceiling((window.ActualHeight + margin * 2) * scale),
            fileName);
    }

    /// <summary>把浮层出现的那一瞬间截一帧存到日志目录。</summary>
    private static void SaveToastProof(ToastService toast)
    {
        if (!toast.TryGetCaptureFrame(out var x, out var y, out var width, out var height))
        {
            Log.Warning("提示浮层截图跳过：拿不到抓屏矩形（可能被全屏应用让路或尚未完成布局）");
            return;
        }

        SaveScreenProof(x, y, width, height, "smoke-toast.png");
    }

    private static void SaveScreenProof(int x, int y, int width, int height, string fileName)
    {
        var shot = ScreenCapture.Grab(x, y, width, height);
        if (shot is null)
        {
            Log.Warning("自检截图失败：{Name} 抓屏返回空（锁屏或安全桌面）", fileName);
            return;
        }

        var path = Path.Combine(WellKnownPaths.LogsFolder, fileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(shot));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        Log.Information("自检截图：{Path}，{Width}x{Height} 物理像素", path, width, height);
    }

    /// <summary>
    /// 开合的现场校验：打开后内容层要真的看得见；关闭要**当场**不在。
    /// <para>
    /// 「当场」这条是新加的：以前关闭走内容层淡出，淡出期间露出来的是窗口自己那块实心底色，
    /// 屏幕上会先留一个「同样大小、里面空空的框」再消失，收尾一旦没走表它就赖着不走。
    /// 现在关闭直接 Hide()，所以不留这一拍也不该看见窗口。
    /// </para>
    /// </summary>
    private async Task VerifyOpenCloseAsync(FolderManagerWindow folders)
    {
        var panel = (FrameworkElement)folders.Content;

        folders.ShowManager();
        await Task.Delay(700);
        if (!folders.IsVisible || panel.Opacity < 0.99)
        {
            SmokeFail($"文件夹管理打开后：可见={folders.IsVisible}，内容不透明度={panel.Opacity:F2}");
        }

        folders.Close();

        if (folders.IsVisible)
        {
            SmokeFail("文件夹管理点关闭后当场还在：关闭又退回成「等动画收尾」了，会留下一个空壳框");
        }
    }

    /// <summary>
    /// 「关掉的同时马上再呼出」这个竞态。用户最初报的「关了还剩一个空壳窗口」就是它：
    /// 淡出收尾只挂在动画 Completed 上，重新呼出时新时钟把旧的换掉，Completed 永远不来。
    /// 现在关闭不再等动画，但这条仍要验 —— 它保证快速开关之后界面是完整的、也关得掉。
    /// </summary>
    private async Task VerifyOpenCloseRaceAsync(FolderManagerWindow folders)
    {
        var panel = (FrameworkElement)folders.Content;

        folders.ShowManager();
        folders.Close();
        folders.ShowManager();
        await Task.Delay(900);

        if (!folders.IsVisible || panel.Opacity < 0.99)
        {
            SmokeFail($"关掉后立刻重新呼出：可见={folders.IsVisible}，内容不透明度={panel.Opacity:F2}");
        }

        folders.Close();

        if (folders.IsVisible)
        {
            SmokeFail("竞态之后窗口关不掉：点关闭之后还留在屏幕上");
        }
    }

    /// <summary>
    /// 单个快捷键入口的现场校验：调它绑定的动作，确认窗口真的出现，再收掉。
    /// 注册成功只说明系统认了这个组合，回调链断在协调器里照样是「按了没反应」。
    /// </summary>

    /// <summary>
    /// 拖拽反馈的现场校验：被拿起的那一枚要变半透明、插入指示线要显示在指定缝隙上。
    /// 合成输入在这台机器上起不了 OLE 拖拽循环，所以改成把那个状态直接摆出来再截图。
    /// </summary>
    private async Task VerifyDockDragFeedbackAsync(DockWindow dock)
    {
        dock.ShowDragFeedbackForSelfCheck(2);
        await Task.Delay(250);

        if (!dock.IsDropIndicatorVisible)
        {
            SmokeFail("拖拽插入指示线没显示出来");
        }

        SaveWindowProof(dock, "smoke-dock-drag.png");
        dock.HideDragFeedbackForSelfCheck();
    }

    /// <summary>
    /// 启动台网格的滚动校验。超过一屏的条目要是滚不动，页面下面那几行就永远点不到 ——
    /// 「已隐藏」页正是靠这里恢复误隐藏的条目，滚不动等于把恢复入口也一起封死。
    /// </summary>
    private async Task VerifyLauncherScrollAsync(LauncherWindow launcher, LauncherViewModel viewModel)
    {
        await VerifyGridScrollAsync(launcher, viewModel, "全部", viewModel.ShowAllTabAsync);

        if (viewModel.HasHidden)
        {
            await VerifyGridScrollAsync(launcher, viewModel, "已隐藏", viewModel.ShowHiddenTabAsync);
        }

        await viewModel.ShowAllTabAsync();
    }

    private async Task VerifyGridScrollAsync(
        LauncherWindow launcher,
        LauncherViewModel viewModel,
        string page,
        Func<Task> switchTab)
    {
        await switchTab();
        launcher.UpdateLayout();

        var scroller = Descendants(launcher.RowsHost).OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is null)
        {
            SmokeFail($"启动台「{page}」网格没有滚动容器：超过一屏的条目点不到");
            return;
        }

        var rows = viewModel.Rows.Count;

        // 逻辑滚动下偏移量的单位是「行」，不是像素：ScrollableHeight 就是还要滚出去几行才看得见最后一行。
        if (scroller.ScrollableHeight <= 0)
        {
            Log.Information("启动台滚动校验 · {Page}：{Rows} 行不满一屏，无需滚动", page, rows);
            return;
        }

        var target = Descendants(launcher.RowsHost).OfType<Button>().FirstOrDefault(element => element.IsVisible);
        if (target is null || Mouse.PrimaryDevice is null)
        {
            Log.Warning("启动台滚动校验 · {Page}：取不到磁贴或鼠标设备，只确认可滚动高度 {Height}", page, scroller.ScrollableHeight);
            return;
        }

        // 先把偏移清回顶部：换页时 ScrollViewer 会留着上一页的偏移，要是已经停在底部，
        // 滚轮推不动是应该的，那条断言就会误报（第一版就是这么假失败过一次）。
        scroller.ScrollToVerticalOffset(0);
        launcher.UpdateLayout();

        var before = scroller.VerticalOffset;

        // 用真正的滚轮事件（从磁贴往上冒泡），而不是直接调 ScrollToVerticalOffset：
        // 要验的是「手放在图标上滚滚轮，网格跟不跟着走」，手动设偏移测不出事件有没有被中间层吃掉。
        for (var notch = 0; notch < 3; notch++)
        {
            target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = target,
            });

            await Task.Delay(30);
        }

        launcher.UpdateLayout();

        if (scroller.VerticalOffset <= before)
        {
            SmokeFail($"启动台「{page}」滚轮滚不动：共 {rows} 行、还能滚 {scroller.ScrollableHeight:F0} 行，滚完后偏移仍是 {scroller.VerticalOffset:F0}");
            return;
        }

        SaveWindowProof(launcher, $"smoke-launcher-scroll-{page}.png");
        Log.Information("启动台滚动校验通过 · {Page}：{Rows} 行，滚轮把偏移从 {Before:F0} 推到 {After:F0}", page, rows, before, scroller.VerticalOffset);
    }

    /// <summary>
    /// 右键菜单必须真的用上主题样式。隐式样式没被认领会静默退回系统那个白底直角框 ——
    /// 既不报错也不影响功能，只有把菜单真打开才看得出来，所以这里边校验边截图。
    /// </summary>
    private void VerifyMenuStyle(DependencyObject root, string area, string proofName)
    {
        var target = Descendants(root)
            .OfType<FrameworkElement>()
            .FirstOrDefault(element => element.ContextMenu is not null);

        if (target?.ContextMenu is not { } menu)
        {
            Log.Warning("{Area}：找不到可打开的右键菜单，跳过样式校验", area);
            return;
        }

        menu.PlacementTarget = target;
        menu.IsOpen = true;
        menu.UpdateLayout();

        var rounded = Descendants(menu)
            .OfType<Border>()
            .Any(border => border.CornerRadius.TopLeft > 0);

        if (!rounded)
        {
            SmokeFail($"{area}：右键菜单没套上主题样式（没有圆角外壳），看起来会是系统默认的白框");
        }
        else
        {
            var origin = menu.PointToScreen(new Point(0, 0));
            var scale = VisualTreeHelper.GetDpi(menu).PixelsPerDip;
            const int pad = 10;

            SaveScreenProof(
                (int)Math.Floor(origin.X) - pad,
                (int)Math.Floor(origin.Y) - pad,
                (int)Math.Ceiling(menu.ActualWidth * scale) + pad * 2,
                (int)Math.Ceiling(menu.ActualHeight * scale) + pad * 2,
                proofName);

            Log.Information("{Area}：右键菜单样式校验通过，截图 {Name}", area, proofName);
        }

        menu.IsOpen = false;
    }

    /// <summary>
    /// 收藏与新建的现场校验。只验到「星星把配置写进去、收藏页画得出卡片、新建浮层判得住名字」，
    /// 不真按「新建」—— 那会在用户自己的目录里落下东西，那部分交给单元测试在临时目录里跑。
    /// </summary>
    private async Task VerifyFavoritesAndCreateAsync(FolderManagerWindow window, FolderManagerViewModel viewModel)
    {
        var favoritesTab = viewModel.Tabs.FirstOrDefault(tab => tab.Name.StartsWith("★ 收藏"));
        if (favoritesTab is null)
        {
            SmokeFail("面板顶部没有「★ 收藏」Tab，收藏功能等于没有入口");
            return;
        }

        // 只挑「还没收藏过」的那张卡片。挑到用户自己收藏过的那一张，这次自检就会顺手把他的收藏取消掉
        // —— 第一版正是这么写的，而且取消完还反过来报「页面没跟上」。
        // 卡片右下角那两颗按钮的 DataContext 就是卡片本身（卡片外壳不是 Button），这样找不用比对字形字符。
        // 必须在还停在文件夹视图时问一句：切到收藏页之后卡片就不在可视树里了。
        if (!Descendants(window).OfType<Button>().Any(button => button.DataContext is FolderCardViewModel))
        {
            SmokeFail("文件夹卡片上找不到星星 / ＋按钮");
        }

        var card = viewModel.Cards.FirstOrDefault(entry => entry.IsValid && !entry.IsFavorite);
        var addedBySelfCheck = card is not null;

        if (addedBySelfCheck)
        {
            await viewModel.ToggleFavoriteAsync(card!);

            if (!card!.IsFavorite)
            {
                SmokeFail($"星星点过了却没记成已收藏（{card.DisplayName}）");
                addedBySelfCheck = false;
                card = null;
            }
        }

        viewModel.SelectTab(favoritesTab);
        window.UpdateLayout();

        if (!viewModel.IsFavoritesView)
        {
            SmokeFail("点了「★ 收藏」Tab 却没切到收藏视图");
        }
        else if (card is not null && !viewModel.FavoriteCards.Any(favorite =>
                     string.Equals(favorite.FullPath, card.RealPath, StringComparison.OrdinalIgnoreCase)))
        {
            SmokeFail("收藏页里没有刚收藏的那一条：星星写了配置，页面却没跟上");
        }
        else
        {
            // 抓的是屏幕矩形：窗口被人盖住就拍到别人的画面，而且改完可见性必须先让出一次 UI 线程才画得出来。
            window.Activate();
            await Task.Delay(250);
            SaveWindowProof(window, "smoke-favorites.png");
            Log.Information(
                "收藏校验通过：收藏页 {Count} 项，本轮自检新增「{Name}」",
                viewModel.FavoriteCards.Count,
                card?.DisplayName ?? "（没有可新增的卡片）");
        }

        // 自检加的那条要拿掉：用户自己收藏的东西一条都不能少。
        if (addedBySelfCheck)
        {
            await viewModel.ToggleFavoriteAsync(card!);
        }

        viewModel.SelectTab(viewModel.Tabs[0]);
        window.UpdateLayout();

        var anyValidCard = viewModel.Cards.FirstOrDefault(entry => entry.IsValid);
        if (anyValidCard is null)
        {
            Log.Warning("自检：当前一个有效文件夹都没有，跳过新建浮层校验");
            return;
        }

        window.UpdateLayout();

        // 新建浮层：只验「打开 / 边输边判 / 收掉」，不提交。
        // 判的是**渲染出来的可见性**而不是视图模型里的布尔 —— 浮层 Visibility 绑的属性名一旦对不上，
        // 绑定是静默失败的，Visibility 会停在默认值 Visible，用户看到的就是「一打开面板就挂着新建框，
        // 点取消也收不掉」，而 VM 状态自始至终是对的，只断言 VM 测不出来。
        if (window.CreateEntryOverlay.IsVisible)
        {
            SmokeFail("新建浮层在没点「＋」的时候就是挂着的（Visibility 没跟着视图模型走）");
        }

        viewModel.BeginCreateEntry(anyValidCard);
        window.UpdateLayout();

        if (!window.CreateEntryOverlay.IsVisible)
        {
            SmokeFail($"点「＋」没弹出新建浮层（{anyValidCard.DisplayName}）");
            return;
        }

        await Task.Delay(250);
        SaveWindowProof(window, "smoke-create-entry.png");

        // 类型胶囊必须真的画出来、并且恰好选中一个：ItemsSource 或 IsChecked 绑定写错都是静默的，
        // 浮层照样弹，只是里面空了或者一个都不亮。
        var pills = Descendants(window)
            .OfType<RadioButton>()
            .Where(element => element.DataContext is NewEntryOptionViewModel)
            .ToList();

        if (pills.Count != NewEntryCatalog.Options.Count)
        {
            SmokeFail($"新建浮层里的类型选项画出了 {pills.Count} 个，应该有 {NewEntryCatalog.Options.Count} 个");
        }
        else if (pills.Count(element => element.IsChecked == true) != 1)
        {
            SmokeFail($"类型选项的勾选状态不对：{pills.Count(element => element.IsChecked == true)} 个亮着，应该只有 1 个");
        }

        viewModel.CreateEntryText = "非法/名字";
        if (viewModel.CreateEntryError.Length == 0)
        {
            SmokeFail("带斜杠的名字没被判出来，建下去会失败或建错位置");
        }

        if (viewModel.CommitCreateEntryCommand.CanExecute(null))
        {
            SmokeFail("名字不合法时「新建」按钮还能按");
        }

        viewModel.CreateEntryText = "自检不提交";
        if (!viewModel.CommitCreateEntryCommand.CanExecute(null))
        {
            SmokeFail("合法名字被误判成不可建，「新建」按钮一直置灰");
        }

        viewModel.CancelCreateEntry();
        window.UpdateLayout();

        if (window.CreateEntryOverlay.IsVisible)
        {
            SmokeFail("新建浮层收不掉：取消之后还挂在屏幕上");
        }

        Log.Information("新建浮层校验通过：默认不显示、点「＋」才出现、取消能收掉、非法名拦住、合法名放行");
    }

    /// <summary>
    /// 面板内搜索的现场校验。两条都是用户报过的：搜索框被工具栏挤窄到点不中，
    /// 以及搜索只筛受管文件夹、桌面上的文件永远搜不出来。
    /// </summary>
    private async Task VerifyPanelSearchAsync(FolderManagerWindow window, FolderManagerViewModel viewModel)
    {
        window.UpdateLayout();

        if (window.SearchBox.ActualWidth < 160)
        {
            SmokeFail($"面板搜索框只有 {window.SearchBox.ActualWidth:F0} DIP 宽，太窄，很难点中");
        }

        var card = viewModel.Cards.FirstOrDefault();
        if (card is null)
        {
            Log.Warning("自检：面板里没有卡片，跳过搜索校验");
            return;
        }

        viewModel.Keyword = card.DisplayName[..Math.Min(3, card.DisplayName.Length)];

        // 关键词有 150ms 防抖，抓屏还要再等一帧真画出来。
        await Task.Delay(300);
        window.UpdateLayout();

        if (!viewModel.IsSearchView)
        {
            SmokeFail("输入了关键词却没切到搜索视图");
        }
        else if (!viewModel.SearchHits.OfType<FolderCardViewModel>().Any())
        {
            SmokeFail($"搜索视图里没有文件夹结果（关键词「{viewModel.Keyword}」明明命中一张卡片）");
        }
        else
        {
            SaveWindowProof(window, "smoke-panel-search.png");
            Log.Information(
                "面板搜索校验通过：搜索框 {Width:F0} DIP 宽，「{Key}」命中 {Hits} 条（其中文件 {Files} 条）",
                window.SearchBox.ActualWidth,
                viewModel.Keyword,
                viewModel.SearchHits.Count,
                viewModel.SearchHits.OfType<DeskMate.App.ViewModels.DesktopFileItemViewModel>().Count());
        }

        viewModel.Keyword = string.Empty;
        await Task.Delay(200);

        // 清空之后状态栏不能还写着刚才那句搜索结果。
        if (viewModel.StatusText.Contains("搜索「", StringComparison.Ordinal))
        {
            SmokeFail($"清空搜索框后状态栏还留着「{viewModel.StatusText}」");
        }
    }

    /// <summary>
    /// 「在哪儿都能收藏 + 收藏页单击先看」。用户报的是两件事：收藏页点一下就打开了外部程序；
    /// 以及除了文件夹卡片以外，桌面文件与预览行根本没有收藏入口。
    /// 后一条是绑定的事 —— 星星画出来了但 Command 解析成 null 是静默的，
    /// 所以查的是「渲染出来的那颗星星手里真的握着命令」，而不是再去调一次视图模型。
    /// </summary>
    private async Task VerifyFavoriteStarsAndPreviewAsync(FolderManagerWindow window, FolderManagerViewModel viewModel)
    {
        var favoritesTab = viewModel.Tabs.FirstOrDefault(tab => tab.Name.StartsWith("★ 收藏"));
        if (favoritesTab is null)
        {
            SmokeFail("面板顶部没有「★ 收藏」Tab");
            return;
        }

        // 自检加进去的收藏最后都要撤掉：用户自己收藏的一条都不能少。
        PreviewEntry? starredRow = null;
        DesktopFileItemViewModel? starredFile = null;
        FolderCardViewModel? starredCard = null;

        var previewed = viewModel.Cards.FirstOrDefault(entry => entry.IsValid);
        if (previewed is not null)
        {
            viewModel.SelectTab(viewModel.Tabs[0]);
            viewModel.PreviewCard(previewed);
            window.UpdateLayout();

            var rowStars = Descendants(window.BrowseList)
                .OfType<Button>()
                .Where(button => button.DataContext is PreviewEntry && button.IsVisible)
                .ToList();

            if (rowStars.Count == 0)
            {
                Log.Warning("自检诊断：条目 {Items}，栏可见 {Panel}，列表可见 {List}，列表里按钮 {Buttons} 个，其中 PreviewEntry {Rows} 个",
                    viewModel.PreviewItems.Count,
                    window.PreviewPanel.IsVisible,
                    window.BrowseList.IsVisible,
                    Descendants(window.BrowseList).OfType<Button>().Count(),
                    Descendants(window.BrowseList).OfType<Button>().Count(button => button.DataContext is PreviewEntry));

                SmokeFail("预览列表里没有画出来的星星：预览行还是不能收藏");
            }
            else
            {
                var target = rowStars.FirstOrDefault(button => ((PreviewEntry)button.DataContext!).HasPath
                    && !((PreviewEntry)button.DataContext!).IsFavorite);

                if (target is null)
                {
                    Log.Warning("自检：预览里 {Count} 行不是路径就是已收藏，跳过预览行收藏校验", rowStars.Count);
                }
                else
                {
                    var entry = (PreviewEntry)target.DataContext;
                    starredRow = entry;
                    await ClickAsync(target);
                    window.UpdateLayout();

                    if (!entry.IsFavorite)
                    {
                        SmokeFail($"预览行的星星点下去没记成已收藏（{entry.Name}）");
                    }
                    else if (!string.Equals(target.Content?.ToString(), entry.StarGlyph, StringComparison.Ordinal))
                    {
                        SmokeFail($"预览行的星星没跟着换成实心（{entry.Name}）：Content 绑的不是 StarGlyph，或者状态通知断了");
                    }
                }
            }
        }

        // 桌面分类里的文件卡片：星星要在卡片画出来的那一页上找，收藏页此时不显示它们。
        foreach (var tab in viewModel.Tabs.Where(tab => DesktopFileRules.IsCategoryTab(tab.Id)))
        {
            viewModel.SelectTab(tab);
            window.UpdateLayout();

            starredFile = viewModel.DesktopFiles.FirstOrDefault(item => !item.IsFavorite);
            var fileStar = Descendants(window)
                .OfType<Button>()
                .FirstOrDefault(button => ReferenceEquals(button.DataContext, starredFile) && button.IsVisible);

            if (starredFile is null || fileStar is null)
            {
                starredFile = null;
                continue;
            }

            await ClickAsync(fileStar);
            window.UpdateLayout();

            if (!starredFile.IsFavorite)
            {
                SmokeFail($"「{tab.Name}」里的文件卡片点了星星没收藏（{starredFile.Name}）");
            }
            else if (!string.Equals(fileStar.Content?.ToString(), starredFile.StarGlyph, StringComparison.Ordinal))
            {
                SmokeFail($"文件卡片的星星没换成实心（{starredFile.Name}）");
            }

            break;
        }

        // 收藏页：单击只弹预览，双击才打开。这里发的是真的 MouseLeftButtonUp，
        // 走的是卡片事件那条路 —— 直接调 PreviewFavorite 测不到「点一下变成打开」那个回归。
        starredCard = viewModel.Cards.FirstOrDefault(entry => entry.IsValid && !entry.IsFavorite);
        if (starredCard is not null)
        {
            await viewModel.ToggleFavoriteAsync(starredCard);
        }

        viewModel.SelectTab(favoritesTab);
        window.UpdateLayout();
        viewModel.ClosePreview();
        window.UpdateLayout();

        var favoriteShell = Descendants(window)
            .OfType<Border>()
            .FirstOrDefault(element => element.DataContext is FavoriteCardViewModel
                && element.IsVisible
                && element.ActualWidth >= 100);

        if (favoriteShell is null)
        {
            Log.Warning("自检：收藏页没画出可点的卡片，跳过单击预览校验");
        }
        else if (Mouse.PrimaryDevice is null)
        {
            Log.Warning("自检：拿不到鼠标设备，跳过单击预览校验");
        }
        else
        {
            // 合成的按下 / 抬起只能从列表那一层往上抛：从卡片外壳直接 RaiseEvent 冒不出去
            // （探针实测：ClickCount 是 1、祖先层挂了 handledEventsToo 的处理器也一个都不响）。
            // OriginalSource 仍然填卡片外壳，和真鼠标点进来时一样，处理器要判的两条都判得到。
            var favoritesHost = Descendants(window)
                .OfType<ItemsControl>()
                .FirstOrDefault(control => ReferenceEquals(control.ItemsSource, viewModel.FavoriteCards));

            if (favoritesHost is null)
            {
                SmokeFail("收藏列表的 ItemsControl 找不到，单击预览这条链验不了");
            }
            else
            {
                favoritesHost.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                    Source = favoriteShell,
                });

                window.UpdateLayout();
                await Task.Delay(150);

                var clicked = (FavoriteCardViewModel)favoriteShell.DataContext;
                var clickedDirectory = Directory.Exists(clicked.FullPath);

                if (!viewModel.IsBrowsing)
                {
                    SmokeFail("单击收藏卡片没有反应：既没进入那个文件夹，也没打开内容窗格");
                }
                else if (clickedDirectory && !SamePath(viewModel.BrowsePath, clicked.FullPath))
                {
                    SmokeFail($"单击收藏的目录没列出它自己（主区现在在「{viewModel.BrowsePath}」）");
                }
                else if (!clickedDirectory && window.PreviewPanel.ActualWidth < 200)
                {
                    SmokeFail("单击收藏的文件没出内容窗格");
                }
                else
                {
                    SaveWindowProof(window, "smoke-favorite-preview.png");
                    Log.Information("收藏校验通过：单击收藏的「{Name}」= {Action}",
                        clicked.Title,
                        clickedDirectory ? "进入它，主区列它内部" : "右边看它的内容");
                }
            }
        }

        // ② 收藏里如果有**文件**：点它只能开右边窗格，左边必须还是收藏那一页。
        // （用户报的就是这个：点收藏里的 md，左边被换成它所在的桌面，收藏列表整个被顶掉。）
        // 上一步点收藏的目录已经进了浏览，这里要先退回收藏页再测 —— 浏览中点别处的文件换层是对的。
        viewModel.ClosePreview();
        viewModel.SelectTab(favoritesTab);
        window.UpdateLayout();

        var favoriteFile = viewModel.FavoriteCards.FirstOrDefault(card => File.Exists(card.FullPath));

        if (favoriteFile is null)
        {
            Log.Warning("自检：收藏里没有文件（只有目录），跳过「点收藏文件不换页」校验");
        }
        else
        {
            viewModel.PreviewFavorite(favoriteFile);
            window.UpdateLayout();

            var stillOnFavorites = Descendants(window).OfType<Border>()
                .Any(element => element.DataContext is FavoriteCardViewModel && element.IsVisible);

            if (viewModel.IsBrowsing)
            {
                SmokeFail($"点收藏里的文件「{favoriteFile.Title}」把主区换成了它所在的文件夹，收藏列表被顶掉了");
            }
            else if (!window.PreviewPanel.IsVisible)
            {
                SmokeFail($"点收藏里的文件「{favoriteFile.Title}」没开内容窗格");
            }
            else if (!stillOnFavorites)
            {
                SmokeFail("内容窗格开着，左边的收藏列表却不见了");
            }
            else
            {
                Log.Information("收藏文件校验通过：点「{Name}」只开右边窗格，左边仍是收藏页", favoriteFile.Title);
            }

            viewModel.CloseContent();
            window.UpdateLayout();
        }

        // ③ 双击要认「手势起点」，不是「此刻光标底下是谁」。第一下点开内容窗格 → 卡片区换了列数 →
        // 卡片挪位置 → 第二下常常落到卡片之间那道缝上，用户看到的就是「双击收藏里的 md 一点反应都没有」。
        // 自检不许启动外部程序，所以只问窗口一句：把第二下的落点换成空白，还认不认得第一下那个条目。
        var gestureShell = Descendants(window.FavoritesHost)
            .OfType<Border>()
            .FirstOrDefault(element => element.DataContext is FavoriteCardViewModel && element.IsVisible);

        if (gestureShell is null)
        {
            Log.Warning("自检：收藏页没画出卡片，跳过双击手势校验");
        }
        else
        {
            // 和真鼠标一样从列表那一层往上抛，OriginalSource 填卡片外壳。
            window.FavoritesHost.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = gestureShell,
            });

            window.UpdateLayout();

            if (!window.DoubleClickKeepsTheGestureItemForSelfCheck())
            {
                SmokeFail("双击认不出手势起点：第二下的落点一挪到空白上就静默没反应（就是「双击 md 没反应」那条）");
            }
            else
            {
                Log.Information(
                    "双击手势校验通过：第二下落点换成空白，仍然认得第一下点中的「{Name}」",
                    ((FavoriteCardViewModel)gestureShell.DataContext!).Title);
            }

            viewModel.ClosePreview();
            window.UpdateLayout();
        }

        // 撤掉自检加的那几条
        if (starredRow is not null)
        {
            await starredRow.ToggleFavoriteAsync();
        }

        if (starredFile is not null)
        {
            await starredFile.ToggleFavoriteAsync();
        }

        if (starredCard is not null)
        {
            await viewModel.ToggleFavoriteAsync(starredCard);
        }

        viewModel.ClosePreview();
        viewModel.SelectTab(viewModel.Tabs[0]);
        window.UpdateLayout();
    }

    /// <summary>
    /// 文件内容预览的现场校验。用户报的是「点一个文件，怎么只列出它旁边的东西」。
    /// 内容页有六块面板 + 一排按钮 + 一个放大窗，全靠绑定串起来，而绑定写错是静默的：
    /// 面板照样在，里面是空的。所以这里按「每一种格式各挑一个真文件」逐个断言真画出了东西。
    /// </summary>
    private async Task VerifyFileContentAsync(
        FolderManagerWindow window,
        FolderManagerViewModel viewModel,
        FilePreviewWindow popout)
    {
        var card = viewModel.Cards.FirstOrDefault(entry => entry.IsValid);

        if (card is null)
        {
            Log.Warning("自检：没有有效卡片，跳过内容预览校验");
            return;
        }

        viewModel.SelectTab(viewModel.Tabs[0]);
        viewModel.PreviewCard(card);
        window.UpdateLayout();

        // 还没点文件的时候，内容页一块都不许画出来：它的开关绑错了对象就会一直盖在目录列表上，
        // 现象是「预览栏一片空白 + 一排按钮」，列表其实还在，只是看不见也点不到。
        if (Descendants(window).OfType<FilePreviewControl>().Any(element => element.IsVisible))
        {
            SmokeFail("没点任何文件，内容页就已经画出来了（Visibility 绑到了自己的 DataContext 上）");
        }

        // 每种格式各找一个真实存在的文件来画：只看一种的话，另外几块的绑定错了也是绿的。
        // Office 再按扩展名拆开（docx / xlsx / pptx 走的是三条不同的解析路径），否则永远只验到先出现的那一种。
        var samples = new Dictionary<string, (FilePreviewKind Kind, string Path)>();

        foreach (var candidate in viewModel.Cards.Where(entry => entry.IsValid))
        {
            viewModel.PreviewCard(candidate);
            window.UpdateLayout();

            foreach (var entry in viewModel.PreviewItems)
            {
                if (!entry.HasPath || entry.IsDirectory)
                {
                    continue;
                }

                var kind = FilePreviewRules.Classify(entry.Path);

                if (kind == FilePreviewKind.Unsupported)
                {
                    continue;
                }

                var key = kind == FilePreviewKind.Office
                    ? $"Office{Path.GetExtension(entry.Path).ToLowerInvariant()}"
                    : kind.ToString();

                if (!samples.ContainsKey(key))
                {
                    samples[key] = (kind, entry.Path);
                }
            }
        }

        if (samples.Count == 0)
        {
            Log.Warning("自检：受管文件夹里一个能看内容的文件都没有，跳过内容预览校验");
            return;
        }

        FilePreviewViewModel? last = null;

        // 截的是屏幕矩形：窗口被人盖住就拍到别人的画面。
        window.Activate();

        foreach (var (key, sample) in samples)
        {
            last = await VerifyOneFileAsync(window, viewModel, sample.Path, sample.Kind, key);
        }

        if (last is null)
        {
            return;
        }

        // 放大窗：必须和窄栏共用同一个视图模型，否则一边翻页另一边不动。
        last.PopoutCommand.Execute(null);
        await Task.Delay(300);
        popout.UpdateLayout();

        if (!popout.IsVisible)
        {
            SmokeFail("点「⤢」没弹出放大窗");
        }
        else if (!ReferenceEquals(popout.DataContext, last))
        {
            SmokeFail("放大窗绑的不是同一份预览");
        }
        else if (popout.ActualWidth < 400)
        {
            SmokeFail($"放大窗只有 {popout.ActualWidth:F0} DIP，放大个寂寞");
        }
        else
        {
            popout.Activate();
            await Task.Delay(250);
            SaveWindowProof(popout, "smoke-file-popout.png");
            Log.Information("放大窗校验通过：{Width:F0}×{Height:F0}，与窄栏共用同一份预览", popout.ActualWidth, popout.ActualHeight);
        }

        popout.Dismiss();

        // 按住空格呼出：这条链最容易坏在「抢焦点」上 —— 一激活，主窗再也收不到 KeyUp，松手关不掉。
        var holdFailure = window.SelfCheckHoldPreview();

        if (holdFailure.Length == 0)
        {
            Log.Information("按住空格预览校验通过：呼出不抢焦点、松手即收");
        }
        else
        {
            SmokeFail($"按住空格「呼出 → 松手关闭」不通：{holdFailure}");
        }

        viewModel.CloseContent();
        window.UpdateLayout();

        // 收起内容窗格只该收窗格：左边的列表要在，而且不能顺手被人换成别的页。
        if (!viewModel.IsBrowsing || !window.BrowseList.IsVisible)
        {
            SmokeFail("收起内容窗格把左边的列表也带走了");
        }

        if (window.PreviewPanel.IsVisible)
        {
            SmokeFail("内容窗格收不掉");
        }

        viewModel.ClosePreview();
        window.UpdateLayout();

        Log.Information("内容预览校验共走过 {Kinds} 种格式：{List}", samples.Count, string.Join("、", samples.Keys));
    }

    /// <summary>把一份文件画出来，并断言它那一块面板真有内容。</summary>
    private async Task<FilePreviewViewModel?> VerifyOneFileAsync(
        FolderManagerWindow window,
        FolderManagerViewModel viewModel,
        string path,
        FilePreviewKind kind,
        string key)
    {
        viewModel.PreviewFile(path);
        var preview = viewModel.FilePreview;

        if (preview is null)
        {
            SmokeFail($"点文件「{Path.GetFileName(path)}」没切到内容页（IsFilePreviewOpen 仍为 false）");
            return null;
        }

        for (var tick = 0; tick < 60 && preview.IsBusy; tick++)
        {
            await Task.Delay(50);
        }

        // PDF 的第一页要等界面报上显示宽度才渲（保证是 1:1 像素），所以这里再等一小会儿。
        for (var tick = 0; tick < 40 && kind == FilePreviewKind.Pdf && preview.PdfPage is null; tick++)
        {
            window.UpdateLayout();
            await Task.Delay(50);
        }

        window.UpdateLayout();

        if (preview.HasError)
        {
            SmokeFail($"读「{preview.Name}」失败：{preview.Error}");
        }

        var control = Descendants(window).OfType<FilePreviewControl>().FirstOrDefault(element => element.IsVisible);

        if (control is null)
        {
            SmokeFail("内容页没画出来：FilePreviewControl 不在可视树里，或开关绑错了属性");
            return null;
        }

        if (!ReferenceEquals(control.DataContext, preview))
        {
            SmokeFail("内容页画出来了，但 DataContext 不是当前这一份预览（放大窗和窄栏会各看各的）");
        }

        if (control.ActualWidth < 100)
        {
            SmokeFail($"内容页宽度只有 {control.ActualWidth:F0} DIP，等于没给地方画");
        }

        // 内容页此刻是真画出来的，那一排按钮的绑定要按「可见即须可用」来判。
        VerifyCommandBindings(control, $"文件内容页 · {preview.KindLabel}");

        var painted = kind switch
        {
            FilePreviewKind.Text
                => Descendants(control).OfType<TextBox>().Any(box => box.IsVisible && box.Text.Length > 0),

            // Markdown 与 Word/PPT/Excel 共用块渲染。这里判两件事：块真画出来了，
            // 而且屏幕上不残留 ** 与 ` 这类标记 —— 留着就是用户说的「没渲染格式」。
            FilePreviewKind.Markdown or FilePreviewKind.Office
                => RenderedBlocksHaveNoRawMarkers(control),

            // 压缩包要连「右边那一列」一起验：绑错属性时名字照样列得出来，只有大小那列是空的。
            FilePreviewKind.Archive
                => Descendants(control).OfType<ItemsControl>().Any(list => list.IsVisible && list.Items.Count > 0)
                   && preview.Rows.Count > 0
                   && Descendants(control).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == preview.Rows[0].SizeText),

            FilePreviewKind.Image
                => Descendants(control).OfType<Image>().Any(image => image.IsVisible && image.ActualHeight > 20),

            FilePreviewKind.Pdf
                => PdfRenderedAtDeviceResolution(control),
            _ => true,
        };

        if (!painted)
        {
            SmokeFail($"「{preview.KindLabel}」这一类没真画出内容（{preview.Name}）");
            return preview;
        }

        // 同层文件胶囊：内容页顶上那一行，解决「看了这个就不知道同层还有什么」。
        // 只验视图模型里的集合是不够的 —— 命令绑在 ItemsControl 的祖先上，写错就是静默 null。
        if (preview.HasChips)
        {
            var chips = Descendants(control).OfType<Button>()
                .Where(button => button.DataContext is FileChipViewModel && button.IsVisible)
                .ToList();

            var current = chips.FirstOrDefault(button => ((FileChipViewModel)button.DataContext!).IsCurrent);
            var other = chips.FirstOrDefault(button => !((FileChipViewModel)button.DataContext!).IsCurrent);

            if (chips.Count == 0)
            {
                SmokeFail("同层文件胶囊没画出来（ItemsSource 或模板的绑定断了）");
            }
            else if (current is null)
            {
                SmokeFail("胶囊里没有一个被标成「当前文件」：高亮绑错就看不出自己在哪");
            }
            else if (other?.Command is null)
            {
                SmokeFail("胶囊的点击命令没解析到（经祖先那条绑定写错了），点它不会切文件");
            }
            else if (other is not null)
            {
                var wanted = ((FileChipViewModel)other.DataContext!).Path;
                await ClickAsync(other);

                var actual = viewModel.FilePreview?.FullPath ?? string.Empty;

                if (!string.Equals(actual.TrimEnd('\\'), wanted.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    SmokeFail($"点了一枚胶囊却没切过去（期望 {Path.GetFileName(wanted)}，实际 {viewModel.FilePreview?.Name ?? "没有预览"}）");
                }
                else
                {
                    SaveWindowProof(window, "smoke-file-chips.png");
                    Log.Information(
                        "同层文件胶囊校验通过：{Count} 枚、当前项有标记，点其中一枚切到了「{Name}」",
                        chips.Count,
                        Path.GetFileName(wanted));
                }

                // 切回去：这一轮后面还要按原文件截图，别把自检自己的现场改了。
                viewModel.PreviewFile(path);
            }
        }

        // 连着抓多张时，上一张的合成还没落屏就会拍到全黑帧：每次截图前让出一帧。
        await Task.Delay(150);
        SaveWindowProof(window, $"smoke-file-content-{key}.png");
        Log.Information(
            "内容预览校验通过：{Name} 按「{Kind}」画在 {Width:F0} DIP 宽的窄栏里",
            preview.Name,
            preview.KindLabel,
            control.ActualWidth);

        return preview;
    }

    /// <summary>
    /// Markdown / Office 的块真画出来了，而且行内标记没被原样印到屏幕上。
    /// 用户报的「没渲染格式」现象就是满屏的 <c>**粗**</c>：转换器一断，星号全露出来。
    /// </summary>
    private static bool RenderedBlocksHaveNoRawMarkers(DependencyObject control)
    {
        var rendered = Descendants(control).OfType<ContentPresenter>().Count(presenter => presenter.Content is TextBlock);

        if (rendered == 0)
        {
            return false;
        }

        var texts = Descendants(control)
            .OfType<ContentPresenter>()
            .Select(presenter => presenter.Content as TextBlock)
            .Where(block => block is not null)
            .Select(block => block!.Text);

        return !texts.Any(text => text.Contains("**", StringComparison.Ordinal));
    }

    /// <summary>
    /// PDF 的清晰度判据：位图宽度必须够铺满「显示宽度 × 系统缩放」。
    /// 只量 <c>ActualHeight</c> 会放过「渲一张小的再让 WPF 拉伸」，那正是「字发糊」的成因。
    /// </summary>
    private static bool PdfRenderedAtDeviceResolution(DependencyObject control)
    {
        var viewport = Descendants(control).OfType<ScrollViewer>().FirstOrDefault(element => element.IsVisible && element.ActualWidth > 60);
        var image = Descendants(control).OfType<Image>().FirstOrDefault(element => element.IsVisible && element.Source is not null);

        if (viewport is null || image?.Source is not BitmapSource bitmap || image.ActualWidth <= 1)
        {
            return false;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(image).PixelsPerDip;

        return bitmap.PixelWidth >= image.ActualWidth * pixelsPerDip * 0.9
            && image.ActualWidth <= viewport.ActualWidth + 1;
    }

    /// <summary>
    /// 预览栏必须是「让位」而不是「盖上去」：它展开时卡片网格要跟着变窄，
    /// 否则右边那一列卡片永远看不见（用户报的就是这个）。
    /// </summary>
    /// <summary>
    /// 资源管理器式两栏的现场校验：单击卡片 = 主区走进这个文件夹，单击文件 = 右边出内容窗格，
    /// 而且**列表与内容必须同时可见** —— 用户报的就是「看了内容就看不到同层还有什么」。
    /// </summary>
    /// <summary>
    /// 「预览随用随走」：换一个 Tab / 开始搜索，右边那块内容窗格必须真的从屏幕上消失，
    /// 而不是只改视图模型的布尔（窗格的开关绑在外层 Grid 上，这类回归只有量渲染结果才抓得住）。
    /// </summary>
    /// <summary>
    /// 卡片区到底排了几列 —— 量的是**屏幕上真实的位置**，不是视图模型里那个宽度数字。
    /// 卡片宽度以前写死，右边内容窗格一开左边就掉回一列、中间空一大片；
    /// 改成按实测宽度分列之后，这条断言才管得住「算对了但没排出来」这种情况。
    /// </summary>
    private static int RenderedColumns(ItemsControl host)
    {
        var panel = Descendants(host).OfType<WrapPanel>().FirstOrDefault(element => element.Children.Count > 0);
        if (panel is null)
        {
            return 0;
        }

        double? firstRowTop = null;
        var columns = 0;

        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement element || !element.IsVisible || element.ActualWidth < 20)
            {
                continue;
            }

            var at = element.TranslatePoint(new Point(0, 0), panel);
            firstRowTop ??= at.Y;

            if (Math.Abs(at.Y - firstRowTop.Value) > 1)
            {
                break; // 已经换到下一行
            }

            columns++;
        }

        return columns;
    }

    private async Task VerifyCardColumnsAsync(FolderManagerWindow window, FolderManagerViewModel viewModel)
    {
        if (viewModel.Cards.Count(entry => entry.IsValid) < 2)
        {
            Log.Warning("自检：有效卡片不足两张，跳过列数校验");
            return;
        }

        viewModel.SelectTab(viewModel.Tabs.First());
        window.UpdateLayout();
        await Task.Delay(200);

        var wide = RenderedColumns(window.CardsHost);
        Log.Information("卡片区（整宽 {Width:F0} DIP）实测排了 {Columns} 列", window.CardsHost.ActualWidth, wide);
        if (wide < 2)
        {
            SmokeFail($"整宽时卡片区只有 {wide} 列，中间那片空白就是用户报的问题");
        }

        // 内容窗格展开后左边变窄，这是最容易掉回一列的场合。
        // 量的是桌面分类那一页（用户截图里的现场）：浏览列表时卡片网格整块让位，没有列可数。
        var documentTab = viewModel.Tabs.FirstOrDefault(tab => tab.Id == "desktop:document");
        var desktopFiles = window.DesktopHost;

        if (documentTab is not null && desktopFiles is not null)
        {
            viewModel.SelectTab(documentTab);
            window.UpdateLayout();
            await Task.Delay(200);

            if (viewModel.DesktopFiles.Count >= 2)
            {
                var first = viewModel.DesktopFiles[0];
                viewModel.PreviewFile(first.Path);
                window.UpdateLayout();
                await Task.Delay(200);

                var narrow = RenderedColumns(desktopFiles);
                Log.Information(
                    "内容窗格开着时桌面分类卡片区（{Width:F0} DIP、{Files} 个文件）排了 {Columns} 列",
                    desktopFiles.ActualWidth,
                    viewModel.DesktopFiles.Count,
                    narrow);

                if (narrow < 2)
                {
                    SmokeFail($"内容窗格展开后卡片区掉回 {narrow} 列（可用宽度 {desktopFiles.ActualWidth:F0} DIP）");
                }

                viewModel.ClosePreview();
            }
            else
            {
                Log.Warning("自检：桌面分类不足两个文件，跳过窄栏列数校验");
            }
        }

        viewModel.SelectTab(viewModel.Tabs.First());
        window.UpdateLayout();
    }

    private async Task VerifyPreviewFollowsContextAsync(FolderManagerWindow window, FolderManagerViewModel viewModel)
    {
        // 挨个卡片试到找到「这一层里有个文件」为止。只挑第一张的话，它恰好只有子目录时
        // 这一步会整段静默不跑，日志里什么都看不出来（这个坑踩过）。
        FolderCardViewModel? source = null;
        PreviewEntry? file = null;

        foreach (var candidate in viewModel.Cards.Where(entry => entry.IsValid))
        {
            viewModel.PreviewCard(candidate);
            file = viewModel.PreviewItems.FirstOrDefault(entry => entry.HasPath && !entry.IsDirectory);
            if (file is not null)
            {
                source = candidate;
                break;
            }
        }

        if (source is null || file is null)
        {
            Log.Warning("自检：受管目录里找不到一个能预览的文件，跳过「预览随用随走」校验");
            return;
        }

        viewModel.SelectTab(viewModel.Tabs.First());
        viewModel.PreviewCard(source);
        viewModel.PreviewFile(file.Path);
        window.UpdateLayout();
        await Task.Delay(150);

        if (!window.PreviewPanel.IsVisible)
        {
            SmokeFail($"点文件「{file.Name}」后内容窗格没出现，后面的换页校验无从谈起");
            return;
        }

        var otherTab = viewModel.Tabs.FirstOrDefault(tab => tab.Id != "all") ?? viewModel.Tabs.First();
        viewModel.SelectTab(otherTab);
        window.UpdateLayout();
        await Task.Delay(150);

        if (viewModel.IsFilePreviewOpen || window.PreviewPanel.IsVisible)
        {
            SmokeFail(
                $"切到「{otherTab.Name}」后内容窗格还挂着上一份预览（视图模型 {viewModel.IsFilePreviewOpen}，屏幕上 {window.PreviewPanel.IsVisible}）");
        }
        else
        {
            Log.Information(
                "预览随用随走校验通过：看「{Name}」时切到「{Tab}」，内容窗格自己收掉了",
                file.Name,
                otherTab.Name);
        }

        // 搜索同理：那一眼也算看完了。
        viewModel.SelectTab(viewModel.Tabs.First());
        viewModel.PreviewCard(source);
        viewModel.PreviewFile(file.Path);
        window.UpdateLayout();
        await Task.Delay(150);

        if (!window.PreviewPanel.IsVisible)
        {
            SmokeFail("重开内容窗格这一步就没画出来，搜索那半条校验没跑");
        }
        else
        {
            viewModel.Keyword = "自检搜索词";
            window.UpdateLayout();
            await Task.Delay(150);

            if (viewModel.IsFilePreviewOpen || window.PreviewPanel.IsVisible)
            {
                SmokeFail("开始搜索后内容窗格还挂着");
            }
            else
            {
                Log.Information("预览随用随走校验通过：一开始搜索，内容窗格就收掉了");
            }
        }

        // 收尾：把现场还给用户（自检不许留下换掉的 Tab 或搜索词）。
        viewModel.Keyword = string.Empty;
        viewModel.SelectTab(viewModel.Tabs.First());
        viewModel.ClosePreview();
        window.UpdateLayout();
    }

    private async Task VerifyBrowseAndContentAsync(FolderManagerWindow window, FolderManagerViewModel viewModel)
    {
        var card = viewModel.Cards.FirstOrDefault(entry => entry.IsValid);
        if (card is null)
        {
            Log.Warning("自检：当前没有有效卡片，跳过两栏布局校验");
            return;
        }

        viewModel.ClosePreview();
        window.UpdateLayout();

        var gridWide = window.CardsScroll.ActualWidth;

        // ① 单击卡片 = 进入它：主区换成这一层的列表，卡片网格整块收起。
        viewModel.PreviewCard(card);
        window.UpdateLayout();
        await Task.Delay(150);

        if (!viewModel.IsBrowsing)
        {
            SmokeFail("单击卡片没进入这个文件夹（IsBrowsing 仍为 false）");
        }

        if (window.CardsScroll.IsVisible)
        {
            SmokeFail("已经进入文件夹了，卡片网格还露在主区");
        }

        if (!window.BrowseList.IsVisible || window.BrowseList.ActualWidth < 200)
        {
            SmokeFail($"这一层的列表没画出来（宽 {window.BrowseList.ActualWidth:F0} DIP）");
        }

        if (window.PreviewPanel.IsVisible)
        {
            SmokeFail("只进了文件夹、还没选文件，右边内容窗格不该出现");
        }

        SaveWindowProof(window, "smoke-browse-list.png");
        Log.Information("进入文件夹校验通过：主区换成「{Name}」的列表（{Width:F0} DIP），卡片网格已收起",
            card.DisplayName,
            window.BrowseList.ActualWidth);

        // ①a 行上的右键菜单（打开 / 显示 / 重命名 / 移到回收站）：命令挂在条目自己身上，
        // 写成「经祖先取窗口命令」就是静默 null、点了没反应 —— 这条以前踩过两次。
        VerifyTileMenus(window.BrowseList, "浏览列表行");

        // ①b 面包屑：每一段都得是真能点的按钮（命令经祖先绑，写错就是静默 null）。
        var crumbs = Descendants(window).OfType<Button>()
            .Where(button => button.DataContext is BreadcrumbItem && button.IsVisible)
            .ToList();

        if (crumbs.Count != viewModel.Breadcrumbs.Count)
        {
            SmokeFail($"面包屑画了 {crumbs.Count} 段，视图模型有 {viewModel.Breadcrumbs.Count} 段");
        }
        else if (crumbs.Any(button => button.Command is null))
        {
            SmokeFail("面包屑有点不到的段（命令没经祖先取到，点了不会换层）");
        }
        else if (crumbs.Count >= 2)
        {
            var deep = viewModel.BrowsePath;

            crumbs[^2].Command!.Execute(crumbs[^2].DataContext);
            window.UpdateLayout();

            if (SamePath(viewModel.BrowsePath, deep))
            {
                SmokeFail($"点了面包屑的上一段却没换层（还在 {viewModel.BrowsePath}）");
            }
            else
            {
                Log.Information("面包屑校验通过：{Count} 段可点，点上一段跳到了「{Name}」",
                    crumbs.Count,
                    Path.GetFileName(viewModel.BrowsePath.TrimEnd('\\', '/')));

                // 回到原来那一层：后面的步骤都在它上面做。
                viewModel.BrowseTo(new BreadcrumbItem("复位", deep, true));
                window.UpdateLayout();
            }
        }

        // ①c 再点一次已经选中的 Tab 就该回主界面（人钻进子文件夹后的第一反应就是这个）。
        var allTab = Descendants(window).OfType<RadioButton>()
            .FirstOrDefault(button => button.DataContext is GroupTabViewModel tab && tab.Id == "all");

        if (allTab?.Command is null)
        {
            SmokeFail("顶部的「全部」Tab 没绑上命令，点它不会回主界面");
        }
        else
        {
            allTab.Command.Execute(allTab.DataContext);
            window.UpdateLayout();

            if (viewModel.IsBrowsing)
            {
                SmokeFail("点了已经选中的「全部」还是没回到卡片网格");
            }
            else
            {
                Log.Information("Tab 回主界面校验通过：点已经选中的「全部」即退出浏览");
            }

            viewModel.PreviewCard(card);
            window.UpdateLayout();
        }

        // ② 单击这一层里的一个文件 = 右边出内容，**列表留在原地**。
        // 第一张卡片可能整层都是子文件夹（一个文件都没有），那就换一张试，别把这条最重要的断言跳过。
        var target = viewModel.PreviewItems.FirstOrDefault(entry =>
            entry.HasPath && !entry.IsDirectory && FilePreviewRules.CanPreview(entry.Path))
            ?? viewModel.PreviewItems.FirstOrDefault(entry => entry.HasPath && !entry.IsDirectory);

        foreach (var candidate in viewModel.Cards.Where(entry => entry.IsValid && !ReferenceEquals(entry, card)))
        {
            if (target is not null)
            {
                break;
            }

            viewModel.PreviewCard(candidate);
            window.UpdateLayout();

            target = viewModel.PreviewItems.FirstOrDefault(entry =>
                entry.HasPath && !entry.IsDirectory && FilePreviewRules.CanPreview(entry.Path))
                ?? viewModel.PreviewItems.FirstOrDefault(entry => entry.HasPath && !entry.IsDirectory);
        }

        if (target is null)
        {
            Log.Warning("自检：「{Name}」里没有文件，跳过「列表与内容同时可见」校验", card.DisplayName);
        }
        else
        {
            viewModel.PreviewFile(target.Path);
            window.UpdateLayout();

            for (var tick = 0; tick < 40 && (viewModel.FilePreview?.IsBusy ?? false); tick++)
            {
                await Task.Delay(50);
            }

            window.UpdateLayout();

            if (!window.BrowseList.IsVisible)
            {
                SmokeFail("点开文件之后左边的列表不见了 —— 用户要的就是列表与内容同时出现");
            }
            else if (!window.PreviewPanel.IsVisible || window.PreviewPanel.ActualWidth < 200)
            {
                SmokeFail($"点开文件没出内容窗格（宽 {window.PreviewPanel.ActualWidth:F0} DIP）");
            }
            else if (window.CardsScroll.IsVisible)
            {
                SmokeFail("浏览时卡片网格又露了出来，主区应该只有这一层的列表");
            }
            else if (viewModel.PreviewItems.FirstOrDefault(entry => entry.IsSelected) is null)
            {
                SmokeFail("列表里没有一行被标成选中：右边那块内容对不上「它是列表里的哪一个」");
            }
            else
            {
                await Task.Delay(250);
                SaveWindowProof(window, "smoke-browse-split.png");
                Log.Information(
                    "两栏同时可见校验通过：列表 {List:F0} DIP + 内容 {Pane:F0} DIP，选中行是「{Name}」",
                    window.BrowseList.ActualWidth,
                    window.PreviewPanel.ActualWidth,
                    viewModel.FilePreview!.Name);
            }

            // ③ 收起内容窗格：列表要在，窗格要走。
            viewModel.CloseContent();
            window.UpdateLayout();

            if (window.PreviewPanel.IsVisible)
            {
                SmokeFail("内容窗格的 × 收不掉");
            }

            if (!window.BrowseList.IsVisible)
            {
                SmokeFail("收起内容窗格把左边的列表也带走了");
            }
        }

        // ④ 内容已收起、又没下钻过目录，所以 ← 这一下就是「退出浏览」，主区回到卡片网格。
        viewModel.GoBackPreview();
        window.UpdateLayout();

        if (viewModel.IsBrowsing)
        {
            SmokeFail($"← 该退出浏览了，主区还停在「{viewModel.BrowsePath}」");
        }

        viewModel.ClosePreview();
        window.UpdateLayout();

        if (!window.CardsScroll.IsVisible || window.CardsScroll.ActualWidth < gridWide - 1)
        {
            SmokeFail($"退出浏览后卡片区没回位（{gridWide:F0} → {window.CardsScroll.ActualWidth:F0} DIP）");
        }
    }

    /// <summary>
    /// 预览卡片上的 ×：每张卡片都要有一个独立、非空的关闭热区，而且不能压在「切窗口」的热区上。
    /// 两个矩形一旦重叠，点 × 会先被当成切窗口，看起来就是 × 没反应。
    /// </summary>
    private async Task VerifyPreviewCloseAsync(WindowPreviewWindow preview, DockViewModel viewModel)
    {
        var running = viewModel.Items.FirstOrDefault(item => item.IsRunning);
        if (running is null)
        {
            Log.Warning("自检：当前没有正在运行的应用，跳过预览关闭按钮校验");
            return;
        }

        var windows = await viewModel.GetWindowsAsync(running.Id);
        if (windows.Count == 0)
        {
            Log.Warning("自检：「{Name}」被标成运行中但一个窗口都没有，跳过预览关闭按钮校验", running.DisplayName);
            return;
        }

        preview.ShowFor(windows, 480, 760, 1.0);
        preview.UpdateLayout();

        var rects = preview.HitRectsForSelfCheck();
        var expected = Math.Min(windows.Count, 6);

        if (rects.Count != expected)
        {
            SmokeFail($"预览画了 {rects.Count} 张卡片，应该是 {expected} 张");
        }
        else if (rects.Any(rect => rect.Close.Width <= 0))
        {
            SmokeFail("预览卡片没有关闭热区（× 点不到）");
        }
        else if (rects.Any(rect => rect.Card.IntersectsWith(rect.Close)))
        {
            SmokeFail("预览卡片的 × 热区与「切窗口」热区重叠，点 × 会先切窗口");
        }
        else
        {
            SaveWindowProof(preview, "smoke-preview-close.png");
            Log.Information("预览关闭按钮校验通过：{Count} 张卡片各有独立 × 热区", rects.Count);
        }

        preview.Dismiss();
    }

    private async Task VerifyHotkeyActionAsync(string name, Action invoke, Window window)
    {
        if (window.IsVisible)
        {
            window.Hide();
        }

        invoke();

        // 断言必须在 invoke 之后立刻做，不能等一拍：Show() 是同步的，而搜索窗、启动台都有
        // 「失焦就自己关掉」的需求 —— 自检时人真的坐在屏幕前，鼠标在 Dock 上蹭一下就把前台抢走了，
        // 等 700ms 再问就会把「功能正常」误判成「窗口呼不出来」。
        if (!window.IsVisible)
        {
            SmokeFail($"{name} 的快捷键动作没能把窗口呼出来");
            return;
        }

        await Task.Delay(120);
        window.Hide();
        Log.Information("快捷键动作校验通过：{Name}", name);
    }

    private void SmokeFail(string message)
    {
        _smokeTestFailures++;
        Log.Error("冒烟自检失败：{Message}", message);
    }

    /// <summary>
    /// 把已实例化条目的右键菜单真打开，逐条叶子项确认 Command 解析得到。
    /// <para>
    /// 绑定路径写错不会抛异常、也不会编译失败，只会点了没反应 ——
    /// 「添加到 Dock 不成功」就是这么来的，所以只能主动验，不能靠编译。
    /// </para>
    /// </summary>
    private void VerifyTileMenus(DependencyObject root, string area)
    {
        // 菜单可能挂在 Button（Dock 图标、磁贴）也可能挂在 Border（文件夹卡片）上，所以按 FrameworkElement 找。
        var targets = Descendants(root)
            .OfType<FrameworkElement>()
            .Where(element => element.ContextMenu is not null)
            .Take(6)
            .ToList();

        if (targets.Count == 0)
        {
            Log.Information("{Area}：当前没有条目，跳过菜单校验", area);
            return;
        }

        var failures = new List<string>();

        foreach (var button in targets)
        {
            var menu = button.ContextMenu!;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            menu.UpdateLayout();

            foreach (var leaf in LeafItems(menu))
            {
                BindingOperations.GetBindingExpression(leaf, MenuItem.CommandProperty)?.UpdateTarget();

                if (leaf.Command is null)
                {
                    failures.Add(leaf.Header?.ToString() ?? "(无标题)");
                }
            }

            menu.IsOpen = false;
        }

        if (failures.Count > 0)
        {
            // 一条点不动的菜单等于一个坏掉的功能，自检必须让它影响退出码，不能只留在日志里等人翻。
            SmokeFail($"{area}：{failures.Count} 条菜单命令绑定失败 → {string.Join("、", failures.Distinct())}");
            return;
        }

        Log.Information("{Area}：已校验 {Count} 个条目的右键菜单，命令全部可用", area, targets.Count);
    }

    /// <summary>
    /// 检测「命令绑定解析不到」的死按钮。绑定路径写错既不报错也不影响编译，只会点下去毫无反应，
    /// 判据用「设了 Binding 却取不到值」，所以用 Click 的按钮不会被误报。
    /// </summary>
    private void VerifyCommandBindings(DependencyObject root, string area)
    {
        var broken = new List<string>();
        var checkedCount = 0;
        var hiddenCount = 0;

        foreach (var button in Descendants(root).OfType<ButtonBase>())
        {
            if (!BindingOperations.IsDataBound(button, ButtonBase.CommandProperty))
            {
                continue;
            }

            // 没画出来的按钮不判：内容页整块收起时它的 DataContext 是 null，绑定解析不到是应该的。
            // 它该被验到的时候，验它的那一步会显式把它打开（例如内容页校验里再调一次本方法）。
            if (!button.IsVisible)
            {
                hiddenCount++;
                continue;
            }

            checkedCount++;
            BindingOperations.GetBindingExpression(button, ButtonBase.CommandProperty)?.UpdateTarget();

            if (button.Command is null)
            {
                broken.Add(button.Content?.ToString() ?? "(未命名按钮)");
            }
        }

        if (broken.Count > 0)
        {
            SmokeFail($"{area}：{broken.Count} 个按钮的命令绑定失败 → {string.Join("、", broken.Distinct())}");
            return;
        }

        Log.Information(
            "{Area}：已校验 {Count} 个命令按钮，绑定全部可用{Hidden}",
            area,
            checkedCount,
            hiddenCount > 0 ? $"（另有 {hiddenCount} 个当前没画出来，跳过）" : string.Empty);
    }

    /// <summary>
    /// 按下一个按钮并等它的命令跑完。星星那条命令是 async 的，
    /// <see cref="ICommand.Execute"/> 只把它推到第一个 await 就返回，紧接着读状态必然还是旧的
    /// —— 自检第一版就是这么假失败了一次「点了星星没收藏」。
    /// </summary>
    private static async Task ClickAsync(ButtonBase button)
    {
        if (button.Command is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(button.CommandParameter);
        }
        else
        {
            button.Command?.Execute(button.CommandParameter);
        }

        await Task.Delay(50);
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;

            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<MenuItem> LeafItems(ItemsControl host)
    {
        foreach (var item in host.Items.OfType<MenuItem>())
        {
            if (item.HasItems)
            {
                foreach (var nested in LeafItems(item))
                {
                    yield return nested;
                }

                continue;
            }

            yield return item;
        }
    }

    private async Task ShowAndRecord(string name, Action present)
    {
        try
        {
            present();

            // 停一拍让布局与异步图标加载真的跑起来，只构造对象测不出模板里的问题。
            await Task.Delay(900);
            Log.Information("窗口自检通过：{Name}", name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "窗口自检失败：{Name}", name);
        }
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.WithProperty("Application", "DeskMate")
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(WellKnownPaths.LogsFolder, "deskmate-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: LogFileLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void AttachGlobalExceptionHandlers()
    {
        // UI 线程异常：记录并提示，标记已处理，让桌面工具继续活着而不是整个消失。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 非 UI 线程的异常无法挽回，只能尽力留证据。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "非 UI 线程发生未处理异常，进程即将终止");

        // 没被 await 的 Task 异常：记录后标记为已观察，避免终结器里再抛一次崩掉进程。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "后台任务异常未被等待");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常");

        // 自检里弹任何框都会挂住整个流程，只记账，最后用退出码说话。
        if (_smokeTest)
        {
            _smokeTestFailures++;
            e.Handled = true;
            return;
        }

        var message = $"操作出现异常：{e.Exception.Message}\n\n详细信息见 %APPDATA%\\DeskMate\\logs。";

        // 启动阶段容器还没建好时没有浮层可用，那种情况仍然只能靠模态框把话说出来。
        if (_container?.GetService<ToastService>() is { } toast)
        {
            toast.Error(message);
        }
        else
        {
            MessageBox.Show(message, "DeskMate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders().AddSerilog(Log.Logger, dispose: false));

        // ---- Core ----
        services.AddSingleton(_ => new StaScheduler());
        // 带可选路径参数的服务必须用工厂注册：MS DI 不会替可选构造参数填默认值。
        services.AddSingleton<IConfigService>(sp => new ConfigService(sp.GetRequiredService<ILogger<ConfigService>>()));
        services.AddSingleton<IIconService, IconService>();
        services.AddSingleton<IAppLauncherService, AppLauncherService>();
        services.AddSingleton<IFileOperationService, FileOperationService>();
        services.AddSingleton<IAppScannerService>(sp => new AppScannerService(
            sp.GetRequiredService<StaScheduler>(),
            sp.GetRequiredService<IConfigService>(),
            sp.GetRequiredService<ILogger<AppScannerService>>()));
        services.AddSingleton<IFolderService, FolderService>();
        services.AddSingleton<IUsageService>(sp => new UsageService(sp.GetRequiredService<ILogger<UsageService>>()));
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IAutoStartService, AutoStartService>();
        services.AddSingleton<IProcessProbeService, ProcessProbeService>();
        services.AddSingleton<IWindowService, WindowService>();
        // 桌面文件归类同样带可选目录参数，必须工厂注册；退出时容器会 Dispose 它，监听器跟着关掉。
        services.AddSingleton<DesktopFileService>(sp => new DesktopFileService(sp.GetRequiredService<ILogger<DesktopFileService>>()));
        services.AddSingleton<IDesktopFileService>(sp => sp.GetRequiredService<DesktopFileService>());

        // 这两个类既要满足接口，又要被 App 用具体类型调用（挂 HwndSource / 启动钩子），所以双注册。
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<HotkeyService>());
        services.AddSingleton<FullscreenService>();
        services.AddSingleton<IFullscreenDetector>(sp => sp.GetRequiredService<FullscreenService>());

        // ---- App ----
        services.AddSingleton<ThemeService>();
        services.AddSingleton<AppShell>();
        services.AddSingleton<ToastService>();

        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<TrayWindow>();

        services.AddSingleton<DockViewModel>();
        services.AddSingleton<DockWindow>();
        services.AddSingleton<TaskbarCoverWindow>();
        services.AddSingleton<WindowPreviewWindow>();

        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<LauncherWindow>();

        services.AddSingleton<FolderManagerViewModel>();
        services.AddSingleton<FolderManagerWindow>();
        services.AddSingleton<FilePreviewWindow>();

        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SearchWindow>();

        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsWindow>();

        return services.BuildServiceProvider();
    }
}
