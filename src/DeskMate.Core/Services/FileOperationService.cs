using DeskMate.Core.Interfaces;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 改名与移到回收站。校验都放在这一层：视图模型只负责问用户要什么、然后把结果讲清楚。
/// </summary>
public sealed class FileOperationService : IFileOperationService
{
    private readonly ILogger<FileOperationService> _logger;

    /// <summary>构造文件操作服务。</summary>
    public FileOperationService(ILogger<FileOperationService> logger) => _logger = logger;

    /// <inheritdoc />
    public string Rename(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。", nameof(path));
        }

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            throw new FileNotFoundException($"要改名的东西已经不在了：{path}", path);
        }

        var name = newName.Trim().Trim('"');
        if (name.Length == 0)
        {
            throw new ArgumentException("名字不能为空。", nameof(newName));
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"「{name}」里有不能用在名字里的字符。", nameof(newName));
        }

        if (name is "." or "..")
        {
            throw new ArgumentException("这个名字指的是上一级，不能当文件名。", nameof(newName));
        }

        // 资源管理器 F2 只选中主名，所以用户常常只敲新主名。扩展名没写就把原来那个补回去 ——
        // 否则 README.md 改成 README 之后，系统就不认识它了。目录没有扩展名这一说。
        var extension = isDirectory ? string.Empty : Path.GetExtension(path);
        if (extension.Length > 0 && Path.GetExtension(name).Length == 0)
        {
            name += extension;
        }

        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"「{path}」不在任何目录里。");
        var target = Path.Combine(directory, name);

        if (SamePath(target, path))
        {
            return path; // 名字没变（Windows 的路径不区分大小写），不做无谓的写
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"同一目录里已经有「{name}」了。");
        }

        if (isDirectory)
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        _logger.LogInformation("已改名：{From} → {To}", path, target);
        return target;
    }

    /// <inheritdoc />
    public bool Recycle(string path) => ShellFileOperations.Recycle(path);

    private static bool SamePath(string left, string right)
    {
        static string Clean(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(Clean(left), Clean(right), StringComparison.OrdinalIgnoreCase);
    }
}
