# 设计：自动构建并发布构建产物的 GitHub Actions workflow

日期：2026-09-04 ｜ 状态：已批准 ｜ 目标仓库：star620/TRAE-Automatic-sign-in

## 背景与动机

TraeCheckin 目前发版是**全手工**：本地构建 → 手动打 zip → 手动建 GitHub Release → 手动上传资产（还踩过 PowerShell 中文正文乱码、upload URL 不一致等坑）。历史 v1.4.2 / v1.4.3 / v1.4.4 每个 Release 只含一个资产 `TraeCheckin-vX.Y.Z-win-x64.zip`。

希望把「推一个 tag」之后的构建、打包、建 Release、上传资产全部自动化，与现有发版产物形态完全一致。

## 目标

- 推送 `v*` tag 到源仓库 main 时，自动完成：构建 → 打包完整目录 zip → 生成 changelog → 创建 GitHub Release 并上传 zip。
- zip 内容、命名与现有手工产物**完全一致**（完整 `bin\TraeCheckin\` 目录，含 TraeCheckin.exe、TraeCheckin.Launcher.exe、WebView2/Sodium/运行依赖）。
- Release notes 自动生成（上一个版本 tag 至今的 commit 列表），免手动维护。
- 提供 `workflow_dispatch` 手动触发开关，便于冒烟验证。

## 非目标

- 不自动 bump csproj 版本号（保留「先改版本再打 tag」习惯，tag 是唯一版本来源）。
- 不做多平台矩阵（项目是 Windows 桌面工具，win-x64 即可）。
- 不建 draft / 不加人工放行闸（方案 B 已否决）。
- 不改动现有 `checkin.yml` 云端签到 workflow 与 `checkin.py`。

## Workflow 设计（方案 A 定稿）

### 触发

```yaml
on:
  push:
    tags: ["v*"]
  workflow_dispatch: {}
```

`v*` 匹配 `v1.4.5` 等格式 tag。

### 权限

```yaml
permissions:
  contents: write   # 创建 Release + 上传资产所需
```

### Job：build-release（runs-on: windows-latest）

windows-latest 保证与本地产物一致（自包含 win-x64、WebView2 原生依赖），避免交叉编译差异。

步骤（对齐现有本地手工流程）：

1. **Checkout**：`actions/checkout@v4`，`fetch-depth: 0`（需完整 tag 历史生成 changelog）。
2. **Setup .NET**：`actions/setup-dotnet@v4`，`dotnet-version: "9.x"`。
3. **构建主程序**：`dotnet build TraeCheckin.csproj -c Release` → 输出到 `bin\TraeCheckin\`（csproj 已配 OutputPath）。
4. **发布启动器**：`dotnet publish TraeCheckin.Launcher\TraeCheckin.Launcher.csproj -c Release` → 自包含单文件进同一 `bin\TraeCheckin\` 目录（csproj 已配 PublishDir）。
5. **生成 changelog**：从 git 推导当前 tag 与上一 `v*` tag：
   - 取 `git describe --tags --abbrev=0` 得到最近 tag；若最近 tag 不是当前 tag，说明中间可能有未发版的 commit，`git log <prev>..<current>` 列出 commit 消息。
   - 简单可靠做法：`git log` 用 `--format="- %s"`，上一个 tag 用 `git tag --list 'v*' --sort=-v:refname | 取第 2 个`（第 1 个即当前 tag）。若只有一个 tag 则取全部历史。
   - 写入临时文件 `RELEASE_NOTES.md`。
6. **打包**：`Compress-Archive` 把 `bin\TraeCheckin\*` 打成 `<repo root>\TraeCheckin-${{ env.TAG }}-win-x64.zip`（tag 去 `v` 前缀，与历史命名一致）。
7. **创建 Release 并上传**：`gh release create <tag> <zip> --title <tag> --notes-file RELEASE_NOTES.md --repo ${{ github.repository }}`；`gh` 使用自动注入的 `GITHUB_TOKEN`。

### 错误处理

- 任一构建/发布步骤失败即 workflow 失败并红标，不产生半成品 Release（`gh release create` 在最后一步，资产未就绪前不会建 Release）。
- 若 tag 已存在对应 Release（如重跑），`gh release create` 会失败 → 引导提示先删除旧 Release 或改 tag 版本。

## 影响面

- 新增：`.github/workflows/release.yml`。
- 不改动：源码、`checkin.yml`、csproj、README（README 的「构建」小节保留，供本地开发者参考；Release 自动化属 CI 行为，不重复写入 README）。
- 该 workflow 只在源仓库 tag push 时运行；云端签到 fork 副本不会因此触发发版。

## 测试与可行度验证

1. **YAML 语法与语义**：push workflow 文件到源仓库后，在 Actions 页用 `workflow_dispatch` 手动冒烟——预期 job 跑通、生成 zip、创建同名 Release 并附带资产（此时 tag 是 latest main 的 HEAD，需先确保本地 tag 已推送或手动触发针对最新 main）。
   > 注意：`workflow_dispatch` 无 tag 上下文。冒烟时需先打一个真实 tag 或用 `ref` 指定；若仅验证构建链路可临时在 dispatch 分支读 `git describe`。
2. **真实 tag 演练**：打 `v1.4.5`（需先本地 bump csproj 版本号并 push main + tag），确认 Release 页出现 zip 资产、notes 列出 v1.4.4 后全部 commit、下载 zip 解压可运行（TraeCheckin.Launcher.exe）。
3. **回归**：确认未触碰 `checkin.yml`，云端签到每日运行不受影响。

## 关键既有配置参考

- `TraeCheckin.csproj`：`OutputPath=bin\TraeCheckin\`、`AppendTargetFrameworkToOutputPath=false`。
- `TraeCheckin.Launcher.csproj`：`PublishDir=..\bin\TraeCheckin\`、win-x64 自包含单文件。
- 历史资产命名：`TraeCheckin-v1.4.4-win-x64.zip`。
