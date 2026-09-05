namespace TraeCheckin;

/// <summary>
/// 一个 Trae 账号。每个账号独立 Token(JWT)/Session(Cookie)/DeviceId，
/// 本地自动签到遍历所有 Enabled 账号，云端部署按顺序写入独立 secret。
/// </summary>
public class TraeAccount
{
    /// <summary>账号唯一标识（也用于隔离 WebView2 用户数据目录）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>用户备注名（如「主号 / 小号」）；为空时 UI 显示「账号 n」。</summary>
    public string? Name { get; set; }
    /// <summary>JWT（约 8 小时，失效时用 Session 静默换新）。</summary>
    public string? Token { get; set; }
    /// <summary>X-Cloudide-Session 会话 Cookie 值（约 14 天）。</summary>
    public string? Session { get; set; }
    /// <summary>x-device-id（16 位数字，风控关键），每账号独立。</summary>
    public string DeviceId { get; set; } = "";
    public DateTime? TokenUpdatedAt { get; set; }
    /// <summary>该账号最近一次本地签到日期。</summary>
    public DateTime? LastCheckinDate { get; set; }
    /// <summary>是否参与本地自动签到与云端部署。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// 是否会员：非会员每日签到只获得基础 credits（实测 150），
    /// extra_credits（连签 +50）仅会员到账。默认 false。
    /// </summary>
    public bool IsMember { get; set; }
}
