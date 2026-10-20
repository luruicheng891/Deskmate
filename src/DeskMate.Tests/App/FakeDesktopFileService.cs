using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;

namespace DeskMate.Tests.App;

/// <summary>
/// 桌面文件服务的替身：列表由测试直接塞，绝不碰用户真实桌面。
/// 只测文件夹逻辑的用例用它，测桌面监听的文件行为改用真的 DesktopFileService（指向临时目录）。
/// </summary>
public sealed class FakeDesktopFileService : IDesktopFileService
{
    public List<DesktopFile> Seed { get; } = new();

    public event EventHandler? Changed;

    public IReadOnlyList<DesktopFile> Files => Seed;

    public int RefreshCount { get; private set; }

    public IReadOnlyList<DesktopFile> Refresh()
    {
        RefreshCount++;
        return Seed;
    }

    /// <summary>模拟 watcher 去抖后的变化通知。</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>按真实归类规则塞一个文件进去，省得测试自己维护一份扩展名表。</summary>
    public FakeDesktopFileService Add(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var extension = DesktopFileRules.ExtensionOf(name);
        Seed.Add(new DesktopFile(
            name,
            path,
            extension,
            DesktopFileRules.Categorize(extension),
            1_024,
            new DateTime(2026, 1, 12)));
        return this;
    }
}
