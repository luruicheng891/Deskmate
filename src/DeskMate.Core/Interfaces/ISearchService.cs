using DeskMate.Core.Models;

namespace DeskMate.Core.Interfaces;

/// <summary>跨来源的统一搜索。</summary>
public interface ISearchService
{
    /// <summary>
    /// 搜索应用、受管文件夹与 Dock 项。
    /// 排序：完全匹配 &gt; 前缀匹配 &gt; 包含 &gt; 拼音，同分按使用频率。
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string keyword, int maxResults = 20);
}
