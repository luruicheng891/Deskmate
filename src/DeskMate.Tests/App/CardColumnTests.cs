using DeskMate.App.ViewModels;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 卡片区怎么按可用宽度排几列。写死卡片宽度时，右边内容窗格一开，
/// 左边刚好只塞得下一列、中间留一大片空白 —— 用户原话「文件本可以排成两排」。
/// </summary>
public class CardColumnTests : IDisposable
{
    private const double Gap = 12;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private FolderManagerViewModel New()
    {
        var config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        var folders = new FolderService(config, NullLogger<FolderService>.Instance);
        return new FolderManagerViewModel(folders, new NullLauncher(), new FakeDesktopFileService(), new FakeFileOperations(), NullLogger<FolderManagerViewModel>.Instance);
    }

    [Theory]
    [InlineData(458, 2)]   // 内容窗格开着时左边的实测宽度（用户截图里就是这一种）
    [InlineData(700, 3)]
    [InlineData(1180, 5)]  // 窗格收起来、整宽可用
    [InlineData(200, 1)]   // 窄到只放得下一列时不硬挤
    public void TheColumnCountFollowsTheAvailableWidth(double width, int expectedColumns)
    {
        var viewModel = New();
        viewModel.ReportCardAreaWidth(width);

        Assert.Equal(expectedColumns, ColumnsThatFit(width, viewModel.CardItemWidth));
    }

    [Fact]
    public void CardsNeverOverflowTheirRow()
    {
        var viewModel = New();

        // 算出来的宽度必须真的放得下那一列数，否则 WrapPanel 会自己掉回一列，等于白改。
        foreach (var width in new[] { 200d, 320, 458, 470, 600, 723, 900, 1180, 1560 })
        {
            viewModel.ReportCardAreaWidth(width);
            var item = viewModel.CardItemWidth;
            var columns = ColumnsThatFit(width, item);

            Assert.True(columns >= 1, $"{width} 时一列都放不下（卡片 {item}）");
            Assert.True(columns * (item + Gap) <= width + 0.5, $"{width} 时报了 {columns} 列，实际会掉行（卡片 {item}）");
            Assert.True(item >= FolderManagerViewModel.CardMinWidth - 8 || columns == 1, $"卡片被压到 {item}，字都要换三行了");
        }
    }

    [Fact]
    public void AnUnmeasuredHostKeepsTheLastKnownWidth()
    {
        var viewModel = New();
        viewModel.ReportCardAreaWidth(458);
        var settled = viewModel.CardItemWidth;

        // 视图切换时 ItemsControl 会先报 0（Collapsed 没有尺寸），那不能把卡片压成一条缝。
        viewModel.ReportCardAreaWidth(0);

        Assert.Equal(settled, viewModel.CardItemWidth);
    }

    private static int ColumnsThatFit(double width, double itemWidth)
        => Math.Max(1, (int)Math.Floor((width + Gap) / (itemWidth + Gap)));

    private sealed class NullLauncher : Core.Interfaces.IAppLauncherService
    {
        public void Launch(string path)
        {
        }

        public void OpenInExplorer(string path)
        {
        }

        public void OpenUri(string uri)
        {
        }

        public bool HasDefaultProgram(string path) => true;
    }
}
