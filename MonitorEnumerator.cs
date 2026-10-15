using System.Runtime.InteropServices;

namespace MultiWallpaper;

/// <summary>系统上所有物理显示器的信息。</summary>
public sealed class Monitor
{
    public int Index { get; set; }               // 0-based 位置序号（UI 列序）
    public string Name { get; set; } = "";        // 显示名：显示器 1 / 显示器 2 …
    public string Device { get; set; } = "";      // Win32 设备名 \\.\DISPLAYn（稳定身份）
    public bool IsPrimary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }

    public override string ToString() =>
        $"{Name} ({Device}){Width}x{Height} @ ({Left},{Top}){(IsPrimary ? " [主]" : "")}";
}

public static class MonitorEnumerator
{
    /// <summary>
    /// 枚举所有物理显示器。
    /// 排序：主显示器排第一，其余按 Left（再 Top）。
    /// 排序后按位置重编号 Index（0..N），并生成显示名「显示器 N+1」。
    /// Device（\\.\DISPLAYn）是稳定身份，用于跨枚举映射勾选。
    /// </summary>
    public static List<Monitor> Enumerate()
    {
        var list = new List<Monitor>();

        NativeMethods.MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            };
            if (!NativeMethods.GetMonitorInfoW(hMonitor, ref mi))
                return true;

            bool isPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
            int width = mi.rcMonitor.right - mi.rcMonitor.left;
            int height = mi.rcMonitor.bottom - mi.rcMonitor.top;

            list.Add(new Monitor
            {
                Index = list.Count,
                Name = "",
                Device = mi.szDevice ?? "",
                IsPrimary = isPrimary,
                Width = width,
                Height = height,
                Left = mi.rcMonitor.left,
                Top = mi.rcMonitor.top,
            });
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);

        // 主显示器排第一，其余按位置（Left，再 Top）
        list.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            int cmp = a.Left.CompareTo(b.Left);
            if (cmp != 0) return cmp;
            return a.Top.CompareTo(b.Top);
        });

        // 重新编号（主显示器 = 1）
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Index = i;
            list[i].Name = $"显示器 {i + 1}";
        }
        return list;
    }
}
