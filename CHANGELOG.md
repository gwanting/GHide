# Changelog

## v1.3.2

- 修复：只分发 `GHide.exe` 单个文件时（例如从 GitHub Release 仅下载 exe），Win11 任务栏透明失效的问题。
  `taskbar_transparency.dll` 现在**内嵌进 exe**（单文件分发）：运行时若 exe 同目录没有 DLL，
  自动从内嵌资源释放到 `%LOCALAPPDATA%\GHide\` 后注入；同目录放置 DLL 仍优先使用，可自行更新覆盖。
- 界面改进：任务栏透明应用失败时（DLL 缺失/注入被拦截/系统不支持），托盘气泡会提示具体原因，
  不再静默失败，便于在他人电脑上排查。
- `build.ps1` 在编译时自动把 native DLL 嵌入 exe（`/resource:`），MSYS2 缺失但已有现成 DLL 时也会嵌入。
- 版本号更新为 1.3.2.0。

## v1.3.1

- 版权署名统一为 **Copyright © 2026 Wanting. 保留所有权利**。
- 关于对话框新增版权署名行；LICENSE、README、使用说明同步更新。
- 版本号更新为 1.3.1.0。
- 工程化：接入 GitHub Actions CI——push 自动构建（MSYS2 编译 native DLL + 主程序）与冒烟检查、上传产物；打 v* tag 自动发布 Release。`build.ps1` 支持 `-MsysBash` 参数，`.gitattributes` 强制脚本 LF。

## v1.3.0

- 正式更名为 **GHide**：程序名、exe 文件名、托盘与“关于”文案、日志路径、开机启动项均同步更新。
- GitHub 仓库更名为 GHide（旧链接自动跳转）。
- 任务栏透明注入模块 `taskbar_transparency.dll` 与共享内存协议保持不变，与已发布二进制兼容。
- 版本号更新为 1.3.0.0。

## v1.2.1

- 修复：程序隐藏桌面图标后退出（托盘菜单“退出”），图标不再残留隐藏状态，退出时自动恢复显示。
- 仅恢复由本程序隐藏的图标；若用户通过其他方式手动隐藏，退出时不会打扰。

## v1.2

- 新增任务栏全透明：程序运行时将 Windows 任务栏设为全透明，托盘菜单可开关，退出时自动恢复原样。
- Win11（XAML 任务栏）通过注入 taskbar_transparency.dll 到 explorer.exe，使用 XAML Diagnostics
  把任务栏背景设为全透明；Win10 经典任务栏回退到 SetWindowCompositionAttribute。
- 健壮性：程序被强杀/崩溃时，注入 DLL 检测到主进程退出会自动还原任务栏；Explorer 重启后自动重新应用。
- 新增系统测试：任务栏透明应用/还原验证。

## v1.1

- 将桌面识别和 Shell 查询移出低级鼠标钩子，避免钩子超时后失效。
- 使用桌面列表自身的无障碍命中测试，区分图标、图标文字与真正空白区域。
- 修复高 DPI 或多显示器缩放下只有部分桌面区域可触发的问题。
- 使用 Shell 文件夹视图接口切换图标，并在切换后回读验证实际状态。
- 移除会闪屏且无法确认结果的 Explorer 命令后备路径。
- 加入滚动诊断日志和托盘“打开诊断日志”入口。
- 新增可重复运行的系统测试脚本。

## v1.0

- 首次发布。
