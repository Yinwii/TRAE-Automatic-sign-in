# Trae 每日签到助手（TRAE-Automatic-sign-in）

> 作者：**星梦**

一个用于 **Trae** 每日积分自动签到的 Windows 桌面小工具。它可以挂在桌面 / 后台运行，每天到点自动签到，并实时显示剩余积分与今日签到状态。

![C#](https://img.shields.io/badge/C%23-.NET%209-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-green)
![License](https://img.shields.io/badge/License-GPL--3.0-orange)

---

## 功能特性

- ✅ **每日自动签到**：到设定时间自动执行签到，无需手动操作。
- ✅ **手动签到**：侧边栏或系统托盘均可一键「立即签到」。
- ✅ **积分实时查询**：显示当前剩余积分，卡片化展示。
- ✅ **签到状态展示**：明确显示「今日已签到 ✓」或「今日可签到」。
- ✅ **单日奖励展示**：显示本次签到可获得的积分（200 积分）。
- ✅ **签到记录**：本地记录每次成功签到的时间与积分（`history.txt`）。
- ✅ **系统托盘驻留**：关闭窗口自动最小化到托盘，后台继续自动签到。
- ✅ **内嵌登录**：首次使用 WebView2 内嵌浏览器登录一次，登录态自动保存，后续无需重复登录。
- ✅ **深色侧边栏 UI**：固定小窗，导航切页（仪表盘 / 签到记录 / 设置）。

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
| `MainForm.cs` | 主界面、自动签到定时器、UI 交互、托盘 |
| `LoginForm.cs` | WebView2 内嵌登录窗口，读取登录 token |
| `TraeApiClient.cs` | Trae 云 API 客户端（状态 / 签到 / 积分） |
| `AppConfig.cs` | 本地配置的加载与保存 |

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
# 在项目目录下执行
dotnet build TraeCheckin.csproj -c Release
```

生成的可执行文件位于 `bin/Release/net9.0-windows/TraeCheckin.exe`。

---

## 配置文件位置

| 内容 | 路径 |
|------|------|
| 配置（token、自动签到设置） | `%APPDATA%\TraeCheckin\config.json` |
| 签到历史 | `%APPDATA%\TraeCheckin\history.txt` |
| WebView2 用户数据 | `%LOCALAPPDATA%\TraeCheckin\WebView` |

> ⚠️ **安全提示**：`config.json` 中保存着你的登录 token。请勿将本项目中的 `config.json` 提交到任何公开仓库，或分享给他人。

---

## 免责声明

本项目仅用于个人学习与自动化签到研究，请遵守 Trae 的服务条款。请勿用于任何违反平台规则或滥用目的的行为。

---

## License

[GPL-3.0](LICENSE)

© 星梦