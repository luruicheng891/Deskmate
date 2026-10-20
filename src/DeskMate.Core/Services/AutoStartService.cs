using DeskMate.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeskMate.Core.Services;

/// <summary>开机自启：唯一允许写注册表的地方，只动 HKCU\...\Run 下的 DeskMate 一个值。</summary>
public sealed class AutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskMate";

    private readonly ILogger<AutoStartService> _logger;

    /// <summary>构造自启服务。</summary>
    public AutoStartService(ILogger<AutoStartService> logger) => _logger = logger;

    /// <inheritdoc />
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value)
                && string.Equals(Unquote(value!), CurrentExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取开机自启状态失败");
            return false;
        }
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException($"无法打开注册表项 {RunKeyPath}");

            if (enabled)
            {
                // Run 项里带空格的路径必须加引号，否则被当成参数截断。
                key.SetValue(ValueName, $"\"{CurrentExecutablePath}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            _logger.LogInformation("开机自启已{State}", enabled ? "开启" : "关闭");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置开机自启失败");
            throw;
        }
    }

    /// <summary>当前可执行文件路径，单文件发布下即 DeskMate.exe。</summary>
    private static string CurrentExecutablePath
        => Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

    private static string Unquote(string value) => value.Trim().Trim('"');
}
