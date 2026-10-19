# ListenFlow Edge 扩展

固定扩展标识：`chdkljcgemgeljdnfdnblbelfhmkkfbp`
Native Messaging Host：`com.listenflow.native_host`

扩展只在 `http` 和 `https` 页面运行。正文提取、DOM 映射、点击定位和高亮均在浏览器本地执行；文字通过 Native Messaging 发送给本机 ListenFlow，不上传到网络。

## 本地加载

1. Release 构建 ListenFlow、`ListenFlow.NativeHost`。
2. 以管理员权限无关的当前用户脚本注册宿主：

   ```powershell
   .\register-native-host.ps1 -HostExecutable <ListenFlow.NativeHost.exe 的完整路径>
   ```

3. 在 Edge 扩展管理页启用开发人员模式，加载本目录作为解压缩扩展。
4. 打开普通网页，右键选择“使用 ListenFlow 朗读所选文字”或“使用 ListenFlow 朗读网页正文”。

仓库 `bin\Release` 下的开发构建依赖 .NET 10 运行时。若系统未全局安装该运行时，测试 Edge 进程必须继承正确的 `DOTNET_ROOT`；正式交付时应把主程序和 Native Messaging Host 一并发布为适合 Windows x64 的自包含产物。自包含发布仍不包含 Windows 语音包。

删除测试注册：

```powershell
.\unregister-native-host.ps1
```

## 当前交互

- 右键朗读所选文字或正文。
- 右键暂停、继续、停止。
- 启动朗读后，左键点击非交互式正文进行定位。
- 链接、按钮、输入框、可编辑区域和空白位置不拦截左键。
- 优先使用 CSS Custom Highlight API 在网页原文上高亮；不支持时用不修改正文节点的覆盖层回退。
- 页面正文节点发生变化时停止当前会话，避免错位高亮。

## 验证

静态验证：

```powershell
.\test-edge-extension.ps1
```

先启动本地测试页、加载扩展并注册 Native Messaging Host，再向隔离 Edge 开启本机调试端口，可运行完整闭环：

```powershell
.\test-edge-extension.ps1 `
  -CdpEndpoint http://127.0.0.1:9223 `
  -PageUrl http://127.0.0.1:8765/ `
  -ScreenshotDirectory .\test-artifacts\edge-screenshots
```

闭环测试会使用真实 Native Messaging Host、命名管道、ListenFlow 主程序和 SAPI 进度，验证所选文字与正文朗读、网页原文视觉高亮、左键定位、暂停定位、继续、连续定位、交互控件放行和停止清理。浏览器原生右键菜单项仍需人工点选一次，因为调试协议不能操作浏览器原生菜单。

CSS Custom Highlight API 可用且样式可见时优先使用；若扩展隔离执行环境只保留高亮范围但未绘制样式，则自动使用基于同一组 DOM Range 矩形的覆盖层。覆盖层不复制正文、不改写文字节点，也不接收鼠标事件。

Mozilla Readability `0.6.0` 已固定并放在 `vendor` 目录，许可证见 `READABILITY-LICENSE.md`。运行时不从网络加载脚本。
