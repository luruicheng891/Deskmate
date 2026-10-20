using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// JSON 配置读写。写盘采用「临时文件 + 替换」，
/// 避免用户正在改 Dock 时突然断电/崩溃留下半个 config.json。
/// </summary>
public sealed class ConfigService : IConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILogger<ConfigService> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AppConfig _current = new();

    /// <summary>构造配置服务。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="configPath">配置文件路径，默认 %APPDATA%\DeskMate\config.json，测试可注入临时路径。</param>
    public ConfigService(ILogger<ConfigService> logger, string? configPath = null)
    {
        _logger = logger;
        _path = configPath ?? WellKnownPaths.ConfigFile;
    }

    /// <inheritdoc />
    public AppConfig Current => _current;

    /// <inheritdoc />
    public string FilePath => _path;

    /// <inheritdoc />
    public event EventHandler? ConfigChanged;

    /// <inheritdoc />
    public async Task LoadAsync()
    {
        if (!File.Exists(_path))
        {
            _current = new AppConfig();
            _logger.LogInformation("配置文件不存在，使用默认值：{Path}", _path);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (loaded is null)
            {
                throw new JsonException("config.json 解析结果为空");
            }

            // 隐藏列表存的是条目 Id（目标路径大写）。历史数据里可能留着 8.3 短路径写法，
            // 而扫描统一按长路径给 Id，不在这里换算一次，那些应用就会「解不开隐藏」或「又冒出来」。
            loaded.HiddenApps = loaded.HiddenApps
                .Select(PathInterop.GetLongPath)
                .Select(path => path.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _current = loaded;
            _logger.LogInformation("配置已载入，Dock {DockCount} 项、文件夹 {FolderCount} 项",
                _current.DockItems.Count, _current.Folders.Count);
        }
        catch (JsonException ex)
        {
            // 坏配置留档而不是直接覆盖，用户手工改坏了还能找回原始内容。
            _current = new AppConfig();
            var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Copy(_path, backup, overwrite: true);
                _logger.LogError(ex, "config.json 格式错误，已备份到 {Backup} 并回落默认配置", backup);
            }
            catch (Exception copyEx)
            {
                _logger.LogError(copyEx, "config.json 格式错误且备份失败");
            }
        }
        catch (Exception ex)
        {
            _current = new AppConfig();
            _logger.LogError(ex, "读取配置文件失败，回落默认配置");
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync()
    {
        // 保留同一份对象引用的语义由调用方承担：这里整体替换，避免逐个字段回滚漏掉新增项。
        _current = new AppConfig();
        await SaveAsync();
        _logger.LogInformation("配置已重置为默认值");
    }

    /// <inheritdoc />
    public async Task SaveAsync()
    {
        // 序列化在锁外做，锁里只留磁盘 IO，避免持锁时间被 JSON 计算放大。
        var json = JsonSerializer.Serialize(_current, JsonOptions);

        await _writeGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? string.Empty);

            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, json, Encoding.UTF8);
            ReplaceOrMove(temp, _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入配置文件失败：{Path}", _path);

            // 磁盘上还是上一次成功写入的那份（临时文件 + 替换，不会留下半个），所以把内存退回它。
            // 不回退的话：界面显示「已删除」，用户重启后条目又凭空回来，比直接报错更难理解。
            await RollbackToDiskAsync();
            throw;
        }
        finally
        {
            _writeGate.Release();
        }

        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RollbackToDiskAsync()
    {
        try
        {
            await LoadAsync();
            ConfigChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写盘失败之后回退内存配置也失败了");
        }
    }

    // 某些同步盘（OneDrive / 云盘重定向 AppData）下 File.Replace 会抛错，退化成覆盖写。
    private void ReplaceOrMove(string temp, string target)
    {
        try
        {
            if (File.Exists(target))
            {
                File.Replace(temp, target, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "File.Replace 失败，改用直接覆盖写入");
            File.Copy(temp, target, overwrite: true);
            File.Delete(temp);
        }
    }
}
