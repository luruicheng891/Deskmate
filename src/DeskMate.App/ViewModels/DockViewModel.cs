using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskMate.App.Services;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.ViewModels;

/// <summary>
/// Dock 的视图模型：配置里 DockItems 的唯一写入者。
/// <para>
/// 界面上是一条由两段拼起来的条：<b>运行区</b>（当前开着窗口的应用，临时、不落盘）
/// + <b>固定区</b>（config.DockItems）。两段都塞进 <see cref="Items"/> 供窗口绑定，
/// 但只有固定区会被写进配置。
/// </para>
/// </summary>
public sealed partial class DockViewModel : ObservableObject
{
    /// <summary>
    /// 轮询间隔。2 秒是「应用关掉后图标自己消失」的可接受上限；
    /// 一次快照只是遍历进程表，版本信息按路径缓存，不会每轮都读盘。
    /// </summary>
    private const int PollIntervalSeconds = 2;

    /// <summary>
    /// 悬停时图标会放大到 1.35 倍（见 DockItemControl 的动画），所以取图要按放大后的尺寸去要。
    /// 按原始尺寸取，鼠标一停上去就是拉伸，看起来像「Dock 图标变模糊了」。
    /// </summary>
    private const double HoverMagnification = 1.35;

    private readonly IConfigService _config;
    private readonly IIconService _icons;
    private readonly IAppLauncherService _launcher;
    private readonly IProcessProbeService _probe;
    private readonly IWindowService _windows;
    private readonly IUsageService _usage;
    private readonly ILogger<DockViewModel> _logger;
    private readonly DispatcherTimer _pollTimer;

    /// <summary>固定区，顺序与 config.DockItems 一致。</summary>
    private readonly List<DockItemViewModel> _pinned = new();

    /// <summary>运行区：按 exe 路径缓存的临时图标，应用一关就整条丢掉。</summary>
    private readonly Dictionary<string, DockItemViewModel> _runningPool = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>运行区当前的显示顺序（进程启动时间升序）。</summary>
    private readonly List<string> _runningOrder = new();

    /// <summary>构造 Dock 视图模型。</summary>
    public DockViewModel(
        IConfigService config,
        IIconService icons,
        IAppLauncherService launcher,
        IProcessProbeService probe,
        IWindowService windows,
        IUsageService usage,
        ILogger<DockViewModel> logger)
    {
        _config = config;
        _icons = icons;
        _launcher = launcher;
        _probe = probe;
        _windows = windows;
        _usage = usage;
        _logger = logger;

        // Background 优先级：轮询只是补图标，不该和悬停动画抢渲染。
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(PollIntervalSeconds),
        };
        _pollTimer.Tick += OnPollTick;
    }

    /// <summary>Dock 上实际显示的图标：运行区在前、固定区在后。</summary>
    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    /// <summary>需要用户感知的失败信息（启动不了、路径不存在等）。</summary>
    public event EventHandler<NoticeEventArgs>? UserNotice;

    [ObservableProperty]
    private double _iconSize = 56;

    /// <summary>Dock 是否竖着贴边。分隔线要跟着换方向，所以让 XAML 能绑到它。</summary>
    [ObservableProperty]
    private bool _isVertical;

    /// <summary>从配置载入固定项，再补齐图标与运行状态。</summary>
    public async Task LoadAsync()
    {
        IconSize = Math.Clamp(_config.Current.DockIconSize, AppConfig.DockIconSizeMin, AppConfig.DockIconSizeMax);

        _pinned.Clear();
        foreach (var model in _config.Current.DockItems.OrderBy(item => item.Order))
        {
            _pinned.Add(await CreateItemAsync(model));
        }

        await RefreshRunningStateAsync();
    }

    /// <summary>启动运行状态轮询。</summary>
    public void StartRunningPolling()
    {
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    /// <summary>停止轮询（窗口隐藏时调用，省 CPU）。</summary>
    public void StopRunningPolling() => _pollTimer.Stop();

    /// <summary>
    /// 添加一项并落盘。
    /// </summary>
    /// <returns>真的加上了返回 true；路径无效或本来就已经在 Dock 上返回 false（批量拖放要靠它数清结果）。</returns>
    public async Task<bool> AddItemAsync(string path)
    {
        var normalized = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var existing = _config.Current.DockItems.FirstOrDefault(
            item => string.Equals(item.TargetPath, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!_pinned.Any(item => item.Id == existing.Id))
            {
                _pinned.Add(await CreateItemAsync(existing));
            }

            RebuildItems();
            return false;
        }

        var model = new DockItem
        {
            DisplayName = DisplayNameFor(normalized),
            TargetPath = normalized,
            Order = _config.Current.DockItems.Count,
            IsPinned = true,
        };

        _config.Current.DockItems.Add(model);
        if (!await SaveOrTellAsync("添加到 Dock 失败"))
        {
            return false;
        }

        _pinned.Add(await CreateItemAsync(model));
        RebuildItems();
        _logger.LogInformation("Dock 已添加 {Name}", model.DisplayName);
        return true;
    }

    /// <summary>把运行区里的应用钉成固定项。</summary>
    public Task<bool> PinItemAsync(string id)
    {
        var item = Find(id);
        return item is null || item.IsPinned
            ? Task.FromResult(false)
            : AddItemAsync(ExecutableOf(item));
    }

    /// <summary>移除一项（只删引用，不动磁盘）。运行区的临时项本来就不在配置里，无需移除。</summary>
    public async Task RemoveItemAsync(string id)
    {
        var removed = _config.Current.DockItems.RemoveAll(item => item.Id == id);
        if (removed == 0)
        {
            return;
        }

        RenumberOrders();
        if (!await SaveOrTellAsync("从 Dock 移除失败"))
        {
            return;
        }

        var item = _pinned.FirstOrDefault(candidate => candidate.Id == id);
        if (item is not null)
        {
            _pinned.Remove(item);
        }

        RebuildItems();
    }

    /// <summary>按给定 Id 顺序重排并落盘。运行区的临时项不在配置里，忽略掉。</summary>
    public async Task ReorderAsync(IEnumerable<string> orderedIds)
    {
        var ordered = orderedIds.ToList();
        var byId = _config.Current.DockItems.ToDictionary(item => item.Id);
        var models = ordered.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        models.AddRange(_config.Current.DockItems.Where(item => !ordered.Contains(item.Id)));

        for (var position = 0; position < models.Count; position++)
        {
            models[position].Order = position;
        }

        _config.Current.DockItems = models;
        if (!await SaveOrTellAsync("保存 Dock 顺序失败"))
        {
            return;
        }

        var pinnedById = _pinned.ToDictionary(item => item.Id);
        var next = ordered.Where(pinnedById.ContainsKey).Select(id => pinnedById[id]).ToList();
        next.AddRange(_pinned.Where(item => !ordered.Contains(item.Id)));

        _pinned.Clear();
        _pinned.AddRange(next);
        RebuildItems();
    }

    /// <summary>
    /// 点击图标：应用已在运行就切回它的窗口（已在前台则最小化），
    /// 一个窗口都没有才启动新实例 —— 这是与任务栏一致的语义，避免越点越多窗口。
    /// </summary>
    public async Task LaunchItemAsync(string id)
    {
        var item = Find(id);
        if (item is null)
        {
            return;
        }

        if (!item.IsValid)
        {
            // 目标早就不在了：说清楚并且给一条出路，不要每次点都抛一个「找不到文件」的错。
            OnUserNotice(
                $"「{item.DisplayName}」的目标已经不存在了（可能被卸载或换了目录）。右键这个图标可以重新选择目标。",
                ToastKind.Warning);
            await RefreshValidityAsync();
            return;
        }

        try
        {
            if (await _windows.SwitchToAsync(ExecutableOf(item)))
            {
                // 切回窗口同样是用了一次。只统计「新启动」的话，一天点几十次的微信永远进不了「常用」页。
                _usage.Record(item.ExecutablePath.ToUpperInvariant());
                return;
            }

            _launcher.Launch(item.TargetPath);
            _usage.Record(item.ExecutablePath.ToUpperInvariant());
            item.IsRunning = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从 Dock 启动失败 {Path}", item.TargetPath);
            OnUserNotice($"无法启动「{item.DisplayName}」：{ex.Message}");
        }
    }

    /// <summary>预览卡片被点击：切到那个窗口。</summary>
    public void ActivateWindow(AppWindowInfo window) => _windows.Activate(window);

    /// <summary>预览卡片标题行右侧的 ×：只关掉那一个窗口，不动这个应用的其它窗口。</summary>
    public void CloseWindow(AppWindowInfo window)
    {
        if (!_windows.Close(window))
        {
            OnUserNotice("这个窗口已经不在了。", ToastKind.Info);
        }
    }

    /// <summary>
    /// 右键「关闭窗口」：关掉这个图标对应应用当前的所有窗口。走的是 WM_CLOSE，
    /// 所以程序自己想弹「保存吗」还是会弹 —— 我们不去替它决定要不要丢工作。
    /// </summary>
    public async Task CloseAllWindowsAsync(string id)
    {
        var item = Find(id);
        if (item is null)
        {
            return;
        }

        try
        {
            var closed = await _windows.CloseAllAsync(ExecutableOf(item));

            // 一个都没关掉也得把话说清楚：多半是它缩回了托盘，屏幕上本来就没有窗口。
            // 默默什么都不发生会被读成「右键菜单坏了」。
            OnUserNotice(
                closed > 0
                    ? $"已关闭「{item.DisplayName}」的 {closed} 个窗口"
                    : $"「{item.DisplayName}」现在没有可关闭的窗口（可能是缩在托盘里）",
                closed > 0 ? ToastKind.Success : ToastKind.Info);

            await RefreshRunningStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭窗口失败 {Path}", item.TargetPath);
            OnUserNotice($"关闭窗口失败：{ex.Message}");
        }
    }

    /// <summary>取该图标对应应用当前打开的窗口，供悬停预览。</summary>
    public Task<IReadOnlyList<AppWindowInfo>> GetWindowsAsync(string id)
    {
        var item = Find(id);
        return item is null
            ? Task.FromResult<IReadOnlyList<AppWindowInfo>>([])
            : _windows.GetWindowsAsync(ExecutableOf(item));
    }

    private static string ExecutableOf(DockItemViewModel item)
        => string.IsNullOrEmpty(item.ExecutablePath) ? item.TargetPath : item.ExecutablePath;

    // 该取哪一档图标：按悬停放大后的显示尺寸算，物理 DPI 与档位由 IconService 再换算。
    // 下限 96：Dock 图标只有 30px 上下，取大一档让 WPF 做降采样，比贴着显示尺寸取更耐放大与高 DPI。
    private int IconRequestSize() => (int)Math.Round(Math.Max(96, IconSize * HoverMagnification));

    /// <summary>在资源管理器中打开真实 exe 所在位置。</summary>
    public void OpenLocation(string id)
    {
        var item = Find(id);
        if (item is null)
        {
            return;
        }

        try
        {
            _launcher.OpenInExplorer(ExecutableOf(item));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开文件位置失败 {Path}", item.TargetPath);
            OnUserNotice($"打不开该位置：{ex.Message}");
        }
    }

    /// <summary>需要窗口为某个失效项弹出「选新目标」的文件框，参数是 Dock 项 Id。</summary>
    public event EventHandler<string>? RelocateRequested;

    /// <summary>右键：请求为这一项重新挑一个目标。</summary>
    public void BeginRelocate(string id) => RelocateRequested?.Invoke(this, id);

    /// <summary>
    /// 把某个 Dock 项指向新的可执行文件或快捷方式，图标与运行判定一起跟上。
    /// 视图模型里的条目整个重建一次：TargetPath 是只读的，改它等于换了一个启动项。
    /// </summary>
    public async Task RelocateItemAsync(string id, string newPath)
    {
        var normalized = newPath.Trim().Trim('"');
        var model = _config.Current.DockItems.FirstOrDefault(item => item.Id == id);
        var current = _pinned.FirstOrDefault(item => item.Id == id);

        if (model is null || current is null)
        {
            OnUserNotice("这个 Dock 项已经不在了。", ToastKind.Info);
            return;
        }

        if (!TargetExists(normalized))
        {
            OnUserNotice($"选中的东西不存在：{normalized}");
            return;
        }

        // 名字还是自动取的文件名时跟着换；用户自己起的名字保留。
        if (string.Equals(model.DisplayName, DisplayNameFor(model.TargetPath), StringComparison.Ordinal))
        {
            model.DisplayName = DisplayNameFor(normalized);
        }

        model.TargetPath = normalized;
        if (!await SaveOrTellAsync("重新选择目标失败"))
        {
            return;
        }

        var index = _pinned.IndexOf(current);
        _pinned[index] = await CreateItemAsync(model);
        RebuildItems();

        OnUserNotice($"已把「{model.DisplayName}」指向新的位置", ToastKind.Success);
    }

    /// <summary>需要窗口弹输入框/文件框配合改名与换图标，参数是 Dock 项 Id。</summary>
    public event EventHandler<string>? RenameRequested;

    /// <summary>需要窗口弹文件框让用户挑一个图标来源。</summary>
    public event EventHandler<string>? IconRequested;

    /// <summary>右键：请求改显示名。</summary>
    public void BeginRename(string id) => RenameRequested?.Invoke(this, id);

    /// <summary>右键：请求换图标。</summary>
    public void BeginChangeIcon(string id) => IconRequested?.Invoke(this, id);

    /// <summary>
    /// 改 Dock 上显示的名字。只改配置里的 <see cref="DockItem.DisplayName"/>，
    /// 磁盘上的程序与快捷方式文件名不动 —— 这是产品的最高约束。
    /// </summary>
    public async Task RenameItemAsync(string id, string name)
    {
        var trimmed = name.Trim();
        var model = _config.Current.DockItems.FirstOrDefault(item => item.Id == id);
        var item = _pinned.FirstOrDefault(candidate => candidate.Id == id);

        if (model is null || item is null)
        {
            OnUserNotice("这个 Dock 项已经不在了。", ToastKind.Info);
            return;
        }

        if (trimmed.Length == 0)
        {
            OnUserNotice("名字不能是空的。", ToastKind.Warning);
            return;
        }

        model.DisplayName = trimmed;
        if (!await SaveOrTellAsync("改名失败"))
        {
            return;
        }

        item.DisplayName = trimmed;
        RebuildItems();
        OnUserNotice($"图标名字已改成「{trimmed}」，磁盘上的文件名没动", ToastKind.Success);
    }

    /// <summary>用某个 .ico / .exe / .dll 上的图标替换显示，启动目标不变。</summary>
    public async Task SetIconAsync(string id, string iconPath)
    {
        var model = _config.Current.DockItems.FirstOrDefault(item => item.Id == id);
        var item = _pinned.FirstOrDefault(candidate => candidate.Id == id);

        if (model is null || item is null)
        {
            return;
        }

        if (!File.Exists(iconPath))
        {
            OnUserNotice($"图标文件不存在：{iconPath}");
            return;
        }

        model.IconOverride = iconPath;
        if (!await SaveOrTellAsync("设置图标失败"))
        {
            return;
        }

        await ApplyIconAsync(item, iconPath, hasOverride: true);
        OnUserNotice("已换上新的图标，点下去启动的还是原来那个程序", ToastKind.Success);
    }

    /// <summary>取消自定义图标，回到用目标自己的。</summary>
    public async Task RestoreIconAsync(string id)
    {
        var model = _config.Current.DockItems.FirstOrDefault(item => item.Id == id);
        var item = _pinned.FirstOrDefault(candidate => candidate.Id == id);

        if (model is null || item is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(model.IconOverride))
        {
            OnUserNotice("这个图标本来就没有自定义过。", ToastKind.Info);
            return;
        }

        model.IconOverride = string.Empty;
        if (!await SaveOrTellAsync("恢复图标失败"))
        {
            return;
        }

        await ApplyIconAsync(item, item.TargetPath, hasOverride: false);
        OnUserNotice("已恢复用目标自己的图标", ToastKind.Success);
    }

    private async Task ApplyIconAsync(DockItemViewModel item, string source, bool hasOverride)
    {
        item.IconSource = source;
        item.HasIconOverride = hasOverride;
        item.Icon = await _icons.GetIconAsync(source, IconRequestSize());
    }

    /// <summary>固定到最前。</summary>
    public async Task PinToFrontAsync(string id)
    {
        var model = _config.Current.DockItems.FirstOrDefault(item => item.Id == id);
        if (model is null)
        {
            return;
        }

        model.IsPinned = true;
        _config.Current.DockItems.Remove(model);
        _config.Current.DockItems.Insert(0, model);
        RenumberOrders();
        if (!await SaveOrTellAsync("固定到最前失败"))
        {
            return;
        }

        var item = _pinned.FirstOrDefault(candidate => candidate.Id == id);
        if (item is not null)
        {
            item.IsPinned = true;
            _pinned.Remove(item);
            _pinned.Insert(0, item);
        }

        RebuildItems();
    }

    /// <summary>图标大小变化时刷新所有图标（缓存按尺寸分键，来回切换不重复提取）。</summary>
    public async Task ApplyIconSizeAsync(double size)
    {
        var clamped = Math.Clamp(size, AppConfig.DockIconSizeMin, AppConfig.DockIconSizeMax);
        if (Math.Abs(clamped - IconSize) < 0.5)
        {
            return;
        }

        IconSize = clamped;
        var pixelSize = IconRequestSize();

        foreach (var item in Items)
        {
            item.IconSize = clamped;
            item.Icon = await _icons.GetIconAsync(
                string.IsNullOrEmpty(item.IconSource) ? item.TargetPath : item.IconSource,
                pixelSize);
        }
    }

    /// <summary>重算固定项的运行状态，并把运行区同步成当前开着窗口的应用。</summary>
    public async Task RefreshRunningStateAsync()
    {
        await RefreshValidityAsync();

        var names = await _probe.SnapshotRunningNamesAsync();
        foreach (var item in _pinned)
        {
            item.IsRunning = _probe.IsRunning(ExecutableOf(item), names);
        }

        await SyncRunningSectionAsync();
        RebuildItems();
    }

    /// <summary>向用户播报一条提示（由 <see cref="ToastService"/> 显示成非模态浮层）。</summary>
    public void Notify(string message, ToastKind kind = ToastKind.Warning) => OnUserNotice(message, kind);

    private async Task<DockItemViewModel> CreateItemAsync(DockItem model)
    {
        var item = new DockItemViewModel(this, model.Id, model.DisplayName, model.TargetPath, model.IsPinned)
        {
            IconSize = IconSize,
            IsValid = TargetExists(model.TargetPath),
        };

        item.ExecutablePath = await _probe.ResolveExecutablePathAsync(model.TargetPath);
        item.IconSource = string.IsNullOrWhiteSpace(model.IconOverride) ? model.TargetPath : model.IconOverride;
        item.HasIconOverride = !string.IsNullOrWhiteSpace(model.IconOverride);
        item.Icon = await _icons.GetIconAsync(item.IconSource, IconRequestSize());
        return item;
    }

    /// <summary>
    /// 重新问一遍每个固定项的目标还在不在。
    /// 判断放到后台线程：网络盘断的时候 File.Exists 能挂好几秒，而这跟着 2 秒一次的轮询在跑，
    /// 压在 UI 线程上就是整个 Dock 卡住。
    /// </summary>
    private async Task RefreshValidityAsync()
    {
        var items = _pinned.ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var present = await Task.Run(() => items.Select(item => TargetExists(item.TargetPath)).ToArray());

        for (var index = 0; index < items.Length; index++)
        {
            items[index].IsValid = present[index];
        }
    }

    private static bool TargetExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            // 路径本身写坏了、盘暂时不可达：都按「不在」处理，让用户看到失效标记而不是异常。
            return false;
        }
    }

    /// <summary>自动取的名字：文件夹（包括盘根）取不到文件名时直接用路径，不能留一个空名字。</summary>
    private static string DisplayNameFor(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    /// <summary>
    /// 运行区对齐当前快照：新开的补上（图标要提取，所以是异步），关掉的整条丢掉。
    /// 已经固定过的应用不进运行区 —— 它的固定项自带运行指示，再来一个是重复。
    /// </summary>
    private async Task SyncRunningSectionAsync()
    {
        var apps = await _probe.SnapshotRunningAppsAsync();
        var pinnedKeys = new HashSet<string>(_pinned.Select(PinnedKey), StringComparer.OrdinalIgnoreCase);
        var nextOrder = new List<string>();

        foreach (var app in apps)
        {
            if (pinnedKeys.Contains(app.ExecutablePath))
            {
                continue;
            }

            if (!_runningPool.TryGetValue(app.ExecutablePath, out var item))
            {
                item = new DockItemViewModel(this, RunningId(app.ExecutablePath), app.DisplayName, app.ExecutablePath, isPinned: false)
                {
                    IconSize = IconSize,
                    ExecutablePath = app.ExecutablePath,
                    IsRunning = true,
                    Icon = await _icons.GetIconAsync(app.ExecutablePath, IconRequestSize()),
                };

                _runningPool[app.ExecutablePath] = item;
            }

            nextOrder.Add(app.ExecutablePath);
        }

        foreach (var stale in _runningPool.Keys.Where(key => !nextOrder.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _runningPool.Remove(stale);
        }

        _runningOrder.Clear();
        _runningOrder.AddRange(nextOrder);

        MarkTrayHiddenApps(apps);
    }

    /// <summary>
    /// 标出「进程在但窗口藏在托盘」的固定项。判据直接用现成的快照，不额外 EnumWindows：
    /// 快照要的是「有算任务窗口的进程」，缺的就是缩回托盘的那批。
    /// </summary>
    private void MarkTrayHiddenApps(IReadOnlyList<RunningApp> apps)
    {
        var withWindows = new HashSet<string>(apps.Select(app => app.ExecutablePath), StringComparer.OrdinalIgnoreCase);

        foreach (var item in _pinned)
        {
            item.IsHiddenInTray = item.IsRunning && !withWindows.Contains(ExecutableOf(item));
        }
    }

    /// <summary>运行区的临时 Id：加前缀保证不可能和配置里的 Guid 撞上。</summary>
    private static string RunningId(string executablePath) => $"running:{executablePath}";

    private static string PinnedKey(DockItemViewModel item) => ExecutableOf(item);

    private List<DockItemViewModel> BuildDesiredOrder()
    {
        var pinnedKeys = new HashSet<string>(_pinned.Select(PinnedKey), StringComparer.OrdinalIgnoreCase);
        var desired = new List<DockItemViewModel>(_pinned.Count + _runningOrder.Count);

        foreach (var key in _runningOrder)
        {
            if (!pinnedKeys.Contains(key) && _runningPool.TryGetValue(key, out var item))
            {
                desired.Add(item);
            }
        }

        desired.AddRange(_pinned);
        return desired;
    }

    /// <summary>
    /// 把 Items 对齐到目标顺序。刻意逐个 Move/Insert 而不是 Clear 重建：
    /// 轮询每两秒一次，整体重建会让容器全部重新生成，鼠标正停在上面时会闪、拖拽也会断。
    /// </summary>
    private void RebuildItems()
    {
        var desired = BuildDesiredOrder();

        for (var index = 0; index < desired.Count; index++)
        {
            if (index < Items.Count && ReferenceEquals(Items[index], desired[index]))
            {
                continue;
            }

            var current = Items.IndexOf(desired[index]);
            if (current >= 0)
            {
                Items.Move(current, index);
            }
            else
            {
                Items.Insert(Math.Min(index, Items.Count), desired[index]);
            }
        }

        while (Items.Count > desired.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        ApplySeparators(RunningSectionLength(desired));
    }

    /// <summary>运行区有多长：从头数到第一个固定项为止。</summary>
    private static int RunningSectionLength(IReadOnlyList<DockItemViewModel> desired)
    {
        var length = 0;
        while (length < desired.Count && !desired[length].IsPinned)
        {
            length++;
        }

        return length;
    }

    /// <summary>运行区与固定区之间画一条分隔线，免得「图标自己冒出来」看起来像故障。</summary>
    private void ApplySeparators(int runningSectionLength)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            Items[index].ShowSeparator = runningSectionLength > 0 && index == runningSectionLength;
        }
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshRunningStateAsync();
        }
        catch (Exception ex)
        {
            // 定时器回调里抛出会直接干掉进程，必须就地记录。
            _logger.LogError(ex, "运行状态轮询失败");
        }
    }

    private DockItemViewModel? Find(string id) => Items.FirstOrDefault(item => item.Id == id);

    private void RenumberOrders()
    {
        var ordered = _config.Current.DockItems.OrderBy(item => item.Order).ToList();
        for (var position = 0; position < ordered.Count; position++)
        {
            ordered[position].Order = position;
        }
    }

    /// <summary>
    /// 落盘，失败就说清楚并退回改动前的状态。
    /// <para>
    /// 配置服务在写失败时已经把内存退回磁盘上那份（云盘占用、磁盘满、文件被别的程序锁住都会这样），
    /// 这里再重载一次条目，界面上就不会留着一个根本没存进去的改动 —— 那种「看着删掉了、重启又回来」
    /// 比直接报错更难理解。
    /// </para>
    /// </summary>
    private async Task<bool> SaveOrTellAsync(string failurePrefix)
    {
        try
        {
            await _config.SaveAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}", failurePrefix);
            OnUserNotice($"{failurePrefix}：{ex.Message}，已退回改动前的样子");
            await LoadAsync();
            return false;
        }
    }

    private void OnUserNotice(string message, ToastKind kind = ToastKind.Error)
        => UserNotice?.Invoke(this, new NoticeEventArgs(message, kind));
}
