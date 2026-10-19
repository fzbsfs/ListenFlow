# Edge 人工验收安装与回退说明

本说明适用于基于提交 `5146717` 生成的 ListenFlow 0.2.0 Edge 人工验收构建。它用于验证真实 Edge 原生右键菜单、网页原文高亮和点击定位，不是正式发布版本。

## 安装包与默认路径

- 构建目录：`artifacts\ListenFlow-0.2.0-Edge人工验收-x64`
- 压缩包：`artifacts\ListenFlow-0.2.0-Edge人工验收-x64.zip`
- 验收程序：`%LOCALAPPDATA%\Programs\ListenFlow-EdgeTest\app\ListenFlow.exe`
- 原生消息宿主：`%LOCALAPPDATA%\Programs\ListenFlow-EdgeTest\app\ListenFlow.NativeHost.exe`
- Edge 扩展：`%LOCALAPPDATA%\Programs\ListenFlow-EdgeTest\extension`
- 原生宿主清单：`%LOCALAPPDATA%\ListenFlow-EdgeTest\NativeMessaging\com.listenflow.native_host.json`
- 当前用户注册：`HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.listenflow.native_host`
- 本地阅读数据：`%LOCALAPPDATA%\ListenFlow`

人工验收版使用独立程序目录，不覆盖原有 0.2.0 程序文件。它继续读取现有本地阅读数据，以便回归原有功能。卸载时默认保留这些数据。

## 安装

1. 解压整个 ZIP，保存当前朗读进度并关闭正在运行的 ListenFlow。
2. 双击“安装 ListenFlow.cmd”。脚本先校验安装负载的 SHA256，再复制主程序、宿主和扩展。
3. 安装脚本使用当前用户注册表，不要求管理员权限，不修改默认浏览器。
4. 脚本启动 ListenFlow，并打开 `edge://extensions/` 和扩展目录。
5. 在 Edge 扩展管理页开启“开发人员模式”，点击“加载解压缩的扩展”，选择 `%LOCALAPPDATA%\Programs\ListenFlow-EdgeTest\extension`。
6. 确认扩展标识为 `chdkljcgemgeljdnfdnblbelfhmkkfbp`。

普通脚本不能静默安装本地解压缩扩展，因此第 5 步必须人工完成一次。安装脚本会核对扩展公钥计算出的固定标识、Manifest V3 权限、原生宿主清单和注册值；发现不匹配时终止安装。

## 人工验收

1. 打开普通 `http` 或 `https` 长文章页面，在正文处点击右键，选择“使用 ListenFlow 朗读网页正文”。确认 Xiaoxiao 开始朗读，黄色高亮直接出现在网页原文当前句。
2. 选中一段文字，点击右键，选择“使用 ListenFlow 朗读所选文字”。确认只朗读选择范围并在原位置高亮。
3. 在指读模式中左键点击另一处普通正文。确认旧声音立即停止，高亮跳转，并从新位置继续。
4. 快速连续点击前后多个正文位置。确认最终只有最后一次定位继续发声和推进高亮。
5. 在扩展右键菜单依次验证暂停、继续、停止。暂停后点击另一处正文时不应提前发声；继续后应从新位置开始；停止后声音和高亮不再推进。
6. 点击链接、按钮和输入框。确认网页自身交互没有被无条件拦截；空白处和图片不应触发错误定位。
7. 关闭 Edge 和 ListenFlow，断开网络，使用本地可访问的普通测试页重新执行以上操作。确认仍使用本机 `Microsoft Xiaoxiao`。

Edge 内部页面、扩展商店以及禁止内容脚本注入的页面不属于通过范围。真实 Edge 原生右键菜单只有完成上述人工点选后才能标记为通过；内部消息自动化结果不能替代此项验收。

## 卸载与回退

1. 在 `edge://extensions/` 中移除“ListenFlow 网页听读”。
2. 从开始菜单运行“卸载 ListenFlow Edge 人工验收版”，或双击原安装包中的“卸载 ListenFlow.cmd”。
3. 卸载脚本只删除人工验收版程序目录和它创建的原生宿主清单；本地阅读进度和历史默认保留。
4. 若安装前同名原生消息宿主已有注册，卸载时会恢复原注册，前提是原宿主清单仍存在。
5. 原有 0.2.0 程序文件未被覆盖，可从原位置重新启动。

自包含 EXE 包含所需的 .NET 运行时，不包含 Windows 语音包。目标电脑仍须安装并启用可由 SAPI 使用的 `Microsoft Xiaoxiao`。程序没有在线语音回退路径，运行时网页处理、通信和语音播放均在本机完成。
