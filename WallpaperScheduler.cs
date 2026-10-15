namespace MultiWallpaper;

/// <summary>
/// 轮换调度器（按显示器分组）：
///   每个显示器拥有自己独立的图片序列。某张图勾选了哪些显示器，
///   就进哪个显示器的序列；序列按表格顺序排列，每张图片带自己的「显示时长(分钟)」。
///
/// 实现（标准做法，无第三方依赖）：
///   - 每个显示器按自己的序列独立循环（各屏独立游标，互不干扰）
///   - 应用时把每台显示器「当前图」按虚拟桌面布局拼成新主壁纸文件设成系统壁纸，
///     并同步写各 Device--DISPLAYn 设备键；各屏显示各自那张图，互不覆盖
///   - 程序自身计时器：每屏步长 = 该屏当前图时长，谁的剩余时间先到 0 谁先前进，
///     统一用「最小剩余秒数」做轮询间隔
///
/// 这样「显示器1、显示器2 用图片 3-18，显示器3 用图片 1-2」会得到：
///   D1/D2 各自循环 [3,4,…,18]，D3 循环 [1,2]，互不干扰。
/// </summary>
public sealed class WallpaperScheduler
{
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;

    public Config? Config { get; private set; }
    public List<Monitor> Monitors { get; private set; } = new();
    public string Status { get; private set; } = "未启动";
    public event Action? StatusChanged;
    public event Action? Applied;

    public bool IsRunning { get; private set; }

    /// <summary>各显示器当前图文件名（供 UI 显示）。</summary>
    public List<string> CurrentImages { get; } = new();

    /// <summary>每个显示器的当前轮播位置（0-based），与 Monitors 列表同序。</summary>
    private readonly List<int> _cursor = new();

    /// <summary>每个显示器的计时剩余（毫秒），与 Monitors 列表同序。</summary>
    private readonly List<long> _remainingMs = new();

    /// <summary>启动：各屏游标重置为 0，立即应用一轮，然后按步长计时。</summary>
    public void Start(Config cfg)
    {
        lock (_gate)
        {
            if (IsRunning) Stop();
            Config = cfg;
            Monitors = MonitorEnumerator.Enumerate();
            IsRunning = true;

            // 初始化各屏状态
            _cursor.Clear();
            _remainingMs.Clear();
            CurrentImages.Clear();
            for (int i = 0; i < Monitors.Count; i++)
            {
                _cursor.Add(0);
                int minutes = GetImageMinutes(i, 0);
                _remainingMs.Add((long)Math.Max(minutes, 1) * 60_000);
                CurrentImages.Add("");
            }

            ApplyPerMonitor();

            int stepMs = NextStepMillis();
            _timer = new System.Threading.Timer(_ => Tick(), null,
                TimeSpan.FromMilliseconds(Math.Max(stepMs, 1_000)), Timeout.InfiniteTimeSpan);

            Status = $"运行中 — {cfg.Images.Count} 张图片，{Monitors.Count} 个显示器，整轮 {RoundTotalMinutes()} 分钟";
        }
        StatusChanged?.Invoke();
    }

    /// <summary>停止轮换。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            IsRunning = false;
            Status = "已停止";
        }
        StatusChanged?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (Config == null || Monitors.Count == 0) return;

            // 各屏计时器统一 tick 1 秒，累减剩余
            for (int i = 0; i < Monitors.Count; i++)
            {
                _remainingMs[i] -= 1_000;
                if (_remainingMs[i] <= 0)
                {
                    // 该屏前进一张
                    var seq = GetSequenceForMonitor(i);
                    if (seq.Count > 0)
                    {
                        _cursor[i] = (_cursor[i] + 1) % seq.Count;
                        _remainingMs[i] = (long)Math.Max(GetImageMinutes(i, _cursor[i]), 1) * 60_000;
                    }
                    else
                    {
                        _remainingMs[i] = 0;
                    }
                }
            }

            ApplyPerMonitor();
            Applied?.Invoke();

            // 安排下一次 tick：取各屏剩余的最小值（秒）
            int stepMs = NextStepMillis();
            _timer = new System.Threading.Timer(_ => Tick(), null,
                TimeSpan.FromMilliseconds(Math.Max(stepMs, 1_000)), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>下一个 tick 间隔（毫秒）= 各屏剩余时间的最小值。</summary>
    private int NextStepMillis()
    {
        if (Monitors.Count == 0) return 1_000;
        long min = long.MaxValue;
        for (int i = 0; i < Monitors.Count; i++)
        {
            var seq = GetSequenceForMonitor(i);
            if (seq.Count == 0) continue; // 没配图的屏不参与计时
            min = Math.Min(min, _remainingMs[i]);
        }
        if (min == long.MaxValue) min = 1_000;
        return (int)Math.Min(Math.Max(min, 1_000), int.MaxValue);
    }

    /// <summary>取某显示器某位置的图片时长（分钟）。</summary>
    private int GetImageMinutes(int monitorIdx, int cursorPos)
    {
        if (Config == null) return 1;
        var seq = GetSequenceForMonitor(monitorIdx);
        if (cursorPos < 0 || cursorPos >= seq.Count) return 1;
        var img = Config.Images.FirstOrDefault(x => x.File == seq[cursorPos]);
        return img != null ? Math.Max(img.Minutes, 1) : 1;
    }

    /// <summary>取某显示器勾选的图片路径列表（按表格顺序，只含勾选了该屏的图）。</summary>
    private List<string> GetSequenceForMonitor(int monitorIdx)
    {
        var seq = new List<string>();
        if (Config == null) return seq;
        foreach (var img in Config.Images)
        {
            if (img.Monitors.Contains(monitorIdx))
                seq.Add(img.File);
        }
        return seq;
    }

    /// <summary>整轮循环总分钟数 = 任一显示器序列各图时长之和（取最大值）。</summary>
    private int RoundTotalMinutes()
    {
        if (Config == null || Monitors.Count == 0) return 0;
        int max = 0;
        for (int i = 0; i < Monitors.Count; i++)
        {
            int total = 0;
            foreach (var img in Config.Images)
            {
                if (img.Monitors.Contains(i)) total += Math.Max(img.Minutes, 1);
            }
            max = Math.Max(max, total);
        }
        return max;
    }

    /// <summary>
    /// 核心：按显示器分组，每个显示器取自己序列的当前图，写入各自的注册表设备键
    /// （系统原生「每显示器不同壁纸」机制），再触发系统刷新。未勾选任何图的屏不动。
    /// </summary>
    public void ApplyPerMonitor()
    {
        if (Config == null || Monitors.Count == 0) return;

        var monitorToPath = new Dictionary<int, string>();
        var parts = new List<string>();
        int empty = 0;

        for (int i = 0; i < Monitors.Count; i++)
        {
            var seq = GetSequenceForMonitor(i);
            if (seq.Count == 0)
            {
                empty++;
                continue;
            }
            int cursor = _cursor[i] % seq.Count;
            string path = seq[cursor];
            monitorToPath[Monitors[i].Index] = path;
            parts.Add($"{Monitors[i].Name}:{System.IO.Path.GetFileName(path)}");
            if (i < CurrentImages.Count) CurrentImages[i] = System.IO.Path.GetFileName(path);
        }

        bool ok = NativeMethods.ApplyPerMonitorNative(Monitors, monitorToPath);
        Status = ok
            ? $"已应用 — " + string.Join("  ", parts) + (empty > 0 ? $"  ({empty}屏未配)" : "")
            : "应用失败";
        StatusChanged?.Invoke();
    }

    /// <summary>把配置里的序列立即套用到各屏（"应用"按钮用）。</summary>
    public void ApplyFirstImmediately()
    {
        lock (_gate)
        {
            for (int i = 0; i < _cursor.Count; i++) _cursor[i] = 0;
            for (int i = 0; i < Monitors.Count; i++)
                _remainingMs[i] = (long)Math.Max(GetImageMinutes(i, 0), 1) * 60_000;
            ApplyPerMonitor();
        }
    }

    /// <summary>托盘「立即切换下一张」：各屏游标前进一格并立即应用。</summary>
    public void AdvanceAll()
    {
        lock (_gate)
        {
            if (Config == null || Monitors.Count == 0) return;
            for (int i = 0; i < Monitors.Count; i++)
            {
                var seq = GetSequenceForMonitor(i);
                if (seq.Count == 0) continue;
                _cursor[i] = (_cursor[i] + 1) % seq.Count;
                _remainingMs[i] = (long)Math.Max(GetImageMinutes(i, _cursor[i]), 1) * 60_000;
            }
            ApplyPerMonitor();
        }
    }
}
