namespace DeskMate.Core.Interfaces;

/// <summary>
/// 启动次数统计。启动台的「常用」页与搜索同分决胜都依赖它，
/// 数据独立存于 %APPDATA%\DeskMate\usage.json，不混进 config.json。
/// </summary>
public interface IUsageService
{
    /// <summary>记一次启动。</summary>
    void Record(string id);

    /// <summary>取启动次数，未知 Id 返回 0。</summary>
    int GetCount(string id);

    /// <summary>载入历史数据。</summary>
    Task LoadAsync();

    /// <summary>
    /// 清空全部统计并立即落盘，返回清掉了多少条。
    /// 「常用」页排得不满意时用户需要从零开始，而不是自己去删 usage.json。
    /// </summary>
    int Reset();
}
