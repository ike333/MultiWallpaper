using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiWallpaper;

/// <summary>
/// 设置窗口：
///  1. 顶部：显示器列表（编号、主/副、分辨率）
///  2. 中部：图片目录选择
///  3. 下部：图片配置表格（每行：文件名 | 各显示器勾选 | 显示时长）+ 右侧图片预览
///  4. 底部：保存配置 / 立即应用 / 刷新显示器
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly WallpaperScheduler _scheduler;

    private Label _lblMonitors = null!;
    private ListBox _monitorsList = null!;
    private TextBox _dirBox = null!;
    private Button _pickDirBtn = null!;
    private DataGridView _grid = null!;
    private PictureBox _preview = null!;
    private Label _previewFile = null!;
    private Label _lblStatus = null!;
    private Button _btnApply = null!;
    private Button _btnSave = null!;
    private FlowLayoutPanel _selectBar = null!;
    private List<CheckBox> _selectAllBoxes = new();

    private List<Monitor> _monitors = new();
    private List<ImageEntry> _images = new();
    private string _directory = "";

    public SettingsForm(WallpaperScheduler scheduler)
    {
        _scheduler = scheduler;
        BuildUi();
        LoadConfigIntoUi();
    }

    private void BuildUi()
    {
        Text = "多显示器壁纸轮换 — 设置";
        Width = 860;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 600);

        // ---------- 顶部：显示器区 ----------
        _lblMonitors = new Label
        {
            Text = "检测到的显示器：",
            AutoSize = true,
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(8, 2, 0, 0),
        };

        _monitorsList = new ListBox
        {
            Height = 64,
            Dock = DockStyle.Top,
            Padding = new Padding(6),
        };

        // ---------- 目录区 ----------
        var dirPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(8, 4, 8, 4),
            FlowDirection = FlowDirection.LeftToRight,
        };
        var lblDir = new Label
        {
            Text = "图片目录：",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0),
        };
        _dirBox = new TextBox
        {
            Width = 440,
            ReadOnly = true,
            Margin = new Padding(0, 5, 8, 0),
        };
        _pickDirBtn = new Button
        {
            Text = "选择…",
            Width = 80,
            Height = 24,
            Margin = new Padding(0, 5, 0, 0),
        };
        _pickDirBtn.Click += OnPickDir;
        dirPanel.Controls.Add(lblDir);
        dirPanel.Controls.Add(_dirBox);
        dirPanel.Controls.Add(_pickDirBtn);

        // ---------- 图片配置区（左侧表格 + 右侧预览） ----------
        var bodyPanel = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 620,
            SplitterWidth = 6,
            Panel1MinSize = 380,
            Panel2MinSize = 120,
        };

        // 左侧：表格上方放一行「全选」复选框，与显示器列对齐
        var selectBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(6, 4, 6, 2),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(240, 240, 248),
        };
        _selectBar = selectBar;

        var selLabel = new Label
        {
            Text = "图片文件",
            AutoSize = true,
            Width = 180,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 5, 0, 0),
        };
        selectBar.Controls.Add(selLabel);
        AddSelectAllBoxes();

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ColumnHeadersHeight = 32,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        };
        _grid.RowTemplate.Height = 28;
        // 列 0：文件名
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "图片文件",
            Width = 180,
            ReadOnly = true,
            FillWeight = 100,
        });
        AddMonitorColumns();

        // 左侧：表格 + 全选栏
        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_grid);
        leftPanel.Controls.Add(selectBar);

        // 右侧：预览面板
        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(30, 30, 30),
            BorderStyle = BorderStyle.FixedSingle,
        };
        _previewFile = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f),
            Text = "未选择图片",
        };
        bodyPanel.Panel1.Controls.Add(leftPanel);
        bodyPanel.Panel2.Controls.Add(_previewFile);
        bodyPanel.Panel2.Controls.Add(_preview);

        // ---------- 底部 ----------
        _lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0),
            Text = "就绪",
        };

        var btnBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
            FlowDirection = FlowDirection.LeftToRight,
        };
        _btnApply = new Button { Text = "▶ 立即应用", Width = 130, Height = 30, Margin = new Padding(0, 0, 10, 0) };
        _btnApply.Click += OnApplyNow;
        _btnSave = new Button { Text = "💾 保存配置", Width = 130, Height = 30, Margin = new Padding(0, 0, 10, 0) };
        _btnSave.Click += OnSave;
        var btnRefresh = new Button { Text = "🔄 刷新显示器", Width = 130, Height = 30 };
        btnRefresh.Click += OnRefreshMonitors;
        btnBar.Controls.Add(_btnApply);
        btnBar.Controls.Add(_btnSave);
        btnBar.Controls.Add(btnRefresh);

        Controls.Add(bodyPanel);
        Controls.Add(btnBar);
        Controls.Add(_lblStatus);
        Controls.Add(dirPanel);
        Controls.Add(_monitorsList);
        Controls.Add(_lblMonitors);

        _grid.CellValidating += Grid_CellValidating;
        _grid.SelectionChanged += (_, _) => UpdatePreview();
    }

    /// <summary>按当前枚举的显示器，重建勾选列（列 1..N）+ 时长列，并让各显示器列头可点击=全选该列。</summary>
    private void AddMonitorColumns()
    {
        while (_grid.Columns.Count > 1)
            _grid.Columns.RemoveAt(1);

        // 窄列头：用简短标题（"D1""D2"…），鼠标悬停可看完整信息
        // 点击该列头 = 全选/反选该显示器列所有图片（见 HeaderCellMouseClick）
        foreach (var m in _monitors)
        {
            var col = new DataGridViewCheckBoxColumn
            {
                Name = $"mon_{m.Index}",
                HeaderText = $"D{m.Index + 1}",
                ToolTipText = $"{m.Name}{(m.IsPrimary ? "（主）" : "")} — 点击列头全选/反选该显示器",
                Width = 46,
                TrueValue = 1,
                FalseValue = 0,
            };
            _grid.Columns.Add(col);
        }
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "minutes",
            HeaderText = "时长(分)",
            Width = 70,
        });

        // 文件名列自适应剩余宽度，其他列固定宽度
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Name?.StartsWith("mon_") == true)
            {
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                col.Width = 46;
            }
            else if (col.Name == "minutes")
            {
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                col.Width = 70;
            }
        }
    }

    /// <summary>在表格上方为每个显示器创建一个可见的「全选」复选框，点击后把该显示器的列所有图片全部勾选。</summary>
    private void AddSelectAllBoxes()
    {
        _selectAllBoxes.Clear();
        _selectBar.SuspendLayout();
        // 清除旧的复选框（保留第一个"图片文件"标签）
        for (int i = _selectBar.Controls.Count - 1; i >= 1; i--)
        {
            var c = _selectBar.Controls[i];
            _selectBar.Controls.RemoveAt(i);
            c.Dispose();
        }
        foreach (var m in _monitors)
        {
            var cb = new CheckBox
            {
                Text = $"全选 {m.Name}",
                AutoSize = true,
                Margin = new Padding(10, 5, 4, 0),
                Tag = m.Index,
            };
            cb.CheckedChanged += OnSelectAllChecked;
            _selectBar.Controls.Add(cb);
            _selectAllBoxes.Add(cb);
        }
        // 末尾"时长"占位
        _selectBar.Controls.Add(new Label
        {
            Text = "时长(分)",
            AutoSize = true,
            Width = 70,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(10, 5, 0, 0),
        });
        _selectBar.ResumeLayout();
    }

    private void OnSelectAllChecked(object? sender, EventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not int mi) return;
        // 找到对应显示器列（按 Index 匹配列名）
        int colIdx = -1;
        for (int i = 0; i < _monitors.Count; i++)
        {
            if (_monitors[i].Index == mi) { colIdx = i + 1; break; }
        }
        if (colIdx < 0) return;

        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            ((DataGridViewCheckBoxCell)_grid.Rows[i].Cells[colIdx]).Value = cb.Checked;
        }
        _lblStatus.Text = cb.Checked
            ? $"已全选：所有图片已分配到 {new Monitor { Name = $"显示器 {mi + 1}" }.Name}"
            : $"已取消：显示器 {mi + 1} 上的所有图片分配";
        // 同步其它复选框状态（避免重复触发）
        SyncSelectAllStates();
    }

    private void SyncSelectAllStates()
    {
        foreach (var cb in _selectAllBoxes)
        {
            if (cb.Tag is not int mi) continue;
            int colIdx = -1;
            for (int i = 0; i < _monitors.Count; i++)
            {
                if (_monitors[i].Index == mi) { colIdx = i + 1; break; }
            }
            if (colIdx < 0) { cb.Checked = false; continue; }
            bool all = false;
            if (_grid.Rows.Count > 0)
            {
                all = true;
                for (int i = 0; i < _grid.Rows.Count; i++)
                {
                    if (!IsCellChecked(_grid.Rows[i].Cells[colIdx].Value)) { all = false; break; }
                }
            }
            cb.Checked = all;
        }
    }

    private void LoadConfigIntoUi()
    {
        _monitors = MonitorEnumerator.Enumerate();
        _monitorsList.Items.Clear();
        foreach (var m in _monitors)
            _monitorsList.Items.Add(m.ToString());

        var saved = ConfigStore.Load();
        if (saved != null)
        {
            _directory = saved.Directory;
            _dirBox.Text = _directory;
            _images = saved.Images;
            if (_images.Count == 0 && !string.IsNullOrWhiteSpace(_directory))
                _images = ConfigStore.ScanImages(_directory);
        }
        AddSelectAllBoxes();
        AddMonitorColumns();
        PopulateGrid();
        if (_grid.Rows.Count > 0)
            _grid.Rows[0].Selected = true;
    }

    private void PopulateGrid()
    {
        int rowCount = _images.Count;
        _grid.Rows.Clear();
        for (int i = 0; i < rowCount; i++)
        {
            var img = _images[i];
            int row = _grid.Rows.Add(Path.GetFileName(img.File));
            var r = _grid.Rows[row];
            r.Tag = img;
            ((DataGridViewTextBoxCell)r.Cells[0]).Value = Path.GetFileName(img.File);
            for (int mi = 0; mi < _monitors.Count; mi++)
            {
                var chk = (DataGridViewCheckBoxCell)r.Cells[mi + 1];
                chk.Value = img.Monitors.Contains(_monitors[mi].Index);
            }
            ((DataGridViewTextBoxCell)r.Cells[_grid.Columns.Count - 1]).Value = img.Minutes.ToString();
        }
        if (_grid.Rows.Count > 0)
        {
            _grid.Rows[0].Selected = true;
            UpdatePreview();
        }
    }

    private void Grid_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.ColumnIndex == _grid.Columns.Count - 1)
        {
            string txt = e.FormattedValue?.ToString() ?? "";
            if (!int.TryParse(txt, out int v) || v <= 0)
            {
                e.Cancel = true;
                _lblStatus.Text = "显示时长必须是大于 0 的整数（分钟）";
            }
        }
    }

    /// <summary>把当前选中行对应的图片显示到右侧预览。</summary>
    private void UpdatePreview()
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        _previewFile.Text = "未选择图片";

        if (_grid.SelectedRows.Count == 0) return;
        int rowIdx = _grid.SelectedRows[0].Index;
        if (rowIdx < 0 || rowIdx >= _images.Count) return;

        var img = _images[rowIdx];
        _previewFile.Text = Path.GetFileName(img.File);
        if (File.Exists(img.File))
        {
            try
            {
                _preview.Image = LoadThumbnail(img.File, 360, 240);
            }
            catch
            {
                _previewFile.Text = "预览加载失败：" + Path.GetFileName(img.File);
            }
        }
    }

    /// <summary>加载图片，生成缩略图避免大内存占用。</summary>
    private static Bitmap LoadThumbnail(string path, int maxW, int maxH)
    {
        using var full = new Bitmap(path);
        int w = Math.Min(full.Width, maxW);
        int h = Math.Min(full.Height, maxH);
        float scale = Math.Min((float)w / full.Width, (float)h / full.Height);
        int tw = (int)(full.Width * scale);
        int th = (int)(full.Height * scale);

        var bmp = new Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(full, 0, 0, tw, th);
        return bmp;
    }

    private void OnPickDir(object? sender, EventArgs e)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "选择包含图片的目录",
            UseDescriptionForTitle = true,
            SelectedPath = _directory,
        };
        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            _directory = fbd.SelectedPath;
            _dirBox.Text = _directory;
            _images = ConfigStore.ScanImages(_directory);
            AddSelectAllBoxes();
            AddMonitorColumns();
            PopulateGrid();
            _lblStatus.Text = $"已选择目录：{_directory}，共 {_images.Count} 张图片";
        }
    }

    /// <summary>刷新显示器：重新枚举。
    /// 勾选状态在保存配置时已读回 _images（位置索引），重新枚举后若显示器集合不变则沿用，
    /// 若变化（接/拔屏）则按位置对齐到新枚举。</summary>
    private void OnRefreshMonitors(object? sender, EventArgs e)
    {
        // 1. 先把当前网格勾选读回 _images（位置索引）
        ReadGridToImages();

        // 2. 重新枚举
        var newMonitors = MonitorEnumerator.Enumerate();

        // 3. 无论数量是否变化，都按 Device 名把旧的位置索引重新对齐到新枚举的 Index。
        //    这样即使刷新后枚举顺序变化（同一台物理屏拿到不同 Index），勾选也不会错位。
        {
            // 旧顺序里每个 Index 对应的 Device 名
            var oldIndexToDevice = new Dictionary<int, string>();
            for (int i = 0; i < _monitors.Count; i++)
                oldIndexToDevice[_monitors[i].Index] = _monitors[i].Device;
            // 新顺序里每个 Device 对应的 Index
            var deviceToNewIndex = new Dictionary<string, int>();
            for (int i = 0; i < newMonitors.Count; i++)
                deviceToNewIndex[newMonitors[i].Device] = newMonitors[i].Index;

            foreach (var img in _images)
            {
                var newIdx = new List<int>();
                foreach (var oldIdx in img.Monitors)
                {
                    if (oldIndexToDevice.TryGetValue(oldIdx, out var dev) &&
                        deviceToNewIndex.TryGetValue(dev, out var newIdxVal))
                        newIdx.Add(newIdxVal);
                }
                img.Monitors = newIdx;
            }
        }

        _monitors = newMonitors;
        _monitorsList.Items.Clear();
        foreach (var m in _monitors)
            _monitorsList.Items.Add(m.ToString());

        AddSelectAllBoxes();
        AddMonitorColumns();
        PopulateGrid();
        _lblStatus.Text = $"刷新显示器：共 {_monitors.Count} 个";
    }

    /// <summary>把网格里的勾选/时长写回 _images。
    /// 勾选判定兼容三种形态：bool（程序写入/全选按钮）、int 1/0（列 TrueValue/FalseValue 为 1/0 时用户手点提交）、字符串 "True"/"False"。</summary>
    private void ReadGridToImages()
    {
        int minutesCol = _grid.Columns.Count - 1;
        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            if (_images.Count <= i)
                _images.Add(new ImageEntry { File = _grid.Rows[i].Cells[0].Value?.ToString() ?? "" });
            var img = _images[i];
            img.Monitors = new List<int>();
            for (int mi = 0; mi < _monitors.Count; mi++)
            {
                object? v = _grid.Rows[i].Cells[mi + 1].Value;
                if (IsCellChecked(v)) img.Monitors.Add(_monitors[mi].Index);
            }
            string mt = _grid.Rows[i].Cells[minutesCol].Value?.ToString() ?? "30";
            img.Minutes = int.TryParse(mt, out int m) ? m : 30;
        }
    }

    /// <summary>判断复选框单元格是否处于勾选状态，兼容 bool / int(1=勾) / 字符串 "True" 三种值形态。
    /// （列 TrueValue=1、FalseValue=0 时，用户手点提交的是 int 1；全选按钮写入的是 bool true；
    ///  旧代码只认 bool 和 "True" 字符串，把 int 1 当成未勾选，导致手点勾选的分配被丢失。）</summary>
    private static bool IsCellChecked(object? v)
    {
        switch (v)
        {
            case null:
            case DBNull:
                return false;
            case bool b:
                return b;
            case int n:
                return n != 0;
            case long l:
                return l != 0;
            case string s:
                return s.Equals("True", StringComparison.OrdinalIgnoreCase) || s == "1";
            default:
                return v.ToString() == "True" || v.ToString() == "1";
        }
    }

    private void OnApplyNow(object? sender, EventArgs e)
    {
        ReadGridToImages();
        var cfg = new Config { Directory = _directory, Images = _images };
        ConfigStore.Save(cfg);
        _scheduler.Start(cfg);
        _lblStatus.Text = "已应用当前图片到各显示器，后台轮换已启动";
    }

    private void OnSave(object? sender, EventArgs e)
    {
        ReadGridToImages();
        var cfg = new Config { Directory = _directory, Images = _images };
        ConfigStore.Save(cfg);
        _lblStatus.Text = $"配置已保存到 {ConfigStore.FilePath}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _preview.Image?.Dispose();
        base.OnFormClosing(e);
    }
}
