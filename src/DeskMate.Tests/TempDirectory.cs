namespace DeskMate.Tests;

/// <summary>
/// 临时目录夹具：测试用完即删，绝不碰 %APPDATA%\DeskMate 里的真实配置。
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeskMate.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 删不掉只是留下临时垃圾，不该让测试失败。
        }
    }
}
