# 多账号签到（云端）实现计划 — Phase 2

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 云端一个 fork 仓库支持最多 4 个 Trae 账号：部署页把全部启用账号写成 `TRAE_SESSION[_N]` / `TRAE_DEVICE_ID[_N]` 成对 secret，`checkin.py` 逐账号换 token + 签到并汇总一条飞书推送，任一失败整体红标；旧 1 账号部署完全向后兼容。

**Architecture:** `GitHubApiClient` 增加序号 secret 命名辅助与「连续已部署账号数」检测（含删除多余 secret）；`MainForm.Cloud.DeployAsync` 由「只写激活账号」改为「遍历所有启用账号 + 清理多余 secret + 上限 4 校验」，状态文案带账号数；`checkin.yml` 显式枚举 `TRAE_SESSION[_N]` 到 4；`checkin.py` 改为 `iter_sessions()` 多账号循环，逐账号输出 `[账号 N]` 行，结束统一飞书汇总并在任一失败时 `sys.exit(1)`。

**Tech Stack:** C# / .NET 9 / WinForms / xUnit（测试目录 `TraeCheckin.Tests` 在仓库外，用 `-p:OutputPath` 覆盖运行）；Python 3.12（仅标准库）；GitHub Actions。

**测试运行命令（贯穿全计划）：**
```
& "C:\Program Files\dotnet\dotnet.exe" test --nologo -p:OutputPath="$env:TEMP\traebuild_cloud" -v q
```
**编译命令：** `& "C:\Program Files\dotnet\dotnet.exe" build TraeCheckin.csproj -c Release --nologo`

**测试项目位置：** `c:\Users\星梦\Desktop\插件开发\TraeCheckin.Tests`（仓库外，xUnit，ProjectReference 主工程，不随仓库提交）。

**关键既有代码（Phase 1 已提交后的行号）：**
- `Api/GitHubApiClient.cs` — 常量 `SessionSecretName = "TRAE_SESSION"` / `DeviceIdSecretName = "TRAE_DEVICE_ID"`(L73-74)；`DeploymentStatus`(L26-38)；`SetSecretAsync(token, login, name, value)`(L239)；`GetDeploymentStatusAsync`(L412-440) 内调用私有 `HasSecretsAsync`(L443-457)；`SendApiAsync`(L473-479) 已支持任何 HttpMethod。
- `Forms/MainForm.Cloud.cs` — `DeployAsync()`(L349-453，Phase 1 已改为写「激活账号」单份 secret)；`RefreshDeploymentStateAsync()`(L184-242，L221-225 已部署文案)；`HandleDeployFailure()`(L456-469)。
- `.github/workflows/checkin.yml` — L25-28 `env:` 仅 `TRAE_SESSION/TRAE_DEVICE_ID/FEISHU_WEBHOOK`。
- `checkin.py` — 单账号 `main()`(L88-128)；`get_token/checkin/notify_feishu/beijing_now_str`(L37-85) 保持不变。

---

## Task 1: `GitHubApiClient` 序号 secret 命名 + 已部署账号数检测（TDD）

**Files:**
- Modify: `Api/GitHubApiClient.cs`
- Test: `TraeCheckin.Tests/CloudAccountTests.cs`（新建）

- [ ] **Step 1: 写失败测试**

创建 `c:\Users\星梦\Desktop\插件开发\TraeCheckin.Tests\CloudAccountTests.cs`：

```csharp
using TraeCheckin;

namespace TraeCheckin.Tests;

/// <summary>云端多账号 secret 命名与部署数量检测相关纯逻辑测试。</summary>
public class CloudAccountTests
{
    [Theory]
    [InlineData(1, "TRAE_SESSION")]
    [InlineData(2, "TRAE_SESSION_2")]
    [InlineData(3, "TRAE_SESSION_3")]
    public void SessionSecretNameFor_按序号返回(int index, string expected)
        => Assert.Equal(expected, GitHubApiClient.SessionSecretNameFor(index));

    [Theory]
    [InlineData(1, "TRAE_DEVICE_ID")]
    [InlineData(2, "TRAE_DEVICE_ID_2")]
    [InlineData(3, "TRAE_DEVICE_ID_3")]
    public void DeviceSecretNameFor_按序号返回(int index, string expected)
        => Assert.Equal(expected, GitHubApiClient.DeviceSecretNameFor(index));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run（cwd=`TraeCheckin.Tests`）：
```
dotnet test --filter CloudAccountTests
```
Expected: 编译失败，`GitHubApiClient` 缺 `SessionSecretNameFor / DeviceSecretNameFor`。

- [ ] **Step 3: 新增命名辅助方法**

在 `Api/GitHubApiClient.cs` 的 `public const string FeishuWebhookSecretName = "FEISHU_WEBHOOK";`(L75) 之后插入：

```csharp
    /// <summary>第 index 个（从 1 起）账号的 Session secret 名；1 → TRAE_SESSION，N → TRAE_SESSION_N。</summary>
    public static string SessionSecretNameFor(int index)
        => index <= 1 ? SessionSecretName : "TRAE_SESSION_" + index;

    /// <summary>第 index 个（从 1 起）账号的 DeviceId secret 名；1 → TRAE_DEVICE_ID，N → TRAE_DEVICE_ID_N。</summary>
    public static string DeviceSecretNameFor(int index)
        => index <= 1 ? DeviceIdSecretName : "TRAE_DEVICE_ID_" + index;
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test --filter CloudAccountTests`
Expected: 5 条用例全部通过。

- [ ] **Step 5: 扩展 DeploymentStatus 与检测逻辑**

`DeploymentStatus`(L26-38) 增加账号数字段，在 `HasSecrets` 属性后插入：

```csharp
    /// <summary>已部署（session+device 成对存在）的连续账号数；0 表示尚未写入。</summary>
    public int DeployedAccountCount { get; set; }
```

把私有 `HasSecretsAsync`(L443-457) 整体替换为「拉取一次 secret 名集合 + 计数连续成对账号」的实现，并新增公开计数方法与删除方法：

```csharp
    /// <summary>拉取仓库全部 Actions secret 名；失败返回 null。</summary>
    private async Task<HashSet<string>?> FetchSecretNamesAsync(string token, string login)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/secrets", token);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("secrets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in arr.EnumerateArray())
            if (s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                names.Add(n.GetString() ?? "");
        return names;
    }

    /// <summary>
    /// 统计已部署的连续账号数：账号 1 需要 TRAE_SESSION 与 TRAE_DEVICE_ID 都存在，
    /// 之后依 TRAE_SESSION_2/TRAE_DEVICE_ID_2 … 递增，遇缺失即停止（返回前缀长度）。
    /// </summary>
    public async Task<int> CountDeployedAccountsAsync(string token, string login)
    {
        var names = await FetchSecretNamesAsync(token, login);
        if (names == null) return 0;
        int n = 1;
        while (names.Contains(SessionSecretNameFor(n)) && names.Contains(DeviceSecretNameFor(n)))
            n++;
        return n - 1;
    }

    /// <summary>删除仓库某个 Actions secret；404（不存在）视为成功。删除失败返回 false。</summary>
    public async Task<bool> DeleteSecretAsync(string token, string login, string secretName)
    {
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Delete, $"/repos/{login}/{SourceRepo}/actions/secrets/{secretName}", token);
            if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound) return true;
            LastError = $"删除 secret 失败：HTTP {(int)resp.StatusCode}";
            return false;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }
```

`GetDeploymentStatusAsync`(L430-431) 中把：
```csharp
            result.HasSecrets = await HasSecretsAsync(token, login);
```
替换为：
```csharp
            result.DeployedAccountCount = await CountDeployedAccountsAsync(token, login);
            result.HasSecrets = result.DeployedAccountCount >= 1;
```
（`HasSecretsAsync` 方法体已随替换删除，不再有调用点。）

- [ ] **Step 6: 编译主工程**

Run: `dotnet build TraeCheckin.csproj -c Release`
Expected: `0 个错误`。

- [ ] **Step 7: 全量测试**

Run: `dotnet test`
Expected: 全部通过（原 47 + 新 5 = 52）。

- [ ] **Step 8: 提交**

```bash
git add Api/GitHubApiClient.cs
git commit -m "feat: GitHubApiClient 支持多账号 secret 序号命名与已部署账号数检测"
```

---

## Task 2: 云端部署页遍历全部启用账号

**Files:**
- Modify: `Forms/MainForm.Cloud.cs`（`DeployAsync` L349-453、`RefreshDeploymentStateAsync` L221-225）

- [ ] **Step 1: DeployAsync 改为遍历启用账号**

把 `Forms/MainForm.Cloud.cs` `DeployAsync()`(L349-453) 中：

```csharp
    private async Task DeployAsync()
    {
        // Phase 1：云端部署先按「当前激活账号」写入 secret；多账号逐一部署在 Phase 2 规划
        var acc = CurAccount;
        if (acc == null || string.IsNullOrEmpty(acc.Session))
        {
            MessageBox.Show("尚未登录 Trae，请先到「设置」页添加/登录账号后再部署。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrEmpty(acc.DeviceId))
        {
            _accountStore.EnsureDeviceId(acc);
            _config.Save();
        }
```
替换为：

```csharp
    /// <summary>云端最多部署的账号数（与 checkin.yml 显式枚举一致，超限需扩 workflow）。</summary>
    private const int CloudAccountLimit = 4;

    private async Task DeployAsync()
    {
        var enabled = _accountStore.EnabledAccounts().ToList();
        if (enabled.Count == 0)
        {
            MessageBox.Show("没有启用的 Trae 账号，请先到「设置」页添加/登录账号后再部署。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (enabled.Any(a => string.IsNullOrEmpty(a.Session)))
        {
            MessageBox.Show("存在未登录的账号（缺少会话 Cookie），请先在「设置」页逐一登录后再部署。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (enabled.Count > CloudAccountLimit)
        {
            MessageBox.Show($"当前启用了 {enabled.Count} 个账号，云端部署上限为 {CloudAccountLimit} 个"
                + "（受 checkin.yml 显式 secret 映射限制）。如需更多，请停用部分账号或扩展 workflow。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var a in enabled)
        {
            _accountStore.EnsureDeviceId(a);
        }
        _config.Save();
```

- [ ] **Step 2: 写入段改为逐账号循环 + 清理多余 secret**

`DeployAsync()` 中从「正在写入 TRAE_SESSION secret…」到「TRAE_DEVICE_ID 写入成功」(L398-404) 的整段：

```csharp
            SetCloudLog("正在写入 TRAE_SESSION secret…");
            if (!await _ghApi.SetSecretAsync(token, login, GitHubApiClient.SessionSecretName, acc.Session)) { HandleDeployFailure(); return; }
            SetCloudLog("TRAE_SESSION 写入成功");

            SetCloudLog("正在写入 TRAE_DEVICE_ID secret…");
            if (!await _ghApi.SetSecretAsync(token, login, GitHubApiClient.DeviceIdSecretName, acc.DeviceId)) { HandleDeployFailure(); return; }
            SetCloudLog("TRAE_DEVICE_ID 写入成功");
```
替换为：

```csharp
            for (int i = 0; i < enabled.Count; i++)
            {
                var acc = enabled[i];
                int idx = i + 1;
                var sName = GitHubApiClient.SessionSecretNameFor(idx);
                var dName = GitHubApiClient.DeviceSecretNameFor(idx);

                SetCloudLog($"正在写入 {sName} secret…");
                if (!await _ghApi.SetSecretAsync(token, login, sName, acc.Session)) { HandleDeployFailure(); return; }
                SetCloudLog($"{sName} 写入成功");

                SetCloudLog($"正在写入 {dName} secret…");
                if (!await _ghApi.SetSecretAsync(token, login, dName, acc.DeviceId)) { HandleDeployFailure(); return; }
                SetCloudLog($"{dName} 写入成功");
            }

            // 清理本次减少后遗留的多余 secret，避免云端误签已删除/停用的账号
            for (int n = enabled.Count + 1; n <= CloudAccountLimit; n++)
            {
                var stale = GitHubApiClient.SessionSecretNameFor(n);
                SetCloudLog($"正在清理多余 {stale} secret…");
                if (!await _ghApi.DeleteSecretAsync(token, login, stale)) { HandleDeployFailure(); return; }
                SetCloudLog($"{stale} 已清理");
                var staleD = GitHubApiClient.DeviceSecretNameFor(n);
                if (!await _ghApi.DeleteSecretAsync(token, login, staleD)) { HandleDeployFailure(); return; }
            }
```

- [ ] **Step 3: 部署成功文案带上账号数**

`DeployAsync()` 成功分支(L433-435)：

```csharp
            if (conclusion == "success")
            {
                SetCloudLog("部署成功！云端自动签到已就绪，GitHub 将每天北京时间 8:00 自动签到。");
                _lblCloudState.Text = "已部署完成，云端每天自动签到";
                _btnCloudAction.Text = "重新部署";
```
替换为：

```csharp
            if (conclusion == "success")
            {
                SetCloudLog($"部署成功！{enabled.Count} 个账号已就绪，GitHub 将每天北京时间 8:00 自动签到。");
                _lblCloudState.Text = enabled.Count >= 2
                    ? $"已部署完成（{enabled.Count} 个账号），云端每天自动签到"
                    : "已部署完成（1 个账号），云端每天自动签到";
                _btnCloudAction.Text = "重新部署";
```

- [ ] **Step 4: RefreshDeploymentStateAsync 文案带账号数**

`RefreshDeploymentStateAsync()` 已部署分支(L221-225)：

```csharp
            if (status.IsDeployed)
            {
                _lblCloudState.Text = "已部署完成，云端每天北京时间 8:00 自动签到";
                _btnCloudAction.Text = "重新部署";
            }
```
替换为：

```csharp
            if (status.IsDeployed)
            {
                _lblCloudState.Text = status.DeployedAccountCount >= 2
                    ? $"已部署完成（{status.DeployedAccountCount} 个账号），云端每天北京时间 8:00 自动签到"
                    : "已部署完成（1 个账号），云端每天北京时间 8:00 自动签到";
                _btnCloudAction.Text = "重新部署";
            }
```

- [ ] **Step 5: 编译 + 全量测试**

Run: `dotnet build TraeCheckin.csproj -c Release`
Expected: `0 个错误`（若报 `enabled.Any`/`ToList` 缺 using，文件顶部加 `using System.Linq;`）。
Run: `dotnet test`
Expected: 52 项全部通过。

- [ ] **Step 6: 提交**

```bash
git add Forms/MainForm.Cloud.cs
git commit -m "feat: 云端部署遍历全部启用账号写入多组 secret，自动清理多余账号并显示部署账号数"
```

---

## Task 3: checkin.yml 显式映射 4 组账号 secret

**Files:**
- Modify: `.github/workflows/checkin.yml`

- [ ] **Step 1: 扩展 env 映射**

`.github/workflows/checkin.yml` `Run checkin` 步骤的 `env:`(L25-28)：

```yaml
        env:
          TRAE_SESSION: ${{ secrets.TRAE_SESSION }}
          TRAE_DEVICE_ID: ${{ secrets.TRAE_DEVICE_ID }}
          FEISHU_WEBHOOK: ${{ secrets.FEISHU_WEBHOOK }}
```
替换为：

```yaml
        env:
          TRAE_SESSION: ${{ secrets.TRAE_SESSION }}
          TRAE_DEVICE_ID: ${{ secrets.TRAE_DEVICE_ID }}
          TRAE_SESSION_2: ${{ secrets.TRAE_SESSION_2 }}
          TRAE_DEVICE_ID_2: ${{ secrets.TRAE_DEVICE_ID_2 }}
          TRAE_SESSION_3: ${{ secrets.TRAE_SESSION_3 }}
          TRAE_DEVICE_ID_3: ${{ secrets.TRAE_DEVICE_ID_3 }}
          TRAE_SESSION_4: ${{ secrets.TRAE_SESSION_4 }}
          TRAE_DEVICE_ID_4: ${{ secrets.TRAE_DEVICE_ID_4 }}
          FEISHU_WEBHOOK: ${{ secrets.FEISHU_WEBHOOK }}
```

> 说明：未填的 secret 在 Actions 中展开为空字符串；`checkin.py` 遇到空的 `TRAE_SESSION_N` 即停止递增，旧 1 账号部署天然兼容。

- [ ] **Step 2: 提交**

```bash
git add .github/workflows/checkin.yml
git commit -m "feat: checkin.yml 显式映射 4 组账号 secret，支持云端多账号签到"
```

---

## Task 4: checkin.py 多账号循环签到 + 汇总推送

**Files:**
- Modify: `checkin.py`

- [ ] **Step 1: 新增 iter_sessions 与随机 device id 辅助**

在 `checkin.py` `def beijing_now_str():`(L83-85) 之后插入：

```python
def iter_sessions():
    """按顺序产出 (账号序号, session, device_id)。账号 1 读 TRAE_SESSION；
    之后依次读 TRAE_SESSION_2, TRAE_SESSION_3… 直到缺空为止。"""
    s = os.environ.get("TRAE_SESSION", "").strip()
    if s:
        yield 1, s, os.environ.get("TRAE_DEVICE_ID", "").strip()
    n = 2
    while True:
        s = os.environ.get("TRAE_SESSION_%d" % n, "").strip()
        if not s:
            break
        yield n, s, os.environ.get("TRAE_DEVICE_ID_%d" % n, "").strip()
        n += 1


def random_device_id():
    """随机生成 16 位数字风控设备号（仅在缺省时兜底）。"""
    return str(random.randint(10**15, 10**16 - 1))
```

- [ ] **Step 2: 重写 main() 为多账号循环 + 汇总推送**

把 `checkin.py` 整个 `def main():`(L88-128) 替换为：

```python
def main():
    accounts = list(iter_sessions())
    if not accounts:
        print("错误：缺少环境变量 TRAE_SESSION")
        sys.exit(1)

    webhook = os.environ.get("FEISHU_WEBHOOK", "").strip()
    ok_names, fail_names = [], []
    all_ok = True

    for index, session, device_id in accounts:
        name = "账号 %d" % index
        device_id = device_id or random_device_id()
        print("[%s] device_id=%s" % (name, device_id))
        try:
            token = get_token(session)
            print("[%s] 已换取新 JWT，长度=%d" % (name, len(token)))
            result = checkin(token, device_id)
            body = result["body"]
            code = body.get("code", -1)
            checked = body.get("checked_in", False)
            ok = (result["http"] == 200) and (code == 0 or checked)
            credits = body.get("credits", 0)
            if ok:
                print("[%s] 签到成功，本次获得：%s 积分" % (name, credits))
                ok_names.append(name)
            else:
                reason = body.get("message") or ("HTTP %s" % result["http"])
                print("[%s] 签到失败：%s" % (name, reason))
                fail_names.append(name)
                all_ok = False
        except Exception as e:
            print("[%s] 签到异常: %s" % (name, e))
            fail_names.append(name)
            all_ok = False

    # 汇总一条飞书推送（无论成功/失败都汇总，webhook 为空则跳过）
    summary = ["Trae 多账号签到结果", "时间：%s" % beijing_now_str()]
    if ok_names:
        summary.append("成功：" + "、".join(ok_names))
    if fail_names:
        summary.append("失败：" + "、".join(fail_names))
    if webhook and (ok_names or fail_names):
        notify_feishu(webhook, "\n".join(summary))

    if not all_ok:
        sys.exit(1)
    print("全部账号签到完成")
```

- [ ] **Step 3: 更新文件头环境变量说明**

`checkin.py` 顶部 docstring 的「环境变量：」段(L13-15)：

```python
环境变量：
  TRAE_SESSION   必填。X-Cloudide-Session 的 Cookie 值（登录后从浏览器抓取）
  TRAE_DEVICE_ID 选填。x-device-id 风控值，16 位数字；缺省随机生成（实测不敏感）
```
替换为：

```python
环境变量：
  TRAE_SESSION        账号 1 的 X-Cloudide-Session Cookie（必填；后端兼容单账号部署）
  TRAE_DEVICE_ID      账号 1 的 x-device-id，16 位数字（选填，缺省随机）
  TRAE_SESSION_N      第 N(N≥2) 个账号的会话 Cookie；缺失即停止读取更多账号
  TRAE_DEVICE_ID_N    第 N 个账号的 x-device-id（选填，缺省随机）
  全部账号共享：       FEISHU_WEBHOOK（选填，签到后推送一条汇总）
```

- [ ] **Step 4: 语法与枚举行为验证（离线，不触网）**

```powershell
python -m py_compile checkin.py
```
Expected: 无输出、退出码 0。

用 `iter_sessions` 空跑验证枚举（不触发网络）：

```powershell
$env:TRAE_SESSION="s1"; $env:TRAE_DEVICE_ID="1111111111111111"; $env:TRAE_SESSION_2="s2"; $env:TRAE_DEVICE_ID_2="2222222222222222"; $env:TRAE_SESSION_3=""
python -c "import checkin; print([(i, s, d) for (i, s, d) in checkin.iter_sessions()])"
```
Expected 输出形如：`[(1, 's1', '1111111111111111'), (2, 's2', '2222222222222222')]`（账号 3 因空被截断，且旧环境变量全为空的场景在第 1 个用例已覆盖——若本机无 python，则跳到 Step 6 后由云端 E2E 一并验证）。

- [ ] **Step 5: 提交**

```bash
git add checkin.py
git commit -m "feat: checkin.py 支持多账号循环签到并汇总飞书推送，任一失败整体红标"
```

---

## Task 5: 收尾验证

**Files:** 无代码改动（全量测试 + Release 编译 + 产物同步）

- [ ] **Step 1: 全量测试 + 编译**

Run: `dotnet test`
Expected: 52 项全部通过。
Run: `dotnet build TraeCheckin.csproj -c Release`
Expected: `0 个错误`。

- [ ] **Step 2: 同步产物并重启冒烟**

```powershell
Stop-Process -Name TraeCheckin -ErrorAction SilentlyContinue
Copy-Item "bin\TraeCheckin\TraeCheckin.exe","bin\TraeCheckin\TraeCheckin.dll","bin\TraeCheckin\TraeCheckin.pdb","bin\TraeCheckin\TraeCheckin.deps.json","bin\TraeCheckin\TraeCheckin.runtimeconfig.json" "..\TraeCheckin开发\" -Force
Start-Process "..\TraeCheckin开发\TraeCheckin.exe"
```

- [ ] **Step 3: GUI + 云端 E2E（需用户配合）**

1. 本地已在设置页有两个启用账号（Phase 1 添加过）。
2. 打开「云端签到」→ 点「重新部署」，日志逐账号出现 `TRAE_SESSION…/TRAE_DEVICE_ID…` 与 `TRAE_SESSION_2…/TRAE_DEVICE_ID_2…` 写入成功；状态行显示「已部署完成（2 个账号）…」。
3. GitHub Actions 页面手动触发一次 workflow，日志出现 `[账号 1]`、`[账号 2]` 两行且均成功；飞书收到一条汇总。
4. （可选反向验证）把任一账号 `TRAE_SESSION` 改为无效值重部署 → Actions 该账号行失败且整体红标。

- [ ] **Step 4: 汇报**

向用户汇报测试数、编译结果、GUI 冒烟结果、云端 E2E 结果与遗留问题；确认版本号是否定为 v1.5.0 并准备发布。

---

## 自审记录

**Spec 覆盖（Phase 2 部分）：**
- Secret 命名：账号 1 旧名、N≥2 加 `_N` 后缀 → Task 1 `SessionSecretNameFor/DeviceSecretNameFor` ✅
- 部署只写 Enabled 账号、幂等覆盖、上限 4 → Task 2 ✅
- 清理本次减少后遗留的多余 secret（防误签已删除账号）→ Task 2 Step 2（spec 补充的正确性项）✅
- `HasSecretsAsync` → 连续账号数检测，IsDeployed 展示「N 个账号已部署」→ Task 1 + Task 2 ✅
- checkin.yml 显式枚举到 4 组 → Task 3 ✅
- checkin.py `iter_sessions()` + 逐账号 `[账号 N]` 输出 + 任一失败 exit(1) → Task 4 ✅
- 飞书汇总一条（成功/失败列表）→ Task 4 `main()` 汇总段 ✅
- 无账号 / 空 TRAE_SESSION → 保持报错 exit(1) → Task 4 ✅

**Type/命名一致性：** `SessionSecretNameFor(int)` / `DeviceSecretNameFor(int)` / `CountDeployedAccountsAsync(token,login)` / `DeleteSecretAsync(token,login,name)` 均在 Task 1 定义且后续使用处拼写一致；`DeploymentStatus.DeployedAccountCount` 仅 Task 2 读取；`CloudAccountLimit = 4` 与 checkin.yml 映射行数一致。

**已知风险/边界：**
- Actions secret 展开：未填 `TRAE_SESSION_N` 为空字符串 → `iter_sessions` 在空处截断，向后兼容 1 账号；若用户手动在 GitHub 只填 `_2` 不填 `_1`，会因账号 1 缺失而整体不签到（符合预期，账号顺序从 1 连续）。
- `checkin.py` 行为变化：单账号失败不再立即 exit，而是处理完所有账号后统一汇总并 exit(1)——单账号场景结果一致（红标），飞书消息格式略有变化（多一行「时间」与「成功/失败」段）。
- 删除多余 secret 依赖 `SendApiAsync` 支持 DELETE（方法体对 method 无限制）✅。
- 本机无 python 时 Task 4 Step 4 离线验证跳过，交给 Task 5 云端 E2E 覆盖。
