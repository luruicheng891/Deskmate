using DeskMate.Core.Interfaces;

namespace DeskMate.Tests.App;

/// <summary>
/// 文件操作替身。单元测试不许真去碰磁盘上的名字，更不许往用户的回收站里丢东西 ——
/// 所以这里只记录调用，新路径由测试自己给。
/// <para>
/// 真改名的规则（非法字符、同名拒绝、不写后缀就补回原后缀）由
/// <c>FileOperationServiceTests</c> 在临时目录里直接测真实现。
/// </para>
/// </summary>
public sealed class FakeFileOperations : IFileOperationService
{
    public List<(string From, string To)> Renamed { get; } = new();

    public List<string> Recycled { get; } = new();

    /// <summary>改名要返回的新路径怎么算。测试可以换成抛异常来验「改坏了要说明原因」。</summary>
    public Func<string, string, string> RenameTo { get; set; } =
        (path, name) => Path.Combine(Path.GetDirectoryName(path) ?? ".", name);

    /// <summary>抛出来的异常，用来验失败路径。</summary>
    public Exception? RenameThrows { get; set; }

    /// <summary>false = 用户取消了，什么都不该动。</summary>
    public bool RecycleResult { get; set; } = true;

    /// <summary>
    /// 移进回收站时顺手把临时目录里那个文件真的删掉 —— 不删的话「列表重数一遍」还能看到它，
    /// 测试就验不到真行为。只在 TempDirectory 里跑，碰不到用户的文件。
    /// </summary>
    public bool DeleteOnRecycle { get; set; } = true;

    public Exception? RecycleThrows { get; set; }

    /// <summary>
    /// 替身也真的在临时目录里挪一下 —— 不然「改完之后列表重数一遍看到的还是旧名字」，
    /// 视图模型那半条链就验不到真行为。真改名的规则由 FileOperationServiceTests 直接测真实现。
    /// </summary>
    public bool MoveOnDisk { get; set; } = true;

    public string Rename(string path, string newName)
    {
        if (RenameThrows is not null)
        {
            throw RenameThrows;
        }

        var target = RenameTo(path, newName);
        Renamed.Add((path, target));

        if (MoveOnDisk)
        {
            if (File.Exists(path))
            {
                File.Move(path, target);
            }
            else if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
        }

        return target;
    }

    public bool Recycle(string path)
    {
        if (RecycleThrows is not null)
        {
            throw RecycleThrows;
        }

        Recycled.Add(path);

        if (DeleteOnRecycle)
        {
            if (Path.Exists(path) && !Directory.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        return RecycleResult;
    }
}
