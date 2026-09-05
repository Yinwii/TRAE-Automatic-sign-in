# 设计：多账号签到（本地 + 云端）

日期：2026-09-05 ｜ 状态：已批准（分三部分逐节确认） ｜ 目标版本：v1.5.0 ｜ 来源：issue #3「多账号签到」

## 背景与动机

GitHub issue #3（KLFDan0534 提出）请求支持多账号签到。澄清后确认为**同一用户持有多个 Trae 账号**（不同手机号注册），希望在本地与云端两层都支持：本地 App 能添加多个账号并到点逐个自动签到；云端部署能在单个 fork 仓库里存多份 session 每天循环签到。

## 目标

- 本地：可添加/删除多个 Trae 账号，每个账号独立登录（WebView2 数据目录隔离）、独立 DeviceId、独立「今日已签」状态；自动签到遍历所有启用账号。
- 云端：一个 fork 仓库通过 N 组 secret（`TRAE_SESSION` + `TRAE_SESSION_2...`）部署多账号，`checkin.py` 循环签到，任一个失败则整体红标。
- 向后兼容：老用户单账号配置自动迁移为第一个账号；云端第 1 账号沿用旧 secret 名不破坏既有部署。
- 主窗口适当放大，缓解各功能区 UI 拥挤。

## 非目标

- 不引入多人/多用户隔离体系（账号归属同一个人）。
- 不做账号间积分曲线分开绘制（趋势图仅展示当前激活账号）。
- 不做账号顺序拖拽排序（重部署顺序以本地列表当前顺序为准）。
- 不改云端签到接口本身（仍复用 GetUserToken + claim 链路）。

## 组件设计

### 1. 数据模型（新增/改 `Config/AppConfig.cs`）

新增模型（放 AppConfig.cs 同级或独立 `Config/TraeAccount.cs`）：

```csharp
public class TraeAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? Name { get; set; }            // 备注名
    public string? Token { get; set; }           // JWT（8h，自动换新）
    public string? Session { get; set; }         // X-Cloudide-Session Cookie（约 14 天）
    public string DeviceId { get; set; } = "";   // 每账号独立（16 位数字）
    public DateTime? TokenUpdatedAt { get; set; }
    public DateTime? LastCheckinDate { get; set; } // 该账号最近签到日（本地）
    public bool Enabled { get; set; } = true;      // 是否参与本地自动签到
}
```

AppConfig 增加：
```csharp
public List<TraeAccount> Accounts { get; set; } = new();
public string? ActiveAccountId { get; set; }   // 仪表盘当前展示账号
```

旧字段 `Token / Session / DeviceId / TokenUpdatedAt / LastCheckinDate` **保留**做迁移源与兼容读取。

**迁移逻辑（`Load()` 中）**：若 `Accounts` 为空 且 旧 `Token` 或 `Session` 非空 → 构造第一个账号（Name="账号 1"）放入 Accounts，随后可清旧字段；若两者皆空则保持现状。迁移只执行一次。

### 2. 账号隔离资源

- **WebView2 用户数据目录按账号隔离**：`%LOCALAPPDATA%\TraeCheckin\WebView\account_<Id>\`（现为共享 `WebView\`）。LoginForm 增加 userDataDir 参数（已是参数传入，改造小）。
- **DeviceId 每账号独立**：新增账号时走 `TryResolveAhaDeviceId()` 相同规则；不可得则随机 16 位。
- 历史文件：
  - `history.txt` 每行前缀 `[账号名] `（现有行不带前缀时按账号 1 展示，向后兼容）。
  - `credits_total.txt` 仅记录**当前激活账号**的总积分（切账号时重新从该账号拉一次并入曲线）。

### 3. 账号辅助方法（新增 `Services/AccountStore.cs` 或并入 MainForm）

纯逻辑、可单测：
- `GetAccounts()` / `GetActive()` / `SetActive(id)`
- `AddAccount(TraeAccount)` / `RemoveAccount(id)`（同时删 WebView2 子目录）
- `EnsureAccountDeviceId(account)` —— DeviceId 空时填充
- `EnabledAccounts()` —— 参与自动签到的有序列表

### 4. 本地 UI（`Forms/MainForm.cs` 设置页 + 仪表盘）

- **主窗口放大**：`MainForm` 初始 ClientSize 适当调大（建议 1200×760），核对各页面固定行高是否溢出（尤其 Dashboard 5 行、Cloud 3 行）。
- **设置页「账号」卡改造**（现 `BuildAccountRow` 区域）：
  - 账号下拉（ComboBox，展示 `Name（Session 剩余天数）`）+ 切换按钮 → 写 `ActiveAccountId` 并 `RefreshAllAsync`
  - 「登录当前账号」→ 对当前激活账号弹内嵌登录（独立 userDataDir）
  - 「添加账号」→ 新 LoginForm（新 userDataDir），成功后入 Accounts 并设为激活
  - 「删除账号」→ 确认框后移除（至少保留 1 个；删最后一个后视为未登录态）
- **设置页其他卡不动**（token 展示等按当前激活账号读取）。
- 仪表盘/历史/云端页读取均以激活账号为数据源。

### 5. 本地签到流程改造（`MainForm.cs`）

- `GetStatusWithValidTokenAsync()` 改为接收账号参数（`_api.GetStatusAsync(token)` 已 token 参数化）。
- `DoCheckinAsync()`：遍历 `EnabledAccounts()` 顺序签到；每账号独立「今日已签」判断与 `RecordCheckin`；日志逐账号输出「[账号名] 正在签到… / 成功 +N」。
- 定时器 `CheckAutoCheckinAsync()`：对所有启用账号签到，去重逻辑基于「所有账号当日都签过」或逐账号 `LastCheckinDate`（采用逐账号）。
- 飞书推送（本地）：若配了 webhook，多账号签到后汇总成一条（成功/失败账号列表），不再单账号发多次。
- `GetStatusWithValidTokenAsync` 中"token 失效→session 换新→再登录"整条链都要按账号操作。

### 6. 云端 secret 部署（`Forms/MainForm.Cloud.cs` + `Api/GitHubApiClient.cs`）

- **Secret 命名**：账号 1 → `TRAE_SESSION` / `TRAE_DEVICE_ID`（保持旧名）；账号 N(N≥2) → `TRAE_SESSION_N` / `TRAE_DEVICE_ID_N`。飞书 webhook 仍单份 `FEISHU_WEBHOOK`。
- `DeployAsync()`：按 `EnabledAccounts()` 顺序写 secrets；只部署 Enabled 账号。重新部署幂等覆盖。
- `HasSecretsAsync` 判定：`TRAE_SESSION` 存在即第一账号就绪，再依次探测 `TRAE_SESSION_2, _3...` 直到 404；返回 `(bool firstReady, int accountCount)` 结构以支持 IsDeployed 展示「N 个账号已部署」。
- `GitHubApiClient` 常量新增 `GetSessionSecretName(int index)` / `GetDeviceSecretName(int index)` 辅助（index 从 1 起，1 返回无后缀）。

### 7. checkin.py 多账号循环

```python
def iter_sessions():
    s = os.environ.get("TRAE_SESSION", "").strip()
    if s: yield (1, s, os.environ.get("TRAE_DEVICE_ID", "").strip())
    n = 2
    while True:
        s = os.environ.get(f"TRAE_SESSION_{n}", "").strip()
        if not s: break
        yield (n, s, os.environ.get(f"TRAE_DEVICE_ID_{n}", "").strip())
        n += 1
```

- `main()`：遍历 iter_sessions()，每个账号独立换 token + claim；结果分行打印 `[账号 n] ...`。
- 汇总判定：任一账号失败 → 最后 `sys.exit(1)`；全部成功 → 0。
- 飞书推送改为一条汇总（成功列表 + 失败列表）；无账号时维持现有报错行为。
- `checkin.yml` 无需改动（secret 名经 Actions 自动映射环境变量 `TRAE_SESSION_2` 需在 yml 显式声明！→ 改为使用 `env:` 下通配注入不可行，需把脚本改为直接读 `${{ secrets.TRAE_SESSION_N }}`？**方案：checkin.yml 不逐行声明，改为 checkin.py 无法直接读 secrets —— 因此 checkin.yml 必须显式列出 TRAE_SESSION_2..._N。**）

> ⚠️ 修正上面矛盾点：GitHub Actions 里 secrets 需在 `env:` 显式映射才能进入进程环境。**checkin.yml 需显式枚举**：
> ```yaml
> env:
>   TRAE_SESSION: ${{ secrets.TRAE_SESSION }}
>   TRAE_DEVICE_ID: ${{ secrets.TRAE_DEVICE_ID }}
>   TRAE_SESSION_2: ${{ secrets.TRAE_SESSION_2 }}
>   TRAE_DEVICE_ID_2: ${{ secrets.TRAE_DEVICE_ID_2 }}
>   TRAE_SESSION_3: ${{ secrets.TRAE_SESSION_3 }}
>   TRAE_DEVICE_ID_3: ${{ secrets.TRAE_DEVICE_ID_3 }}
>   TRAE_SESSION_4: ${{ secrets.TRAE_SESSION_4 }}
>   TRAE_DEVICE_ID_4: ${{ secrets.TRAE_DEVICE_ID_4 }}
>   FEISHU_WEBHOOK: ${{ secrets.FEISHU_WEBHOOK }}
> ```
> 支持上限 4 账号；缺的 secrets 展开为空，checkin.py 遇空即停（向后兼容 1 账号）。如需更多上限可再扩（每扩 2 行 yml）。

### 8. 错误处理汇总

| 场景 | 行为 |
|---|---|
| 旧版单账号配置 | Load 时自动迁移为 Accounts[0] |
| 添加账号登录取消/失败 | 不写入列表，提示 |
| 删除最后一个账号 | 确认后清空，回到未登录态 |
| 某账号签到失败 | 本地继续签其余账号，日志标红；云端该账号标失败且整体红标 |
| 云端写 secret 中途失败 | 沿用现 HandleDeployFailure（授权失效清理 / 提示重试） |
| 云端部署超过 4 账号 | UI 提示最多 4 个（checkin.yml 上限），超出需扩 yml |

## 测试计划（TDD）

新增 `TraeCheckin.Tests/AccountTests.cs`（纯逻辑）：
1. 迁移：旧 Token/Session 非空、Accounts 空 → 生成 1 个账号，旧字段可清。
2. 迁移幂等：已有 Accounts 时不再追加。
3. Secret 命名：index=1 → `TRAE_SESSION`；index=2 → `TRAE_SESSION_2`。
4. DeviceId 补全：空 → 生成 16 位数字；非空 → 保留。
5. Enabled 过滤顺序。

`checkin.py` 逻辑用「离线造 2 个假 session 环境变量 + monkeypatch urllib」的轻量方式可选验证（若不便则靠真实云部署 E2E）。

## 可行度验证

1. `dotnet test` 全绿（新增用例后预计 45+）。
2. Release 编译 0 错误。
3. 本地：添加第 2 个账号 → 两账号均能独立签到、历史带前缀、仪表盘可切换。
4. 云端：部署 2 账号后手动触发 workflow → Actions 日志出现 `[账号 1]` 与 `[账号 2]` 两行；造一个失效 session 验证整体红标。

## 影响面

- 改：`Config/AppConfig.cs`（+TraeAccount 可放同文件或新文件）、`Forms/MainForm.cs`、`Forms/MainForm.Cloud.cs`、`Api/GitHubApiClient.cs`、`checkin.py`、`.github/workflows/checkin.yml`、`Forms/LoginForm.cs`（userDataDir 已参数化，仅调用方传不同值）。
- 新增：`Services/AccountStore.cs`（可选）、测试文件、spec/plan 文档。
- 不改：签到 API 链路、云端 secret 加密逻辑、飞书 webhook 机制。
