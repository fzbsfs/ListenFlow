# ListenFlow 0.2.2 长网页与版本交付报告

## 问题证据

- 用户截图中的 Edge 扩展版本为 0.1.0；磁盘安装目录中的清单为 0.2.1。此前安装器只替换文件，没有保证正在运行的 Edge 重新加载，因此浏览器可能继续运行旧脚本。
- 2026 年 10 月 21 日使用隔离 Edge 实测 JavaGuide 的 `java-basic-questions-01.html`：页面正文快照为 29,759 个 UTF-16 字符、2,747 个原始文字节点；完整页面快照为 30,014 个字符。滚动到底部后数量没有变化，未发现固定长度截断。
- 协议最大正文长度为 5,000,000 个字符，单帧上限为 8 MiB；JavaGuide 实测正文远低于限制。
- 0.2.1 只把已映射节点的变化视为快照失效。正文根节点新增的懒加载段落不会触发更新，这是动态长页面继续使用旧快照的真实缺口。

## 长网页修复

- 正文根节点追加、删除或修改可读文字时，扩展会识别快照失效。
- 页面稳定 700 毫秒后重新提取正文，以相同会话和递增命令序号原子替换播放快照。
- 通过旧正文附近的文字锚点把朗读位置映射到新快照，避免正文前方插入内容后继续使用错误字符位置。
- 暂停期间只建立待恢复快照，不提前发声；继续时才从新位置启动。
- ListenFlow 自己的提示和回退高亮覆盖层被排除，不会造成递归重扫。
- 已经从虚拟列表 DOM 中卸载的文字仍无法可靠映射回网页原文；该边界不能靠字符串搜索掩盖。

## 音频输出调查

- `SapiSpeechService` 在创建时调用 `SetOutputToDefaultAudioDevice()`，停止时取消当前队列，退出时取消队列并释放 `SpeechSynthesizer`。
- 自动化回归在桌面程序存在时仍能再次创建 SAPI 合成器、枚举并使用 Microsoft Xiaoxiao，没有发现语音包被 ListenFlow 独占的证据。
- 当前检查环境只枚举到内置扬声器和内置麦克风，没有蓝牙音频端点，因此无法在本轮自动确认真实蓝牙听感。
- 微软官方说明 Windows 可以为每个应用保存独立音量和输出设备；Edge 有声但没有进入默认蓝牙设备时，应先检查“系统 → 声音 → 音量合成器”中的 Microsoft Edge 输出路由。
- 参考资料：
  - https://learn.microsoft.com/dotnet/api/system.speech.synthesis.speechsynthesizer.setoutputtodefaultaudiodevice
  - https://support.microsoft.com/windows/hardware/audio/fix-app-audio-not-working-while-system-sounds-work-in-windows
  - https://support.microsoft.com/accessibility/edge/accessibility-features-in-microsoft-edge

## 统一版本与目录

- 桌面程序、Native Messaging Host、协议程序集和 Edge 扩展统一为 0.2.2；消息协议仍为第 1 版。
- 唯一程序目录：`%LOCALAPPDATA%\Programs\ListenFlow`。
- 唯一用户数据目录：`%LOCALAPPDATA%\ListenFlow`。
- 最新构建目录：`artifacts\latest`。
- 版本压缩包：`artifacts\ListenFlow-0.2.2-Windows-x64.zip`。
- 旧 `ListenFlow-EdgeTest` 程序文件会被删除；该目录只保留一个指向最新版扩展的目录连接，使已经从旧路径加载扩展的 Edge 在重新加载或重启后直接读取 0.2.2。
- 旧验收数据先完整备份到统一数据目录的 `migration-backups`，再合并阅读历史；较新的进度文件会成为当前进度。
- 安装时若 Edge 正在运行，安装元数据明确记录需要重新加载。普通本地脚本不能安全地强制刷新正在运行的开发者模式扩展，因此用户仍需点击一次“重新加载”，或关闭所有 Edge 窗口再打开。

## 验证状态

- .NET 自动化测试：51 项通过，其中包含 3 项本机 Xiaoxiao 集成测试。
- Edge 单元与 DOM 测试：39 项通过。
- JavaGuide 实际 DOM 与快照测量：通过，正文约 2.9 万字符。
- 蓝牙耳机实际输出：待用户连接蓝牙音频端点后人工验证。
- 正在运行的正常 Edge 是否已经重新加载 0.2.2：安装后需按验收说明确认扩展卡片版本。
