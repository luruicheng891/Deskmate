using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

public class ConfigServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static ConfigService Create(string path)
        => new(NullLogger<ConfigService>.Instance, path);

    /// <summary>
    /// 隐藏列表存的是条目 Id（目标路径大写）。老数据里可能留着 8.3 短路径写法，
    /// 载入时必须换算成长路径，否则扫描给的长路径 Id 跟它对不上，用户就会「解不开隐藏」。
    /// </summary>
    [Fact]
    public async Task LoadAsync_NormalizesHiddenIdsToLongPaths()
    {
        var directory = _temp.File("Some Long Folder Name");
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "Some Tool.exe");
        File.WriteAllText(exe, "MZ");
        var shortForm = DeskMate.Interop.PathInterop.GetShortPath(exe).ToUpperInvariant();

        var path = _temp.File("hidden-config.json");
        var writer = Create(path);
        await writer.LoadAsync();
        writer.Current.HiddenApps.Add(shortForm);
        await writer.SaveAsync();

        var reader = Create(path);
        await reader.LoadAsync();

        var expected = exe.ToUpperInvariant();
        var id = Assert.Single(reader.Current.HiddenApps);
        Assert.Equal(expected, id);
    }

    [Fact]
    public async Task LoadAsync_UsesDefaults_WhenFileMissing()
    {
        var service = Create(_temp.File("config.json"));

        await service.LoadAsync();

        Assert.Equal("Alt+Space", service.Current.Hotkey);
        Assert.Equal(56, service.Current.DockIconSize);
        Assert.Empty(service.Current.DockItems);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var path = _temp.File("config.json");
        var writer = Create(path);
        await writer.LoadAsync();

        writer.Current.DockPosition = "Left";
        writer.Current.LauncherHotkey = "Ctrl+Alt+L";
        writer.Current.FolderHotkey = "Ctrl+Alt+F";
        writer.Current.DockIconSize = 72;
        writer.Current.DockOpacity = 0.45;
        writer.Current.BlurEnabled = false;
        writer.Current.Groups.Add(new FolderGroup { Name = "工作", Color = "#FF8A00" });
        writer.Current.Folders.Add(new FolderItem { DisplayName = "资料", RealPath = @"C:\Temp\资料", Order = 3 });
        writer.Current.DockItems.Add(new DockItem { DisplayName = "记事本", TargetPath = @"C:\Windows\System32\notepad.exe", Order = 0, IsPinned = true });

        await writer.SaveAsync();

        var reader = Create(path);
        await reader.LoadAsync();

        Assert.Equal("Left", reader.Current.DockPosition);
        Assert.Equal("Ctrl+Alt+L", reader.Current.LauncherHotkey);
        Assert.Equal("Ctrl+Alt+F", reader.Current.FolderHotkey);
        Assert.Equal(72, reader.Current.DockIconSize);
        Assert.Equal(0.45, reader.Current.DockOpacity);
        Assert.False(reader.Current.BlurEnabled);
        Assert.Equal("资料", reader.Current.Folders.Single().DisplayName);
        Assert.True(reader.Current.DockItems.Single().IsPinned);
    }

    [Fact]
    public async Task SaveAsync_RaisesConfigChanged()
    {
        var service = Create(_temp.File("config.json"));
        await service.LoadAsync();

        var raised = 0;
        service.ConfigChanged += (_, _) => raised++;

        await service.SaveAsync();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task LoadAsync_KeepsDefaultsAndBacksUp_WhenJsonIsCorrupt()
    {
        var path = _temp.File("config.json");
        await File.WriteAllTextAsync(path, "{ this is not json ");

        var service = Create(path);
        await service.LoadAsync();

        Assert.Equal(56, service.Current.DockIconSize);
        Assert.True(Directory.GetFiles(_temp.Path, "config.json.corrupt-*").Length >= 1, "损坏的配置应留档而不是直接丢弃");
    }

    [Fact]
    public async Task LoadAsync_ToleratesOlderConfigWithoutNewFields()
    {
        // 只含早期字段，验证新增字段不会让老配置读不出来。
        var path = _temp.File("config.json");
        await File.WriteAllTextAsync(path, """{"hotkey":"Ctrl+Alt+S","dockIconSize":48}""");

        var service = Create(path);
        await service.LoadAsync();

        Assert.Equal("Ctrl+Alt+S", service.Current.Hotkey);
        Assert.Equal(48, service.Current.DockIconSize);
        Assert.Equal("Bottom", service.Current.DockPosition);

        // 老配置里没有启动台/面板快捷键：必须是「不绑定」，不能凭空抢一个系统按键。
        Assert.Equal(string.Empty, service.Current.LauncherHotkey);
        Assert.Equal(string.Empty, service.Current.FolderHotkey);
    }

    [Fact]
    public async Task SaveAsync_FailureRollsMemoryBackToTheLastGoodState()
    {
        var path = _temp.File("config.json");
        var service = Create(path);
        await service.LoadAsync();

        service.Current.DockItems.Add(new DockItem { Id = "a", DisplayName = "A", TargetPath = @"C:\a.exe" });
        await service.SaveAsync();

        // 模拟「写不进去」：临时文件的位置被一个目录占了。云盘占用、磁盘满、文件被别的程序锁住都是这一类。
        Directory.CreateDirectory(path + ".tmp");
        service.Current.DockItems.Add(new DockItem { Id = "b", DisplayName = "B", TargetPath = @"C:\b.exe" });

        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveAsync());

        // 内存必须退回磁盘上最后一次成功写入的样子。不回退的话：界面显示两条都在，
        // 用户重启后发现刚加的那条没了 —— 这比直接报错更难理解。
        var kept = Assert.Single(service.Current.DockItems);
        Assert.Equal("A", kept.DisplayName);
    }

    public void Dispose() => _temp.Dispose();
}
