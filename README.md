# MultiWallpaper — 多显示器壁纸独立轮换

Windows 原生桌面工具（C# / .NET 8 WinForms，无第三方依赖）：让**每台显示器拥有自己独立的图片序列**，
按你给每张图片设置的**显示时长（分钟）**自动轮换，各屏互不干扰。

```
图 1–2   → 显示器 3   （30 分钟一张，循环 1→2→1…）
图 3–18  → 显示器 1、2（各自 30 分钟一张，循环 3→4→…→18→3…）
```

## 功能

- **按显示器分组**：每个显示器一条独立序列、独立游标、独立计时，某屏未配图的时刻保持黑底
- **每张图独立时长**：表格里每行一个「显示时长（分钟）」输入框
- **直观的配置界面**：顶部列出全部显示器（编号 / 主副 / 分辨率），表格「图片 × 显示器勾选 × 时长」+ 图片预览 + 每列「全选」
- **托盘常驻**：关设置窗口不停轮换；菜单可「立即切换下一张 / 启动 / 停止 / 退出」
- **配置持久化**：`%APPDATA%\MultiWallpaper\config.json`，重启自动恢复
- **显示器热插拔安全**：勾选以稳定的 Win32 设备名（`\\.\DISPLAYn`）存储，刷新/重枚举不会错位或丢失

## 系统要求

- Windows 10 **1809+**（每显示器独立壁纸机制）
- .NET 8（构建需要 .NET 8 SDK；运行自包含版不需要）

## 构建

```bash
dotnet build -c Release
# 产物: bin\Release\net8.0-windows\win-x64\MultiWallpaper.exe
```

单文件免运行时发布：

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## 使用

1. 运行 exe，打开设置窗口
2. 「选择…」指定图片目录（png/jpg/jpeg/bmp/webp/gif）
3. 表格里为每张图勾选要显示的显示器（列头或「全选」复选框可整列操作），填写显示时长
4. **▶ 立即应用**：当前各屏立刻切到第一张，后台开始计时轮换
5. **💾 保存配置**：写 config.json，下次启动恢复

## 它是怎么让各屏显示不同图的（原理一句话版）

Windows 10 多屏桌面**最终由一张覆盖整个虚拟桌面的主壁纸驱动**（系统把它按各屏位置裁剪显示，
转码缓存为 `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`）。本程序：

1. 把各屏「当前图」按虚拟桌面真实布局（`EnumDisplayMonitors` 坐标）拼成一张 PNG；
2. `SystemParametersInfoW(SPI_SETDESKWALLPAPER)` 设为主壁纸；
3. **关键**：把 `HKCU\Control Panel\Desktop\WallpaperStyle` 设为 **22（跨所有显示器）**——
   否则默认的 10（填充）会让每个屏单独把整张长图「填充居中裁剪」，三屏就都显示中间那块；
4. 各屏当前图同步写入 `HKCU\Control Panel\backgrounds\Device--DISPLAYn\Transitions`，
   让系统注册表与实际显示保持一致（重启 / 系统设置里查看也对得上）。

详见 [docs/DESIGN.md](docs/DESIGN.md)。

## 开发历程（踩坑记录）

这个项目从「浏览器 + Python 计时切壁纸」一路重写成原生程序，中间踩了不少坑：
不存在的 P/Invoke 导致启动崩溃、假 API 误导、拼图画布格式全黑、按图而非按屏轮换导致三屏同图、
位置索引不稳定导致刷新丢勾选、DataGridView 复选框值形态误读、误以为写注册表就能让各屏独立渲染、
以及最后的真凶 `WallpaperStyle`。完整弯路日志（每个坑的现象 / 根因 / 修复）见：

📖 **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)** —— 建议先读，里面有受控实验的验证方法，以后排查同类问题能少走很多弯路。

## 项目结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 入口：单实例 Mutex、托盘、日志、启动恢复配置 |
| `SettingsForm.cs` | 设置窗口：显示器列表 / 目录选择 / 图片×显示器×时长表格 / 预览 / 按钮 |
| `WallpaperScheduler.cs` | 轮换核心：按显示器分组，各屏独立游标与计时，触发应用 |
| `MonitorEnumerator.cs` | 枚举显示器（主屏优先 + 位置排序，稳定设备名） |
| `ConfigStore.cs` | 配置 JSON 读写（UTF-8 无 BOM、中文路径安全、旧版 int 索引自动迁移） |
| `NativeMethods.cs` | P/Invoke 与壁纸落盘：枚举、拼主壁纸、WallpaperStyle、Device 键 Transitions |

## 备注

- 早期浏览器 + Python 版本已弃用（计时依赖浏览器标签页、图片走 base64/HTTP、
  每次切换写临时文件），仅留作对照。
- 程序会在 exe 同目录写 `MultiWallpaper.log`（纯文本日志，排查问题很有用）。
