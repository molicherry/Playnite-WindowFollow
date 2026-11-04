# Playnite Sunshine Window Follow 插件（方案4）

Playnite 配套插件：**游戏启动时让 Sunshine 把当前串流会话的捕获目标从“整块显示器”切换为“游戏窗口”，游戏退出后恢复整屏捕获。**

配套的 Sunshine 侧改动（新增本地控制端点 `POST/GET /api/capture/window-follow`）由方案4 的 Sunshine 补丁实现，插件只负责在 Playnite 启动/退出游戏时调用该端点。

> 方案4 背景：用户通过 Sunshine 串流 Playnite Fullscreen（`Playnite.FullscreenApp.exe` 作为 Sunshine 应用）。Sunshine 默认采集整块显示器，Playnite 启动的窗口化游戏在手机端只占一小块画面。Sunshine 补丁让捕获目标可以“跟随前台窗口”，本插件负责在正确时机触发这次切换。

---

## 1. 功能

| 功能 | 行为 |
| --- | --- |
| 游戏启动联动 | `OnGameStarted` 触发后，延迟 `StartDelayMs`（默认 1500ms，等游戏窗口出现），调用 `POST /api/capture/window-follow`，`action=enable`、`mode` 按设置（默认 `foreground` 跟随前台窗口），并带上模式需要的 `window_title` / `process_id` |
| 游戏退出联动 | `OnGameStopped` 触发后调用 `action=disable`，让 Sunshine 恢复整屏捕获 |
| Playnite 退出兜底 | `OnApplicationStopped` 时如果仍处于窗口跟随状态，会尽力发送 disable；`Dispose` 把最终 disable 排入同一个后台 worker 并等待收尾（自身不直接发网络请求） |
| 启动状态查询 | 插件加载后 `GET /api/capture/window-follow` 查询一次当前状态，写入调试日志 |
| 会话状态解析 | 读取真实的 `sessions` / `selected_session_id` / `selected_client_uuid`；`Active` 取选中会话的 `override.enabled`（不是 `observed.active`），`Window` / `Mode` / `Fallback` 从选中会话正确映射，并保留完整会话列表供重同步 |
| 会话定向 | `EnableWindowFollow(..., int sessionId=0)` / `DisableWindowFollow(..., int sessionId=0)`；请求 JSON 携带 `session_id`，0 表示由 Sunshine 选择唯一活动会话 |
| 无活动会话 | HTTP 404（当前没有活动串流会话）视为正常情况，**静默跳过**，不写错误日志、不重试 |
| 事件竞态保护 | 游戏“启动后立刻退出”（崩溃/秒退）时，取消延迟中的 enable，避免跟随一个已经不存在的窗口 |
| 后台线程 | 所有 HTTP 请求都在独立后台线程串行执行，绝阻塞 Playnite 主线程，异常全部在后台线程内捕获 |
| 设置界面 | 地址、用户名、密码（PasswordBox）、总开关、捕获模式、标题/进程 ID、启动延迟、调试日志 + “测试连接”按钮 |
| 主菜单 | 插件菜单提供“测试 Sunshine 连接”，用通知显示结果 |
| 本机专用 | 只允许回环地址（`127.0.0.1` / `::1` / `localhost`）；`http://` 仅允许不携带凭据的调试场景；禁止自动重定向 |
| 自签名证书 | 只对**本次请求且 URI 完全匹配的本机端点**放行证书错误；现代运行时用 per-handler 回调，旧运行时用 per-request 回调，**不修改全局** `ServicePointManager`；无法使用回调时按默认校验并清晰提示自签名错误 |
| 失败重试 | 传输失败 / HTTP 5xx 自动重试一次；4xx 不重试 |
| 超时 | 每次请求 3 秒超时 |

### 事件 → 请求时序

```
Playnite 启动游戏
   └─ OnGameStarted
        └─ [后台线程] 等待 StartDelayMs (默认 1.5s)
             └─ POST enable {mode: foreground|title|pid}
                  ├─ 200: 记录状态，手机端跟随游戏窗口
                  ├─ 404: 没有活动串流会话，静默跳过（单机游玩时正常情况）
                  └─ 其它: WARN/ERROR 写入 Playnite 日志

Playnite 退出游戏
   └─ OnGameStopped
        └─ [后台线程] POST disable → 恢复整屏捕获
```

---

## 2. 文件结构

```
Playnite-WindowFollow/
├── SunshineWindowFollow.csproj      # 官方 SDK-style 工程（net462 + UseWPF，PlayniteSDK 引用可切换）
├── extension.yaml                   # Playnite 插件清单（字段格式已按 Playnite 源码确认）
├── icon.png                         # 插件图标（Player 扩展列表 / 设置页显示）
├── build.ps1                        # 构建脚本：有 dotnet 用 dotnet，没有则用 VS BuildTools 的 Roslyn csc 离线构建
├── install.ps1                      # 安装/更新/卸载到 Playnite 扩展目录
├── README.md                        # 本文
├── .gitignore
├── src/
│   ├── Properties/AssemblyInfo.cs          # 程序集信息（含 net462 TargetFramework 标记，SDK-style 下关闭自动生成）
│   ├── SunshineWindowFollowPlugin.cs       # GenericPlugin 入口：事件重写、设置视图、主菜单、测试连接
│   ├── SunshineSettings.cs                 # 持久化设置模型（Playnite LoadPluginSettings/SavePluginSettings）
│   ├── SunshineSettingsViewModel.cs        # ISettings：BeginEdit/CancelEdit/EndEdit/VerifySettings
│   ├── SunshineSettingsView.cs             # 设置 UI（代码构建 WPF 控件，不依赖 XAML 编译工具链）
│   ├── SunshineController.cs               # 后台工作线程：事件→请求、延迟、取消、404 静默、状态缓存
│   ├── SunshineClient.cs                   # HTTP 客户端：Basic 认证、自签名证书、超时、重试、会话定向
│   ├── SunshineEndpoint.cs                 # 地址规范化（scheme/host/port、仅回环）与请求 URI
│   ├── SunshineModels.cs                   # 状态模型（sessions/override/observed）、捕获模式常量、日志文本工具
│   └── JsonLite.cs                         # 内置最小 JSON 解析器（零外部依赖）
└── tests/
    ├── SunshineWindowFollow.ProtocolTests/ # 独立 net8 协议/TLS 回归测试（不编进插件）
    └── SunshineLifecycle/                  # 独立 net8 控制器生命周期测试（不编进插件）
```

---

## 3. 构建

### 3.1 一键构建（推荐，本机已验证）

本机**没有 dotnet CLI**，也没有 .NET Framework 4.6.2 targeting pack，`build.ps1` 会自动走“离线 csc 构建”：

1. 自动查找 VS2022 BuildTools 自带的 Roslyn `csc.exe`；
2. 首次运行时从 NuGet 下载并缓存两个包到 `.build\packages\`（只用于构建，不进入插件产物）：
   - `Microsoft.NETFramework.ReferenceAssemblies.net462`（.NET Framework 4.6.2 参考程序集，等价于 targeting pack）
   - `PlayniteSDK`（官方 Playnite SDK 程序集，默认 6.17.0）
3. 用 csc 编译 `src\**\*.cs`，输出到 `dist\`：

```powershell
# 在插件目录下执行（Windows PowerShell 5.1 或 PowerShell 7 均可）
.\build.ps1

# 常用参数
.\build.ps1 -Configuration Debug        # 调试版（保留完整 PDB、不优化）
.\build.ps1 -OfflineOnly                # 强制使用 csc 离线构建
.\build.ps1 -PlayniteDir "C:\Program Files (x86)\Playnite"   # 优先引用本机 Playnite 自带的 Playnite.SDK.dll
.\build.ps1 -PlayniteSdkVersion 6.18.0  # 无本机 Playnite 时使用指定版本的 NuGet 包
```

产物：

```
dist\
├── SunshineWindowFollow.dll
├── SunshineWindowFollow.pdb   # 仅用于调试，Playnite 不加载
├── extension.yaml
└── icon.png
```

如果机器上装了 dotnet SDK，脚本会自动改用官方的 `dotnet build SunshineWindowFollow.csproj -c Release`（产物同样复制到 `dist\`），
可用 `-OfflineOnly` 强制走 csc 路径。

### 3.2 用 Visual Studio / dotnet SDK 构建（官方常规方式）

工程是标准的 SDK-style 工程（和 Playnite 官方扩展模板一致）：

```powershell
dotnet build .\SunshineWindowFollow.csproj -c Release
# 或者在 Visual Studio 2022 中直接打开 SunshineWindowFollow.csproj 构建
```

`Playnite.SDK` 引用策略（csproj 内已写死条件，无需手改）：

| 场景 | 引用方式 |
| --- | --- |
| 本机安装了 Playnite（默认 `C:\Program Files (x86)\Playnite`，可用 `-p:PlayniteDir=...` 覆盖） | 直接引用 `$(PlayniteDir)\Playnite.SDK.dll`（`HintPath`），保证和运行中的 Playnite 版本完全一致，且不复制进产物 |
| 没有本机 Playnite | 回退到 NuGet 官方包 `PlayniteSDK`（默认版本 6.17.0，可用 `-p:PlayniteSdkVersion=x.y.z` 覆盖） |

> 如果使用 `dotnet build` 但机器没有 .NET Framework 4.6.2 targeting pack，会报 MSB3644。两种解决方式：
> 1. 用本仓库的 `build.ps1`（自动下载参考程序集，不污染系统）；
> 2. 在 VS Installer 里勾选 “.NET Framework 4.6.2 targeting pack”，或给工程加 `Microsoft.NETFramework.ReferenceAssemblies` 包引用。

### 3.3 实际构建验证结果（本机）

本机环境：Windows，**无 dotnet CLI**，无 `.NETFramework\v4.6.2` targeting pack；
`MSBuild.exe` 17.14.60 @ `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe`；
Roslyn `csc.exe` 4.x 同目录 `Roslyn\csc.exe`。

实际执行 `.\build.ps1` 的关键输出：

```
==> 使用 Roslyn csc 编译（离线构建）
    csc          : ...\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
    参考程序集   : ...\.build\packages\msnetfxref.net462.1.0.3\build\.NETFramework\v4.6.2
    Playnite SDK : ...\.build\packages\playnitesdk.6.17.0\lib\net462\Playnite.SDK.dll（版本 6.17.0.0）
    源文件       : 10 个

==> 构建成功
    输出目录    : ...\dist
    Playnite SDK: 6.17.0.0
    产物文件    :
      - extension.yaml
      - icon.png
      - SunshineWindowFollow.dll
      - SunshineWindowFollow.pdb
```

编译产物校验（用 .NET Framework 运行时加载检查）：

- `SunshineWindowFollow, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`
- 引用程序集：`mscorlib / Playnite.SDK 6.17.0.0 / System / System.Net.Http / PresentationCore / PresentationFramework`
- **没有**引用 `Playnite` / `Playnite.Common`（Playnite 加载插件时强制校验，`ExtensionFactory.VerifyAssemblyReferences`）
- `SunshineWindowFollowPlugin` 继承 `Playnite.SDK.Plugins.GenericPlugin`，重写了 `OnGameStarted` / `OnGameStopped`，`Id` 为 Guid
- `SunshineSettingsViewModel` 实现了 `Playnite.SDK.ISettings`
- 程序集带 `.NETFramework,Version=v4.6.2` 的 `TargetFrameworkAttribute`

另外，也尝试过直接用本机 MSBuild 构建官方 SDK-style 工程（预期失败，如实记录）：

```
& MSBuild.exe SunshineWindowFollow.csproj /t:Build /p:Configuration=Release
error MSB4236: 找不到指定的 SDK“Microsoft.NET.Sdk”。
  MSB4276: 默认 SDK 解析程序解析 SDK“Microsoft.NET.Sdk”失败，
  因为目录“...\BuildTools\MSBuild\Sdks\Microsoft.NET.Sdk\Sdk”不存在。
```

原因：本机只装了 VS2022 BuildTools（MSBuild + Roslyn），没有 .NET SDK（`dotnet` CLI 不存在），
所以 SDK-style 工程无法由裸 MSBuild 解析；`build.ps1` 的 csc 路径就是为此准备的无 SDK 构建方案。
有 .NET SDK / 完整 VS 的机器上，官方工程仍是最推荐的构建方式。

### 3.4 协议 / TLS 回归测试（可复现）

`tests/` 下是独立的 .NET 8 测试工程，直接链接 `src/` 的协议文件，**不需要安装 Playnite**；
测试工程已从插件 csproj 中排除（`tests/**/*.cs`），不会编进 `SunshineWindowFollow.dll`。

```powershell
# 协议 / TLS（本 README 第 6、9 节的行为）
dotnet run --project .\tests\SunshineWindowFollow.ProtocolTests -c Release

# 控制器生命周期（多游戏、会话定向、404 对账、Dispose 收尾）
dotnet run --project .\tests\SunshineLifecycle -c Release
```

测试使用本地 Kestrel 启动 HTTP/HTTPS 服务，HTTPS 证书在运行时生成（自签名且 CN 与 `127.0.0.1` 不匹配），覆盖：

- 真实嵌套响应解析：`sessions` / `selected_session_id` / `selected_client_uuid`、`override.enabled`、`observed.*`；
- `Active=override.enabled`（即使 `observed.active` 相反）、`Mode` / `Window` / `Fallback` 映射、会话列表保留；
- 旧根级字段不再被误用；无 selected 时回退到 selected/唯一会话；
- 请求 JSON 包含 `session_id`（含中文/引号转义）；
- 非回环、URL 内嵌凭据、明文 HTTP 携带凭据均被拒绝；
- 自签名 HTTPS 回环可用，且进程级 `ServicePointManager.ServerCertificateValidationCallback` 不被修改；
- `requestUri==null` / 非本机 URI / 不同端口 / 不同 scheme 不放行证书错误；
- 302 重定向不被跟随；旧运行时证书错误有清晰提示。

---

## 4. 安装

### 4.1 自动安装（推荐）

先构建，再执行：

```powershell
.\install.ps1
```

脚本会按 Playnite 源码规则（`source\Playnite\Settings\PlaynitePaths.cs`）解析扩展目录：

| Playnite 类型 | 判定 | 扩展目录 |
| --- | --- | --- |
| 便携版 | 安装目录没有 `unins000.exe` | `<Playnite目录>\Extensions` |
| 安装版 | 安装目录有 `unins000.exe` | `%AppData%\Playnite\Extensions` |

也可显式指定：

```powershell
.\install.ps1 -PlayniteDir "C:\Program Files (x86)\Playnite"
.\install.ps1 -ExtensionsDir "D:\Playnite\Extensions"
.\install.ps1 -Uninstall          # 卸载（只删扩展目录，保留插件设置数据）
```

安装完成的目录结构：

```
<扩展目录>\SunshineWindowFollow\
├── SunshineWindowFollow.dll
├── extension.yaml
├── icon.png
└── SunshineWindowFollow.pdb
```

安装/更新时如果旧版本存在，会先备份到 `.build\install-backup\<时间戳>\`；卸载时只会删除扩展目录下 `extension.yaml` 的 Id 属于本插件的文件夹，避免误删。

> 本机没有安装 Playnite，所以 `install.ps1` 的“自动检测 Playnite 安装目录”只做了代码路径验证，
> 实际安装测试是用 `-ExtensionsDir <临时目录>` 完成的（安装/覆盖/卸载均通过）。到真机上建议显式传 `-PlayniteDir`。

### 4.2 手动安装

把 `dist\` 整个目录复制到 `%AppData%\Playnite\Extensions\SunshineWindowFollow\`
（便携版 Playnite 则是 `<Playnite目录>\Extensions\SunshineWindowFollow\`），然后重启 Playnite。

### 4.3 重启并配置

1. 重启 Playnite（Desktop 模式）；
2. `设置 → 扩展 → Sunshine Window Follow`；
3. 填写 Sunshine WebUI 地址、用户名、密码（见下一节）；
4. 点“测试连接”，看到 `连接成功（HTTP 200）。ok=True, active=...` 即配置正确；
5. 正常使用时用 Fullscreen 模式（Sunshine 应用是 `Playnite.FullscreenApp.exe`），插件在 Fullscreen 进程里同样生效。

---

## 5. 配置项

设置界面位于 **Desktop 模式：设置 → 扩展 → Sunshine Window Follow**（Fullscreen 模式没有插件设置页，但配置是 Desktop/Fullscreen 共享的）。

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| 启用插件 | `true` | 总开关。关闭后插件不发起任何请求 |
| Sunshine 地址 | `https://127.0.0.1:47990` | Sunshine WebUI 端口（默认 47990）。可省略 `https://`；路径部分会被忽略；**只允许回环地址**（`127.0.0.1` / `::1` / `localhost`）。`http://` 仅允许在用户名和密码都为空时用于本机调试，带凭据会被拒绝 |
| 用户名 | 空 | Sunshine WebUI 用户名（HTTP Basic 认证） |
| 密码 | 空 | Sunshine WebUI 密码（HTTP Basic 认证）。**以明文保存在插件配置文件中**，见安全说明 |
| 输出调试日志 | `false` | 打开后把请求/响应详情写入 Playnite 日志（`logger.Debug`） |
| 捕获模式 | `foreground` | `foreground` 跟随前台窗口（推荐）；`title` 按窗口标题匹配；`pid` 按进程 ID 匹配 |
| 窗口标题 | 空 | 仅 `title` 模式使用；留空则使用 Playnite 启动的游戏名称 |
| 进程 ID | 0 | 仅 `pid` 模式使用；`<=0` 则使用 Playnite 报告的 `StartedProcessId`（`OnGameStartedEventArgs.StartedProcessId`） |
| 启动延迟（毫秒） | `1500` | 游戏启动后等待窗口出现的时长，范围 0–10000。窗口出现慢的游戏可调大 |

模式细节：

- `foreground`：Sunshine 侧跟随当前前台窗口。兼容性最好，推荐；但如果游戏窗口没有抢到前台（或启动器/弹窗抢先），可能跟随错误窗口。
- `title`：按 `window_title` 匹配窗口。适合一个游戏进程弹多个窗口的情况；Sunshine 侧具体匹配规则（精确/子串）以实现补丁为准。
- `pid`：按 `process_id` 匹配窗口。适合游戏由多个进程组成（启动器 + 游戏本体）的情况。
- `title`/`pid` 模式下如果插件拿不到有效标题/进程 ID，会**自动回退为 `foreground` 并写警告日志**，不会让游戏启动失败。

设置存储位置（Playnite SDK 行为，`Plugin.LoadPluginSettings/SavePluginSettings`）：

```
%AppData%\Playnite\ExtensionsData\c40d275b-5dad-4f8e-9640-137ec5267ef1\config.json
# 便携版：<Playnite目录>\ExtensionsData\<插件 GUID>\config.json
```

---

## 6. Sunshine 端点契约（插件按此实现）

Base URL 默认 `https://127.0.0.1:47990`（Sunshine WebUI 端口，本机自签名证书）；认证 HTTP Basic（Sunshine WebUI 用户名/密码）。该控制端点只接受本机请求，插件也仅在回环地址上工作。

### 请求

```
POST /api/capture/window-follow
Content-Type: application/json

{"action":"enable","mode":"foreground","window_title":"","process_id":0,"session_id":0}
```

| 字段 | 说明 |
| --- | --- |
| `action` | `enable` / `disable` / `status` |
| `mode` | `foreground`（推荐）/ `title` / `pid` |
| `window_title` | `mode=title` 时使用；插件在设置为空时填游戏名 |
| `process_id` | `mode=pid` 时使用；插件在设置为 0 时填 `StartedProcessId` |
| `id` | 目标会话 ID（新版本地控制协议字段）；`0` = 由 Sunshine 选择唯一活动会话 |
| `session_id` | 会话 ID；`0` = 自动选择唯一活动会话 |

`EnableWindowFollow(endpoint, mode, title, pid, sessionId)` 与
`DisableWindowFollow(endpoint, sessionId)` 的最后一个参数就是上面的会话 ID；
控制器在 enable 成功后保存 `selected_session_id`，退出时定向 disable 同一个会话，避免多客户端时自动选择返回 409。

`GET /api/capture/window-follow` 返回会话列表（插件启动时查询、设置页“测试连接”使用）。

### 响应

成功 200（GET 与 POST 相同；POST 额外带 `selected_*`）：

```json
{
  "ok": true,
  "feature": "window-follow",
  "supported": true,
  "sessions": [
    {
      "session_id": 1,
      "client_uuid": "...",
      "client_name": "Moonlight",
      "app_name": "Playnite",
      "running": true,
      "control_only": false,
      "override": { "enabled": true, "mode": "foreground", "window_title": "", "process_id": 0 },
      "observed": {
        "active": true,
        "fallback_to_display": false,
        "window_title": "Hades",
        "process_id": 1234,
        "hwnd": "0x00000000000A1B2C",
        "fallback_reason": ""
      }
    }
  ],
  "selected_session_id": 1,
  "selected_client_uuid": "..."
}
```

插件解析规则（`SunshineModels.cs`）：

| 插件字段 | 来源 |
| --- | --- |
| `Ok` | 顶层 `ok` |
| `Active` | **选中会话的 `override.enabled`**（不是 `observed.active`；后者只表示捕获后端当前是否真的跟随到窗口） |
| `Mode` | 选中会话的 `override.mode` |
| `Window` | 选中会话的 `observed.window_title` / `process_id` / `hwnd` |
| `Fallback` | 选中会话的 `observed.fallback_reason` |
| `FallbackToDisplay` | 选中会话的 `observed.fallback_to_display` |
| `SessionId` | 优先 `selected_session_id`；否则按 `selected_client_uuid` 匹配，再取标记/唯一会话 |
| `ClientUuid` | 优先 `selected_client_uuid`；否则取选中/唯一会话的 `client_uuid` |
| `Sessions` | 完整会话列表（含每个会话的 override/observed），供后续按会话重同步 |

**旧版根级解析已移除**：不再读取顶层的 `active` / `mode` / `session_id` / `client_uuid` / `window` / `fallback`。
这些值在真实响应中不存在，误读会把其他会话或 `observed.active` 当成插件状态。

### 插件对状态码的处理

| 状态码 | 含义 | 插件行为 |
| --- | --- | --- |
| 200 | 成功 | 解析状态；`ok=false` 时写 WARN（带 `error` 内容） |
| 400 | 参数错误 | ERROR 日志，带响应正文（不重试） |
| 401 | 认证失败 | ERROR 日志提示检查用户名/密码（不重试） |
| 403 | 非本机 | ERROR 日志提示 Playnite 与 Sunshine 必须在同一台机器（不重试） |
| 404 | 当前没有活动串流会话 | **静默跳过**（DEBUG 日志，不重试，不视为错误） |
| 409 | 多会话 / 不支持 | WARN 日志（不重试）；控制器会尝试用已保存的 `session_id` 定向重试 disable |
| 3xx | 重定向 | **不跟随**；按非 200 错误记录，避免 Basic 凭据/控制请求被带往其他地址 |
| 5xx | 服务端错误 | 自动重试一次；仍失败写 ERROR 日志 |
| 传输失败 | 连接失败/超时/证书错误 | 自动重试一次；仍失败写 ERROR 日志（含根因链，如 `请求超时（3000 毫秒）`） |

---

## 7. 测试连接

两个入口：

- **设置界面**：“测试连接”按钮 → 先 `GET` 一次状态，在按钮下方显示详细结果（成功为绿色，失败为红色）；
- **插件主菜单**：“测试 Sunshine 连接” → 后台执行并在 Playnite 通知中心显示结果。

结果示例：

```
连接成功（HTTP 200）。ok=True, active=True, mode=foreground, session_id=1,
client_uuid=..., window[title="Hades", process_id=1234, hwnd=0x...]
```

`active` 表示**请求的 override 已启用**（`override.enabled=true`）。如果 `observed.active=false`
且带有 `fallback_reason`，说明 Sunshine 已接受请求但暂时没找到窗口，会回退到整屏捕获并继续重试；
此时连接测试仍显示成功，详情里的 `window[...]` / `fallback=...` 可用于排查。

```
已连通 Sunshine，但当前没有活动的串流会话（HTTP 404）。开始串流后再测试可看到完整状态。
```

```
认证失败（HTTP 401），请检查 Sunshine WebUI 用户名和密码
```

测试连接使用的是**当前界面上编辑中的设置**，不需要先保存。

---

## 8. 故障排查

日志位置：`%AppData%\Playnite\playnite.log`（便携版在 Playnite 目录下），搜索 `SunshineWindowFollow`。
打开“输出调试日志”后会额外记录请求/响应详情。

| 现象 | 排查方向 |
| --- | --- |
| 扩展列表里没有本插件 | 检查扩展目录是否正确（安装版 `%AppData%\Playnite\Extensions`，便携版 `<Playnite目录>\Extensions`）；检查文件夹里 `extension.yaml` 的 `Module: SunshineWindowFollow.dll` 与 dll 文件名一致；看日志里 `Failed to load plugin` / `Plugin dependencices are not compatible` |
| 提示 SDK 版本不兼容 | 插件引用的 Playnite SDK 6.17.0 需要 Playnite 6.17+。用 `build.ps1 -PlayniteSdkVersion`（或 `-PlayniteDir` 指向本机 Playnite）重新构建 |
| 测试连接 401 | 用户名/密码错误；Sunshine 首次运行创建的 WebUI 账号信息可在 Sunshine 安装目录的 `sunshine.conf` 中查看/重置 |
| 测试连接 403 | Playnite 与 Sunshine 不在同一台机器，或请求不是从本机回环地址发出（端点只允许本机） |
| 地址被拒绝：只允许回环 | 插件只接受 `127.0.0.1` / `::1` / `localhost`。把 `http://192.168.x.x:47990`、域名或公网地址改成 `https://127.0.0.1:47990` |
| 地址被拒绝：明文 http 凭据 | 使用 `http://` 时不能填写用户名/密码。改回 `https://127.0.0.1:47990`，或清空凭据用于本机无认证调试 |
| 测试连接 404 | 当前没有活动的串流会话。这是正常现象：单机游玩时 enable 会被静默跳过 |
| 测试连接报证书/连接错误 | 地址或端口不对（默认 47990 是 WebUI HTTPS 端口，不是串流端口 47989/47984）；Sunshine 未启动；`https://` vs `http://` 不一致。若日志提示“TLS 证书校验/自签名”：确认地址是**回环地址**；极老运行时无法使用每请求回调时，把 Sunshine 证书安装到本机“受信任的根证书颁发机构”，或改用受信任证书 |
| 游戏启动了但手机端画面没变化 | 1) 插件总开关是否为开；2) Playnite 日志是否有“请求 Sunshine 启用窗口跟随”；3) Sunshine 是否已安装方案4补丁（对未打补丁的 Sunshine，`/api/capture/window-follow` 会返回 404）；4) 调试日志里是否出现 `HTTP 404`（无活动串流会话）；5) 尝试把启动延迟调大（如 2500ms）或改用 `pid` 模式 |
| 游戏退出后画面没有恢复 | 检查日志里 disable 的结果；如果 Playnite 被强退（没有 OnGameStopped），下次串流开始时会由 Sunshine 默认状态决定；插件在 Playnite 正常退出时会兜底发送 disable |
| enable 成功但跟随了错误窗口 | 改用 `title` / `pid` 模式；`title` 模式建议填写准确的窗口标题 |
| 设置无法保存 | 看日志中 `保存插件设置失败`；检查 `%AppData%\Playnite\ExtensionsData\<插件 GUID>` 是否可写 |

---

## 9. 安全说明（请务必阅读）

1. **只允许本机端点**：Sunshine 的 `/api/capture/window-follow` 是 localhost-only API。
   插件在 `SunshineEndpoint.TryCreate` 阶段就拒绝非回环主机（`127.0.0.1` / `::1` / `localhost`），
   避免把控制请求和 Basic 凭据发到局域网/公网。
2. **自签名证书放行范围**：Sunshine WebUI 默认使用自签名证书。插件**不会**全局关闭证书校验，
   也**不会**替换进程级 `ServicePointManager.ServerCertificateValidationCallback`：
   - .NET Framework 4.7.1+：通过反射使用 `HttpClientHandler.ServerCertificateCustomValidationCallback`，
     只作用于**本次请求的 handler**；
   - 旧运行时：使用 `HttpWebRequest.ServerCertificateValidationCallback`（同样只作用于**本次 request**）；
     没有该属性的运行时退回默认校验，自签名证书会失败，但日志会明确提示“TLS 证书校验/自签名”
     以及“安装到受信任根存储或改用受信任证书”的处理办法；
   - 回调只有在 `requestUri != null`、URI 为回环、且 scheme+host+port 与配置端点**完全一致**时才放行
     证书错误；`requestUri==null` 一律拒绝；
   - 不会接受其它主机（包括非回环地址）的证书错误。
3. **不跟随重定向**：`HttpClientHandler.AllowAutoRedirect=false`，旧路径
   `HttpWebRequest.AllowAutoRedirect=false`；302/301 原样返回并按错误处理，避免 Basic 凭据被带到
   `Location` 指定的其它地址。
4. **凭据明文**：Playnite SDK 没有提供加密存储能力，Sunshine 用户名/密码以**明文 JSON** 保存在
   `%AppData%\Playnite\ExtensionsData\<插件 GUID>\config.json`。请确保该目录仅本机可信用户可读；
   不要在多人共用的机器上保存密码。
5. **HTTP Basic 与明文 HTTP**：凭据以 Base64（等价明文）放在请求头里。插件只允许在无凭据时使用
   `http://`（本机调试）；配置了用户名/密码却使用 `http://` 会直接拒绝，客户端还会在发送前再兜底拒绝一次。
   正常使用请保持 `https://127.0.0.1:47990`。
6. **端口暴露**：即使插件只连回环地址，Sunshine WebUI 端口（默认 47990）如果被端口转发/代理暴露到
   局域网或公网，仍可能让外部访问控制接口；请用防火墙限制来源，不要将 47990 暴露到公网。
7. **插件权限**：插件只做“向配置的回环地址发送 HTTP 请求 + 记录日志”，不读写游戏文件、不执行外部程序。

---

## 10. 已验证 / 未验证（诚实声明）

### 已验证 ✅

- **协议/TLS 回归**：`tests/SunshineWindowFollow.ProtocolTests` 在本地 Kestrel HTTP/HTTPS 服务上运行，
  **15/15 通过**（自签名且 CN 与 `127.0.0.1` 不匹配的证书）；覆盖嵌套响应解析、会话选择、
  `Active=override.enabled`、`Window/Mode/Fallback` 映射、旧根级字段忽略、请求 JSON（`session_id`/转义）、
  非回环/凭据/明文 HTTP 拒绝、`requestUri==null` 拒绝、302 不跟随、全局证书回调不变、证书错误提示。
- **控制器生命周期回归**：`tests/SunshineLifecycle` 的 **10/10** 场景通过（多游戏启停、代次竞态、enable 后
  保存 `session_id` 定向 disable、409 回退、404 清理/有界对账、Dispose 在同一 worker 收尾）。
- **编译**：完整插件源码（`src/**/*.cs`）用 Roslyn csc + net462 参考程序集 + PlayniteSDK 6.17.0 编译通过；
  `SunshineWindowFollow.csproj` 已排除 `tests/**/*.cs`，测试代码不会进入插件 DLL。
- **Playnite 加载约束**：产物引用 `Playnite.SDK 6.17.0.0`、不引用 `Playnite`/`Playnite.Common`、插件类继承
  `GenericPlugin` 且 `Id` 为 Guid、设置类实现 `ISettings` —— 与 Playnite `ExtensionFactory` 的加载/校验逻辑一致。
- **安装脚本**：`install.ps1` 安装/覆盖安装（含旧版本备份）/卸载，用临时扩展目录实测通过；
  `extension.yaml` 的 Id/Version/Module 会在安装前校验。

### 未验证 ⚠️

- **未在真实 Playnite 中加载运行**：开发机没有安装 Playnite，无法实际验证扩展列表、设置界面渲染、
  `OnGameStarted/OnGameStopped` 真实事件回调；这些 API 用法是逐行核对 Playnite 源码确认的。
- **未在真机联调真实 Sunshine 补丁端点**：协议 fixture 按需求给出的真实响应结构构造，并用本地模拟服务器
  做端到端验证；真实 `title`/`pid` 窗口匹配、`fallback_reason` 的具体取值仍需在 Windows 真机确认。
- **插件 `dotnet build` / `build.ps1` 路径**：本次验证使用 Roslyn csc 直接编译完整插件源码与 net8 测试工程；
  未在 Windows + Playnite 环境执行 `build.ps1`/`dotnet build`（工程格式沿用原模板，见 3.3）。
- **Playnite 版本兼容性**：默认引用 PlayniteSDK 6.17.0（Playnite ≥6.17）。未在 6.16/6.17/6.18 各版本上实测；
  如目标机器版本不同，用 `build.ps1 -PlayniteDir` 或 `-PlayniteSdkVersion` 重新构建即可。
- **设置界面视觉效果**：设置 UI 是用代码构建的 WPF 控件，**外观未在 Playnite 设置页人工确认**。

---

## 11. 与本文档相关的 Playnite 源码依据

插件 API/清单格式/安装路径均以本机 Playnite 源码为准（`Playnite 源码`）：

| 内容 | 源码位置 |
| --- | --- |
| 目标框架 net462 | `Playnite\Playnite.csproj:5`、`Playnite.DesktopApp\Playnite.DesktopApp.csproj:6`、`PlayniteSDK\Playnite.SDK.csproj:3` |
| `GenericPlugin` 基类、`OnGameStarted`/`OnGameStopped` 签名 | `PlayniteSDK\Plugins\Plugin.cs:149`、`:229`、`:236` |
| `GetSettings` / `GetSettingsView` | `PlayniteSDK\Plugins\Plugin.cs:206`、`:214` |
| `LoadPluginSettings` / `SavePluginSettings`（config.json） | `PlayniteSDK\Plugins\Plugin.cs:390`、`:408`、`:170` |
| `ISettings`（IEditableObject） | `PlayniteSDK\ISettings.cs:13` |
| 事件参数 `Game`/`StartedProcessId`、`OnGameStoppedEventArgs` | `PlayniteSDK\Events\ApplicationEvents.cs:158`、`:178`、`:195` |
| 插件 ID/设置 GUID | `PlayniteSDK\Plugins\Plugin.cs:185`、`:357` |
| `extension.yaml` 字段（Id/Name/Author/Version/Links/Module/Icon/Type） | `Playnite\Manifests\ExtensionManifest.cs:22`、`:52`、`:70` |
| SDK 版本兼容性校验（插件必须引用 Playnite.SDK，且主版本一致、不高于运行时版本） | `Playnite\Plugins\ExtensionFactory.cs:295`、`:312` |
| 扩展扫描目录（用户/程序） | `Playnite\Plugins\ExtensionFactory.cs:248`、`:268` |
| 扩展安装路径规则（便携/安装版） | `Playnite\Settings\PlaynitePaths.cs:24`、`:89`、`:100` |
| 事件回调调用（含 try/catch 隔离） | `Playnite\Plugins\ExtensionFactory.cs:652`、`:556` |
| 设置 JSON 序列化实现 | `Playnite\Common\Serialization.cs:191` |
| 官方扩展模板（清单/csproj/设置 VM 写法） | `Tools\Playnite.Toolbox\Templates\Extensions\GenericPlugin\` |
| 示例插件（事件/主菜单/设置 VM） | `Tests\TestPlugin\TestPlugin.cs`、`TestPluginSettings.cs` |
| Playnite 启动时的 TLS 协议处理 | `Playnite\App\PlayniteApplication.cs:104-108` |

---

## 12. 下一步建议

1. **联调真实 Sunshine 补丁**：在真机上启动一次串流，用 `.\build.ps1` 构建 → `.\install.ps1 -PlayniteDir <Playnite目录>` 安装，从 Playnite 启动游戏，对比手机端画面是否切换为游戏窗口；同时观察 Playnite 日志中 enable/disable 的状态码与返回 JSON。
2. **确认 mode 语义**：与 Sunshine 补丁确认 `title` 模式的匹配规则（精确/子串/正则）和 `pid` 模式是“主进程还是任意子进程”，再决定默认推荐模式（当前默认 `foreground`）。
3. **按实际 Playnite 版本锁定 SDK**：如果用户 Playnite 是 6.18，建议 `.\build.ps1 -PlayniteDir <Playnite目录>` 让插件引用本机 SDK，避免版本错配。
4. **可选增强**（未实现，按需）：设置页增加“游戏退出后延迟恢复”以兼容启动器重开窗口的场景；支持从 Sunshine 配置读取用户名/密码；`title` 模式支持子串匹配开关；插件更新源打包成 `.pext` 发布。
