using System.IO.Compression;
using DeskMate.Core.Models;

namespace DeskMate.Core.Services;

/// <summary>
/// 压缩包列条目：只读目录表，**不解压、不落盘、不写回**。
/// 用 <see cref="ZipArchive"/> 套在一个人手开的 <see cref="FileStream"/> 上，
/// 是为了能把 <see cref="FileShare.ReadWrite"/> 传进去 —— 直接 <c>ZipFile.OpenRead</c> 会以
/// <c>FileShare.Read</c> 打开，用户那边正开着同一个包就会报「文件被占用」。
/// </summary>
public static class ArchiveLister
{
    /// <summary>列出压缩包里的条目（最多 <see cref="FilePreviewRules.MaxArchiveEntries"/> 条）。</summary>
    public static async Task<IReadOnlyList<ArchiveEntryInfo>> ListAsync(string path, CancellationToken token = default)
        => await Task.Run(() => List(path), token);

    private static IReadOnlyList<ArchiveEntryInfo> List(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var entries = new List<ArchiveEntryInfo>(Math.Min(archive.Entries.Count, FilePreviewRules.MaxArchiveEntries));

        foreach (var entry in archive.Entries)
        {
            if (entries.Count >= FilePreviewRules.MaxArchiveEntries)
            {
                entries.Add(new ArchiveEntryInfo(
                    $"（还有 {archive.Entries.Count - FilePreviewRules.MaxArchiveEntries} 项没列出来）",
                    0,
                    DateTime.MinValue,
                    false));
                break;
            }

            // ZipArchive 里目录条目就是名字以 / 结尾、长度为 0 的那几条。
            var isDirectory = entry.FullName.EndsWith('/');
            entries.Add(new ArchiveEntryInfo(entry.FullName, entry.Length, entry.LastWriteTime.LocalDateTime, isDirectory));
        }

        return entries;
    }
}
