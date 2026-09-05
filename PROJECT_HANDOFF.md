# 项目交接档 TraeCheckin

> 由 ProjectHandoff 生成：2026-09-05 19:56 · 最近提交 c1f2b6d docs: remove handoff spec (moved to standalone ProjectHandoff tool)
> 打开本项目时请先读本文件。

## 1. 一句话定位
一个用于 Trae 每日积分自动签到的 Windows 桌面小工具。它可以挂在桌面 / 后台运行，每天到点自动签到，并实时显示剩余积分与今日签到状态。 每日自动签到：到设定时间自动执行签到，无需手动操作。

## 2. 快速开始
dotnet build TraeCheckin.csproj -c Release
dotnet publish TraeCheckin.Launcher\TraeCheckin.Launcher.csproj -c Release

## 3. 关键路径
- README.md
- docs/
- checkin.py
- TraeCheckin.csproj

## 4. 最近进展
- c1f2b6d docs: remove handoff spec (moved to standalone ProjectHandoff tool)
- 93d6532 docs: add project handoff generator design spec
- 52cc0e6 fix: 云端签到状态按 checkin workflow 过滤；签到前兜底补唯一设备号，AccountStore 生成设备号排除重复
- 084a411 feat: 添加新账号成功后当日即签（响应 issue #4 场景：新增账号当天未触发签到）
- 762cc35 ci: 新增 push 触发的工作流，push/PR 自动 Release 编译并校验 checkin.py 语法
- 785a2d0 fix: 修复云端操作按钮行底部截断，加大按钮行高
- 1d91b70 feat: checkin.py 支持多账号循环签到并汇总飞书推送，任一失败整体红标
- 0cd7dd9 feat: checkin.yml 显式映射 4 组账号 secret，支持云端多账号签到
- 04367b4 feat: 云端部署遍历全部启用账号写入多组 secret，自动清理多余账号并显示部署账号数
- bd84e73 feat: GitHubApiClient 支持多账号 secret 序号命名与已部署账号数检测
- 1afd5a2 docs: 添加多账号签到（云端 Phase 2）实现计划
- 2b070ca docs: 勾选 Phase 1 计划完成项并修正格式
- 23bc0c9 fix: 修复设置页按钮行高 DPI 下底部截断，加大按钮与卡片行高
- 7b88b94 feat: 本地签到改为遍历多账号，设置页新增多账号管理，云端部署适配激活账号
- 557997f refactor: TraeApiClient 改为每请求传入 deviceId，新增 AccountStore 账号辅助服务
- 6000b51 feat: 新增 TraeAccount 模型与单账号→多账号迁移
- 2328bce feat: PAT 弹窗支持自动打开 GitHub 创建页并加大说明区防截断
- 5fd282f docs: 添加本地多账号签到实现计划（Phase 1）
- d6c3308 docs: 添加多账号签到（本地+云端）设计文档，响应 issue #3
- c905a03 docs: 移除计划文档中误写的 PAT 明文，改为描述性指引

## 5. 进行中事项
相关计划：
- docs/superpowers/plans/2026-09-05-multi-account-local.md：多账号签到（本地）实现计划 — Phase 1
- docs/superpowers/plans/2026-09-05-multi-account-cloud.md：多账号签到（云端）实现计划 — Phase 2

<!-- ===== 手写区：重新生成时程序不会覆盖 ===== -->

## 6. 已知坑与约定

- **风控 x-device-id**：必须是 16 位数字 Aha 设备号，GUID/UUID 会触发 9074「参与用户太多」；多账号不能共用同一设备号。改动设备号逻辑时走 `AccountStore.NewUniqueDeviceId/EnsureDeviceId`，签到与云端部署前都有 `EnsureDeviceId` 兜底（`Forms/MainForm.cs`、`Forms/MainForm.Cloud.cs`）。
- **凭证体系**：JWT 约 8 小时失效，靠 `X-Cloudide-Session` Cookie（约 14 天）静默换新（桌面端 `GetUserToken`，云端 `checkin.py` 同样先换 token 再签）。云端 `TRAE_SESSION` 过期后须回到本程序重新登录 Trae，再点一次「一键部署」刷新 secret。
- **敏感文件**：`%APPDATA%\TraeCheckin\config.json` 存有 token、GitHub 授权、飞书 webhook，已被 `.gitignore` 的 `**/config.json` 挡住，严禁入库或外传。
- **云端多账号强耦合上限 4**：`CloudAccountLimit = 4`（`MainForm.Cloud.cs`）与 `.github/workflows/checkin.yml` 显式声明的 4 组 secret 强耦合，扩容要同步两处。账号编号必须从 1 连续：`checkin.py` 遇 `TRAE_SESSION_N` 缺失即停止读取；账号 1（`TRAE_SESSION` + `TRAE_DEVICE_ID`）缺失直接报错。每个账号需 Session 与 DeviceId 成对 secret。Actions 无法通配注入 secrets，`checkin.yml` 只能逐行显式列出 `TRAE_SESSION[_2/_3/_4]`。
- **进度判断约定**：docs 计划文档的 checkbox 不可信（仓库无 `- [x]` 先例），判断实际进度以 `git log` + 代码实态为准。
- **工程结构约定**：测试工程 `TraeCheckin.Tests` 在仓库外（`..\TraeCheckin.Tests`，不随仓库提交，靠主 csproj `InternalsVisibleTo`）；Release 产物统一到 `bin\TraeCheckin\`（已 gitignore），运行时复制到 `..\TraeCheckin开发\`；构建脚本命令里 dotnet 用全路径 `C:\Program Files\dotnet\dotnet.exe`。
- **UI DPI 截断史**：多账号阶段两次修复设置/云端页按钮行底部截断（`23bc0c9`、`785a2d0`），改动行高/按钮高度后要重点复查截断。

## 7. 下一步建议

- **优先推代码**：本地 `main` 领先 `origin/main` 36 个提交（截至 2026-09-05），多账号/云端代码从未推送，远端 CI/Release 从未验证过。先推送并确认 workflow 绿。
- **版本收口**：`v1.5.0` 标签打在 `762cc35`，落后当前 HEAD 4 个提交；`TraeCheckin.csproj` 的 `<Version>` 仍是 1.4.5 未 bump。需决策是否把 `084a411`（新增账号当日即签）/`52cc0e6`（云端状态过滤+去重设备号）纳入 v1.5.0 并重打 tag，同时 bump csproj 版本。
- **补收尾验证**（Phase 2 计划 Task 5/6 未见完成记录）：全量测试 + Release 编译 + 产物同步 `..\TraeCheckin开发\` + 本机冒烟；云端 E2E 需真人配合（多账号实际部署、Actions 手动触发、飞书汇总红绿检查）。
- **让档案跟项目走**：本文件目前未被 git 跟踪，建议提交进 TraeCheckin 仓库；换号接手时先让新 AI 读本文件，接手过程中发现的新坑回填到第 6 节。

