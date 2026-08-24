# Trae 每日签到助手（TRAE-Automatic-sign-in）

> 作者：**星梦**

一个用于 **Trae** 每日积分自动签到的 Windows 桌面小工具。它可以挂在桌面 / 后台运行，每天到点自动签到，并实时显示剩余积分与今日签到状态。

![C#](https://img.shields.io/badge/C%23-.NET%209-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-green)
![License](https://img.shields.io/badge/License-GPL--3.0-orange)

---

## 功能特性

- **每日自动签到**：到设定时间自动执行签到，无需手动操作。
- **手动签到**：侧边栏或系统托盘均可一键「立即签到」。
- **签到记录**：本地记录每次成功签到的时间与积分（`history.txt`）。
- **系统托盘驻留**：关闭窗口自动最小化到托盘，后台继续自动签到。
- **开机自启动**：可设置登录 Windows 后自动启动本程序。
- **Token 查看与复制**：设置页显示当前 token 及最后更新时间，支持一键复制。
- **内嵌登录**：首次使用 WebView2 内嵌浏览器登录一次，登录态自动保存，后续无需重复登录。
- **云端自动签到（GitHub Actions）**：可一键把签到脚本部署到自己的 GitHub 仓库，无需本机挂机，由 GitHub 每天定时自动签到。
- **单实例运行**：仅允许启动一个实例，再次点击图标会自动唤起已运行的窗口。
- **启动器依赖检查**：独立启动器（自包含，无需 .NET）启动时自动检测 .NET 9 桌面运行时与 WebView2 Runtime，缺失时自动补全或引导安装。

---

## 程序运行原理

程序是一个 **C# WinForms** 桌面应用（.NET 9 + WebView2），运行流程如下：

```
┌─────────────────────────────────────────────────────────────┐
│                      程序启动                                 │
│  读取本地配置 %APPDATA%\TraeCheckin\config.json (含 token)   │
└──────────────────────────────┬──────────────────────────────┘
                               ▼
┌─────────────────────────────────────────────────────────────┐
│  是否需要登录？                                              │
│  ├─ 无 token / token 失效 ──► 弹出 WebView2 内嵌登录窗口     │
│  │                            用户登录一次                   │
│  │                            从 localStorage 读取 token     │
│  │                            保存到本地配置                 │
│  └─ 有有效 token ────────────► 直接进入主界面                │
└──────────────────────────────┬──────────────────────────────┘
                               ▼
┌─────────────────────────────────────────────────────────────┐
│  启动自动签到定时器（每 10 秒检查一次）                       │
│  到达设定时间且当天未签到 ──► 调用签到接口                   │
│  窗口关闭 ──► 最小化到系统托盘，后台继续运行                 │
└─────────────────────────────────────────────────────────────┘
```

核心组件：

| 文件 | 作用 |
|------|------|
| `Program.cs` | 程序入口，启动主窗体 |
| `Forms/MainForm.cs` | 主界面、自动签到定时器、UI 交互、托盘 |
| `Forms/MainForm.Cloud.cs` | 「云端签到」页（授权与一键部署） |
| `Forms/LoginForm.cs` | WebView2 内嵌登录窗口，读取登录 token |
| `Api/TraeApiClient.cs` | Trae 云 API 客户端（状态 / 签到 / 积分） |
| `Api/GitHubApiClient.cs` | GitHub 设备码授权与云端部署 API 客户端 |
| `Config/AppConfig.cs` | 本地配置的加载与保存 |
| `Services/AutoStartManager.cs` | 开机自启动（注册表 Run 键） |
| `Services/FeishuNotifier.cs` | 飞书机器人推送（签到结果通知） |
| `Services/GitHubSecret.cs` | GitHub Actions secret 加密（libsodium） |
| `Services/TokenUtils.cs` | 登录 token 判定（避免误读失效 token） |
| `Controls/HistoryChart.cs` | 总积分趋势折线图控件 |

---

## 签到原理

Trae 的每日签到本质是调用官方云 API。`TraeApiClient` 封装了三个接口（`BaseUrl = https://api.trae.cn`）：

| 接口 | 说明 |
|------|------|
| `POST /trae/api/v2/ug/checkin_credits/status` | 查询今日签到状态与单日奖励 |
| `POST /trae/api/v2/ug/checkin_credits/claim` | 执行每日签到 |
| `POST /trae/api/v2/pay/user_current_entitlement_list` | 查询剩余积分 |

请求需要携带认证头：

```
Authorization: Cloud-IDE-JWT <token>
x-device-id: <设备号>
```

其中 **token** 是登录后在浏览器 `localStorage` 中存储的 `Cloud-IDE-Token`（JWT）。程序通过内嵌 WebView2 登录页面获取它，并持久化保存，之后每次请求自动带上。

**自动签到逻辑**（`MainForm.CheckAutoCheckinAsync`）：

1. 每 10 秒触发一次检查。
2. 若当天已执行过自动签到则跳过。
3. 读取用户设定的签到时间（如 `08:00`）。
4. 当前时间晚于设定时间且当天未签 → 执行签到。
5. 若程序在设定时间之后才启动，则自动**补签**一次，保证当天不遗漏。

---

## 使用说明

### 运行环境

- Windows 10 / 11
- [.NET 9 运行时](https://dotnet.microsoft.com/download)（或直接使用已发布的可执行文件）
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win10/11 一般自带）

### 首次使用

1. 启动程序。
2. 首次会弹出登录窗口，用手机号 + 验证码登录 Trae。
3. 登录成功后自动识别 token 并进入主界面。
4. 在「仪表盘」或「设置」页设置自动签到时间（默认 `08:00`）。

### 构建

```bash
# 1. 编译主程序（框架依赖，输出到统一产物目录）
dotnet build TraeCheckin.csproj -c Release

# 2. 发布独立启动器（自包含单文件，输出到同一目录）
dotnet publish TraeCheckin.Launcher\TraeCheckin.Launcher.csproj -c Release
```

主程序与启动器的构建产物统一输出到 `TraeCheckin开发\` 目录，主要文件：

| 文件 | 说明 |
|------|------|
| `TraeCheckin.exe` | 主程序（需 .NET 9 桌面运行时） |
| `TraeCheckin.Launcher.exe` | 独立启动器（自包含，自动检测/补全依赖） |

日常使用建议双击 `TraeCheckin.Launcher.exe`，它会自动检查 .NET 9 与 WebView2 运行时，缺失时自动补全，再拉起主程序。

---

## 云端签到部署（GitHub Actions）

除本机自动签到外，程序还支持把签到脚本部署到你的 GitHub 仓库，由 GitHub Actions 每天定时自动签到，**无需本机 24 小时挂机**。

### 部署流程

1. 在「云端签到」页点击「授权 GitHub」，走 **OAuth 设备码授权**拿到 access_token。
2. 再点「一键部署到云端」，程序自动完成：
   - fork 源仓库（若你已是源仓库 owner 则自动跳过）；
   - 写入 `TRAE_SESSION` 与 `TRAE_DEVICE_ID` 两个 Actions secret（若已在设置页配置飞书推送，还会写入 `FEISHU_WEBHOOK`）；
   - 启用定时 workflow；
   - 触发一次验证运行并等待结果。
3. 部署成功后，GitHub 每天 **北京时间 8:00**（cron `0 0 * * *`）自动执行签到。

云端脚本 [checkin.py](checkin.py) 用 `X-Cloudide-Session` Cookie 换取全新 JWT 后执行签到，workflow 定义见 [checkin.yml](.github/workflows/checkin.yml)。

### 注意事项

- 云端签到的凭证是 `TRAE_SESSION`（约 **14 天**有效）。过期后需回到本程序重新登录 Trae，再点一次「一键部署到云端」刷新 secret。
- 首次部署需要 GitHub OAuth 授权；授权信息（access_token 与用户名）保存在本地配置中，不会写入云端仓库。

---

## 签到结果推送（飞书）

可在「设置」页粘贴飞书自定义机器人的 webhook 地址，签到成功/失败后主动推送到飞书群：

1. 在飞书群中添加「自定义机器人」，复制它的 webhook 地址。
2. 在本程序「设置」页粘贴到「签到结果推送」输入框，点「保存」，再点「测试推送」验证。
3. 部署云端时若已配置 webhook，会一并写入 `FEISHU_WEBHOOK` secret，云端签到同样会推送。

> 注意：飞书机器人安全设置建议选「自定义关键词」或关闭「签名校验」；本程序当前实现的是不带签名的文本消息推送。

---

## 配置文件位置

| 内容 | 路径 |
|------|------|
| 配置（token、Session、GitHub 授权、飞书 webhook、自动签到设置） | `%APPDATA%\TraeCheckin\config.json` |
| 签到历史 | `%APPDATA%\TraeCheckin\history.txt` |
| 总积分趋势数据 | `%APPDATA%\TraeCheckin\credits_total.txt` |
| WebView2 用户数据 | `%LOCALAPPDATA%\TraeCheckin\WebView` |

> ⚠️ **安全提示**：`config.json` 中保存着你的登录 token 与 GitHub 授权信息。请勿将本项目中的 `config.json` 提交到任何公开仓库，或分享给他人。

---

## 免责声明

本项目仅用于个人学习与自动化签到研究，请遵守 Trae 的服务条款。请勿用于任何违反平台规则或滥用目的的行为。

---

## License

[GPL-3.0](LICENSE)

© 星梦
