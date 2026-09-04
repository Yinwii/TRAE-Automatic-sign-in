# 自动构建并发布构建产物 workflow 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `.github/workflows/release.yml`：推送 `v*` tag 到源仓库时自动构建、打包完整 `bin\TraeCheckin\` 目录 zip、生成 changelog、创建 GitHub Release 并上传资产。

**Architecture:** 单 job（windows-latest）+ 7 步：checkout(全历史) → setup-dotnet → 解析 tag/上一 tag → build 主程序 → publish 自包含启动器 → 生成 notes → 打包 zip → `gh release create` 上传。tag 版本来源 = `github.ref_name`；changelog 取 `git log <上一tag>..<当前tag>`。

**Tech Stack:** GitHub Actions / YAML / .NET 9 / PowerShell (pwsh, windows-latest 自带)。

**测试命令（贯穿全计划）：**
```
# 本地模拟 CI 构建命令（PowerShell，cwd = TraeCheckin 仓库根）
dotnet build TraeCheckin.csproj -c Release --nologo
dotnet publish TraeCheckin.Launcher\TraeCheckin.Launcher.csproj -c Release --nologo
Compress-Archive -Path "bin\TraeCheckin\*" -DestinationPath "$env:TEMP\dryrun\TraeCheckin-dryrun-win-x64.zip" -Force
```

**关键既有配置参考：**
- `TraeCheckin.csproj`：`OutputPath=bin\TraeCheckin\` → 主程序 build 产物落在仓库内 `bin\TraeCheckin\`。
- `TraeCheckin.Launcher\TraeCheckin.Launcher.csproj`：`PublishDir=..\bin\TraeCheckin\` → 自包含启动器 publish 也落在同一目录（README 本地发版顺序：先 build 主程序、再 publish launcher）。
- `.gitignore` 已忽略 `bin/`、`*.zip`：CI 生成的 zip 不会被误提交。
- 历史资产命名：`TraeCheckin-v1.4.4-win-x64.zip`（tag 保留 `v` 前缀）。

---

## Task 1: 编写 release.yml

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: 创建 workflow 文件**

```yaml
name: Build & Auto Release

on:
  push:
    tags: ["v*"]
  workflow_dispatch: {}

permissions:
  contents: write

jobs:
  build-release:
    runs-on: windows-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "9.0.x"

      # 解析当前 tag 与上一 v* tag（dispatch 无 tag 时取仓库最高 v tag）
      - name: Resolve tags
        id: version
        shell: pwsh
        run: |
          $all = @(git tag --list 'v*' --sort=-v:refname)
          $tag = $null
          if ($env:GITHUB_EVENT_NAME -eq "push") { $tag = $env:GITHUB_REF_NAME }
          if (-not $tag -and $all.Count -gt 0) { $tag = $all[0] }
          if (-not $tag) { throw "未找到 v* tag，请先打 tag 再触发" }
          $prev = $all | Where-Object { $_ -ne $tag } | Select-Object -First 1
          "TAG=$tag" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
          "PREV_TAG=$prev" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
          Write-Host "release tag=$tag prev=$prev"

      - name: Build main app
        shell: pwsh
        run: dotnet build TraeCheckin.csproj -c Release --nologo

      - name: Publish launcher (self-contained)
        shell: pwsh
        run: dotnet publish TraeCheckin.Launcher\TraeCheckin.Launcher.csproj -c Release --nologo

      - name: Generate changelog
        shell: pwsh
        run: |
          if ($env:PREV_TAG) {
            $log = git log "$env:PREV_TAG..$env:TAG" --pretty=format:"- %s" --no-merges
          } else {
            $log = git log --pretty=format:"- %s" --no-merges
          }
          $body = "## Changelog`r`n" + ($log -join "`r`n")
          [System.IO.File]::WriteAllText(
            "$env:RUNNER_TEMP\notes.md",
            $body,
            [System.Text.UTF8Encoding]::new($false))

      - name: Package zip
        shell: pwsh
        run: |
          Compress-Archive -Path "bin\TraeCheckin\*" -DestinationPath "TraeCheckin-$env:TAG-win-x64.zip" -Force
          Get-Item "TraeCheckin-$env:TAG-win-x64.zip" | Select-Object Name,Length

      - name: Create GitHub Release
        env:
          GH_TOKEN: ${{ github.token }}
        shell: pwsh
        run: |
          gh release create $env:TAG "TraeCheckin-$env:TAG-win-x64.zip" --title $env:TAG --notes-file "$env:RUNNER_TEMP\notes.md"
```

- [ ] **Step 2: YAML 语法检查**

本地用 PowerShell 快速校验 YAML 可解析（无 PyYAML 时退化为人工复查缩进）：

```powershell
python -c "import yaml,sys; yaml.safe_load(open(r'.github\workflows\release.yml', encoding='utf-8')); print('YAML OK')"
```
Expected: 打印 `YAML OK`（若本机无 python / 无 PyYAML，则跳过，靠 Step 3 语义复查兜底）。

- [ ] **Step 3: 语义自检（不依赖工具）**

逐条核对：
1. `on.push.tags: ["v*"]` 正确缩进（trigger 下两层）。
2. `permissions: contents: write` 与 `on` 同级。
3. `$env:GITHUB_REF_NAME` 在 push tag 时等于 `v1.4.5`；`gh release create` 要求 tag 已存在（push tag 场景天然满足）。
4. `bin\TraeCheckin\*` 同时包含主程序与 Launcher（先 build 后 publish 到同目录）。
5. notes 写入用 `[System.Text.UTF8Encoding]::new($false)`（无 BOM），避免 gh 读 notes 时中文乱码。
6. zip 路径不含空格、在仓库根生成，`.gitignore` 已忽略 `*.zip`，不会污染工作区。

- [ ] **Step 4: 本地 dry-run 模拟 CI 构建命令**

在仓库根依次执行（验证命令在真实 .NET 9 下可用、产物目录正确）：

```
dotnet build TraeCheckin.csproj -c Release --nologo
dotnet publish TraeCheckin.Launcher\TraeCheckin.Launcher.csproj -c Release --nologo
```
Expected：两者均 `0 个错误`；`bin\TraeCheckin\` 下同时出现 `TraeCheckin.exe`（主程序产物）与 `TraeCheckin.Launcher.exe`（publish 产物，时间戳较新）。

再打包验证：
```
New-Item -ItemType Directory -Force -Path "$env:TEMP\trae_ci_dryrun" | Out-Null
Compress-Archive -Path "bin\TraeCheckin\*" -DestinationPath "$env:TEMP\trae_ci_dryrun\TraeCheckin-v9.9.9-win-x64.zip" -Force
(Get-Item "$env:TEMP\trae_ci_dryrun\TraeCheckin-v9.9.9-win-x64.zip").Length
```
Expected：输出 zip 字节数 > 10_000_000（自包含 Launcher 单文件通常数十 MB）。打包后删除临时目录。

- [ ] **Step 5: 提交**

```bash
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" add .github/workflows/release.yml
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" commit -m "feat: 新增自动构建并发布 Release 的 workflow（tag 触发 + changelog + zip 上传）"
```
> 注意：当前工作区有未提交的 PAT 弹窗改动（Controls/TextInputDialog.cs、Forms/MainForm.Cloud.cs、PAT spec）——本计划只 `git add` workflow 文件，不要把那些改动混入本提交。

---

## Task 2: 端到端验证（真实 tag）

**Files:** 无代码改动

**前置：** workflow 已在 main 分支上（Task 1 提交），源仓库本地能 push（origin 镜像已配置）。

- [ ] **Step 1: 推送 main**

```bash
git -C "c:\Users\星梦\Desktop\插件开发\TraeCheckin" push origin main
```
Expected：成功推送到 GitHub（含 Task 1 的 workflow 提交）。

> ⚠️ main 目前领先 origin 若干提交且带未提交改动。push 前需与用户确认：只推 workflow 相关提交，还是连同 PAT 系列一起推（PAT 遗留改动是否先提交由用户决定）。**不要擅自 push。**

- [ ] **Step 2: 决定验证方式（AskUserQuestion 与用户确认）**

提供两个选项：
- **A. 真实 tag v1.4.5**：bump 版本 → commit → push → `git tag v1.4.5 && git push origin v1.4.5` → 等 Actions 跑完验证 Release。
- **B. 临时冒烟 tag（不 bump csproj）**：打 `git tag v1.4.5-dryrun && git push origin v1.4.5-dryrun`（名称不匹配 `v*`？匹配，会触发；zip 名含 `-dryrun`），验证链路后删除 Release 与 tag。

Expected（两条路径相同）：
1. Actions 页出现 `Build & Auto Release` 运行，绿色 success。
2. Releases 页出现新 Release（title = tag），含 1 个资产 `TraeCheckin-<tag>-win-x64.zip`。
3. Release notes 顶部为 `## Changelog`，列出上一个版本以来的 commit 行。
4. 下载 zip 解压，双击 `TraeCheckin.Launcher.exe` 可启动主程序（此步可由用户在本地执行确认）。

- [ ] **Step 3: 回归确认**

确认 `checkin.yml` 每日签到 workflow 不受影响（其文件未被改动、权限未被触碰），云端每日 8:00 签到继续正常运行。

- [ ] **Step 4: 汇报结果**

向用户汇报：YAML 检查、本地 dry-run 结果、真实 tag 端到端结果（Release 链接 / 资产大小）、回归情况、是否建议后续把「打 tag 前 bump 版本号」也自动化（可另起任务，YAGNI 默认不做）。

---

## 自审记录（写完后运行）

**Spec 覆盖：**
- push tag 触发 + workflow_dispatch → Task 1 Step 1 `on` 段 ✅
- windows-latest 单 job → ✅
- checkout fetch-depth 0 / setup-dotnet 9 / build / publish → ✅
- 完整 `bin\TraeCheckin\*` 打包、命名保留 `v` → ✅
- changelog（上一 tag 到当前）→ `Resolve tags` + `git log PREV..TAG` ✅
- `gh release create` 上传 zip、contents: write → ✅
- YAML/语义检查 + dry-run + 真实 tag E2E → Task 2 ✅

**类型/命名一致性：** env 变量统一 `TAG` / `PREV_TAG`；step id `version` 不使用 outputs（仅靠 GITHUB_ENV），无跨 step 读取冲突。zip 名 `TraeCheckin-${{ env.TAG }}-win-x64.zip` 全计划唯一。

**已知风险：**
- `gh release create` 在 tag 已有 Release 时失败 → Task 2 中冒烟 tag 用 `-dryrun` 名称避免与真实 v1.4.5 冲突；正式发版时先确认无同名 Release。
- windows-latest 上 pwsh 默认存在，`Out-File -Encoding utf8` 与 `[Text.UTF8Encoding]` 均可用。
- `git tag --list 'v*' --sort=-v:refname` 依赖 tag 已 fetch：checkout `fetch-depth: 0` 保证全历史与 tags。
