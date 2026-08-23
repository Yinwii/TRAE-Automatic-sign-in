using System.Text.Json;

namespace TraeCheckin;

/// <summary>
/// 本地配置：存储 token、设备号、自动签到设置。
/// 保存到 %APPDATA%\TraeCheckin\config.json。
/// </summary>
public class AppConfig
{
    public string? Token { get; set; }
    /// <summary>X-Cloudide-Session 会话 Cookie 值（约 14 天有效），用于 token 失效时静默换新。</summary>
    public string? Session { get; set; }
    public DateTime? TokenUpdatedAt { get; set; }
    /// <summary>GitHub OAuth 设备码授权得到的 access_token（用于云端自动签到部署）。</summary>
    public string? GitHubToken { get; set; }
    /// <summary>GitHub 授权后的登录用户名（fork 目标 owner）。</summary>
    public string? GitHubLogin { get; set; }
    public string DeviceId { get; set; } = GenerateDeviceId();
    public bool AutoCheckinEnabled { get; set; } = true;
    public string AutoCheckinTime { get; set; } = "08:00";
    public DateTime? LastCheckinDate { get; set; }
    public double LastRemaining { get; set; } = -1;

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    // 迁移：优先复用官方客户端的真实 Aha 设备 ID，否则签到接口风控会返回 9074
                    var aha = TryResolveAhaDeviceId();
                    if (aha != null && aha != cfg.DeviceId)
                    {
                        cfg.DeviceId = aha;
                        cfg.Save();
                    }
                    return cfg;
                }
            }
        }
        catch { /* 配置损坏时回退默认 */ }

        var fresh = new AppConfig();
        var resolved = TryResolveAhaDeviceId();
        if (resolved != null) fresh.DeviceId = resolved;
        return fresh;
    }

    /// <summary>
    /// 生成一个 16 位十进制设备 ID（与 TraeWork Aha SDK 的设备号格式一致）。
    /// 签到接口风控要求 x-device-id 为数字设备号，使用 GUID/UUID 会触发 9074。
    /// </summary>
    private static string GenerateDeviceId()
    {
        return Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
    }

    /// <summary>
    /// 从本机 TraeWork 官方客户端的数据目录解析 Aha 设备 ID（16 位数字）。
    /// storage.json 中存在形如 "iCubeAuthInfo://icube-dc:3049374157909753" 的键。
    /// </summary>
    internal static string? TryResolveAhaDeviceId()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new[] { "TRAE SOLO CN", "Trae CN", "TRAE SOLO" };
        foreach (var dir in dirs)
        {
            var path = Path.Combine(appData, dir, "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    const string prefix = "iCubeAuthInfo://icube-dc:";
                    if (!prop.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var id = prop.Name.Substring(prefix.Length);
                    if (id.Length >= 8 && id.All(char.IsDigit)) return id;
                }
            }
            catch { /* 忽略读取/解析失败，继续尝试其他目录 */ }
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 忽略保存失败 */ }
    }
}