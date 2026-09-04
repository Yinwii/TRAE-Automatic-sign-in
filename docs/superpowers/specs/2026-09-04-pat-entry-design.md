# 设计：云端页「手动粘贴 PAT」入口

日期：2026-09-04 ｜ 状态：已批准 ｜ 目标版本：v1.4.5

## 背景与动机

TraeCheckin 的 GitHub 授权使用 device-flow OAuth（OAuth App `Ov23lix0Kb9ldJHrOpKv`）。实测该 App 分发的 token 会被 GitHub 风控快速撤销（最快几秒，今天已连续撤销 5 个），导致用户必须反复走网页授权，且无法可靠完成部署/状态检测/发版。

改用 **Fine-grained PAT**（用户在 GitHub 网页手动创建、不依附 OAuth App、不会被自动撤销）作为长期凭据。本设计为程序增加「手动粘贴 PAT」入口；PAT 本身已实证可用（`GET /user` 200、`GET /repos` 返回 `permissions.push=true`、实际完成过 PR merge）。

## 目标

- 未授权时，云端页可同时选择「网页授权」或「粘贴 PAT」两种方式。
- 粘贴 PAT 时做「有效性 + 写权限」两级校验，避免存入部署时才发现用不了的 token。
- PAT 保存后，现有部署/状态检测/仪表盘全部逻辑零改动复用。
- 不引入对 OAuth App 的任何新依赖。

## 非目标

- 不迁移/删除现有 device-flow 网页授权（保留为可选方式）。
- 不新增「授权方式」存储字段或 UI 标记（YAGNI，当前以 login 展示即可）。
- 不改动云端部署流程本身。

## 组件设计

### 1. `Controls/TextInputDialog.cs`（新增）

轻量模态输入弹窗，单例模式（一次一个实例）。元素：

- 标题：调用方传入（如「粘贴 GitHub Token」）
- 说明区（可多行）：调用方传入的引导文案（如何创建 PAT、需要勾选哪些权限）
- `TextBox`：密码掩码显示；提供「显示/隐藏」勾选
- 「从剪贴板粘贴」按钮：读剪贴板填入
- 确定 / 取消

返回 `string?`（取消/关闭返回 null）。样式沿用现有主题色（CardBg、Accent、TextMain 等）。

### 2. `GitHubApiClient` 校验方法（改 `Api/GitHubApiClient.cs`）

新增返回类型与方法：

```csharp
public class PatValidation
{
    public bool IsValid { get; set; }
    public string? Login { get; set; }
    public bool CanWrite { get; set; }
    public string? Error { get; set; }
}

public async Task<PatValidation> ValidatePatAsync(string token)
```

流程：

1. 预检：`IsPlausibleToken(token)`——空或长度 < 20 直接返回 Error =「请输入有效的 GitHub Token（github_pat_ 或 gho_ 开头）」。放行 PAT 与旧 OAuth token 两种格式。
2. `GET /user`：非 200 → Error =「Token 无效或已被撤销」；200 → 解析 login。
3. 写权限探测，目标仓库按 login 分派：
   - login == `star620`（owner）：`GET /repos/star620/TRAE-Automatic-sign-in`，要求 `permissions.push == true`。
   - 其他 login：`GET /repos/{login}/TRAE-Automatic-sign-in`。
     - 200 且 `permissions.push == true` → 通过。
     - 200 但 push != true → Error = 权限不足提示。
     - 404（尚未 fork）→ **放行**，`CanWrite=false`（fork 后对自有仓库天然可写，避免卡住首次部署）。
     - 其他状态码 → Error = `HTTP {code}`。
4. 网络异常 → 捕获并 Error = 异常消息（UI 提示可重试，不保存）。

配套可测静态成员：

- `IsPlausibleToken(string token)`：预检。
- `ParsePermissionsPush(JsonElement root)` / 重载接收 `string json`：解析 `permissions.push`。
- `BuildPatScopeHint()`：权限不足文案，列出需勾选权限（Contents、Workflows、Pull requests、Secrets、Actions 均 Read and write，Metadata 只读自动带）。

### 3. 云端页 UI（改 `Forms/MainForm.Cloud.cs`）

字段：新增 `_btnUsePat`（次按钮）、`_cloudActionRow`（底部 1×2 容器）。

`BuildCloudAction()` 改造：

- 底部按钮区替换为 `TableLayoutPanel _cloudActionRow`（Dock=Bottom，高 40，2 列各 50%）：
  - 列 0：`_btnCloudAction`（主按钮）
  - 列 1：`_btnUsePat`（次按钮，文本「粘贴 Token（PAT）」，样式：白底描边）
- 新增 `UpdateCloudActionUi(bool patOption)`，集中控制：
  - `patOption=true`（未授权态）：两按钮都可见，主按钮文本「授权 GitHub」。
  - `patOption=false`（已授权/检测中/已部署）：`_btnUsePat` 隐藏，`_btnCloudAction` `ColumnSpan=2`。
- 现有各状态入口（`RefreshCloudState`、`RefreshDeploymentStateAsync`、`DeployAsync` 结束、`ClearCloudAuth`）统一改调 `UpdateCloudActionUi` 收敛文案/显隐逻辑，消除目前散落设置 `_btnCloudAction.Text/Enabled` 的做法。

`_btnUsePat.Click` → `UsePatAsync()`：

1. `TextInputDialog` 弹出，说明文案含：创建地址 `https://github.com/settings/personal-access-tokens/new`、选择本仓库、勾选 Actions/Contents/Pull requests/Secrets/Workflows = Read and write、Metadata = Read（自动）。
2. 取回输入为空 → 直接返回。
3. `_cloudBusy = true` 期间禁用按钮，调 `_ghApi.ValidatePatAsync(token)`。
4. 失败（`!IsValid`）→ `SetCloudLog` 红字显示 `Error`，不保存。
5. 成功：
   - `_config.GitHubToken = token; _config.GitHubLogin = login; _config.Save();`
   - `SetCloudLog("已使用 PAT 授权：" + login + (CanWrite ? "" : "（尚未检测到 fork 仓库，部署时会自动创建）"))`
   - `RefreshCloudState()`（触发部署状态检测）
6. `finally` 释放 `_cloudBusy`。

### 4. 存储

复用 `_config.GitHubToken` / `_config.GitHubLogin` 字段。PAT 与网页 token 互斥覆盖（后存者生效）。下游（`RefreshDeploymentStateAsync` / `GetDeploymentStatusAsync` / 仪表盘 `RefreshCloudStatusAsync`）读同一字段，零改动。

### 5. 错误处理汇总

| 场景 | 行为 |
|---|---|
| 输入为空 / 长度 <20 | 弹窗内校验或日志提示，不调 API |
| `/user` 非 200 | 日志「Token 无效或已被撤销」，不保存 |
| owner 仓库 push=false | 日志权限不足 + 勾选指引，不保存 |
| 非 owner 已 fork 但 push=false | 同上 |
| 非 owner 未 fork（404） | 保存，日志注明部署时自动 fork |
| 网络异常 | 日志异常消息，提示重试，不保存 |

## 测试计划（TDD）

新增到 `TraeCheckin.Tests/GitHubApiClientTests.cs` 或新文件 `PatValidationTests.cs`：

1. `IsPlausibleToken`：null/空/短 → false；`github_pat_` 长串 → true；`gho_` 长串 → true。
2. `ParsePermissionsPush`：含 `permissions.push=true` 的 JSON → true；`push=false` → false；无 `permissions` → false；`push` 缺失 → false。
3. `BuildPatScopeHint` 文案包含关键权限名（Contents、Workflows）。

HTTP 层（/user、/repos 分派、404 放行）不 mock，通过实现后真实 PAT 端到端手测覆盖（本仓库 owner 场景：`push=true` 通过；另可用一次性无效串验证 401 拒绝路径）。

## 可行度验证（实现完成后执行）

1. `dotnet test` 全绿（含新增用例，预期 28+）。
2. 主程序 Release 编译 0 错误。
3. 真实 PAT 端到端：启动应用 → 云端页「粘贴 Token（PAT）」→ 粘贴用户 PAT → 应校验通过并显示「已使用 PAT 授权：star620」，随后部署状态检测正常返回「已部署完成」（工作流每天在跑）。

## 影响面

- 改动文件：`Api/GitHubApiClient.cs`、`Forms/MainForm.Cloud.cs`、新增 `Controls/TextInputDialog.cs`、新增测试。
- 不改动：`checkin.py` 云脚本、`.github/workflows/checkin.yml`、`MainForm.cs` 仪表盘、`Config/AppConfig.cs`（仅复用字段）。
