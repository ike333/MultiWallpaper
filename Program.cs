using System.Windows.Forms;
using System.IO;

namespace MultiWallpaper;

internal static class Program
{
    private static NotifyIcon? _tray;
    private static SettingsForm? _settingsForm;
    private static readonly System.Collections.Generic.List<System.Windows.Forms.Timer> _timers = new();
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "MultiWallpaper.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); }
        catch { /* ignore */ }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Log("=== 程序启动 ===");
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            Log("Main 致命异常: " + ex);
        }
        finally
        {
            Log("=== 程序退出 ===");
        }
    }

    private static void Run()
    {
        ApplicationConfiguration.Initialize();

        // 全局异常兜底
        Application.ThreadException += (_, e) =>
        {
            Log("UI线程异常: " + e.Exception);
            MessageBox.Show("UI线程异常:\n" + e.Exception.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("未处理异常: " + e.ExceptionObject);

        // 单实例：若已有实例在跑，提示并退出
        bool singleInstance = true;
        using var mutex = new Mutex(true, @"Local\MultiWallpaper_Singleton", out singleInstance);
        if (!singleInstance)
        {
            Log("检测到已有实例，退出");
            MessageBox.Show("多显示器壁纸程序已在运行（见系统托盘）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var scheduler = new WallpaperScheduler();

        // 若有已保存配置，启动轮换
        var cfg = ConfigStore.Load();
        if (cfg != null && cfg.Images.Count > 0 && !string.IsNullOrWhiteSpace(cfg.Directory))
        {
            scheduler.Start(cfg);
            Log("已启动轮换，图片数=" + cfg.Images.Count);
        }

        // 创建托盘图标（常驻；程序退出的唯一出口）
        _tray = CreateTrayIcon(scheduler);
        _tray.Visible = true;
        Log("托盘已创建");

        // 显示设置窗口（可独立关闭，不影响程序常驻）
        _settingsForm = new SettingsForm(scheduler);
        _settingsForm.Show();
        Log("设置窗口已 Show");

        // 运行消息循环（不绑定任何窗体；只有从托盘点「退出程序」才结束）
        Application.Run();
        Log("消息循环结束");

        // 退出清理
        foreach (var t in _timers) t.Stop();
        _settingsForm?.Dispose();
        _tray?.Dispose();
    }

    private static NotifyIcon CreateTrayIcon(WallpaperScheduler scheduler)
    {
        // 托盘图标用程序自带的 ico（从 exe 资源提取），与任务栏图标一致
        var tray = new NotifyIcon
        {
            Text = "多显示器壁纸轮换",
            Icon = LoadAppIcon(),
        };

        var menu = new ContextMenuStrip();

        void OpenSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                _settingsForm.Show();
                _settingsForm.WindowState = FormWindowState.Normal;
                _settingsForm.BringToFront();
                _settingsForm.Activate();
            }
            else
            {
                _settingsForm = new SettingsForm(scheduler);
                _settingsForm.Show();
            }
        }

        var miOpen = new ToolStripMenuItem("打开设置…", null, (_, _) => OpenSettings());

        var miStatus = new ToolStripMenuItem("状态")
        {
            Enabled = false,
        };

        var miApplyNow = new ToolStripMenuItem("立即切换下一张", null, (_, _) =>
        {
            // 不重启整个轮换（那样会回到每屏第1张），而是各屏游标前进一格并立即应用
            scheduler.AdvanceAll();
        });

        var miStart = new ToolStripMenuItem("启动轮换")
        {
            CheckOnClick = true,   // 对号 = 当前轮换状态；点击文字自动切换启停
        };
        // 点击时根据当前对号状态切换启/停，并让文字同步语义
        miStart.CheckedChanged += (_, _) =>
        {
            if (miStart.Checked)
            {
                var c = ConfigStore.Load();
                if (c != null) scheduler.Start(c);
            }
            else
            {
                scheduler.Stop();
            }
        };
        // 初始对号反映真实运行状态
        UpdateStartItem(scheduler, miStart);
        // 后续每次状态变化（启停/应用/切换）同步对号与文字
        scheduler.StatusChanged += () => UpdateStartItem(scheduler, miStart);

        var miExit = new ToolStripMenuItem("退出程序", null, (_, _) =>
        {
            foreach (var t in _timers) t.Stop();
            _settingsForm?.Close();
            tray.Visible = false;
            Application.Exit();
        });

        menu.Items.Add(miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miStatus);
        menu.Items.Add(miApplyNow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);
        tray.ContextMenuStrip = menu;

        // 定期刷新托盘状态文字
        var timer = new System.Windows.Forms.Timer { Interval = 15000 };
        timer.Tick += (_, _) =>
        {
            miStatus.Text = scheduler.Status;
            UpdateStartItem(scheduler, miStart);
        };
        timer.Start();
        _timers.Add(timer);

        tray.DoubleClick += (_, _) => OpenSettings();

        miStatus.Text = scheduler.Status;

        return tray;
    }

    /// <summary>
    /// 同步开关菜单项的对号与文字：对号表示"轮换正在运行"，
    /// 文字随之切换为"停止轮换 / 启动轮换"，让状态一目了然。
    /// </summary>
    private static void UpdateStartItem(WallpaperScheduler scheduler, ToolStripMenuItem item)
    {
        item.Checked = scheduler.IsRunning;
        item.Text = scheduler.IsRunning ? "停止轮换" : "启动轮换";
    }

    /// <summary>
    /// 从 exe 内嵌的 ico 资源提取托盘图标；失败时退回系统默认图标。
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith(".ico", System.StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = asm.GetManifestResourceStream(name)!;
                    return new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
                }
            }
        }
        catch { }
        return SystemIcons.Application;
    }
}
