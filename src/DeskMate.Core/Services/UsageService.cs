using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Timers;
using DeskMate.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace DeskMate.Core.Services;

/// <summary>
/// 启动次数统计，存 %APPDATA%\DeskMate\usage.json。
/// 写入做了 2 秒合并：连点多个应用时不会每次都落盘。
/// </summary>
public sealed class UsageService : IUsageService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int PersistDelayMs = 2000;

    private readonly string _path;
    private readonly ILogger<UsageService> _logger;
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;

    /// <summary>构造统计服务。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="usagePath">默认 %APPDATA%\DeskMate\usage.json，测试可注入临时路径。</param>
    public UsageService(ILogger<UsageService> logger, string? usagePath = null)
    {
        _logger = logger;
        _path = usagePath ?? WellKnownPaths.UsageFile;
        _debounce = new Timer(PersistDelayMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Persist();
    }

    /// <inheritdoc />
    public void Record(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _counts.AddOrUpdate(id, 1, (_, current) => current + 1);
        // AutoReset=false 时重新计时要先 Stop 再 Start，等价于一次性防抖复位。
        _debounce.Stop();
        _debounce.Start();
    }

    /// <inheritdoc />
    public int GetCount(string id)
        => string.IsNullOrWhiteSpace(id) || !_counts.TryGetValue(id, out var count) ? 0 : count;

    /// <inheritdoc />
    public async Task LoadAsync()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions);
            if (loaded is not null)
            {
                _counts.Clear();
                foreach (var pair in loaded)
                {
                    _counts[pair.Key] = pair.Value;
                }
            }

            _logger.LogInformation("使用统计已载入 {Count} 条", _counts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取使用统计失败，从空开始");
        }
    }

    /// <inheritdoc />
    public int Reset()
    {
        var cleared = _counts.Count;
        _counts.Clear();
        _debounce.Stop();

        // 清空必须当场落盘：只清内存的话，下次启动历史数据又回来了。
        Persist();
        _logger.LogInformation("使用统计已清空 {Count} 条", cleared);
        return cleared;
    }

    /// <summary>退出时把累计数据写回。</summary>
    public void Dispose()
    {
        _debounce.Stop();
        _debounce.Dispose();
        Persist();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? string.Empty);
            var json = JsonSerializer.Serialize(_counts, JsonOptions);
            File.WriteAllText(_path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入使用统计失败");
        }
    }
}
