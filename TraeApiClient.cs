using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TraeCheckin;

/// <summary>
/// 签到状态响应（/trae/api/v2/ug/checkin_credits/status）
/// </summary>
public class CheckinStatus
{
    public bool enable { get; set; }
    public bool checked_in { get; set; }
    public double credits { get; set; }
    public int code { get; set; }
    public string? message { get; set; }
}

/// <summary>
/// Trae 云 API 客户端：封装签到状态、执行签到、剩余积分查询三个接口。
/// </summary>
public class TraeApiClient
{
    private const string BaseUrl = "https://api.trae.cn";

    private readonly HttpClient _http;
    private readonly string _deviceId;

    public string? LastError { get; private set; }

    public TraeApiClient(string deviceId)
    {
        _deviceId = deviceId;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TraeCheckin/1.0");
    }

    /// <summary>构造每请求的认证头。</summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string path, string? token, string? body)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
        req.Headers.TryAddWithoutValidation("x-device-id", _deviceId);
        if (body != null)
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return req;
    }

    /// <summary>查询今日签到状态与单日奖励。</summary>
    public async Task<CheckinStatus?> GetStatusAsync(string token)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/status", token, "{}");
        return await SendAsync<CheckinStatus>(req);
    }

    /// <summary>执行每日签到。</summary>
    public async Task<CheckinStatus?> ClaimAsync(string token)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/claim", token, "{}");
        return await SendAsync<CheckinStatus>(req);
    }

    /// <summary>查询剩余积分（汇总所有资格包的剩余额度）。</summary>
    public async Task<double> GetRemainingCreditsAsync(string token)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/pay/user_current_entitlement_list", token, "{}");
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("user_entitlement_pack_list", out var packs) &&
                packs.ValueKind == JsonValueKind.Array)
            {
                double remaining = 0;
                foreach (var p in packs.EnumerateArray())
                {
                    double limit = 0;
                    if (p.TryGetProperty("entitlement_base_info", out var info) &&
                        info.TryGetProperty("quota", out var quota) &&
                        quota.TryGetProperty("credits_limit", out var cl) &&
                        cl.ValueKind == JsonValueKind.Number)
                    {
                        limit = cl.GetDouble();
                    }
                    double used = 0;
                    if (p.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("credits_amount", out var ua) &&
                        ua.ValueKind == JsonValueKind.Number)
                    {
                        used = ua.GetDouble();
                    }
                    var rem = limit - used;
                    if (rem < 0) rem = 0;
                    remaining += rem;
                }
                return remaining;
            }
            LastError = "响应缺少 user_entitlement_pack_list 字段";
            return -1;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return -1;
        }
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage req) where T : class
    {
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                LastError = $"HTTP {(int)resp.StatusCode} 空响应";
                return null;
            }
            var obj = JsonSerializer.Deserialize<T>(json);
            if (obj == null) LastError = "无法解析响应";
            return obj;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }
}