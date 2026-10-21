# ListenFlow 本地听读助手

ListenFlow 是面向 Windows 的本地长文本听读工具。程序使用系统已安装、可由 SAPI 访问的语音，不接入在线语音、云端人工智能、在线文字识别、账号、遥测或云同步。

## 下载安装

从 [GitHub 发布页面](https://github.com/fzbsfs/ListenFlow/releases) 下载 Windows x64 测试包，解压后运行 `安装 ListenFlow.cmd`。安装包包含自包含桌面程序、本地通信宿主、Edge 扩展及安装和卸载脚本。

首次安装扩展时，在 Edge 的 `edge://extensions/` 页面启用开发者模式，选择“加载解压缩的扩展”，选择 `%LOCALAPPDATA%\Programs\ListenFlow\extension`。升级后点击“重新加载”，并刷新需要朗读的网页。当前尚未上架扩展商店，脚本不能承诺自动完成首次浏览器扩展加载。

当前预发布版本为 **0.2.2**。安装包不包含微软语音模型，也尚未提供自动下载语音的一键脚本。请先确保本机存在可供 SAPI 调用的中文离线语音。

也可下载仓库根目录的 `Install-ListenFlow.ps1`，用 Windows PowerShell 运行。它会下载上述固定版本安装包、验证 SHA256 后调用统一安装器；已有安装包时可传入 `-PackagePath` 离线安装。`-PrepareOnly` 只校验和解压，不安装。该入口不下载语音模型。

开发机实际使用自然语音 SAPI 适配器 0.2.6 和本地 Xiaoxiao 1.0.9.0；这不是所有 Windows 安装后默认具备的配置。适配器来源：[NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter)。其上游同时支持在线语音；本项目的离线配置要求禁用 Edge 和 Azure 在线语音，并在断网环境验证本地合成。第三方语音模型不适用本项目的 MIT 许可证，未随本仓库分发。

## 开源许可

项目原创代码采用 [MIT 许可证](LICENSE)。随仓库提供的 Mozilla Readability 保留其 [独立许可证](EdgeExtension/vendor/READABILITY-LICENSE.md)。微软系统语音、语音模型及第三方适配器分别遵循其自身许可。

## 当前功能

- 优先选择本机 `Microsoft Xiaoxiao` 中文语音，找不到时优先选择其他 `zh-CN` 本地语音。
- 打开 TXT、粘贴剪贴板文本或直接编辑正文。
- 按中文句读标点和安全字符边界切分长文，保留每段在原文中的字符偏移。
- 异步连续朗读，界面在播放期间保持响应。
- 播放、暂停、继续、停止、后退一句和前进一句。
- 语速调整、字符进度、百分比进度和当前句子选中高亮。
- 使用明确的就绪、朗读中、暂停、停止、完成和错误状态。
- 使用播放代次隔离旧回调；定位、停止或换文后，旧提示不会推进新文章或新位置。
- 播放控制器保留真实语音事件的 UTF-16 字符位置和字符数量，并在单一状态线程中处理命令与回调。
- 自动保存正文、文件路径、阅读位置、语音和语速；状态文件使用临时文件替换并保留备份。
- 保留最近 10 篇文章，每篇文章使用独立的本地状态文件。
- 旧版进度文件迁移、损坏主状态的备份恢复和源 TXT 变化检测。
- 单实例运行和最多保留 10 份的本地异常日志。
- 使用版本化 UTF-8 JSON 协议传递播放会话、正文快照、命令序号、播放代次和 UTF-16 字符范围。
- 独立的薄型 C# Native Messaging Host 通过当前用户命名管道复用唯一 ListenFlow 播放核心。
- 通信断开时返回明确错误；新命令可以重连，旧的开始、定位和停止命令不会自动重放。
- Edge Manifest V3 扩展可朗读所选网页文字或主要正文，正文提取和位置映射完全在本地完成。
- 实际 SAPI 进度映射回网页原始 DOM 范围，并直接在网页文字上显示句级高亮。
- 网页指读模式支持左键点击普通文字重新定位；链接、按钮、输入框和可编辑区域保持原有交互。
- 网页会话支持暂停、继续、停止和连续定位，旧会话、旧快照与旧播放代次不会更新当前高亮。

## 使用方法

1. 运行 `ListenFlow.exe`。
2. 确认“本地语音”显示 `Microsoft Xiaoxiao（zh-CN）`，或选择其他本地语音。
3. 打开 TXT、粘贴剪贴板文字，或直接输入正文。
4. 点击“播放”。
5. 使用暂停、继续、停止、后退一句和前进一句控制阅读位置。
6. 从“最近文章”切换之前保存的内容和进度。

窗口快捷键：

- `F5`：播放、暂停或继续。
- `Esc`：停止。
- `Alt+左方向键`：后退一句。
- `Alt+右方向键`：前进一句。

## 本地数据与隐私

默认数据目录为 `%LOCALAPPDATA%\ListenFlow`：

- `progress.json`：当前文章、阅读位置和设置。
- `progress.json.bak`：上一份有效状态备份。
- `history`：最近文章的独立状态文件。
- `logs`：仅在程序异常时写入的本地错误日志。

状态文件包含文章正文。私人文档也会保存在这一本机目录中。设置环境变量 `LISTENFLOW_DATA_DIR` 可以把数据目录改到其他本地路径，主要用于便携运行和隔离测试。

## 语音与离线边界

- 自包含 `ListenFlow.exe` 包含运行该程序所需的 .NET 运行时，但不包含 Windows 语音包。
- 目标电脑必须已经安装并启用可由 SAPI 访问的语音；`Microsoft Xiaoxiao` 是首选项。
- Edge 浏览器的在线自然语音与本程序使用的 Windows SAPI 语音不是同一项能力。
- 程序没有在线语音回退路径。语音缺失、初始化失败或音频设备异常时会显示本地错误。
- 首次源码构建通常需要联网还原开发依赖；已经发布的程序朗读时不需要这些构建依赖。

## 构建与测试

需要 Windows x64 和 .NET 10 SDK；当前验证版本为 SDK `10.0.402`、运行时和 `System.Speech` `10.0.13`。在项目目录运行：

```powershell
.\build-release.ps1
```

该脚本会关闭 .NET 命令行遥测，依次还原依赖、运行不需要真实音频设备的测试，并发布到 `publish\ListenFlow.exe`。

若当前电脑已安装 `Microsoft Xiaoxiao` 且允许测试时播放声音，可运行包含真实语音集成测试的发布：

```powershell
.\build-release.ps1 -RunVoiceIntegrationTests
```

也可以双击 `build-publish.bat` 执行标准发布。

完整门禁见 [回归检查清单](回归检查清单.md)。

通信协议见 [第二批通信协议说明](docs/第二批通信协议说明.md)。第一批人工验收结果和临时验收工具的界面限制见 [第一批人工验收记录](docs/第一批人工验收记录.md)。

Edge 扩展的加载、注册和验证方法见 [Edge 扩展说明](EdgeExtension/README.md)，实际闭环结果见 [第二批 Edge 网页闭环阶段报告](docs/第二批Edge网页闭环阶段报告.md)。

用于真实 Edge 人工验收的本地安装包可通过以下命令生成：

```powershell
.\build-edge-test-package.ps1 -RunVoiceIntegrationTests
```

输出目录固定为 `artifacts\latest`，版本压缩包为 `artifacts\ListenFlow-0.2.2-Windows-x64.zip`。解压后双击“安装 ListenFlow.cmd”；程序默认安装到 `%LOCALAPPDATA%\Programs\ListenFlow`，数据保存在 `%LOCALAPPDATA%\ListenFlow`，不需要管理员权限，也不修改默认浏览器。安装器会清理旧程序并保留一个指向最新版扩展的旧路径兼容入口。若安装时 Edge 正在运行，需要在 `edge://extensions/` 对 ListenFlow 点击一次“重新加载”，或关闭所有 Edge 窗口后重新打开。

安装入口的 CMD 编码、换行和重复安装回归见 [Edge 人工验收安装脚本修复报告](docs/Edge人工验收安装脚本修复报告.md)。

## 当前边界

Edge 扩展当前只支持允许内容脚本运行的普通 `http` 和 `https` 页面。浏览器内部页、扩展商店及禁止注入的页面不支持；正文动态变化时会停止会话，暂不做模糊重映射。原生右键菜单仍需完成一次人工点选验收后才能标记为最终通过。

第一版不包含 PDF、DOCX、EPUB、屏幕文字识别、全局快捷键、系统托盘、开机启动、在线更新或任何联网服务。当前句高亮使用文本框选区，适合确认阅读位置，但视觉效果仍有后续改进空间。
