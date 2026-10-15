# 开发历程：踩过的坑与走过的弯路

> 本文按开发顺序记录 MultiWallpaper 从「能用」到「正确」的全过程。
> 每个坑都有 **现象 → 根因 → 修复** 三段，方便以后排查同类问题。
> 读完你会发现：多显示器壁纸这个功能，网上很多现成答案给的都是「写注册表 + 刷新桌面」，
> 但在 Windows 10 上**那一步根本不会让屏幕变化**——真正的机制在 [DESIGN.md](DESIGN.md) 里。

---

## 阶段 0：浏览器 + Python 版本（已弃用）

最初的做法是一个浏览器页面 + Python 脚本计时切换壁纸。问题：

- 计时依赖浏览器标签页：标签页切后台被节流、一关页面轮换就停
- 图片经 base64 走 HTTP、每次切换写临时文件，链路绕
- 不是「标准做法」，无法常驻系统

结论：重写为原生 WinForms 程序（阶段 1+）。

---

## 坑 1：不存在的 P/Invoke 导致「有配置时启动即崩溃」

**现象**：程序空配置时能启动；一旦 `config.json` 存在并触发轮换，进程直接挂掉。日志里看不到
异常（UI 线程外的未处理异常），排查很痛苦。

**根因**：第一版代码里写了三个**在 `user32.dll` 中并不导出**的 API：
`ChangeWallpaper`、`SetCurrentTimeOfDay`、`StopSlideshow`。
它们来自某些老文章的「内部函数」描述，实际不存在，P/Invoke 在第一次调用时抛
`EntryPointNotFoundException`。

**修复**：全部删除。多显示器方案改走「写注册表 + `SystemParametersInfoW` 刷新」
（后来进一步演变为坑 8 的最终方案）。**教训：任何 P/Invoke 先验证导出表再写。**

---

## 坑 2：注册表设备子键名拼错（单横线 vs 双横线）

**现象**：按文档写的 `HKCU\Control Panel\backgrounds\Device--DISPLAYn` 写入后，
程序认为写成功，但实际渲染毫无变化。

**根因**：早期代码把子键名拼成了 `Device-DISPLAYn`（单个横线）或带了前缀反斜杠，
和系统真实键名对不上，等于写进了一个无人在乎的孤儿键。

**修复**：统一规则——设备名 `\\.\DISPLAY1` 去掉前导 `\`、`.` 与 `\` 后，
子键名 = `"Device--" + dev`（**双横线**，和系统完全一致）。
代码在 `NativeMethods.SetMonitorWallpaper` 里，带注释。

---

## 坑 3：中文路径在 config.json 里变乱码

**现象**：图片目录带中文（`E:\My Picture\桌面背景`）时，保存再加载后路径变成问号串，
应用壁纸全部失败。

**根因**：`File.WriteAllText` / `ReadAllText` 用了系统默认代码页，
而机器是 GBK 环境，UTF-8 的 JSON 被按 GBK 读/写。

**修复**（`ConfigStore.cs`）：
- 读写一律显式 `new UTF8Encoding(false)`（**无 BOM**，避免首字符被 BOM 吃掉）
- JSON 序列化加 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，让中文原样输出而不是 `\uXXXX`

**教训：Windows 上处理中文路径的持久化，永远要显式指定编码。**

---

## 坑 4：拼合大图全黑（24bppRgb 画布 + GDI）

**现象**：第一次实现「把各屏图拼成一张大图设主壁纸」时，生成出来是一张全黑的图。
一度以为是权限问题、磁盘空间问题，浪费了很长时间。

**根因**：用 `PixelFormat.Format24bppRgb` 的画布 + `Graphics.DrawImage` 后，
GDI 在某些 Windows 版本上不会正确把数据从 ARGB 管线落到 24bpp 位图，
保存 JPEG 后全 0。

**修复**：画布改用 `Format32bppArgb`（`NativeMethods.SetPerMonitorWallpaper`），
输出 PNG（无损、还免掉 JPEG 重压缩失真）。**验证方法**：不要只看「文件生成了」，
用像素采样确认各屏区域中心点真的非黑、且颜色符合该屏配置的图。

---

## 坑 5：按「图片」轮换导致三屏永远同图

**现象**：用户反馈「三个显示器的图片都是 1」。配置里明明给不同屏勾了不同图，
但所有屏显示的永远是同一张。

**根因**：第一版调度器是**按图片**遍历的：每张图到点后，把「这张图」写进它勾选的所有屏。
结果所有屏的节奏被图片顺序统一锁死，屏与屏之间毫无独立性。

**修复**：改为**按显示器分组**（`WallpaperScheduler.ApplyPerMonitor` + `GetSequenceForMonitor`）：
- 每台显示器 = 一条独立序列（只包含勾选了该屏的图，按表格顺序）
- 每台显示器 = 一个独立游标 + 一个独立剩余时间
- 谁到点谁前进，`Tick()` 用各屏剩余时间的**最小值**做下次轮询间隔

**教训：多设备独立轮换，数据模型必须「以设备为主体」；设备间编号（位置索引）会话内够用，
跨会话的稳定身份靠 `\\.\DISPLAYn` 设备名做映射锚点。**

---

## 坑 6：「刷新显示器」后 D3 的勾选神秘消失（位置索引不稳定）

**现象**：图 1-2 勾了显示器 3、图 3-18 勾了 1/2，点「保存配置」或「立即应用」后，
点「刷新显示器」，图 1-2 在 D3 的勾选就没了。反复出现。

**根因**：旧版配置把显示器存成 **0-based 位置索引**（`List<int>`）。而 `MonitorEnumerator`
每次枚举都按「主屏优先 + Left/Top」重排并**重新编号**——两次枚举的顺序一旦不同
（拔插过屏、主屏标志变化、DPI 变化），同一个物理屏的编号就变了，
旧索引指向了错误的屏（或查无此屏），`PopulateGrid` 匹配不上，勾选看起来就"丢了"。

**修复**（`SettingsForm.OnRefreshMonitors`）：
- 刷新时不再丢弃旧勾选：先用**旧**枚举把网格勾选读回 `_images`（位置索引），
  重新枚举后，经 `旧Index → Device名 → 新Index` 映射把旧索引对齐到新枚举，
  再重新填充网格——即使两次枚举顺序不同（拔插屏、主屏标志变化）勾选也不错位
- 曾尝试过更彻底的方案（把 `ImageEntry.Monitors` 直接改成存 `List<string>` 设备名 +
  旧配置自动迁移），后来**回退**了：设备名在驱动重置/多显卡重排时同样会变，
  而且让 UI 持久化与运行时时序都变复杂；最终保留"位置索引存盘 + 刷新时按设备名映射对齐"，
  够用且简单
- 遗留边界（写进文档 [DESIGN.md 1.5](DESIGN.md)）：系统重排显卡输出顺序导致
  `DISPLAYn` 编号变化时，映射会失效，用户需重新勾一次

**教训：Windows 没有公开的"持久显示器 ID"，`DISPLAYn` 也只是"相对稳定"。
把稳定性问题限定在「刷新」这一个入口里处理，比全局改成设备名存储改动面小得多。**

---

## 坑 7：DataGridView 复选框「值形态」误读（最隐蔽的一个）

**现象**：坑 6 修完后，用户测试发现**只有**「图 1、2 → 显示器 3」这一种勾选会被刷新丢掉，
其它（走「全选」按钮设置的）正常。用户判断：「应该是程序的问题，不是设备名的问题」。

**根因**（用一个最小复现程序证实）：
- 勾选列设置了 `TrueValue = 1, FalseValue = 0`（int）
- **用户手点**复选框时，WinForms 提交的是列的 TrueValue，即 **int 1**
- 「全选」按钮写的是 **bool true**
- 而读取勾选的代码是 `v is bool b ? b : (v != null && v.ToString() == "True")`
  —— 它只认 bool 和字符串 "True"，**把 int 1 判成「未勾选」**

所以：手点勾选的「图1-2 → D3」在每次 `ReadGridToImages` 时被静默丢弃；
全选按钮写的 bool 值能读对。**两种写入路径、两种值形态，读取只处理了一种。**

**验证方法（值得收藏）**：写个 30 行的 WinForms 探针，往单元格分别塞
`true / col.TrueValue / col.FalseValue`，打印 `Value.GetType()` 与判定结果：

```
img1  值类型=Int32 值=1  → 现有判定 False   ← 手点勾选被误判未勾选！
img3  值类型=Boolean 值=True → 现有判定 True  ← 程序写入的 bool 正常
```

**修复**（`SettingsForm.IsCellChecked`）：统一兼容所有形态——
`bool`、`int != 0`、`long != 0`、字符串 `"True"`/`"1"`。`ReadGridToImages` 与
`SyncSelectAllStates` 都改走它。

**教训：WinForms 数据格子的 `Value` 实际类型取决于列的 True/FalseValue 设置和写入路径，
读取端永远不能假设只有一种形态。**

---

## 坑 8：误以为「写注册表设备键 + 刷新桌面」就能让各屏独立渲染

**现象**：删掉拼合逻辑、改成只写 `Device--DISPLAYn\Transitions` 后，
屏幕完全不动——「实际屏幕还是之前那张图」。

**根因**（在真实机器上做受控实验证实）：
```
实验前 TranscodedWallpaper 修改时间 = 19:15:08
写 3 个设备键 + SPI_SETDESKWALLPAPER(0) + 广播 WM_SETTINGCHANGE
实验后 TranscodedWallpaper 修改时间 = 19:15:08   ← 纹丝不动
```
Windows 10 的多屏桌面**最终由一张覆盖整个虚拟桌面的主壁纸文件驱动**
（系统转码缓存为 `TranscodedWallpaper`，主壁纸值指向它）。
`Device--DISPLAYn` 键只是「配置记录」，**只改它、不更新主壁纸文件，系统不会重新渲染**。
网上大量「多显示器不同壁纸」教程止步于写注册表 + `RefreshDesktop`，
在「系统已处于每显示器模式且正在运行」的场景下**看起来**能用（因为 UI 路径会顺带更新主壁纸），
但程序化直接写是无效的。

**修复**：恢复「生成主壁纸」这一步，但做成干净的 `SetPerMonitorWallpaper`：
各屏当前图按虚拟桌面布局拼成 32bppArgb PNG → 设主壁纸 → 同步写设备键保持一致。

**教训：对「系统到底怎么渲染的」有疑问时，别信文档也别信感觉——
在真机上做一个能观察到中间产物（文件修改时间、像素采样）的受控实验。**

---

## 坑 9（真凶）：`WallpaperStyle=10` 让三屏都显示「中间那块」

**现象**：坑 8 修完后，各屏内容「还是和原来一样」：主屏显示中间那张图+左图一半，
左屏和主屏一模一样，右屏显示合成图的中段。用户一句话点破：
*「左屏应该用合成图的左边部分，主屏用中间部分，右屏用右边部分——而不是都用中间部分。」*

**根因**：主壁纸虽然已经是正确的 4420×1600 虚拟桌面合成图，但
`HKCU\Control Panel\Desktop\WallpaperStyle = 10`（**填充 Fill**）。
填充语义是「把**整张**主壁纸适配到**每个**显示器」——
4420:1600 的长图适配到 1920×1080 时，系统居中保留宽高比匹配的一块，
**恰好就是长图的中央区域** → 三屏全显示中间那块。
（竖屏 900×1600 适配时取到的也是中央竖条，所以右屏看到的是「中段」。）

**修复**：`SetPerMonitorWallpaper` 设主壁纸后，把
`WallpaperStyle` 改为 **22（跨所有显示器 Span）**、`TileWallpaper = 0`，再广播刷新。
样式 22 的语义是「这张图铺满整个虚拟桌面，每个显示器只显示自己在虚拟桌面中占的那一块」——
于是左屏取左侧、主屏取中间、右竖屏取右侧，各显其图。用户确认「完全好了」。

**`WallpaperStyle` 取值备忘**：

| 值 | 含义 | 多屏行为 |
|---|---|---|
| 0 | 居中 | 每屏显示整图 |
| 1 | 平铺 | 每屏平铺整图 |
| 2 | 拉伸 | 每屏拉伸整图 |
| 6 | 适应 | 每屏等比适配整图 |
| 10 | 填充 | 每屏等比裁剪整图（**中央块**）← 坑 9 元凶 |
| 22 | 跨所有显示器 | 一张图铺满虚拟桌面，各屏裁自己那块 ← 正确 |

**教训：Windows 壁纸「样式」和「主壁纸文件」是两个正交配置。
换了图不换样式，行为就会完全不是你以为的那样。**

---

## 附：验证清单（每次改动后照做）

1. `dotnet build -c Release` 通过，启动后进程存活、窗口正常
2. 注册表：`Device--DISPLAYn\Transitions` 与当前配置一致
3. 文件：`%LOCALAPPDATA%\MultiWallpaper\per-monitor-current.png` 已更新
4. 像素采样合成图三屏区域中心，颜色符合各屏配置的图（非全黑、非同一块）
5. 确认 `WallpaperStyle = 22`
6. UI 回归：勾选（手点 / 全选）→ 保存 → 刷新显示器，勾选不丢；
   「立即切换下一张」三屏各自前进

## 附：日志与数据位置

| 内容 | 位置 |
|---|---|
| 运行日志 | `MultiWallpaper.exe` 同目录 `MultiWallpaper.log` |
| 用户配置 | `%APPDATA%\MultiWallpaper\config.json` |
| 当前合成壁纸 | `%LOCALAPPDATA%\MultiWallpaper\per-monitor-current.png` |
| 系统主壁纸缓存 | `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper` |
| 各屏配置记录 | `HKCU\Control Panel\backgrounds\Device--DISPLAYn` |
