using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("GHide")]
[assembly: System.Reflection.AssemblyDescription("Double-click desktop blank space to toggle desktop icons")]
[assembly: System.Reflection.AssemblyProduct("GHide")]
[assembly: System.Reflection.AssemblyCopyright("Copyright © 2026 Wanting. 保留所有权利")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.3.6")]
[assembly: System.Reflection.AssemblyVersion("1.3.6.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.6.0")]

internal static class Program
{
    private const string AppName = "GHide";
    private const string AppTitle = "GHide";

    [STAThread]
    private static void Main()
    {
        // 鼠标钩子坐标和桌面窗口坐标必须处于同一 DPI 坐标空间。
        // 必须在创建任何 WinForms/Win32 窗口之前调用。
        NativeMethods.EnablePerMonitorDpiAwareness();

        bool createdNew;
        using (Mutex mutex = new Mutex(true, @"Local\GHide.9D323887", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show("桌面图标开关已经在运行。", AppTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                DiagnosticLog.Write("UI thread exception", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                DiagnosticLog.Write("Unhandled exception", e.ExceptionObject as Exception);
            };
            DiagnosticLog.Write("Application started. OS=" + Environment.OSVersion +
                ", Version=" + typeof(Program).Assembly.GetName().Version);
            // 开机自启项若仍指向旧版本/已删除路径（升级换目录后常见），启动时自动刷新为当前 exe。
            StartupManager.RefreshStartupPath();
            using (TrayApplicationContext context = new TrayApplicationContext())
            {
                Application.Run(context);
            }
            DiagnosticLog.Write("Application exited normally (message loop returned).");
        }
    }

    internal static string ExecutablePath
    {
        get { return Process.GetCurrentProcess().MainModule.FileName; }
    }

    internal static string RunValueName
    {
        get { return AppName; }
    }
}

internal static class DiagnosticLog
{
    private static readonly object SyncRoot = new object();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GHide");
    internal static readonly string LogPath = Path.Combine(LogDirectory, "GHide.log");

    internal static void Write(string message)
    {
        Write(message, null);
    }

    internal static void Write(string message, Exception exception)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                {
                    string previous = LogPath + ".old";
                    if (File.Exists(previous))
                        File.Delete(previous);
                    File.Move(LogPath, previous);
                }

                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + message;
                if (exception != null)
                    line += " | " + exception.GetType().Name + ": " + exception.Message;
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never prevent the utility from working.
        }
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem toggleItem;
    private readonly ToolStripMenuItem taskbarItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly DesktopMouseWatcher watcher;
    private readonly System.Windows.Forms.Timer startupTransparencyTimer;

    internal TrayApplicationContext()
    {
        toggleItem = new ToolStripMenuItem("隐藏桌面图标", null, OnToggleClicked);
        taskbarItem = new ToolStripMenuItem("任务栏透明", null, OnTaskbarTransparencyClicked);
        taskbarItem.Checked = true;
        startupItem = new ToolStripMenuItem("开机自动启动", null, OnStartupClicked);
        startupItem.Checked = StartupManager.IsEnabled();

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add(toggleItem);
        menu.Items.Add(taskbarItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripMenuItem("打开诊断日志", null, OnOpenLogClicked));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("关于", null, OnAboutClicked));
        menu.Items.Add(new ToolStripMenuItem("退出", null, OnExitClicked));

        trayIcon = new NotifyIcon();
        trayIcon.Icon = Icon.ExtractAssociatedIcon(Program.ExecutablePath);
        trayIcon.Text = "GHide：双击桌面空白处切换图标";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += OnTrayDoubleClick;

        watcher = new DesktopMouseWatcher();
        watcher.ToggleRequested += OnToggleRequested;
        watcher.Start();
        UpdateToggleText();

        // 开机登录时 Explorer 和 Win11 XAML 任务栏会分阶段初始化。
        // 只在任务栏真正就绪后加载透明模块，避免过早注入 Explorer
        // 导致本次登录期间任务栏保持不透明。
        startupTransparencyTimer = new System.Windows.Forms.Timer();
        startupTransparencyTimer.Interval = 2000;
        startupTransparencyTimer.Tick += OnStartupTransparencyTick;
        startupTransparencyTimer.Start();
        DiagnosticLog.Write("Taskbar transparency deferred until taskbar is ready.");

        trayIcon.ShowBalloonTip(2500, "GHide 已启动",
            "双击桌面空白处，可隐藏或显示全部桌面图标。",
            ToolTipIcon.Info);
    }

    private void OnToggleRequested(object sender, EventArgs e)
    {
        ToggleIcons();
    }

    private void OnToggleClicked(object sender, EventArgs e)
    {
        ToggleIcons();
    }

    private void OnTrayDoubleClick(object sender, EventArgs e)
    {
        ToggleIcons();
    }

    private void OnTaskbarTransparencyClicked(object sender, EventArgs e)
    {
        bool enable = !taskbarItem.Checked;
        if (enable)
        {
            startupTransparencyTimer.Stop();
            TaskbarTransparency.Apply();
            if (TaskbarTransparency.LastError != null)
            {
                trayIcon.ShowBalloonTip(5000, "任务栏透明不可用",
                    TaskbarTransparency.LastError, ToolTipIcon.Warning);
            }
        }
        else
        {
            startupTransparencyTimer.Stop();
            TaskbarTransparency.Restore();
        }
        taskbarItem.Checked = enable;
    }

    private void OnStartupTransparencyTick(object sender, EventArgs e)
    {
        if (!taskbarItem.Checked)
        {
            startupTransparencyTimer.Stop();
            return;
        }

        if (!TaskbarTransparency.IsReadyForApply())
            return;

        startupTransparencyTimer.Stop();
        DiagnosticLog.Write("Taskbar is ready; applying deferred transparency.");
        TaskbarTransparency.Apply();
        if (TaskbarTransparency.LastError != null)
        {
            trayIcon.ShowBalloonTip(5000, "任务栏透明不可用",
                TaskbarTransparency.LastError, ToolTipIcon.Warning);
        }
    }

    private void ToggleIcons()
    {
        if (!DesktopIcons.Toggle())
        {
            trayIcon.ShowBalloonTip(2000, "切换失败",
                "未能确认图标状态，已停止切换。可从托盘菜单打开诊断日志。", ToolTipIcon.Warning);
        }
        UpdateToggleText();
    }

    private void UpdateToggleText()
    {
        toggleItem.Text = DesktopIcons.AreVisible() ? "隐藏桌面图标" : "显示桌面图标";
    }

    private void OnStartupClicked(object sender, EventArgs e)
    {
        bool enable = !startupItem.Checked;
        try
        {
            StartupManager.SetEnabled(enable);
            startupItem.Checked = enable;
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法修改开机启动设置。\n\n" + ex.Message,
                "桌面图标开关", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnExitClicked(object sender, EventArgs e)
    {
        ExitThread();
    }

    private void OnOpenLogClicked(object sender, EventArgs e)
    {
        DiagnosticLog.Write("Diagnostic log opened by user.");
        Process.Start("explorer.exe", "/select,\"" + DiagnosticLog.LogPath + "\"");
    }

    private void OnAboutClicked(object sender, EventArgs e)
    {
        using (AboutForm dialog = new AboutForm())
        {
            dialog.ShowDialog();
        }
    }

    protected override void ExitThreadCore()
    {
        DiagnosticLog.Write("ExitThreadCore entered.");
        startupTransparencyTimer.Stop();
        startupTransparencyTimer.Dispose();
        DesktopIcons.Restore();
        TaskbarTransparency.Shutdown();
        watcher.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.ExitThreadCore();
    }
}

// 程序内"关于"对话框：显示版本、开发者与项目主页。
internal sealed class AboutForm : Form
{
    private const string GithubUrl = "https://github.com/gwanting/GHide";

    internal AboutForm()
    {
        Text = "关于 GHide";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 200);
        Font = SystemFonts.MessageBoxFont;

        Label appName = new Label();
        appName.Text = "GHide";
        appName.Font = new Font(Font, FontStyle.Bold);
        appName.SetBounds(16, 14, 348, 22);
        Controls.Add(appName);

        string version = typeof(Program).Assembly.GetName().Version.ToString(3);
        Label meta = new Label();
        meta.Text = "版本：v" + version;
        meta.SetBounds(16, 42, 348, 20);
        Controls.Add(meta);

        Label copyright = new Label();
        copyright.Text = "Copyright © 2026 Wanting. 保留所有权利";
        copyright.SetBounds(16, 66, 348, 20);
        Controls.Add(copyright);

        Label desc = new Label();
        desc.Text = "双击桌面空白处隐藏/显示桌面图标；运行时可开启任务栏全透明。";
        desc.SetBounds(16, 92, 348, 34);
        Controls.Add(desc);

        LinkLabel link = new LinkLabel();
        link.Text = GithubUrl;
        link.LinkClicked += delegate { Process.Start(GithubUrl); };
        link.SetBounds(16, 126, 348, 20);
        Controls.Add(link);

        Button ok = new Button();
        ok.Text = "确定";
        ok.DialogResult = DialogResult.OK;
        ok.SetBounds(150, 162, 80, 28);
        AcceptButton = ok;
        Controls.Add(ok);
    }
}

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static bool IsEnabled()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
        {
            return key != null && key.GetValue(Program.RunValueName) != null;
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (key == null)
                throw new InvalidOperationException("无法打开当前用户的启动项注册表。 ");

            if (enabled)
                key.SetValue(Program.RunValueName, "\"" + Program.ExecutablePath + "\"");
            else
                key.DeleteValue(Program.RunValueName, false);
        }
    }

    // 程序启动时调用：若开机自启项已存在，但其指向的路径与当前 exe 不一致
    // （升级换目录、旧版本目录被删除等导致开机找不到程序），自动刷新为当前 exe 路径。
    // 未勾选开机自启时不做任何事，不会擅自添加启动项。
    internal static void RefreshStartupPath()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                if (key == null || key.GetValue(Program.RunValueName) == null)
                    return;
            }

            string currentPath = Program.ExecutablePath;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                    return;
                string stored = key.GetValue(Program.RunValueName) as string;
                if (stored == null)
                    return;

                string storedPath = ParseStartupPath(stored);
                if (storedPath == null ||
                    string.Equals(storedPath, currentPath, StringComparison.OrdinalIgnoreCase))
                    return;

                key.SetValue(Program.RunValueName, "\"" + currentPath + "\"");
                DiagnosticLog.Write("Startup path refreshed: '" + storedPath + "' -> '" + currentPath + "'");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Startup path refresh failed", ex);
        }
    }

    // 解析 Run 键值中的 exe 路径：支持 "C:\path\app.exe" 及带参数的 "C:\path\app.exe" -arg 两种格式。
    private static string ParseStartupPath(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"')
        {
            int end = trimmed.IndexOf('"', 1);
            return end < 0 ? null : trimmed.Substring(1, end - 1);
        }
        int space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed.Substring(0, space);
    }
}

// 任务栏全透明。优先使用 Win11 的 DWM 系统背景类型(DWMSBT_TRANSIENTWINDOW),
// 任务栏全透明。
//   - Win11 XAML 任务栏(Shell_TrayWnd 含 DesktopWindowContentBridge):把
//     taskbar_transparency.dll 注入 explorer.exe,通过 XAML Diagnostics 视觉树
//     把任务栏 BackgroundFill/BackgroundStroke 的 Fill 设为全透明;与 DLL 用
//     命名共享内存通信(state=1 透明,state=0 还原)。
//   - Win10 经典任务栏:回退到 SetWindowCompositionAttribute 透明渐变。
// Explorer 重启重建任务栏后,通过 TaskbarCreated 消息自动重新应用。
internal static class TaskbarTransparency
{
    private const string TaskbarWindowClass = "Shell_TrayWnd";
    private const string XamlBridgeClass = "Windows.UI.Composition.DesktopWindowContentBridge";
    // 与注入 DLL (taskbar_transparency.dll) 约定的共享内存名，不得更改：
    // DLL 需在 MSYS2 环境重编译才能同步改名，改名会破坏任务栏透明通信。
    private const string StateName = @"Local\DesktopIconToggleTaskbarState";
    private const uint StateMagic = 0x44544954; // 'DITT'
    private const int OffMagic = 0;
    private const int OffState = 4;
    private const int OffInjected = 8;
    private const int OffOwnerPid = 12;
    private const int StateSize = 16;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
    private const int WS_EX_TOOLWINDOW = 0x80;

    // 注入用进程权限:CREATE_THREAD|QUERY_INFORMATION|VM_OPERATION|VM_WRITE|VM_READ
    private const uint ProcessAllAccess = 0x043A;
    private const uint MemCommitReserve = 0x3000;
    private const uint PageReadWrite = 0x04;
    private const uint MemRelease = 0x8000;

    private static IntPtr mapHandle;
    private static IntPtr mapView;
    private static bool applied;
    private static bool usingInjection;
    private static TaskbarCreatedListener listener;
    private static System.Windows.Forms.Timer reapplyTimer;
    private static int reapplyAttempts;

    private const int ReapplyIntervalMilliseconds = 2000;
    private const int MaxReapplyAttempts = 30;

    // 最近一次 Apply() 的失败原因(成功时为 null),供托盘气泡提示用户。
    internal static string LastError { get; private set; }

    internal static bool IsApplied
    {
        get { return applied; }
    }

    internal static bool IsReadyForApply()
    {
        if (NativeMethods.FindWindow(TaskbarWindowClass, null) == IntPtr.Zero)
            return false;

        // Win11 的 Shell_TrayWnd 会早于 XAML bridge 出现；只看到外层窗口
        // 不代表透明模块已可以安全加载。
        return !IsWindows11() || IsXamlTaskbar();
    }

    internal static void Apply()
    {
        if (applied)
            return;
        LastError = null;

        if (!EnsureSharedState())
        {
            LastError = "共享内存初始化失败，无法控制任务栏。";
            DiagnosticLog.Write("Taskbar transparency: shared state unavailable.");
            return;
        }

        bool xaml = IsXamlTaskbar();
        // Win11 上 explorer 刚启动时任务栏可能尚未就绪(bridge 窗口未创建),
        // 等待并重试几次,避免误判为 Win10 经典任务栏。
        if (!xaml && IsWindows11())
        {
            for (int i = 0; i < 12 && !xaml; i++)
            {
                Thread.Sleep(500);
                xaml = IsXamlTaskbar();
            }
        }
        DiagnosticLog.Write("Taskbar transparency: xamlTaskbar=" + xaml);
        if (xaml)
        {
            usingInjection = true;
            if (!IsDllLoadedInExplorer())
            {
                DiagnosticLog.Write("Taskbar transparency: injecting into explorer...");
                if (!InjectIntoExplorer())
                {
                    LastError = "DLL 注入 explorer.exe 失败（可能被安全软件拦截）。";
                    DiagnosticLog.Write("Taskbar transparency: DLL injection into explorer failed.");
                    return;
                }
                // 等待 DLL 完成 XAML Diagnostics 初始化(最多 6 秒)。
                for (int i = 0; i < 60 && ReadField(OffInjected) == 0; i++)
                    Thread.Sleep(100);
                if (ReadField(OffInjected) == 0)
                    DiagnosticLog.Write("Taskbar transparency: DLL did not report ready.");
            }
            WriteField(OffState, 1);
            applied = true;
            EnsureListener();
            DiagnosticLog.Write("Taskbar transparency applied (XAML injection).");
        }
        else
        {
            usingInjection = false;
            if (!TryApplyAccent())
            {
                LastError = "任务栏透明应用失败（当前系统任务栏不支持）。";
                DiagnosticLog.Write("Taskbar transparency: classic taskbar accent apply failed.");
                return;
            }
            applied = true;
            EnsureListener();
            DiagnosticLog.Write("Taskbar transparency applied (classic accent).");
        }
    }

    internal static void Restore()
    {
        if (!applied)
            return;

        if (usingInjection)
        {
            WriteField(OffState, 0);
            // DLL 控制线程轮询间隔 250ms,稍等它应用还原再退出。
            Thread.Sleep(400);
            DiagnosticLog.Write("Taskbar transparency restored (XAML).");
        }
        else
        {
            TryRestoreAccent();
            DiagnosticLog.Write("Taskbar transparency restored (classic).");
        }
        applied = false;
    }

    internal static void Shutdown()
    {
        Restore();

        if (reapplyTimer != null)
        {
            reapplyTimer.Stop();
            reapplyTimer.Dispose();
            reapplyTimer = null;
        }
        if (listener != null)
        {
            listener.Close();
            listener = null;
        }
        if (mapView != IntPtr.Zero)
        {
            NativeMethods.UnmapViewOfFile(mapView);
            mapView = IntPtr.Zero;
        }
        if (mapHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(mapHandle);
            mapHandle = IntPtr.Zero;
        }
    }

    private static void OnTaskbarRecreated()
    {
        if (!applied)
            return;

        // TaskbarCreated 意味着 Explorer 已重建；旧的注入 DLL 已随旧 Explorer
        // 退出。必须清除“已应用”的缓存，重置 DLL 就绪标记后再注入新进程。
        DiagnosticLog.Write("TaskbarCreated received; scheduling transparency reapply.");
        applied = false;
        usingInjection = false;
        WriteField(OffInjected, 0);
        ScheduleReapply();
    }

    private static void ScheduleReapply()
    {
        if (reapplyTimer == null)
        {
            reapplyTimer = new System.Windows.Forms.Timer();
            reapplyTimer.Interval = ReapplyIntervalMilliseconds;
            reapplyTimer.Tick += OnReapplyTimerTick;
        }
        reapplyAttempts = 0;
        reapplyTimer.Stop();
        reapplyTimer.Start();
    }

    private static void OnReapplyTimerTick(object sender, EventArgs e)
    {
        reapplyAttempts++;
        if (IsReadyForApply())
        {
            DiagnosticLog.Write("Taskbar recreation: taskbar ready, reapplying transparency.");
            Apply();
            if (applied)
            {
                reapplyTimer.Stop();
                return;
            }
        }

        if (reapplyAttempts >= MaxReapplyAttempts)
        {
            reapplyTimer.Stop();
            DiagnosticLog.Write("Taskbar recreation: transparency reapply timed out. " +
                (LastError ?? "Taskbar did not become ready."));
        }
    }

    private static void EnsureListener()
    {
        try
        {
            if (listener == null)
                listener = new TaskbarCreatedListener(OnTaskbarRecreated);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("TaskbarCreated listener failed", ex);
        }
    }

    // ---- 共享内存 ----
    private static bool EnsureSharedState()
    {
        if (mapView != IntPtr.Zero)
            return true;
        try
        {
            mapHandle = NativeMethods.CreateFileMapping(
                new IntPtr(-1), IntPtr.Zero, PageReadWrite, 0, StateSize, StateName);
            if (mapHandle == IntPtr.Zero)
                return false;
            mapView = NativeMethods.MapViewOfFile(mapHandle, 0x0002, 0, 0, UIntPtr.Zero); // FILE_MAP_WRITE
            if (mapView == IntPtr.Zero)
                return false;
            if (ReadField(OffMagic) != StateMagic)
            {
                WriteField(OffMagic, StateMagic);
                WriteField(OffState, 0);
                WriteField(OffInjected, 0);
            }
            // 记录主进程 PID,供注入 DLL 监控"主程序退出则还原任务栏"。
            WriteField(OffOwnerPid, (uint)Process.GetCurrentProcess().Id);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Taskbar transparency shared state failed", ex);
            return false;
        }
    }

    private static uint ReadField(int offset)
    {
        if (mapView == IntPtr.Zero)
            return 0;
        return unchecked((uint)Marshal.ReadInt32(mapView, offset));
    }

    private static void WriteField(int offset, uint value)
    {
        if (mapView != IntPtr.Zero)
            Marshal.WriteInt32(mapView, offset, unchecked((int)value));
    }

    // ---- 任务栏类型检测 ----
    private static bool IsWindows11()
    {
        try
        {
            NativeMethods.OSVERSIONINFOEX info = new NativeMethods.OSVERSIONINFOEX();
            info.dwOSVersionInfoSize = Marshal.SizeOf(typeof(NativeMethods.OSVERSIONINFOEX));
            if (NativeMethods.RtlGetVersion(ref info) == 0)
                return info.dwBuildNumber >= 22000;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Taskbar transparency: version check failed", ex);
        }
        return false;
    }

    private static bool IsXamlTaskbar()
    {
        IntPtr tray = NativeMethods.FindWindow(TaskbarWindowClass, null);
        if (tray == IntPtr.Zero)
            return false;
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumChildWindows(tray, delegate(IntPtr window, IntPtr parameter)
        {
            if (NativeMethods.GetClassNameString(window) == XamlBridgeClass)
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found != IntPtr.Zero;
    }

    // ---- 注入 taskbar_transparency.dll 到 explorer.exe ----
    private static bool IsDllLoadedInExplorer()
    {
        try
        {
            Process[] explorers = Process.GetProcessesByName("explorer");
            if (explorers.Length == 0)
                return false;
            foreach (ProcessModule module in explorers[0].Modules)
            {
                if (string.Equals(module.ModuleName, "taskbar_transparency.dll",
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Taskbar transparency: explorer module check failed", ex);
        }
        return false;
    }

    private static bool InjectIntoExplorer()
    {
        Process[] explorers = Process.GetProcessesByName("explorer");
        if (explorers.Length == 0)
        {
            DiagnosticLog.Write("Taskbar transparency: explorer process not found.");
            return false;
        }

        string dllPath = ResolveDllPath();
        if (dllPath == null)
        {
            DiagnosticLog.Write("Taskbar transparency: taskbar_transparency.dll unavailable " +
                "(not next to exe, not embedded in exe).");
            return false;
        }

        IntPtr process = NativeMethods.OpenProcess(ProcessAllAccess, false, (uint)explorers[0].Id);
        if (process == IntPtr.Zero)
        {
            DiagnosticLog.Write("Taskbar transparency: OpenProcess failed, error=" +
                Marshal.GetLastWin32Error());
            return false;
        }
        try
        {
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
            IntPtr remote = NativeMethods.VirtualAllocEx(
                process, IntPtr.Zero, new UIntPtr((uint)pathBytes.Length),
                MemCommitReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
                return false;
            try
            {
                UIntPtr written;
                if (!NativeMethods.WriteProcessMemory(process, remote, pathBytes,
                    new UIntPtr((uint)pathBytes.Length), out written))
                    return false;

                IntPtr kernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
                IntPtr loadLibrary = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
                uint threadId;
                IntPtr thread = NativeMethods.CreateRemoteThread(
                    process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out threadId);
                if (thread == IntPtr.Zero)
                    return false;
                NativeMethods.WaitForSingleObject(thread, 10000);
                NativeMethods.CloseHandle(thread);
                return true;
            }
            finally
            {
                NativeMethods.VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    // ---- DLL 解析:exe 同目录优先,否则从嵌入资源释放 ----
    private const string DllFileName = "taskbar_transparency.dll";
    // 与 build.ps1 的 /resource:...,GHide.taskbar_transparency.dll 一致。
    private const string EmbeddedDllResource = "GHide.taskbar_transparency.dll";

    // 返回用于注入的 DLL 完整路径:
    // 1) exe 同目录存在则优先使用(用户可放新版本覆盖内置);
    // 2) 否则从嵌入 exe 的资源释放到 %LOCALAPPDATA%\GHide\ 后使用;
    // 3) 都没有则返回 null。
    private static string ResolveDllPath()
    {
        string sideBySide = Path.Combine(
            Path.GetDirectoryName(Program.ExecutablePath), DllFileName);
        if (File.Exists(sideBySide))
        {
            DiagnosticLog.Write("Taskbar transparency: using side-by-side DLL " + sideBySide);
            return sideBySide;
        }

        try
        {
            using (Stream stream = typeof(TaskbarTransparency).Assembly
                .GetManifestResourceStream(EmbeddedDllResource))
            {
                if (stream == null)
                {
                    DiagnosticLog.Write("Taskbar transparency: embedded DLL resource missing.");
                    return null;
                }

                byte[] bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GHide");
                Directory.CreateDirectory(dir);
                string extracted = Path.Combine(dir, DllFileName);
                if (!File.Exists(extracted) ||
                    new FileInfo(extracted).Length != bytes.Length)
                {
                    try
                    {
                        File.WriteAllBytes(extracted, bytes);
                        DiagnosticLog.Write("Taskbar transparency: extracted embedded DLL to " + extracted);
                    }
                    catch (Exception ex)
                    {
                        // 旧版 DLL 可能仍被 explorer 占用,覆盖失败时沿用现有文件。
                        DiagnosticLog.Write("Taskbar transparency: DLL extract failed, reusing existing", ex);
                    }
                }
                return File.Exists(extracted) ? extracted : null;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Taskbar transparency: DLL resolve failed", ex);
            return null;
        }
    }

    // ---- Win10 经典任务栏回退:SetWindowCompositionAttribute ----
    private static bool TryApplyAccent()
    {
        IntPtr hwnd = NativeMethods.FindWindow(TaskbarWindowClass, null);
        if (hwnd == IntPtr.Zero)
            return false;
        // GradientColor 高位为 alpha,0x00 表示全透明(不带模糊)。
        return SetAccent(hwnd, ACCENT_ENABLE_TRANSPARENTGRADIENT, 0x00000000);
    }

    private static void TryRestoreAccent()
    {
        IntPtr hwnd = NativeMethods.FindWindow(TaskbarWindowClass, null);
        if (hwnd != IntPtr.Zero)
            SetAccent(hwnd, ACCENT_DISABLED, 0);
    }

    private static bool SetAccent(IntPtr hwnd, int accentState, int gradientColor)
    {
        try
        {
            NativeMethods.AccentPolicy accent = new NativeMethods.AccentPolicy();
            accent.AccentState = accentState;
            accent.GradientColor = gradientColor;
            int size = Marshal.SizeOf(typeof(NativeMethods.AccentPolicy));
            IntPtr data = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, data, false);
                NativeMethods.WindowCompositionAttributeData wcad =
                    new NativeMethods.WindowCompositionAttributeData();
                wcad.Attribute = WCA_ACCENT_POLICY;
                wcad.Data = data;
                wcad.SizeOfData = size;
                return NativeMethods.SetWindowCompositionAttribute(hwnd, ref wcad);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("SetWindowCompositionAttribute failed", ex);
            return false;
        }
    }

    // 隐藏的消息窗口,用于接收 Explorer 重建任务栏时广播的 TaskbarCreated 消息。
    private sealed class TaskbarCreatedListener : NativeWindow
    {
        private readonly Action callback;
        private readonly uint taskbarCreatedMessage;

        internal TaskbarCreatedListener(Action callback)
        {
            this.callback = callback;
            taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            CreateParams parameters = new CreateParams();
            parameters.Caption = "GHideTaskbarListener";
            parameters.Style = 0;
            parameters.ExStyle = WS_EX_TOOLWINDOW;
            CreateHandle(parameters);
        }

        internal void Close()
        {
            DestroyHandle();
        }

        protected override void WndProc(ref Message msg)
        {
            if (msg.Msg == taskbarCreatedMessage)
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("TaskbarCreated callback failed", ex);
                }
            }
            base.WndProc(ref msg);
        }
    }
}

internal sealed class DesktopMouseWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private NativeMethods.LowLevelMouseProc callback;
    private readonly Control dispatcher;
    private IntPtr hookHandle;
    private uint lastClickTime;
    private NativeMethods.POINT lastClickPoint;
    private bool lastClickWasDesktopBlank;

    internal event EventHandler ToggleRequested;

    internal DesktopMouseWatcher()
    {
        dispatcher = new Control();
        dispatcher.CreateControl();
    }

    internal void Start()
    {
        callback = HookCallback;
        using (Process process = Process.GetCurrentProcess())
        using (ProcessModule module = process.MainModule)
        {
            IntPtr moduleHandle = NativeMethods.GetModuleHandle(module.ModuleName);
            hookHandle = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, callback, moduleHandle, 0);
        }

        if (hookHandle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
        {
            NativeMethods.MSLLHOOKSTRUCT data =
                (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
            NativeMethods.POINT point = data.pt;
            uint time = data.time;
            try
            {
                dispatcher.BeginInvoke(new Action<NativeMethods.POINT, uint>(HandleLeftButtonDown), point, time);
            }
            catch (InvalidOperationException)
            {
                // The application is shutting down.
            }
        }
        return NativeMethods.CallNextHookEx(hookHandle, code, wParam, lParam);
    }

    private void HandleLeftButtonDown(NativeMethods.POINT point, uint time)
    {
        bool blank = DesktopIcons.IsBlankDesktopPoint(point);
        uint elapsed = time - lastClickTime;
        Size doubleClickSize = SystemInformation.DoubleClickSize;
        bool closeEnough = Math.Abs(point.X - lastClickPoint.X) <= doubleClickSize.Width / 2 &&
                           Math.Abs(point.Y - lastClickPoint.Y) <= doubleClickSize.Height / 2;

        if (blank && lastClickWasDesktopBlank && elapsed <= NativeMethods.GetDoubleClickTime() && closeEnough)
        {
            lastClickTime = 0;
            lastClickWasDesktopBlank = false;
            EventHandler handler = ToggleRequested;
            DiagnosticLog.Write("Desktop blank double-click accepted at " + point.X + "," + point.Y);
            if (handler != null)
                handler(this, EventArgs.Empty);
            return;
        }

        lastClickTime = time;
        lastClickPoint = point;
        lastClickWasDesktopBlank = blank;
    }

    public void Dispose()
    {
        if (hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hookHandle);
            hookHandle = IntPtr.Zero;
        }
        callback = null;
        dispatcher.Dispose();
    }
}

internal static class DesktopIcons
{
    // 图标是否由本程序切换为隐藏。退出时据此恢复显示，
    // 避免打扰用户通过其他方式主动隐藏图标的状态。
    private static bool hiddenByUs;

    internal static bool Toggle()
    {
        bool visible;
        if (!ShellFolderView.TryGetIconsVisible(out visible))
        {
            DiagnosticLog.Write("Toggle rejected: unable to read Shell folder flags.");
            return false;
        }

        bool requestedVisible = !visible;
        if (!ShellFolderView.TrySetIconsVisible(requestedVisible))
        {
            DiagnosticLog.Write("Toggle rejected: Shell refused requested visibility=" + requestedVisible);
            return false;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(50);
            bool actualVisible;
            if (ShellFolderView.TryGetIconsVisible(out actualVisible) && actualVisible == requestedVisible)
            {
                DiagnosticLog.Write("Toggle verified. Visible=" + actualVisible + ", attempt=" + (attempt + 1));
                hiddenByUs = !requestedVisible;
                return true;
            }
        }

        DiagnosticLog.Write("Toggle failed verification. Requested visible=" + requestedVisible);
        return false;
    }

    // 程序退出时调用：若图标是本程序隐藏的且当前仍处于隐藏状态，则恢复显示。
    internal static void Restore()
    {
        if (!hiddenByUs)
            return;
        hiddenByUs = false;

        bool visible;
        if (!ShellFolderView.TryGetIconsVisible(out visible))
        {
            DiagnosticLog.Write("Restore skipped: unable to read Shell folder flags on exit.");
            return;
        }
        if (visible)
        {
            DiagnosticLog.Write("Restore skipped: icons already visible on exit.");
            return;
        }

        if (ShellFolderView.TrySetIconsVisible(true))
            DiagnosticLog.Write("Desktop icons restored on exit.");
        else
            DiagnosticLog.Write("Restore failed: Shell refused to show icons on exit.");
    }

    internal static bool AreVisible()
    {
        bool visible;
        if (ShellFolderView.TryGetIconsVisible(out visible))
            return visible;

        IntPtr listView = FindDesktopListView();
        return listView != IntPtr.Zero && NativeMethods.IsWindowVisible(listView);
    }

    internal static bool IsBlankDesktopPoint(NativeMethods.POINT screenPoint)
    {
        IntPtr listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
            return false;

        IntPtr hitWindow = NativeMethods.WindowFromPoint(screenPoint);
        if (NativeMethods.IsWindowVisible(listView))
        {
            if (hitWindow != listView && !NativeMethods.IsChild(listView, hitWindow))
                return false;

            bool isIcon;
            if (!NativeMethods.TryHitTestAccessibleChild(listView, screenPoint, out isIcon))
                return false;
            return !isIcon;
        }

        // 图标控件隐藏后，点击会落到它后面的 SHELLDLL_DefView、WorkerW 或 Progman。
        IntPtr desktopHost = NativeMethods.GetAncestor(listView, 2);
        IntPtr current = hitWindow;
        while (current != IntPtr.Zero)
        {
            if (current == desktopHost)
                return true;

            string className = NativeMethods.GetClassNameString(current);
            if (className == "SHELLDLL_DefView")
                return true;
            if ((className == "WorkerW" || className == "Progman") &&
                NativeMethods.FindWindowEx(current, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return true;

            current = NativeMethods.GetParent(current);
        }
        return false;
    }

    private static IntPtr FindDesktopListView()
    {
        IntPtr defView = FindDesktopDefView();
        if (defView == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr listView = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        if (listView == IntPtr.Zero)
            listView = NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        return listView;
    }

    private static IntPtr FindDesktopDefView()
    {
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        IntPtr defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
            return defView;

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            string hostClass = NativeMethods.GetClassNameString(window);
            if (hostClass != "WorkerW" && hostClass != "Progman")
                return true;

            IntPtr view = NativeMethods.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero)
            {
                found = view;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

// 通过 Windows 支持的 Shell 文件夹视图接口切换 FWF_NOICONS。
// 不再依赖 Explorer 内部的 SysListView32 显示状态。
internal static class ShellFolderView
{
    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint FWF_NOICONS = 0x00001000;
    private const uint FVO_CUSTOMPOSITION = 0x00000001;

    private static readonly Guid CLSID_ShellWindows =
        new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser =
        new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser =
        new Guid("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IID_IFolderView2 =
        new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
    private static readonly Guid IID_IFolderViewOptions =
        new Guid("3CC974D2-B302-4D36-AD3E-06D93F695D3F");

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryActiveShellViewDelegate(IntPtr browser, out IntPtr shellView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentFolderFlagsDelegate(IntPtr folderView, out uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetCurrentFolderFlagsDelegate(IntPtr folderView, uint mask, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFolderViewOptionsDelegate(IntPtr folderViewOptions, uint mask, uint options);

    internal static bool TryGetIconsVisible(out bool visible)
    {
        visible = true;
        IntPtr folderView = IntPtr.Zero;
        IntPtr folderViewOptions = IntPtr.Zero;
        try
        {
            folderView = GetDesktopFolderView2(out folderViewOptions);
            if (folderView == IntPtr.Zero)
                return false;

            IntPtr method = GetVTableMethod(folderView, 25);
            GetCurrentFolderFlagsDelegate getFlags =
                (GetCurrentFolderFlagsDelegate)Marshal.GetDelegateForFunctionPointer(
                    method, typeof(GetCurrentFolderFlagsDelegate));
            uint flags;
            int result = getFlags(folderView, out flags);
            if (result < 0)
                return false;

            visible = (flags & FWF_NOICONS) == 0;
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Unable to read desktop folder flags", ex);
            return false;
        }
        finally
        {
            if (folderViewOptions != IntPtr.Zero)
                Marshal.Release(folderViewOptions);
            if (folderView != IntPtr.Zero)
                Marshal.Release(folderView);
        }
    }

    internal static bool TrySetIconsVisible(bool visible)
    {
        IntPtr folderView = IntPtr.Zero;
        IntPtr folderViewOptions = IntPtr.Zero;
        try
        {
            folderView = GetDesktopFolderView2(out folderViewOptions);
            if (folderView == IntPtr.Zero)
                return false;

            if (folderViewOptions != IntPtr.Zero)
            {
                IntPtr optionsMethod = GetVTableMethod(folderViewOptions, 3);
                SetFolderViewOptionsDelegate setOptions =
                    (SetFolderViewOptionsDelegate)Marshal.GetDelegateForFunctionPointer(
                        optionsMethod, typeof(SetFolderViewOptionsDelegate));
                int optionsResult = setOptions(
                    folderViewOptions, FVO_CUSTOMPOSITION, FVO_CUSTOMPOSITION);
                if (optionsResult < 0)
                    DiagnosticLog.Write("Shell did not accept FVO_CUSTOMPOSITION. HRESULT=0x" +
                        optionsResult.ToString("X8"));
            }

            IntPtr method = GetVTableMethod(folderView, 24);
            SetCurrentFolderFlagsDelegate setFlags =
                (SetCurrentFolderFlagsDelegate)Marshal.GetDelegateForFunctionPointer(
                    method, typeof(SetCurrentFolderFlagsDelegate));
            uint flags = visible ? 0u : FWF_NOICONS;
            return setFlags(folderView, FWF_NOICONS, flags) >= 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Unable to set desktop folder flags", ex);
            return false;
        }
        finally
        {
            if (folderViewOptions != IntPtr.Zero)
                Marshal.Release(folderViewOptions);
            if (folderView != IntPtr.Zero)
                Marshal.Release(folderView);
        }
    }

    private static IntPtr GetDesktopFolderView2(out IntPtr folderViewOptionsResult)
    {
        folderViewOptionsResult = IntPtr.Zero;
        object shellWindows = null;
        object desktopDispatch = null;
        IntPtr browser = IntPtr.Zero;
        IntPtr shellView = IntPtr.Zero;
        IntPtr folderView = IntPtr.Zero;
        IntPtr folderViewOptions = IntPtr.Zero;
        try
        {
            Type type = Type.GetTypeFromCLSID(CLSID_ShellWindows, true);
            shellWindows = Activator.CreateInstance(type);

            object[] arguments = new object[]
            {
                CSIDL_DESKTOP, 0, SWC_DESKTOP, 0, SWFO_NEEDDISPATCH
            };
            desktopDispatch = type.InvokeMember("FindWindowSW",
                System.Reflection.BindingFlags.InvokeMethod, null,
                shellWindows, arguments);
            if (desktopDispatch == null)
                return IntPtr.Zero;

            IServiceProvider provider = desktopDispatch as IServiceProvider;
            if (provider == null)
                return IntPtr.Zero;

            Guid service = SID_STopLevelBrowser;
            Guid browserId = IID_IShellBrowser;
            if (provider.QueryService(ref service, ref browserId, out browser) < 0 ||
                browser == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr queryMethod = GetVTableMethod(browser, 15);
            QueryActiveShellViewDelegate queryView =
                (QueryActiveShellViewDelegate)Marshal.GetDelegateForFunctionPointer(
                    queryMethod, typeof(QueryActiveShellViewDelegate));
            if (queryView(browser, out shellView) < 0 || shellView == IntPtr.Zero)
                return IntPtr.Zero;

            Guid optionsId = IID_IFolderViewOptions;
            Marshal.QueryInterface(shellView, ref optionsId, out folderViewOptions);

            Guid folderViewId = IID_IFolderView2;
            if (Marshal.QueryInterface(shellView, ref folderViewId, out folderView) < 0)
                return IntPtr.Zero;

            IntPtr result = folderView;
            folderView = IntPtr.Zero;
            folderViewOptionsResult = folderViewOptions;
            folderViewOptions = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (folderViewOptions != IntPtr.Zero)
                Marshal.Release(folderViewOptions);
            if (folderView != IntPtr.Zero)
                Marshal.Release(folderView);
            if (shellView != IntPtr.Zero)
                Marshal.Release(shellView);
            if (browser != IntPtr.Zero)
                Marshal.Release(browser);
            if (desktopDispatch != null && Marshal.IsComObject(desktopDispatch))
                Marshal.FinalReleaseComObject(desktopDispatch);
            if (shellWindows != null && Marshal.IsComObject(shellWindows))
                Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    private static IntPtr GetVTableMethod(IntPtr instance, int index)
    {
        IntPtr table = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(table, index * IntPtr.Size);
    }
}

internal static class NativeMethods
{
    private const uint OBJID_CLIENT = 0xFFFFFFFC;
    private static readonly Guid IID_IAccessible =
        new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

    internal delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    internal static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                return;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 1703 之前没有 Per-Monitor V2，退回系统 DPI 感知。
        }

        try
        {
            SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            // 仅为极老系统保留；支持的 Windows 10/11 不会进入这里。
        }
    }

    internal static bool TryHitTestAccessibleChild(IntPtr listView, POINT point, out bool isListItem)
    {
        isListItem = false;
        Accessibility.IAccessible accessible = null;
        object hit = null;
        try
        {
            Guid accessibleId = IID_IAccessible;
            int result = AccessibleObjectFromWindow(
                listView, OBJID_CLIENT, ref accessibleId, out accessible);
            if (result < 0 || accessible == null)
                return false;

            hit = accessible.accHitTest(point.X, point.Y);
            if (hit == null)
                return false;

            if (hit is int || hit is short || hit is long)
                isListItem = Convert.ToInt64(hit) > 0;
            else if (hit is Accessibility.IAccessible || Marshal.IsComObject(hit))
                isListItem = true;
            else
                return false;

            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Desktop accessibility hit-test failed", ex);
            return false;
        }
        finally
        {
            if (hit != null && Marshal.IsComObject(hit) && !Object.ReferenceEquals(hit, accessible))
                Marshal.FinalReleaseComObject(hit);
            if (accessible != null && Marshal.IsComObject(accessible))
                Marshal.FinalReleaseComObject(accessible);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        internal POINT pt;
        internal uint mouseData;
        internal uint flags;
        internal uint time;
        internal IntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        internal int AccentState;
        internal int AccentFlags;
        internal int GradientColor;
        internal int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        internal int Attribute;
        internal IntPtr Data;
        internal int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OSVERSIONINFOEX
    {
        internal int dwOSVersionInfoSize;
        internal int dwMajorVersion;
        internal int dwMinorVersion;
        internal int dwBuildNumber;
        internal int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string szCSDVersion;
        internal ushort wServicePackMajor;
        internal ushort wServicePackMinor;
        internal ushort wSuiteMask;
        internal byte wProductType;
        internal byte wReserved;
    }

    [DllImport("ntdll.dll")]
    internal static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr window,
        uint objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect,
        uint maxSizeHi, uint maxSizeLo, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr MapViewOfFile(IntPtr mapping, uint access,
        uint offsetHi, uint offsetLo, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address,
        UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address,
        byte[] buffer, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes,
        UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    internal static extern IntPtr GetProcAddress(IntPtr module, string name);

    internal static string GetClassNameString(IntPtr window)
    {
        System.Text.StringBuilder value = new System.Text.StringBuilder(256);
        GetClassName(window, value, value.Capacity);
        return value.ToString();
    }
}
