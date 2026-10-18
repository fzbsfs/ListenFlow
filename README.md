# ListenFlow MVP（Windows 本地听读助手）

这是一个离线优先的 Windows 桌面听读器原型，使用 Windows 已安装的 SAPI 语音，不集成云端 TTS 或 AI 语音服务。

## 当前功能

- 枚举系统已安装、可供 SAPI 使用的语音，优先选择 `Microsoft Xiaoxiao`
- 打开 TXT 文件或粘贴剪贴板文字
- 连续朗读长文本（自动按句读标点和长度切分）
- 暂停、继续、停止
- 调整 SAPI 语速（-10 到 +10）
- 保存文章、语音选择、语速和当前字符位置
- 下次启动时恢复上次文章与进度
- 以 Windows x64 自包含单文件形式发布

## 重要说明

1. 本程序本身不调用网络接口，也不集成在线 TTS 或云端 AI。
2. 朗读依赖 Windows 上已安装且能被 SAPI 访问的语音。你目前枚举到的 `Microsoft Xiaoxiao` 是优先选择项。
3. “独立单文件 EXE”不代表把 Windows 语音引擎也塞进 EXE。目标电脑仍需要存在对应的系统语音。
4. 第一次构建需要安装 .NET 8 SDK，并还原 `System.Speech` NuGet 包；发布完成后，朗读过程可以断网运行。
5. 这是第一版 MVP，尚未实现 PDF/DOCX 解析、屏幕 OCR、系统托盘、全局快捷键或句子高亮。

## 构建 EXE

### 方式 A：双击批处理

在已安装 .NET 8 SDK 的 Windows 电脑上，双击 `build-publish.bat`。

发布产物位于：

`publish\\ListenFlow.exe`

### 方式 B：命令行

在项目文件夹打开 PowerShell 或命令提示符，运行：

```powershell
dotnet publish ListenFlow.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\\publish
```

## 使用方法

1. 运行 `publish\\ListenFlow.exe`。
2. 确认“本地语音”下拉框选中了 `Microsoft Xiaoxiao`。
3. 点击“打开 TXT”或“粘贴剪贴板”。
4. 点击“从当前位置播放”。
5. 暂停后可以继续；停止后进度会保存。
6. 关闭再打开程序，会恢复上次的文章和阅读位置。

## 阅读进度保存位置

`%LOCALAPPDATA%\\ListenFlow\\progress.json`

这是本机文件，包含最近文章文本和阅读位置。请注意：如果读取的是私人文档，该文件也会保存对应文本。
