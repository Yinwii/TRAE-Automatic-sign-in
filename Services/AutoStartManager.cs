using Microsoft.Win32;

namespace TraeCheckin;

/// <summary>
/// 开机自启动：通过注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// 写入当前程序 exe 的完整路径（带引号）。这是 Windows 下用户级开机自启动的标准实现。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TraeCheckin";

    /// <summary>当前进程可执行文件的完整路径（apphost 单文件发布下即为本 exe）。</summary>
    private static string ExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>是否已注册开机自启动。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开启或关闭开机自启动。</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                // 路径可能含空格，必须用引号包裹，避免 Windows 解析错误
                key.SetValue(ValueName, $"\"{ExecutablePath}\"");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表无权限等情况下静默失败，由 UI 的复选框状态反映真实结果
        }
    }
}
