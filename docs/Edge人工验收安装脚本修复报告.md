# Edge 人工验收安装脚本修复报告

## 问题证据

问题包基于提交 `362dcad6b8e555159c532c73831d5d77137c6c93`。其中“安装 ListenFlow.cmd”为 281 字节、UTF-8 无字节顺序标记、9 个 `LF`、0 个 `CRLF`，SHA256 为 `5378C5954B096EB57602F70CA8B6D266834FC6E64F93CE8BB32787E780BC489F`。

在真实 Windows `cmd.exe` 中执行含中文的 UTF-8 纯 `LF` 最小批处理，可稳定复现中文行尾之后的命令截断和残片执行；相同内容改为 `CRLF` 后退出码为 0。报告中的 `enFlow`、`rorlevel` 和 `resultresult` 正是批处理解析位置损坏后的字符串残片。此前测试直接运行 `Install-ListenFlow.ps1`，绕过了 `.cmd` 入口，因此没有发现该问题。

首次检查用户默认验收路径时，以下项目均不存在：

- `%LOCALAPPDATA%\Programs\ListenFlow-EdgeTest`
- `%LOCALAPPDATA%\ListenFlow-EdgeTest\NativeMessaging\com.listenflow.native_host.json`
- `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.listenflow.native_host`
- 来自验收目录的 `ListenFlow` 或 `ListenFlow.NativeHost` 进程

因此用户报告的这次失败没有产生部分安装结果，无需清理原有 0.2.0。

## 修复

- 三个 `.cmd` 入口改为纯 ASCII，避免依赖活动代码页；所有行使用 `CRLF`。
- 增加 `.gitattributes`，固定 `.cmd` 检出换行为 `CRLF`。
- 打包脚本再次强制 ASCII 和 `CRLF`，即使源码工作树换行被其他工具改写，验收包仍保持正确格式。
- 自动验收从直接调用 PowerShell 脚本改为真实 `cmd.exe → Windows PowerShell 5.1 → 安装脚本` 链路，并在执行前检查无非 ASCII 字节和无裸 `LF`/`CR`。
- 安装校验改用 .NET SHA256，不再依赖 PowerShell 模块自动加载。
- Edge 路径候选始终保持数组，单一安装路径不会被误当成字符串再取首字符。
- 重复安装只会结束目标 `ListenFlow-EdgeTest` 目录中的进程；先等待窗口并正常关闭，仍未退出时才结束该验收进程。其他路径中的原有 ListenFlow 不会被强制结束。
- 自动打开 Edge 或资源管理器属于安装后的辅助动作，失败时给出警告和手动路径，不再把已经完成的安装误报为失败。

## 实际验证范围

- Windows CMD 入口首次安装。
- 相同目录重复安装。
- Windows PowerShell 5.1 脚本解析和执行。
- 安装文件、Native Messaging Host 清单、当前用户注册和开始菜单快捷方式。
- 主程序自动启动、宿主复用、真实 SAPI 进度、暂停定位、继续、停止和断线重连。
- CMD 入口卸载、验收进程结束、文件与快捷方式删除、原宿主注册恢复。
- 默认真实路径安装、主程序仍运行时重复安装、卸载后零验收版残留。

本次未修改播放核心、消息协议、Edge 网页逻辑或原有 0.2.0 数据。
