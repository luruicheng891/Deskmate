using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 改名规则。这是整个应用唯一一处会改磁盘上用户文件名字的地方，
/// 所以每条拒绝路径都要钉住：宁可什么都不改，也绝不能覆盖或挪走已有的东西。
/// </summary>
public class FileOperationServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileOperationService _service = new(NullLogger<FileOperationService>.Instance);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void RenamesAFileInPlace_AndKeepsItsContent()
    {
        var path = Write("旧名字.md", "正文别丢");
        var target = _service.Rename(path, "新名字");

        Assert.Equal(Path.Combine(_temp.Path, "新名字.md"), target);
        Assert.False(File.Exists(path));
        Assert.Equal("正文别丢", File.ReadAllText(target));

        // 只改名字：它还在原来那一层，没被挪到别处。
        Assert.Equal(_temp.Path, Path.GetDirectoryName(target));
    }

    [Fact]
    public void AnExplicitExtensionIsHonouredAsTyped()
    {
        var path = Write("报告.md", "x");
        var target = _service.Rename(path, "报告.txt");

        Assert.Equal("报告.txt", Path.GetFileName(target));
    }

    [Fact]
    public void RenamesADirectoryWithoutTouchingWhatIsInside()
    {
        var directory = Path.Combine(_temp.Path, "作业");
        Directory.CreateDirectory(directory);
        var inside = Path.Combine(directory, "笔记.txt");
        File.WriteAllText(inside, "内容");

        var target = _service.Rename(directory, "课程作业");

        Assert.False(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(target, "笔记.txt")));
        Assert.Equal("内容", File.ReadAllText(Path.Combine(target, "笔记.txt")));
    }

    [Fact]
    public void RefusesToOverwriteSomethingThatAlreadyExists()
    {
        var path = Write("唯一.md", "原来的内容");
        Write("重名.md", "已经在那儿了");

        var error = Assert.Throws<IOException>(() => _service.Rename(path, "重名"));

        Assert.Contains("已经有", error.Message);
        Assert.True(File.Exists(path));
        Assert.Equal("原来的内容", File.ReadAllText(path));
        Assert.Equal("已经在那儿了", File.ReadAllText(Path.Combine(_temp.Path, "重名.md")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("不能<用>的字符")]
    [InlineData("冒号:也不行")]
    public void RejectsNamesThatCantExistOnDisk(string name)
    {
        var path = Write("还在.md", "x");

        Assert.ThrowsAny<ArgumentException>(() => _service.Rename(path, name));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TheSameNameIsANoOpInsteadOfAnError()
    {
        var path = Write("没打算改.md", "x");

        Assert.Equal(path, _service.Rename(path, "没打算改.md"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RenamingSomethingThatAlreadyVanishedSaysSo()
    {
        var path = Path.Combine(_temp.Path, "不存在.md");

        Assert.Throws<FileNotFoundException>(() => _service.Rename(path, "随便"));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, content);
        return path;
    }
}
