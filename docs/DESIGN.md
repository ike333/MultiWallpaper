# 设计文档：Windows 多显示器壁纸轮换的内部机制与程序架构

> 本文两部分：
> 1. **Windows 是怎么渲染多屏壁纸的** —— 理解这节，很多「现成答案」的错误就能自行识别
> 2. **程序架构** —— 各组件职责与数据流

---

## 一、Windows 10 多屏壁纸的内部机制（实测结论）

### 1.1 一条核心事实：桌面由「一张覆盖虚拟桌面的主壁纸」驱动

无论你在「个性化」里选了「每个显示器一张不同壁纸」还是幻灯片放映，
**Windows 10 桌面窗口实际渲染的来源，始终是一张覆盖整个虚拟桌面（virtual desktop）
的壁纸文件**。

- 虚拟桌面 = 所有显示器外接口的并集包围盒。例如三屏：
  - `DISPLAY1` 1600×900 @ (-1600, 104)（左）
  - `DISPLAY5` 1920×1080 @ (0, 0)（主屏）
  - `DISPLAY4` 900×1600 @ (1920, -258)（右竖屏）
  - → 虚拟桌面 = 4420×1600，原点 (-1600, -258)
- 系统把这张主壁纸转码缓存为
  `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`，
  并让 `HKCU\Control Panel\Desktop\Wallpaper` 指向它。

「每个显示器不同图」的体感来自：系统把各显示器各自的图**按其在虚拟桌面中的位置
拼进那张主壁纸**，各屏只显示自己那一块。所以它「看起来是各屏独立」，
底层仍是一张大图。

### 1.2 `WallpaperStyle`：被几乎所有教程忽略的关键值

`HKCU\Control Panel\Desktop\WallpaperStyle` 决定主壁纸**怎么映射到屏幕**：

| 值 | 含义 | 多屏下每个显示器的行为 |
|---|---|---|
| 0 | 居中 | 每屏显示整图（居中留边） |
| 1 | 平铺 | 每屏平铺整图 |
| 2 | 拉伸 | 每屏拉伸整图 |
| 6 | 适应 | 每屏等比适配整图 |
| 10 | 填充 | 每屏等比裁剪整图（**取中央块**） |
| **22** | **跨所有显示器** | **一张图铺满整个虚拟桌面，各屏裁自己那块** |

多屏各显不同图，**必须** 22。停在 10（系统默认常见值）时，
三屏都会显示长图的中央块——这是本项目最后一个 bug 的真凶（见 DEVELOPMENT.md 坑 9）。

### 1.3 `Device--DISPLAYn` 注册表键：是「配置记录」，不是「渲染开关」

`HKCU\Control Panel\backgrounds\` 下：

```
Transitions            (REG_MULTI_SZ) 历史播放过的图（根级）
TransitionsTimes\...   每张图的停留分钟
Device--DISPLAY1\
    Transitions        (REG_MULTI_SZ, 单元素) 该屏当前图路径
    BackgroundType     (DWORD) 0=幻灯片模式等
    TransitionsTimes\...
Device--DISPLAY5\ ...
```

**实测（在真实机器上做受控实验）**：只改各设备键的 `Transitions`，
再发 `SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, 0, 0x40)` + 广播 `WM_SETTINGCHANGE`，
`TranscodedWallpaper` 的修改时间**不变**——系统没有重新渲染，屏幕停在上次的内容。

也就是说：设备键是**状态记录**（供系统设置、登录时重建等场景读取），
**要屏幕立刻变，必须更新「主壁纸」这条路**。

### 1.4 本项目的落盘管线（每次应用一轮）

```
各屏当前图（来自各自序列游标）
        │  按虚拟桌面布局 DrawImage（32bppArgb 画布，未配图的屏留黑）
        ▼
%LOCALAPPDATA%\MultiWallpaper\per-monitor-current.png   （虚拟桌面尺寸）
        │  SystemParametersInfoW(SPI_SETDESKWALLPAPER, path, 0x40)
        ▼
系统转码 → TranscodedWallpaper，Desktop\Wallpaper 指向它
        │  同时写 HKCU\Control Panel\Desktop\WallpaperStyle = 22
        ▼
各屏裁出自己在虚拟桌面中的那块 → 屏幕上各显其图
        │  同步写各 Device--DISPLAYn\Transitions（与实际显示一致）
        ▼
注册表状态 / 屏幕 / 主壁纸文件 三者对齐
```

设计取舍：

- **PNG 而非 JPEG**：无损；避免每张图反复 JPEG 有损压缩累积劣化
- **32bppArgb 画布**：24bppRgb + GDI 在部分系统上保存后全黑（见坑 4）
- **未配图的屏留黑**：拼合时对应区域保持 `Clear(Color.Black)`，不继承上一轮内容，
  语义明确——"这个屏没分到图"就是黑屏，比"卡在上一张"更容易被用户察觉和修正
- **同步写设备键**：让「系统设置 → 个性化」里看到的各屏背景与实际一致；
  注销/重启后系统按设备键重建时也不会跳回旧图

### 1.5 显示器身份：位置索引是「会话内」键，设备名是「刷新映射」键

- 同一会话内，程序用 **0-based 位置索引**（主屏优先 + Left/Top 排序）作为显示器编号，
  UI 的「显示器 1/2/3」即此编号；**配置 JSON 的 `monitors` 字段也存这组索引**（`List<int>`）
- 但枚举顺序本身**不稳定**（主屏标志、屏幕顺序、热插拔都会改变），所以「刷新显示器」
  不能直接拿旧索引套新枚举，必须借更稳定的身份：Win32 设备名 `\\.\DISPLAYn`
- 刷新时走 `旧Index→Device→新Index` 映射重新对齐（`SettingsForm.OnRefreshMonitors`），
  保证同一台物理屏的勾选项在新旧枚举之间不丢失；映射完仍写回位置索引存盘
- 注意：设备名也不是 100% 稳定——系统重排显卡输出顺序（多显卡机器、驱动重置）时
  `DISPLAYn` 编号可能重排。这是 Win32 没有公开"持久显示器 ID"的历史遗留问题；
  本程序在刷新后会把旧配置映射到新编号，极端场景下用户重新勾一次即可

---

## 二、程序架构

### 2.1 组件职责

```
Program.cs          入口 / 单实例(Mutex) / NotifyIcon 托盘 / 日志 / 启动恢复配置
MonitorEnumerator   EnumDisplayMonitors + GetMonitorInfoW → List<Monitor>
                    （Index, Device, Name, IsPrimary, 坐标）
ConfigStore         config.json 读写（UTF-8 无 BOM）、目录扫描
SettingsForm        UI：显示器列表、目录选择、图片×显示器×时长表格、预览、按钮
WallpaperScheduler  调度核心：按显示器分组 → 各屏独立游标/计时 → Tick → ApplyPerMonitor
NativeMethods       P/Invoke 与落盘：枚举、拼主壁纸 PNG、WallpaperStyle=22、设备键
```

### 2.2 轮换模型（以显示器为主体）

```
每台显示器 i：
  序列 Seq_i = [ 勾选了屏 i 的图片，按表格顺序 ]
  游标 cursor_i ∈ [0, Seq_i.Count)
  剩余 remaining_ms_i = 当前图分钟数 × 60000

Tick（System.Threading.Timer，非 UI 线程）：
  1. 各屏 remaining_ms_i -= 1000
  2. 到期的屏：cursor_i = (cursor_i+1) % Seq_i.Count，重算 remaining_ms_i
  3. ApplyPerMonitor()：所有屏取自己序列的当前图 → 生成主壁纸 PNG → 设壁纸+样式22 → 写设备键
  4. 安排下一次 Tick：interval = min(各屏 remaining_ms_i)（最小剩余，谁先到点谁被唤醒）
```

要点：

- **各屏节奏独立**：D3 序列 2 张 30min、D1/D2 序列 16 张 30min 时，
  D3 每 30min 换一张，D1/D2 各自按 16 张循环，互不牵制
- 某屏没勾任何图 → 该屏序列为空 → 主壁纸里对应区域纯黑（该屏的注册表设备键不动）
- 「立即切换下一张」（托盘）= 所有屏游标 +1 并立即 Apply
- 「保存配置 / 立即应用」= 写 config.json + 重置游标 0 + Apply
- 计时器在**后台线程**，关设置窗口、切后台都照常换；UI 只管配置与查看

### 2.3 稳定性设计（对应用户痛点）

| 场景 | 机制 |
|---|---|
| 拔插显示器后勾选不丢 | `OnRefreshMonitors` 先按旧枚举读回网格，再重枚举，按「旧Index→设备名→新Index」映射对齐 |
| 手点/全选两种写入路径不丢勾选 | `IsCellChecked` 兼容 bool / int / 字符串三种值形态 |
| 关设置窗口轮换不停 | 托盘常驻 + `Application.Run()` 无主窗体的消息循环 |
| 重复启动 | `Local\MultiWallpaper_Singleton` 互斥量，后启者提示并退出 |
| 排障 | `MultiWallpaper.log`（exe 旁）逐条记录启动/应用/异常 |

### 2.4 已知边界

- 需要 Windows 10 1809+；更早版本没有「每显示器不同壁纸」语义，
  程序仍可用（三屏会显示同一张主壁纸）
- 分辨率/布局变化（虚拟桌面形状变了）后，主壁纸尺寸会失配，
  点一次「刷新显示器」+「立即应用」即重新生成
- 锁屏界面不受本程序影响（只改桌面）
