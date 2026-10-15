using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiWallpaper;

/// <summary>
/// Win32 P/Invoke + 多屏壁纸应用（标准做法，无第三方依赖）。
///
/// Windows 10 1809+ 多屏显示机制（经实测验证）：
///   - 桌面最终由「一张覆盖整个虚拟桌面的壁纸文件」驱动，系统按各屏位置裁剪显示，
///     并把这张图转码缓存为 %APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper，
///     让 HKCU\Control Panel\Desktop\Wallpaper 指向它。
///   - 只写 HKCU\Control Panel\backgrounds\Device--DISPLAYn 的 Transitions + SPI 刷新 +
///     广播 WM_SETTINGCHANGE，Windows 10 不会重新生成 TranscodedWallpaper（实测修改时间不变）。
///   - 因此要「各屏真正变」，必须把各屏当前图按虚拟桌面布局拼成一张新主壁纸文件，
///     再用 SystemParametersInfoW(SPI_SETDESKWALLPAPER, path) 设成主壁纸，系统重渲染各屏。
///
/// 本类提供：
///   - 枚举显示器（EnumDisplayMonitors / GetMonitorInfoW）
///   - SetPerMonitorWallpaper：按各屏当前图拼出主壁纸并应用（真正的"每显示器不同图"渲染）
///   - 写各设备键 Transitions（供系统/其它工具保持一致，且重启后仍能恢复）
///   - 单屏兜底：SetMainWallpaper
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------
    // 显示器枚举
    // ------------------------------------------------------------------

    public const int MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr lpData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    // ------------------------------------------------------------------
    // 桌面刷新 / 主壁纸
    // ------------------------------------------------------------------

    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SCREEN_CACHE_REBUILD = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    private const int HWND_BROADCAST = 0xffff;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string? lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>把主壁纸显式设到指定文件（单显示器或兜底），并广播刷新。</summary>
    public static bool SetMainWallpaper(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        IntPtr p = IntPtr.Zero;
        try
        {
            p = Marshal.StringToHGlobalUni(imagePath);
            bool ok = SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, p, SCREEN_CACHE_REBUILD);
            _ = SendMessageTimeoutW((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Control Panel\\Desktop",
                SMTO_ABORTIFHUNG, 1000, out _);
            return ok;
        }
        finally
        {
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>触发系统按当前主壁纸重新渲染（不指定新图）。</summary>
    public static void RefreshDesktop()
    {
        _ = SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, IntPtr.Zero, SCREEN_CACHE_REBUILD);
        _ = SendMessageTimeoutW((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Control Panel\\Desktop",
            SMTO_ABORTIFHUNG, 1000, out _);
    }

    // ------------------------------------------------------------------
    // 多显示器：按各屏当前图生成主壁纸（真正让各屏显示不同图）
    // ------------------------------------------------------------------

    /// <summary>
    /// 把每台显示器「当前该显示的图」按虚拟桌面布局拼成一张主壁纸，设成系统主壁纸。
    /// 未配置图片的屏用纯黑填充。这是 Windows 10 多屏「不同图」的实际渲染方式。
    /// monitors 必须与 monitorToPath 同序（Index 对应）。
    /// </summary>
    public static bool SetPerMonitorWallpaper(List<Monitor> monitors, Dictionary<int, string> monitorToPath)
    {
        if (monitors == null || monitors.Count == 0) return false;

        // 1) 计算虚拟桌面包围盒
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in monitors)
        {
            left = Math.Min(left, m.Left);
            top = Math.Min(top, m.Top);
            right = Math.Max(right, m.Left + m.Width);
            bottom = Math.Max(bottom, m.Top + m.Height);
        }
        int totalW = right - left;
        int totalH = bottom - top;
        if (totalW <= 0 || totalH <= 0) return false;

        // 2) 用 32bppArgb 画布（避免旧 24bppRgb 全黑 bug），逐屏绘制其当前图
        using var canvas = new Bitmap(totalW, totalH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black);
            foreach (var m in monitors)
            {
                int dx = m.Left - left;
                int dy = m.Top - top;
                if (!monitorToPath.TryGetValue(m.Index, out var path) || string.IsNullOrEmpty(path))
                    continue; // 未配图 → 保持黑底
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    using var img = new Bitmap(path);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(img, dx, dy, m.Width, m.Height);
                }
                catch { /* 单张图失败就留黑底 */ }
            }
        }

        // 3) 保存为 PNG（无损、支持透明边）到 %LOCALAPPDATA%\MultiWallpaper，再设为主壁纸
        string dir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "MultiWallpaper");
        System.IO.Directory.CreateDirectory(dir);
        string outPath = System.IO.Path.Combine(dir, "per-monitor-current.png");
        canvas.Save(outPath, ImageFormat.Png);

        if (!SetMainWallpaper(outPath)) return false;

        // 4) 关键：把壁纸样式改为「跨所有显示器」（WallpaperStyle=22）。
        //    否则系统停留在当前样式（如 10=填充）时，会把整张主壁纸按每个显示器独立
        //    填充/裁剪，三屏都显示「图中央」，而不会按虚拟桌面布局裁剪各屏各自的区域。
        //    样式 22 告诉系统：这是一张跨虚拟桌面的图，请每个显示器只显示自己在虚拟
        //    桌面中所占的那一块 —— 左屏显示左侧、主屏显示中间、右竖屏显示右侧。
        using var desk = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true);
        desk?.SetValue("WallpaperStyle", 22, RegistryValueKind.String);
        desk?.SetValue("TileWallpaper", 0, RegistryValueKind.String);
        // 再广播一次，让系统按新样式重新渲染
        RefreshDesktop();
        return true;
    }

    // ------------------------------------------------------------------
    // 多显示器：写各设备键 Transitions（供系统/其它工具保持一致）
    // ------------------------------------------------------------------

    /// <summary>把某显示器当前图写入 HKCU\Control Panel\backgrounds\Device--DISPLAYn 的 Transitions。</summary>
    public static bool SetMonitorWallpaper(string deviceKey, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        const string backgroundsRoot = @"Control Panel\backgrounds";
        using var root = Registry.CurrentUser.OpenSubKey(backgroundsRoot, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(backgroundsRoot);
        if (root == null) return false;

        string dev = deviceKey.TrimStart('\\', '.').Replace("\\", "");
        string subName = "Device--" + dev;
        using var devKey = root.OpenSubKey(subName, writable: true) ?? root.CreateSubKey(subName);
        if (devKey == null) return false;

        devKey.SetValue("Transitions", new[] { imagePath }, RegistryValueKind.MultiString);
        devKey.SetValue("BackgroundType", 0, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>
    /// 应用「各屏当前图」到系统：
    ///   1. 把各屏当前图按虚拟桌面布局拼成新主壁纸并设为主壁纸（让屏幕真正变化）
    ///   2. 同步写各 Device--DISPLAYn 的 Transitions（供系统重启/其它工具一致）
    /// </summary>
    public static bool ApplyPerMonitorNative(List<Monitor> monitors, Dictionary<int, string> monitorToPath)
    {
        // 先写各设备键（即使主壁纸生成失败，注册表也保持一致）
        foreach (var m in monitors)
        {
            if (monitorToPath.TryGetValue(m.Index, out var path) && !string.IsNullOrEmpty(path))
                SetMonitorWallpaper(m.Device, path);
        }

        // 再生成新主壁纸让屏幕真正变
        return SetPerMonitorWallpaper(monitors, monitorToPath);
    }
}
