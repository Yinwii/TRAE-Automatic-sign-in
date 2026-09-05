# 多账号签到（本地）实现计划 — Phase 1

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 本地 App 支持维护多个 Trae 账号：旧单账号自动迁移为第一个账号，每个账号独立登录（WebView2 目录隔离）、独立 DeviceId/今日状态，自动签到与「立即签到」遍历所有启用账号，历史按账号标记，主窗口放大缓解拥挤。

**Architecture:** 新增 `TraeAccount` 数据模型 + `AppConfig` 账号列表与一次性迁移；`TraeApiClient` 改为每请求传 deviceId（去掉构造绑定的单 deviceId）；新增 `AccountStore` 封装激活/枚举/增删账号；`MainForm` 的登录、换 token、签到、历史全部改为面向账号；设置页「账号」卡替换为账号管理下拉 + 添加/删除。Phase 2（云端循环签到）在 Phase 1 完成后另起计划。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit（测试目录 `TraeCheckin.Tests` 在仓库外，用 `-p:OutputPath` 覆盖运行）。

**测试运行命令（贯穿全计划）：**
```
& "C:\Program Files\dotnet\dotnet.exe" test --nologo -p:OutputPath="$env:TEMP\traebuild_multi" -v q
```
**编译命令：** `& "C:\Program Files\dotnet\dotnet.exe" build TraeCheckin.csproj -c Release --nologo`

**关键既有代码：**
- `Config/AppConfig.cs` — `Token/Session/DeviceId/TokenUpdatedAt/LastCheckinDate` 单账号字段；`Load()`(L32-59) 与 `Save()`(L98)。
- `Api/TraeApiClient.cs` — `_deviceId` 字段(L30)、构造(L34)、`BuildRequest`(L42-55) 内部用 `_deviceId`；公开方法 `GetStatusAsync/ClaimAsync/GetRemainingCreditsAsync(string token)`、`GetUserTokenAsync(string session)`。
- `Forms/MainForm.cs` — `_api`(L17)、`_userDataDir`(L18/L79-80)、`_config`(L16)；`OnShown`(L588)、`CheckAutoCheckinAsync`(L605)、`RefreshAllAsync`(L622)、`RefreshCloudStatusAsync`(L651)、`DoCheckinAsync`(L700)、`GetStatusWithValidTokenAsync`(L774)、`LoginAndRefreshAsync`(L806)、`UpdateTokenDisplay`(L423)、`BuildAccountRow`(L472-488)、`CopyToken`(L405)。
- `Forms/LoginForm.cs` — 构造 `(userDataDir, initialToken, onToken)`(L20)。
- `Program.cs` / `MainForm` 构造 `ClientSize = new Size(900, 720)`(MainForm.cs L83)。

---

## Task 1: `TraeAccount` 模型 + 迁移（TDD）

**Files:**
- Create: `Config/TraeAccount.cs`
- Modify: `Config/AppConfig.cs`
- Test: `TraeCheckin.Tests/AccountTests.cs`（新建）

- [ ] **Step 1: 写失败测试**

创建 `c:\Users\星梦\Desktop\插件开发\TraeCheckin.Tests\AccountTests.cs`：

```csharp
using TraeCheckin;

namespace TraeCheckin.Tests;

/// <summary>TraeAccount 模型与 AppConfig 单账号迁移逻辑测试。</summary>
public class AccountTests
{
    // ---- TraeAccount 默认值 ----
    [Fact]
    public void 新账号_Id非空_Enabled默认true()
    {
        var a = new TraeAccount();
        Assert.False(string.IsNullOrWhiteSpace(a.Id));
        Assert.True(a.Enabled);
        Assert.Equal("", a.DeviceId);
    }

    // ---- TryMigrateLegacy ----
    [Fact]
    public void 旧单账号配置_迁移为第一个账号()
    {
        var cfg = new AppConfig
        {
            Token = "old-token",
            Session = "old-session",
            DeviceId = "1234567890123456",
            LastCheckinDate = new DateTime(2026, 9, 1)
        };
        Assert.True(AppConfig.TryMigrateLegacy(cfg));
        Assert.Single(cfg.Accounts);
        Assert.Equal("old-token", cfg.Accounts[0].Token);
        Assert.Equal("old-session", cfg.Accounts[0].Session);
        Assert.Equal("1234567890123456", cfg.Accounts[0].DeviceId);
        Assert.Equal(new DateTime(2026, 9, 1), cfg.Accounts[0].LastCheckinDate);
        Assert.Null(cfg.Accounts[0].Name);
    }

    [Fact]
    public void 已有多账号_不再迁移()
    {
        var cfg = new AppConfig();
        cfg.Accounts.Add(new TraeAccount { Name = "已有" });
        Assert.False(AppConfig.TryMigrateLegacy(cfg));
        Assert.Single(cfg.Accounts);
    }

    [Fact]
    public void 无token无session_不迁移()
    {
        var cfg = new AppConfig();
        Assert.False(AppConfig.TryMigrateLegacy(cfg));
        Assert.Empty(cfg.Accounts);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test`（cwd=`TraeCheckin.Tests`）
Expected: 编译失败，缺 `TraeAccount` 与 `AppConfig.TryMigrateLegacy`。

- [ ] **Step 3: 创建 TraeAccount**

创建 `c:\Users\星梦\Desktop\插件开发\TraeCheckin\Config\TraeAccount.cs`：

```csharp
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
}
```

- [ ] **Step 4: AppConfig 增加列表字段与迁移方法**

在 `Config/AppConfig.cs` 的 `LastRemaining` 属性后、`ConfigDir` 前，插入字段：

```csharp
    /// <summary>全部 Trae 账号（多账号模型主存储）。</summary>
    public List<TraeAccount> Accounts { get; set; } = new();
    /// <summary>仪表盘当前展示的账号 Id；为空时取 Accounts[0]。</summary>
    public string? ActiveAccountId { get; set; }
```

并在 `Save()` 方法之后追加迁移逻辑：

```csharp
    /// <summary>
    /// 单账号 → 多账号迁移：当 Accounts 为空且存在旧版 Token/Session 时，
    /// 生成第一个账号并放入 Accounts。返回是否发生迁移。幂等（已有账号则不动）。
    /// </summary>
    public static bool TryMigrateLegacy(AppConfig cfg)
    {
        if (cfg.Accounts.Count > 0) return false;
        if (string.IsNullOrEmpty(cfg.Token) && string.IsNullOrEmpty(cfg.Session)) return false;

        cfg.Accounts.Add(new TraeAccount
        {
            Token = cfg.Token,
            Session = cfg.Session,
            DeviceId = string.IsNullOrEmpty(cfg.DeviceId) ? "" : cfg.DeviceId,
            TokenUpdatedAt = cfg.TokenUpdatedAt,
            LastCheckinDate = cfg.LastCheckinDate
        });
        return true;
    }
```

- [ ] **Step 5: Load() 中接入迁移**

把 `Config/AppConfig.cs` `Load()` 方法里、`if (cfg != null) { ... return cfg; }` 块内，在返回前加入迁移调用：

```csharp
                if (cfg != null)
                {
                    // 多账号迁移：旧单账号转成 Accounts[0]
                    if (TryMigrateLegacy(cfg))
                    {
                        // 迁移后清空旧字段，避免双份数据源（保留 DeviceId 已被新账号引用）
                        cfg.Token = null;
                        cfg.Session = null;
                        cfg.TokenUpdatedAt = null;
                        cfg.LastCheckinDate = null;
                        if (string.IsNullOrEmpty(cfg.ActiveAccountId))
                            cfg.ActiveAccountId = cfg.Accounts[0].Id;
                        cfg.Save();
                    }
                    // 迁移：优先复用官方客户端的真实 Aha 设备 ID...
                    var aha = TryResolveAhaDeviceId();
                    if (aha != null && aha != cfg.DeviceId)
                    {
                        cfg.DeviceId = aha;
                        cfg.Save();
                    }
                    return cfg;
                }
```

> 注：DeviceId 兜底保留在旧字段（AppConfig.DeviceId）仅用于迁移/单账号兼容；多账号运行时以 `TraeAccount.DeviceId` 为准。`TryResolveAhaDeviceId` 兼容分支保留原样。

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test`
Expected: 新增 3 用例通过。

- [ ] **Step 7: 提交**

```bash
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" add Config/TraeAccount.cs Config/AppConfig.cs
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" commit -m "feat: 新增 TraeAccount 模型与单账号→多账号迁移"
```
> 测试文件在仓库外无需 add。

---

## Task 2: `TraeApiClient` 每请求传 deviceId

**Files:**
- Modify: `Api/TraeApiClient.cs`

- [ ] **Step 1: 去掉构造绑定的 deviceId，改为方法参数**

在 `Api/TraeApiClient.cs` 中做三处替换。

(1) 删除字段与构造参数：
```csharp
    private readonly HttpClient _http;
    private readonly string _deviceId;

    public string? LastError { get; private set; }

    public TraeApiClient(string deviceId)
    {
        _deviceId = deviceId;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TraeCheckin/1.0");
    }
```
→
```csharp
    private readonly HttpClient _http;

    public string? LastError { get; private set; }

    public TraeApiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TraeCheckin/1.0");
    }
```

(2) `BuildRequest` 签名与 deviceId 用法：
```csharp
    internal HttpRequestMessage BuildRequest(HttpMethod method, string path, string? token, string? body)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
        req.Headers.TryAddWithoutValidation("X-User-Region", "cn");
        // 风控关键：x-device-id 必须是 16 位数字 Aha 设备号；使用 GUID/UUID 会触发 9074（"参与用户太多"）
        req.Headers.TryAddWithoutValidation("x-device-id", _deviceId);
        if (body != null)
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return req;
    }
```
→
```csharp
    internal HttpRequestMessage BuildRequest(HttpMethod method, string path, string? token, string deviceId, string? body)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
        req.Headers.TryAddWithoutValidation("X-User-Region", "cn");
        // 风控关键：x-device-id 必须是 16 位数字 Aha 设备号；使用 GUID/UUID 会触发 9074（"参与用户太多"）
        req.Headers.TryAddWithoutValidation("x-device-id", deviceId);
        if (body != null)
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return req;
    }
```

(3) 三个公开方法补 deviceId 参数并透传：
```csharp
    public async Task<CheckinStatus?> GetStatusAsync(string token)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/status", token, "{}");
        return await SendAsync<CheckinStatus>(req);
    }

    public async Task<CheckinStatus?> ClaimAsync(string token)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/claim", token, "{}");
        return await SendAsync<CheckinStatus>(req);
    }
```
→
```csharp
    public async Task<CheckinStatus?> GetStatusAsync(string token, string deviceId)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/status", token, deviceId, "{}");
        return await SendAsync<CheckinStatus>(req);
    }

    public async Task<CheckinStatus?> ClaimAsync(string token, string deviceId)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/claim", token, deviceId, "{}");
        return await SendAsync<CheckinStatus>(req);
    }
```

以及：
```csharp
    public async Task<double> GetRemainingCreditsAsync(string token)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/pay/user_current_entitlement_list", token, "{}");
```
→
```csharp
    public async Task<double> GetRemainingCreditsAsync(string token, string deviceId)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/pay/user_current_entitlement_list", token, deviceId, "{}");
```

> `GetUserTokenAsync(string session)` 只带 Cookie，不涉及 deviceId，**不改签名**。

- [ ] **Step 2: 编译（会因 MainForm 调用点缺参失败，属预期）**

Run: `dotnet build TraeCheckin.csproj -c Release`
Expected: 报错位置全部在 `Forms/MainForm.cs`（`_api.GetStatusAsync/ClaimAsync/GetRemainingCreditsAsync` 与 `new TraeApiClient(_config.DeviceId)`）——下一步统一改调用点。

- [ ] **Step 3: 提交（此时 MainForm 引用旧签名，先不构建整仓，仅在 Task 3 一并修复）**

```bash
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" add Api/TraeApiClient.cs
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" commit -m "refactor: TraeApiClient 改为每请求传入 deviceId，支持多账号独立设备号"
```
> 注：此提交暂时不通过 MainForm 编译；Task 3 结束再整仓编译。若你偏好每提交可编译，可先跳过本步、Task 3 完成后再连同提交。

---

## Task 3: `AccountStore` 辅助服务（TDD）

**Files:**
- Create: `Services/AccountStore.cs`
- Test: `TraeCheckin.Tests/AccountStoreTests.cs`（新建）

- [ ] **Step 1: 写失败测试**

创建 `c:\Users\星梦\Desktop\插件开发\TraeCheckin.Tests\AccountStoreTests.cs`：

```csharp
using TraeCheckin;

namespace TraeCheckin.Tests;

/// <summary>账号存储辅助逻辑测试：激活账号、启用列表、增删、DeviceId 兜底。</summary>
public class AccountStoreTests
{
    private static AppConfig CfgWith(string? activeId, params TraeAccount[] accounts)
    {
        var cfg = new AppConfig();
        foreach (var a in accounts) cfg.Accounts.Add(a);
        cfg.ActiveAccountId = activeId;
        return cfg;
    }

    [Fact]
    public void 无激活Id_取第一个账号为激活()
    {
        var store = new AccountStore(CfgWith(null, new TraeAccount { Name = "A" }, new TraeAccount { Name = "B" }));
        Assert.Equal("A", store.ActiveAccount.Name);
    }

    [Fact]
    public void 有激活Id_返回对应账号()
    {
        var b = new TraeAccount { Name = "B" };
        var store = new AccountStore(CfgWith(b.Id, new TraeAccount { Name = "A" }, b));
        Assert.Equal("B", store.ActiveAccount.Name);
    }

    [Fact]
    public void 启用列表_按添加顺序过滤Enabled()
    {
        var cfg = CfgWith(null,
            new TraeAccount { Name = "A", Enabled = true },
            new TraeAccount { Name = "B", Enabled = false },
            new TraeAccount { Name = "C", Enabled = true });
        var list = new AccountStore(cfg).EnabledAccounts().ToList();
        Assert.Equal(new[] { "A", "C" }, list.Select(a => a.Name));
    }

    [Fact]
    public void SetActive_写入config()
    {
        var cfg = CfgWith(null, new TraeAccount { Name = "A" }, new TraeAccount { Name = "B" });
        new AccountStore(cfg).SetActive(cfg.Accounts[1].Id);
        Assert.Equal(cfg.Accounts[1].Id, cfg.ActiveAccountId);
    }

    [Fact]
    public void DeviceId为空_EnsureDeviceId填充16位数字()
    {
        var a = new TraeAccount();
        new AccountStore(new AppConfig()).EnsureDeviceId(a);
        Assert.Equal(16, a.DeviceId.Length);
        Assert.True(a.DeviceId.All(char.IsDigit));
    }

    [Fact]
    public void DeviceId非空_EnsureDeviceId保留()
    {
        var a = new TraeAccount { DeviceId = "9876543210123456" };
        new AccountStore(new AppConfig()).EnsureDeviceId(a);
        Assert.Equal("9876543210123456", a.DeviceId);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test`
Expected: 编译失败，缺 `AccountStore`。

- [ ] **Step 3: 实现 AccountStore**

创建 `c:\Users\星梦\Desktop\插件开发\TraeCheckin\Services\AccountStore.cs`：

```csharp
namespace TraeCheckin;

/// <summary>
/// 账号存储辅助：激活账号、启用列表、增删账号与 DeviceId 兜底填充。
/// 直接操作传入的 AppConfig（持久化由调用方负责，UI 层 Save）。
/// </summary>
public class AccountStore
{
    private readonly AppConfig _config;

    public AccountStore(AppConfig config)
    {
        _config = config;
        if (_config.Accounts.Count > 0 && string.IsNullOrEmpty(_config.ActiveAccountId))
            _config.ActiveAccountId = _config.Accounts[0].Id;
    }

    /// <summary>当前激活账号（无激活 Id 或 Id 失效时回退第一个）。</summary>
    public TraeAccount ActiveAccount
    {
        get
        {
            if (_config.Accounts.Count == 0) throw new InvalidOperationException("尚无账号");
            var byId = _config.Accounts.FirstOrDefault(a => a.Id == _config.ActiveAccountId);
            if (byId != null) return byId;
            _config.ActiveAccountId = _config.Accounts[0].Id;
            return _config.Accounts[0];
        }
    }

    /// <summary>按添加顺序返回参与本地自动签到/云端部署的账号。</summary>
    public IEnumerable<TraeAccount> EnabledAccounts()
        => _config.Accounts.Where(a => a.Enabled);

    public void SetActive(string id)
    {
        if (_config.Accounts.Any(a => a.Id == id))
            _config.ActiveAccountId = id;
    }

    /// <summary>新增账号并设为激活（调用方负责 Login 与 Save）。</summary>
    public TraeAccount AddNew()
    {
        var acc = new TraeAccount { DeviceId = GenerateDeviceId() };
        _config.Accounts.Add(acc);
        _config.ActiveAccountId = acc.Id;
        return acc;
    }

    /// <summary>删除账号；删空后重置激活 Id。返回是否删除成功。</summary>
    public bool Remove(string id)
    {
        int idx = _config.Accounts.FindIndex(a => a.Id == id);
        if (idx < 0) return false;
        _config.Accounts.RemoveAt(idx);
        if (_config.Accounts.Count == 0)
        {
            _config.ActiveAccountId = null;
        }
        else if (_config.ActiveAccountId == id)
        {
            _config.ActiveAccountId = _config.Accounts[0].Id;
        }
        return true;
    }

    /// <summary>DeviceId 为空时填充 16 位数字；非空保留。</summary>
    public void EnsureDeviceId(TraeAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.DeviceId))
            account.DeviceId = GenerateDeviceId();
    }

    private static string GenerateDeviceId()
        => Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test`
Expected: 新增 6 用例通过（Task 1 的 3 个也在）。

- [ ] **Step 5: 提交**

```bash
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" add Services/AccountStore.cs
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" commit -m "feat: 新增 AccountStore 账号辅助服务（激活/启用列表/增删/DeviceId 兜底）"
```

---

## Task 4: MainForm 本地签到多账号化

**Files:**
- Modify: `Forms/MainForm.cs`

**目标：** 把「登录 / 换 token / 立即签到 / 自动签到 / 历史」全部改为面向账号；多账号各签各的。涉及多段代码，用以下 5 组替换逐步完成，每完成一组编译一次。

- [ ] **Step 1: 构造函数与字段适配**

在 `Forms/MainForm.cs` 构造函数开头（`_config = AppConfig.Load();` 后）改为：

```csharp
        _config = AppConfig.Load();
        _accountStore = new AccountStore(_config);
        _api = new TraeApiClient();
        _userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraeCheckin", "WebView");
```

并在字段区 `private readonly TraeApiClient _api;` 后新增：

```csharp
    private readonly AccountStore _accountStore;
```

`AccountStore` 是实例字段，要求不在字段初始化式里用 `_config`（构造里赋值即可）。

新增账号专属 WebView 目录 helper（放在 `_userDataDir` 声明附近的方法区，如 `LoadAppIcon` 后）：

```csharp
    /// <summary>某账号专属的 WebView2 用户数据目录（账号间登录态隔离）。</summary>
    private string AccountWebViewDir(TraeAccount account)
        => Path.Combine(_userDataDir, "account_" + account.Id);
```

窗口放大：构造函数 `ClientSize = new Size(900, 720);` 改为 `ClientSize = new Size(1200, 780);`

- [ ] **Step 2: 定义账号便捷访问属性**

在 `UpdateTokenDisplay()` 方法前插入：

```csharp
    /// <summary>当前激活账号（无账号时 null，UI 据此呈现未登录态）。</summary>
    private TraeAccount? CurAccount
        => _config.Accounts.Count > 0 ? _accountStore.ActiveAccount : null;
```

- [ ] **Step 3: `GetStatusWithValidTokenAsync` / `LoginAndRefreshAsync` 面向账号**

将 `GetStatusWithValidTokenAsync()`(L774-804) 整体替换为接收账号的版本，并新增调用辅助：

```csharp
    private async Task<CheckinStatus?> GetStatusWithValidTokenAsync(TraeAccount account)
    {
        if (!string.IsNullOrEmpty(account.Token))
        {
            var st = await _api.GetStatusAsync(account.Token, account.DeviceId);
            if (st != null && st.code == 0) return st;
            SetLog("[" + DisplayName(account) + "] token 已失效，尝试用会话 Cookie 静默换新…");
        }

        // 优先用 X-Cloudide-Session（约 14 天有效）静默换新 token，避免频繁重新登录
        if (!string.IsNullOrEmpty(account.Session))
        {
            var renewed = await _api.GetUserTokenAsync(account.Session);
            if (!string.IsNullOrEmpty(renewed))
            {
                account.Token = renewed;
                account.TokenUpdatedAt = DateTime.Now;
                _config.Save();
                SetLog("[" + DisplayName(account) + "] token 已通过会话 Cookie 静默换新。");
                if (account.Id == CurAccount?.Id) UpdateTokenDisplay();
                var st = await _api.GetStatusAsync(renewed, account.DeviceId);
                if (st != null && st.code == 0) return st;
            }
            else
            {
                SetLog("[" + DisplayName(account) + "] 会话 Cookie 已失效：" + (_api.LastError ?? "未知错误"));
            }
        }

        return null;
    }
```

将 `LoginAndRefreshAsync()`(L806-824) 替换为接收账号：

```csharp
    private async Task<CheckinStatus?> LoginAndRefreshAsync(TraeAccount account)
    {
        string token = string.Empty;
        string? session = null;
        using (var login = new LoginForm(AccountWebViewDir(account), account.Token, (t, s) => { token = t; session = s; }))
            login.ShowDialog();
        if (string.IsNullOrEmpty(token))
        {
            SetLog("登录取消。");
            return null;
        }
        account.Token = token;
        if (!string.IsNullOrEmpty(session)) account.Session = session;
        account.TokenUpdatedAt = DateTime.Now;
        _accountStore.EnsureDeviceId(account);
        _config.Save();
        SetLog("[" + DisplayName(account) + "] 登录成功，token 已保存。");
        if (account.Id == CurAccount?.Id) UpdateTokenDisplay();
        return await _api.GetStatusAsync(token, account.DeviceId);
    }
```

- [ ] **Step 4: `RefreshAllAsync` 面向激活账号**

替换 `RefreshAllAsync()`(L622-648)：

```csharp
    private async Task RefreshAllAsync()
    {
        var acc = CurAccount;
        var remaining = _config.LastRemaining;
        if (acc == null)
        {
            _lblRemaining.Text = "—";
            _lblStatus.Text = "未登录";
            _lblReward.Text = "—";
            await RefreshCloudStatusAsync();
            return;
        }

        var status = await GetStatusWithValidTokenAsync(acc);
        if (!string.IsNullOrEmpty(acc.Token))
        {
            var r = await _api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
            if (r >= 0) { remaining = r; _config.LastRemaining = r; _config.Save(); }
        }

        _lblRemaining.Text = remaining >= 0 ? remaining.ToString("0.##") : "—";
        if (status != null)
        {
            _lblStatus.Text = status.enable
                ? (status.checked_in ? "今日已签到 ✓" : "今日可签到")
                : "签到功能未开启";
            _lblStatus.ForeColor = status.checked_in ? Color.FromArgb(16, 185, 129) : Color.FromArgb(245, 158, 11);
            _lblReward.Text = status.credits > 0 ? $"{status.credits:0} 积分" : "—";
        }
        else _lblStatus.Text = "获取失败";

        // 每天首次刷新时记录当前总积分（当前激活账号），便于趋势图立即有数据
        if (remaining >= 0 && !TotalHistoryHasToday())
            AppendTotalHistory(DateTime.Now, remaining);

        await RefreshCloudStatusAsync();
    }
```

- [ ] **Step 5: `DoCheckinAsync` 遍历所有启用账号**

替换 `DoCheckinAsync()`(L700-753)：

```csharp
    private async Task DoCheckinAsync()
    {
        var enabled = _accountStore.EnabledAccounts().ToList();
        if (enabled.Count == 0)
        {
            SetLog("没有可签到的账号，请先在设置页添加账号。");
            return;
        }
        SetLog($"开始为 {enabled.Count} 个账号签到…");

        var results = new List<(TraeAccount Acc, bool Ok, double Gained, double Remaining)>();
        foreach (var acc in enabled)
        {
            await CheckinOneAsync(acc, results);
        }

        // 本地飞书推送：多账号汇总成一条
        await NotifyFeishuBatchAsync(results);

        SetLog("本轮全部账号签到结束。");
        await RefreshAllAsync();
    }

    private async Task CheckinOneAsync(TraeAccount acc, List<(TraeAccount, bool, double, double)> results)
    {
        var name = DisplayName(acc);
        var status = await GetStatusWithValidTokenAsync(acc);
        if (status != null && status.checked_in)
        {
            SetLog($"[{name}] 今日已签到，无需重复签到。");
            if (acc.LastCheckinDate != DateTime.Today)
                RecordCheckin(acc, status.credits);
            var rem = await RemainingOfAsync(acc);
            results.Add((acc, true, status.credits, rem));
            return;
        }
        if (string.IsNullOrEmpty(acc.Token))
        {
            SetLog($"[{name}] 未登录，跳过。");
            results.Add((acc, false, 0, -1));
            return;
        }
        SetLog($"[{name}] 正在签到…");
        var result = await _api.ClaimAsync(acc.Token, acc.DeviceId);
        if (result == null)
        {
            SetLog($"[{name}] 签到失败：" + (_api.LastError ?? "未知错误"));
            results.Add((acc, false, 0, -1));
            return;
        }
        var after = await _api.GetStatusAsync(acc.Token, acc.DeviceId);
        var gained = CheckinEvaluator.ResolveGainedCredits(after ?? result);
        if (gained > 0)
        {
            SetLog($"[{name}] 签到成功！获得 {gained:0} 积分。");
            RecordCheckin(acc, gained);
            var total = await RemainingOfAsync(acc);
            if (total >= 0 && acc.Id == CurAccount?.Id) AppendTotalHistory(DateTime.Now, total);
            results.Add((acc, true, gained, total));
            NotifyNativeCheckin(true, name, gained, total);
        }
        else
        {
            var msg = result.message;
            SetLog($"[{name}] 签到失败：{msg ?? (_api.LastError ?? "未知错误")}");
            results.Add((acc, false, 0, -1));
            NotifyNativeCheckin(false, name);
        }
    }

    private async Task<double> RemainingOfAsync(TraeAccount acc)
    {
        if (string.IsNullOrEmpty(acc.Token)) return -1;
        return await _api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
    }

    /// <summary>本地飞书推送：把一批账号结果汇总为一条消息（webhook 为空自动跳过）。</summary>
    private async Task NotifyFeishuBatchAsync(List<(TraeAccount Acc, bool Ok, double Gained, double Remaining)> results)
    {
        if (string.IsNullOrWhiteSpace(_config.FeishuWebhook) || results.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Trae 多账号签到结果");
        foreach (var r in results)
        {
            var name = DisplayName(r.Acc);
            sb.AppendLine((r.Ok ? "✅ " : "⚠️ ") + name + (r.Ok ? $"  获得 {r.Gained:0} 积分" : "  失败"));
        }
        var ok = await FeishuNotifier.SendTextAsync(_config.FeishuWebhook, sb.ToString());
        SetLog(ok ? "飞书推送已发送。" : "飞书推送发送失败，请检查 webhook。");
    }
```

> 保留旧的 `NotifyNativeCheckin(bool, double, double)` 单账号重载供托盘手动调用，另加 `NotifyNativeCheckin(bool success, string accountName, double gainedCredits, double remaining)` 重载（内部同实现，仅标题带账号名）。若不需托盘逐账号气泡，可简化为不新增重载——`CheckinOneAsync` 中调用处使用带 name 重载。

同时新增 `DisplayName`：
```csharp
    private static string DisplayName(TraeAccount acc)
        => string.IsNullOrWhiteSpace(acc.Name) ? "账号 " + (acc.Id[..4].ToUpperInvariant()) : acc.Name!;
```

- [ ] **Step 6: `CheckAutoCheckinAsync` 改为「到点后为所有启用账号签到」**

替换 `CheckAutoCheckinAsync()`(L605-620)：

```csharp
    private async Task CheckAutoCheckinAsync()
    {
        if (!_config.AutoCheckinEnabled) return;
        var now = DateTime.Now;
        if (now.Date == _lastAutoCheck.Date) return;
        if (!TimeSpan.TryParse(_config.AutoCheckinTime, out var target)) return;
        var scheduled = now.Date.Add(target);
        if (now < scheduled) return;
        _lastAutoCheck = now.Date;
        var late = (now - scheduled).TotalSeconds > 120;
        SetLog(late
            ? $"已超过设定的自动签到时间 {target:hh\\:mm}，执行补签…"
            : $"到达自动签到时间 {target:hh\\:mm}，开始签到…");
        await DoCheckinAsync();
    }
```
（DoCheckinAsync 内部已遍历启用账号，定时器无需重复逻辑。）

- [ ] **Step 7: 历史与文案适配**

- 新增 `RecordCheckin(TraeAccount, double)` 重载，替换原 `RecordCheckin(double)` 调用：
```csharp
    private void RecordCheckin(TraeAccount account, double credits)
    {
        account.LastCheckinDate = DateTime.Today;
        _config.Save();
        AppendHistory(DateTime.Now, credits, DisplayName(account));
    }
```
原 `RecordCheckin(double)`（无账号）删除。

- `AppendHistory` 改为带可选账号名前缀（兼容旧行）：
```csharp
    private void AppendHistory(DateTime time, double credits, string accountName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.AppendAllText(HistoryPath,
                $"{time:yyyy-MM-dd HH:mm}  [{accountName}]  签到成功  +{credits:0} 积分{Environment.NewLine}");
        }
        catch { }
        ReloadHistory();
    }
```

- `UpdateTokenDisplay()` 面向激活账号：
```csharp
    private void UpdateTokenDisplay()
    {
        var acc = CurAccount;
        _txtToken.Text = acc == null || string.IsNullOrEmpty(acc.Token) ? "未登录，暂无 token" : acc.Token;
        _lblTokenTime.Text = acc == null
            ? "未登录"
            : "最后更新：" + (acc.TokenUpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "从未");
    }
```

- `CopyToken()` 内 `_config.Token` 改为激活账号 token：
```csharp
        var acc = CurAccount;
        if (acc == null || string.IsNullOrEmpty(acc.Token))
        {
            MessageBox.Show("暂无 token，请先登录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Clipboard.SetText(acc.Token);
            SetLog("[" + DisplayName(acc) + "] Token 已复制到剪贴板。");
        }
```

- [ ] **Step 8: 整仓编译通过**

Run: `dotnet build TraeCheckin.csproj -c Release`
Expected: `0 个错误`。逐一处理 Task 2 遗留的调用点（若有漏改：`_api.GetStatusAsync/ClaimAsync/GetRemainingCreditsAsync` 调用均补 deviceId；`new TraeApiClient(x)` 去参）。还有托盘「立即签到」的 `_btnCheckin`/`_trayMenu` 直接调 `DoCheckinAsync()`（无参，已保留签名）不受影响。

- [ ] **Step 9: 提交**

```bash
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" add Forms/MainForm.cs Api/TraeApiClient.cs
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" commit -m "feat: 本地签到改为遍历多账号，各账号独立 DeviceId/今日状态/历史前缀"
```

---

## Task 5: 设置页账号管理 UI + 仪表盘适配

**Files:**
- Modify: `Forms/MainForm.cs`（字段区 / BuildSettings / BuildAccountRow / BuildTokenRow 标题 / 页面行高）

**目标：** 「账号」卡改为可管理多账号（下拉 + 登录/添加/删除 + 会话倒计时），仪表盘标题与激活账号联动。

- [ ] **Step 1: 新增字段**

在字段区 `_lblTokenTime` 后新增：

```csharp
    private readonly ComboBox _cmbAccount = new();
```

- [ ] **Step 2: 替换 `BuildAccountRow()`(L472-488)**

整方法替换为：

```csharp
    private Control BuildAccountRow()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = CardBg };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CardBg };

        _cmbAccount.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAccount.Width = 260;
        _cmbAccount.Height = 28;
        _cmbAccount.Margin = new Padding(0, 3, 10, 0);
        _cmbAccount.FlatStyle = FlatStyle.Flat;
        _cmbAccount.SelectedIndexChanged += (_, _) => OnAccountSelected();
        top.Controls.Add(_cmbAccount);

        var btnAdd = new Button { Text = "添加账号", Width = 96, Height = 30, Margin = new Padding(0, 2, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Cursor = Cursors.Hand };
        btnAdd.FlatAppearance.BorderSize = 0;
        btnAdd.Click += async (_, _) => await AddAccountAsync();
        top.Controls.Add(btnAdd);

        var btnLogin2 = new Button { Text = "重新登录", Width = 92, Height = 30, Margin = new Padding(0, 2, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = CardBg, ForeColor = TextMain, Cursor = Cursors.Hand };
        btnLogin2.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        btnLogin2.Click += async (_, _) => { if (CurAccount != null) await LoginAndRefreshAsync(CurAccount); };
        top.Controls.Add(btnLogin2);

        var btnDel = new Button { Text = "删除", Width = 70, Height = 30, Margin = new Padding(0, 2, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = CardBg, ForeColor = TextMain, Cursor = Cursors.Hand };
        btnDel.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
        btnDel.Click += (_, _) => RemoveAccount();
        top.Controls.Add(btnDel);

        var lbl = new Label { Text = "切换账号即刷新仪表盘。", ForeColor = TextMuted, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        top.Controls.Add(lbl);

        table.Controls.Add(top, 0, 0);
        RefreshAccountCombo();
        return table;
    }

    private void RefreshAccountCombo()
    {
        var prev = _config.ActiveAccountId;
        _cmbAccount.Items.Clear();
        foreach (var a in _config.Accounts)
            _cmbAccount.Items.Add(ComboText(a));
        int idx = _config.Accounts.FindIndex(a => a.Id == prev);
        _cmbAccount.SelectedIndex = idx >= 0 ? idx : (_config.Accounts.Count > 0 ? 0 : -1);
        UpdateTokenDisplay();
    }

    private string ComboText(TraeAccount a)
    {
        var days = "";
        if (a.TokenUpdatedAt.HasValue)
            days = "（Token 更新于 " + a.TokenUpdatedAt.Value.ToString("MM-dd HH:mm") + "）";
        return DisplayName(a) + (a.Enabled ? "" : "（停用）") + days;
    }

    private void OnAccountSelected()
    {
        if (_cmbAccount.SelectedIndex < 0) return;
        var id = _config.Accounts[_cmbAccount.SelectedIndex].Id;
        if (_config.ActiveAccountId != id)
        {
            _config.ActiveAccountId = id;
            _config.Save();
            _ = RefreshAllAsync();
        }
        UpdateTokenDisplay();
    }

    private async Task AddAccountAsync()
    {
        var acc = _accountStore.AddNew();
        _accountStore.EnsureDeviceId(acc);
        _config.Save();
        RefreshAccountCombo();
        var status = await LoginAndRefreshAsync(acc);
        if (status == null)
        {
            // 登录取消/失败：移除刚建的空壳账号
            _accountStore.Remove(acc.Id);
            _config.Save();
            RefreshAccountCombo();
            return;
        }
        if (string.IsNullOrWhiteSpace(acc.Name))
        {
            acc.Name = "账号" + (_config.Accounts.Count);
            _config.Save();
        }
        RefreshAccountCombo();
        await RefreshAllAsync();
        SetLog("已添加账号 " + DisplayName(acc));
    }

    private void RemoveAccount()
    {
        var acc = CurAccount;
        if (acc == null) return;
        if (_config.Accounts.Count <= 1)
        {
            MessageBox.Show("至少保留一个账号。要清空请删除后重新添加。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var name = DisplayName(acc);
        var r = MessageBox.Show($"确定删除账号「{name}」？（仅删除本地记录，不影响该 Trae 账号）", "删除账号",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        try { Directory.Delete(AccountWebViewDir(acc), recursive: true); } catch { /* 忽略删除失败 */ }
        _accountStore.Remove(acc.Id);
        _config.Save();
        RefreshAccountCombo();
        SetLog("已删除账号：" + name);
        _ = RefreshAllAsync();
    }
```

- [ ] **Step 3: BuildSettings 行高与标题适配**

`BuildSettings()` 中「账号」卡行高 100 不足以放新控件，且窗口放大后重排行高。将 `BuildSettings()` 内 row 定义：

```csharp
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
```
改为（token 卡 150→140，账号卡 100→150，总高 +100，窗口已放大容纳）：
```csharp
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
```

并把「登录 Token」「账号」两卡的标题带上激活账号名（区分当前查看谁的 token）。将：
```csharp
        var tokenPanel = CardPanel("登录 Token", BuildTokenRow());
        grid.Controls.Add(tokenPanel, 0, 2);

        var acctPanel = CardPanel("账号", BuildAccountRow());
        grid.Controls.Add(acctPanel, 0, 3);
```
改为：
```csharp
        var tokenPanel = CardPanel("当前账号登录 Token", BuildTokenRow());
        grid.Controls.Add(tokenPanel, 0, 2);

        var acctPanel = CardPanel("多账号管理", BuildAccountRow());
        grid.Controls.Add(acctPanel, 0, 3);
```

- [ ] **Step 4: OnShown 中按激活账号初始化**

`OnShown`(L588) 内 `hasToken` 判定改为任一启用账号有 token：
```csharp
        var anyToken = _config.Accounts.Any(a => !string.IsNullOrEmpty(a.Token));
        SetLog($"程序已启动，已{(anyToken ? "登录" : "未登录，请在设置页添加/登录账号")}");
        await RefreshAllAsync();
        StartAutoTimer();
```
`RefreshAccountCombo()` 也需在 `BuildSettings` 后执行一次——BuildAccountRow 已调用；若设置页惰性构建需在 ShowPage 切页刷新。为稳妥，在 `ShowPage(index)` 中当 `index==3` 时调用 `RefreshAccountCombo()`：
```csharp
        for (int i = 0; i < _pages.Count; i++)
        {
            ...
        }
        if (index == 3) RefreshAccountCombo();   // 切到设置页时同步账号下拉
```

- [ ] **Step 5: 编译 + 全量测试**

Run: `dotnet build TraeCheckin.csproj -c Release`
Expected: `0 个错误`。
Run: `dotnet test`
Expected: 全部通过（预计 46）。

- [ ] **Step 6: 提交**

```bash
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" add Forms/MainForm.cs
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" commit -m "feat: 设置页新增多账号管理（下拉切换/添加/删除），窗口放大至 1200x780"
```

---

## Task 6: 可行度验证（Phase 1 收尾）

**Files:** 无代码改动

- [ ] **Step 1: 全量测试 + 编译**

Run: `dotnet test`（37+3+6 ≈ 46）→ 全绿；`dotnet build TraeCheckin.csproj -c Release` → 0 错误。

- [ ] **Step 2: 同步产物并重启冒烟**

```powershell
Stop-Process -Name TraeCheckin -ErrorAction SilentlyContinue
Copy-Item "bin\TraeCheckin\TraeCheckin.exe","bin\TraeCheckin\TraeCheckin.dll","bin\TraeCheckin\TraeCheckin.pdb","bin\TraeCheckin\TraeCheckin.deps.json","bin\TraeCheckin\TraeCheckin.runtimeconfig.json" "..\TraeCheckin开发\" -Force
Start-Process "..\TraeCheckin开发\TraeCheckin.exe"
```
验证（GUI，需用户配合）：
1. 窗口明显变大（1200×780）。
2. 旧单账号配置启动后自动进入「账号1」，可正常查看状态。
3. 设置页「多账号管理」下拉可见，点「添加账号」→ 弹出独立登录窗，登录第二账号成功后列表出现两账号；切下拉仪表盘数据随之切换。
4. 「立即签到」日志逐账号输出，history.txt 行带 `[账号名]` 前缀。

- [ ] **Step 3: 汇报**

向用户汇报测试数、编译结果、GUI 冒烟结果、遗留问题（若有）；并预告 Phase 2（云端多账号）将单独规划。

---

## 自审记录

**Spec 覆盖（Phase 1 部分）：**
- TraeAccount 模型 + 旧字段兼容 → Task 1 ✅
- 一次性迁移（Load 中 TryMigrateLegacy）→ Task 1 ✅
- deviceId 每账号独立（TraeApiClient 去单例绑定）→ Task 2 ✅
- AccountStore 辅助 → Task 3 ✅
- 本地签到遍历启用账号 + 各账号独立今日状态/历史前缀 → Task 4 ✅
- WebView2 目录按账号隔离 → Task 4 Step1 AccountWebViewDir + Task5 删除时清理 ✅
- 设置页账号管理区（下拉/添加/删除）→ Task 5 ✅
- 主窗口放大 → Task 4 Step1（1200×780）✅
- 仪表盘随激活账号刷新 → Task 4 RefreshAllAsync + Task5 OnAccountSelected ✅

**Type/命名一致性：** `TraeAccount{Id,Name,Token,Session,DeviceId,TokenUpdatedAt,LastCheckinDate,Enabled}`；`AccountStore{ActiveAccount,EnabledAccounts,SetActive,AddNew,Remove,EnsureDeviceId}`；`CurAccount`、`DisplayName`、`AccountWebViewDir`、`RefreshAccountCombo`、`ComboText`、`OnAccountSelected`、`AddAccountAsync`、`RemoveAccount`、`CheckinOneAsync`、`NotifyFeishuBatchAsync`、`RemainingOfAsync`、`RecordCheckin(TraeAccount,double)` 在文中首用处即定义，无悬空引用。

**已知风险：**
- `AppConfig.Load()` 现在 Save 于迁移与 Aha 两处——保持现有行为，勿重复清理。
- 手动托盘「立即签到」走无参 `DoCheckinAsync`，遍历逻辑已在其中，无需额外改。
- `LoginAndRefreshAsync(CurAccount)` 空引用：Task5 btnLogin2 已判 `CurAccount != null`；Task4 Step3 由调用方保证传非空账号。
- 历史文件旧行无前缀：ReloadHistory 原样展示，不强制迁移（可读性可接受）。
