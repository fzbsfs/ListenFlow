# Edge 人工验收安装包阶段报告

## 范围

本阶段以分支 `feature/native-communication-infrastructure`、基线提交 `51467174ba6bb3bb16218a3268e035aa8a50e544` 为基础，只完善本地安装、卸载、构建与验收流程。未开发 Word、PDF 或其他新功能，未修改播放核心、通信协议或 Edge 网页逻辑。

## 安装设计

- 使用当前用户目录 `%LOCALAPPDATA%\Programs\ListenFlow-EdgeTest`，不需要管理员权限，不覆盖原有 0.2.0 程序文件。
- 使用当前用户 Edge Native Messaging Host 注册项，不修改系统默认浏览器。
- 安装时根据扩展清单公钥重新计算固定扩展标识，并要求结果为 `chdkljcgemgeljdnfdnblbelfhmkkfbp`。
- 宿主名称固定为 `com.listenflow.native_host`，清单只允许上述固定扩展来源。
- 主程序和宿主放在同一目录，宿主按现有查找规则启动 `ListenFlow.exe`。
- 桌面端继续使用 `ListenFlow.Desktop.Protocol.v1` 命名管道和唯一桌面程序互斥量。
- 安装负载带 SHA256 文件清单，复制前先校验。
- 卸载默认保留 `%LOCALAPPDATA%\ListenFlow` 阅读数据；若安装前存在同名宿主注册，则在原清单仍存在时恢复它。

## 实际验证

- `.NET` Release 回归：41 项通过、0 项失败、0 项跳过，其中包含 3 项本机 `Microsoft Xiaoxiao` 真实 SAPI 集成测试。
- Edge 扩展逻辑：4 项通过，覆盖 UTF-16 边界、中文句子范围和 DOM 范围映射；扩展固定标识与本地依赖检查通过。
- Windows PowerShell 5.1：安装和卸载脚本语法解析通过。首轮隔离安装发现无字节顺序标记的 UTF-8 中文脚本会被 5.1 错误解码，现已改为带字节顺序标记的 UTF-8 并重新验证。
- 隔离安装：程序、宿主、扩展、宿主清单和当前用户注册路径均存在且匹配。
- 真实进程通信：宿主自动启动主程序、握手、真实 SAPI 进度、暂停定位、继续、停止、复用现有进程、断线通知和下一命令重连全部通过。
- 隔离卸载：人工验收目录被删除，原 Native Messaging Host 测试注册被恢复。

## 仍待人工验证

以下项目未标记为通过，必须由用户在真实 Edge 中完成：原生右键菜单实际点选、网页原文可见高亮、左键连续定位的声音停止效果、暂停期间定位、网页交互元素不被拦截，以及断网后的完整 Edge 闭环。

自包含构建不包含 Windows 语音包。完全离线能力依赖目标电脑已有可由 SAPI 使用的 `Microsoft Xiaoxiao`；本阶段没有加入在线回退或任何远程正文处理。
