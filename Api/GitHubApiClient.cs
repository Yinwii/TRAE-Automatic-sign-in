using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraeCheckin;

/// <summary>设备码授权请求返回的 code 信息（snake_case 字段映射）。</summary>
public class GitHubDeviceCode
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
}

/// <summary>设备码授权轮询结果。</summary>
public enum DeviceAuthState
{
    Pending,   // 用户尚未在网页完成授权，继续轮询
    Success,   // 已拿到 access_token
    Failed     // 授权被拒绝或过期
}

/// <summary>云端部署状态检测结果。</summary>
public class DeploymentStatus
{
    /// <summary>access_token 是否仍有效。</summary>
    public bool IsAuthorized { get; set; } = true;
    /// <summary>fork 仓库是否已存在。</summary>
    public bool IsForked { get; set; }
    /// <summary>TRAE_SESSION 与 TRAE_DEVICE_ID 两个 secret 是否都已写入。</summary>
    public bool HasSecrets { get; set; }
    /// <summary>checkin.yml 定时 workflow 是否已启用（state=active）。</summary>
    public bool IsWorkflowEnabled { get; set; }
    /// <summary>是否已完成一次完整部署（fork + secrets + workflow 全部就绪）。</summary>
    public bool IsDeployed => IsForked && HasSecrets && IsWorkflowEnabled;
}

/// <summary>手动粘贴 Token（PAT）的校验结果。</summary>
public class PatValidation
{
    public bool IsValid { get; set; }
    public string? Login { get; set; }
    public bool CanWrite { get; set; }
    public string? Error { get; set; }
}

/// <summary>最近一次 workflow run 的简要信息（用于本地监控云端签到状态）。</summary>
public class WorkflowRunInfo
{
    /// <summary>运行结论：success / failure / null（尚未完成）。</summary>
    public string? Conclusion { get; set; }
    /// <summary>运行状态：completed / in_progress / queued 等。</summary>
    public string? Status { get; set; }
    /// <summary>创建时间（ISO8601）。</summary>
    public string? CreatedAt { get; set; }
    /// <summary>运行编号。</summary>
    public long RunNumber { get; set; }
    /// <summary>运行详情页地址。</summary>
    public string? HtmlUrl { get; set; }
}

/// <summary>
/// GitHub API 客户端：封装设备码授权（Device Flow）与云端自动签到部署所需的
/// fork / 写 secret / 启用 workflow / 触发 workflow 等 REST 接口。
/// </summary>
public class GitHubApiClient
{
    private const string ClientId = "Ov23lix0Kb9ldJHrOpKv";
    private const string SourceOwner = "star620";
    private const string SourceRepo = "TRAE-Automatic-sign-in";
    public const string SessionSecretName = "TRAE_SESSION";
    public const string DeviceIdSecretName = "TRAE_DEVICE_ID";
    public const string FeishuWebhookSecretName = "FEISHU_WEBHOOK";
    private const string WorkflowPath = ".github/workflows/checkin.yml";

    private readonly HttpClient _http;

    public string? LastError { get; private set; }

    public GitHubApiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TraeCheckin/1.4.4");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    // ---------- 设备码授权 ----------

    /// <summary>向 GitHub 申请设备码，返回需要展示给用户的 user_code 与验证地址。</summary>
    public async Task<GitHubDeviceCode?> RequestDeviceCodeAsync()
    {
        try
        {
            var body = JsonSerializer.Serialize(new { client_id = ClientId, scope = "repo workflow" });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"申请设备码失败：HTTP {(int)resp.StatusCode} {json}";
                return null;
            }
            return JsonSerializer.Deserialize<GitHubDeviceCode>(json);
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    /// <summary>轮询设备码授权结果。Pending 表示继续等待，Success 返回 token，Failed 表示已失败。</summary>
    public async Task<(DeviceAuthState State, string? Token)> PollForAccessTokenAsync(string deviceCode)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                client_id = ClientId,
                device_code = deviceCode,
                grant_type = "urn:ietf:params:oauth:grant-type:device_code"
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("access_token", out var tok) && tok.ValueKind == JsonValueKind.String)
                return (DeviceAuthState.Success, tok.GetString());

            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            if (err == "authorization_pending" || err == "slow_down") return (DeviceAuthState.Pending, null);
            LastError = "授权失败：" + err;
            return (DeviceAuthState.Failed, null);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return (DeviceAuthState.Failed, null);
        }
    }

    /// <summary>用 access_token 获取当前登录用户名。</summary>
    public async Task<string?> GetLoginAsync(string token)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, "/user", token);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            LastError = $"获取用户信息失败：HTTP {(int)resp.StatusCode} {json}";
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }

    // ---------- 部署流程 ----------

    /// <summary>fork 源仓库到当前用户账号，并轮询直到 fork 完成。</summary>
    public async Task<bool> ForkAsync(string token, string login)
    {
        // 源仓库 owner 自己部署时无需 fork（GitHub 不允许 fork 自己的仓库）
        if (string.Equals(login, SourceOwner, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Post, $"/repos/{SourceOwner}/{SourceRepo}/forks", token);
            if (resp.StatusCode != System.Net.HttpStatusCode.Accepted && !resp.IsSuccessStatusCode)
            {
                LastError = $"fork 失败：HTTP {(int)resp.StatusCode}";
                return false;
            }
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(2000);
                using var check = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}", token);
                if (check.IsSuccessStatusCode) return true;
            }
            LastError = "fork 超时未完成";
            return false;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>
    /// 给源仓库点 star（顺手推广）。204 表示成功；owner 本人或失败时静默跳过，不影响部署流程。
    /// </summary>
    public async Task<bool> StarSourceRepoAsync(string token, string login)
    {
        if (ShouldSkipStar(login)) return true;
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Put, $"/user/starred/{SourceOwner}/{SourceRepo}", token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>是否应跳过给源仓库点星（owner 本人不能也不会点自己的星）。</summary>
    public static bool ShouldSkipStar(string login)
        => !string.IsNullOrEmpty(login) && string.Equals(login, SourceOwner, StringComparison.OrdinalIgnoreCase);

    /// <summary>workflow 相关错误文案；409 表示仓库未开启 Actions，附解决指引。</summary>
    public static string BuildWorkflowError(int statusCode)
        => statusCode == 409
            ? "GitHub 仓库未开启 Actions：请到仓库 Settings → Actions → General 勾选 Allow owner actions and reusable workflows（或 Allow all actions）后重试"
            : $"HTTP {statusCode}";

    /// <summary>预检：是否像是一段可用的 GitHub token（PAT 或 OAuth token 均放行）。</summary>
    public static bool IsPlausibleToken(string? token)
        => !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= 20;

    /// <summary>解析 GET /repos/{owner}/{repo} 响应中的 permissions.push（无写权限返回 false）。</summary>
    public static bool ParsePermissionsPush(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return ParsePermissionsPush(doc.RootElement);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>解析 permissions.push 的 JsonElement 版本。</summary>
    public static bool ParsePermissionsPush(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("permissions", out var perms) || perms.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;
        return perms.TryGetProperty("push", out var push) && push.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    /// <summary>PAT 对目标仓库写权限不足时的指引文案。</summary>
    public static string BuildPatScopeHint()
        => "该 Token 对目标仓库没有写权限。请确认：1) Repository access 已包含 TRAE-Automatic-sign-in"
           + "（若已 fork 还需包含你自己的 fork 仓库）；"
           + "2) Permissions 中 Actions / Contents / Pull requests / Secrets / Workflows 均为 Read and write，"
           + "Metadata 为只读（自动带出）。修改后需重新生成 Token。";

    /// <summary>用仓库公开密钥加密后，把 secret 写入仓库的指定 Actions secret。</summary>
    public async Task<bool> SetSecretAsync(string token, string login, string secretName, string secretValue)
    {
        try
        {
            using var pkResp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/secrets/public-key", token);
            var pkJson = await pkResp.Content.ReadAsStringAsync();
            if (!pkResp.IsSuccessStatusCode)
            {
                LastError = pkResp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "GitHub 授权已失效，请重新授权"
                    : $"获取公开密钥失败：HTTP {(int)pkResp.StatusCode}";
                return false;
            }
            using var doc = JsonDocument.Parse(pkJson);
            string? key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
            string? keyId = doc.RootElement.TryGetProperty("key_id", out var id) ? id.GetString() : null;
            if (key == null || keyId == null)
            {
                LastError = "公开密钥响应缺少 key/key_id";
                return false;
            }

            var encrypted = GitHubSecret.Encrypt(secretValue, key);
            var body = JsonSerializer.Serialize(new { encrypted_value = encrypted, key_id = keyId });
            using var resp = await SendApiAsync(HttpMethod.Put, $"/repos/{login}/{SourceRepo}/actions/secrets/{secretName}", token, body);
            if (resp.IsSuccessStatusCode) return true;
            LastError = $"写入 secret 失败：HTTP {(int)resp.StatusCode}";
            return false;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>查找 checkin.yml 对应的 workflow id。</summary>
    public async Task<long> GetWorkflowIdAsync(string token, string login)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/workflows", token);
        if (!resp.IsSuccessStatusCode)
        {
            // 409 表示仓库未开启 Actions，附带解决指引
            LastError = BuildWorkflowError((int)resp.StatusCode);
            return -1;
        }
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("workflows", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var wf in arr.EnumerateArray())
            {
                if (wf.TryGetProperty("path", out var p) && p.GetString() == WorkflowPath &&
                    wf.TryGetProperty("id", out var id) && id.TryGetInt64(out var v))
                    return v;
            }
        }
        LastError = "未找到 workflow：" + WorkflowPath;
        return -1;
    }

    /// <summary>启用 workflow（fork 后定时任务默认禁用，需手动启用）。</summary>
    public async Task<bool> EnableWorkflowAsync(string token, string login, long workflowId)
    {
        using var resp = await SendApiAsync(HttpMethod.Put, $"/repos/{login}/{SourceRepo}/actions/workflows/{workflowId}/enable", token);
        if (resp.IsSuccessStatusCode) return true;
        LastError = $"启用 workflow 失败：HTTP {(int)resp.StatusCode}";
        return false;
    }

    /// <summary>手动触发一次 workflow（用于立即验证）。</summary>
    public async Task<bool> DispatchWorkflowAsync(string token, string login, long workflowId)
    {
        var body = JsonSerializer.Serialize(new { @ref = "main" });
        using var resp = await SendApiAsync(HttpMethod.Post, $"/repos/{login}/{SourceRepo}/actions/workflows/{workflowId}/dispatches", token, body);
        if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NoContent) return true;
        LastError = $"触发 workflow 失败：HTTP {(int)resp.StatusCode}";
        return false;
    }

    /// <summary>获取最新一次 workflow run 的结论（success/failure/null）。</summary>
    public async Task<string?> GetLatestRunConclusionAsync(string token, string login)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/runs?per_page=1", token);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("workflow_runs", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in arr.EnumerateArray())
            {
                if (run.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
                if (run.TryGetProperty("status", out var s) && s.GetString() != "completed")
                    return null; // 尚未完成
            }
        }
        return null;
    }

    /// <summary>获取最近一次已完成的 workflow run（结论、状态、时间、编号、地址）。</summary>
    public async Task<WorkflowRunInfo?> GetLatestRunAsync(string token, string login)
    {
        // status=completed 只取已完成的运行，避免启动瞬间拿到 in_progress 的 run 而误显示「运行中」
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/runs?per_page=1&status=completed", token);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var run in arr.EnumerateArray())
        {
            var info = new WorkflowRunInfo();
            info.Conclusion = run.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            info.Status = run.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            info.CreatedAt = run.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null;
            info.RunNumber = run.TryGetProperty("run_number", out var rn) && rn.TryGetInt64(out var rnv) ? rnv : 0;
            info.HtmlUrl = run.TryGetProperty("html_url", out var hu) && hu.ValueKind == JsonValueKind.String ? hu.GetString() : null;
            return info;
        }
        return null;
    }

    // ---------- 部署状态检测 ----------

    /// <summary>
    /// 检测云端部署状态：授权是否有效、fork 仓库是否存在、secrets 是否已写入、workflow 是否已启用。
    /// </summary>
    public async Task<DeploymentStatus> GetDeploymentStatusAsync(string token, string login)
    {
        var result = new DeploymentStatus();
        try
        {
            // 用 GET /user 探测授权（必须认证；公开仓库 GET 匿名可读，token 失效时也会返回 200，不能用来判断授权）
            using var userResp = await SendApiAsync(HttpMethod.Get, "/user", token);
            if (userResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                result.IsAuthorized = false;
                return result;
            }
            result.IsAuthorized = true;

            using var repoResp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}", token);
            result.IsForked = repoResp.IsSuccessStatusCode;
            if (!result.IsForked) return result;

            result.HasSecrets = await HasSecretsAsync(token, login);
            result.IsWorkflowEnabled = await IsWorkflowEnabledAsync(token, login);
        }
        catch (Exception ex)
        {
            // 网络异常等视为授权仍有效，仅状态未知，避免误删授权
            LastError = ex.Message;
            result.IsAuthorized = true;
        }
        return result;
    }

    /// <summary>检查 TRAE_SESSION / TRAE_DEVICE_ID 两个 secret 是否都已写入。</summary>
    private async Task<bool> HasSecretsAsync(string token, string login)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/secrets", token);
        if (!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("secrets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return false;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in arr.EnumerateArray())
            if (s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                names.Add(n.GetString() ?? "");
        return names.Contains(SessionSecretName) && names.Contains(DeviceIdSecretName);
    }

    /// <summary>检查 checkin.yml workflow 是否已启用（state=active）。</summary>
    private async Task<bool> IsWorkflowEnabledAsync(string token, string login)
    {
        long id = await GetWorkflowIdAsync(token, login);
        if (id < 0) return false;
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/workflows/{id}", token);
        if (!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "active";
    }

    // ---------- 内部 ----------

    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string path, string token, string? body = null)
    {
        using var req = new HttpRequestMessage(method, "https://api.github.com" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _http.SendAsync(req);
    }
}
