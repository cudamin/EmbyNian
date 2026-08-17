using System.ComponentModel;
using System.Text;

namespace EmbyMpvClient;

internal sealed class ConfigEditorView : UserControl
{
    private bool _loaded;
    private readonly bool _inputFile;
    private readonly Func<string> _getPath;
    private readonly Action<string> _setStatus;
    private readonly BindingList<ConfigEntry> _entries = [];
    private readonly DataGridView _grid;
    private Encoding _encoding = new UTF8Encoding(false);

    public ConfigEditorView(string fileName, bool inputFile, Func<string> getPath, Action<string> setStatus)
    {
        _inputFile = inputFile;
        _getPath = getPath;
        _setStatus = setStatus;
        BackColor = UiTheme.Background;
        Padding = new Padding(28, 8, 28, 24);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, BackColor = UiTheme.Background };
        var filter = UiTheme.CreateTextBox(inputFile ? "筛选按键、命令或说明" : "筛选选项、值或说明");
        filter.Width = 320;
        filter.Height = 36;
        filter.Margin = new Padding(0, 1, 12, 0);
        var reload = UiTheme.CreateButton("重新载入");
        var save = UiTheme.CreateButton("保存并备份", true);
        reload.Click += (_, _) => LoadDocument();
        save.Click += (_, _) => SaveDocument();
        toolbar.Controls.AddRange([filter, reload, save]);

        _grid = CreateGrid(inputFile);
        filter.TextChanged += (_, _) => ApplyFilter(filter.Text);

        var path = UiTheme.CreateLabel(fileName + "  ·  修改会保留原文件结构，并在保存前自动备份");
        path.Dock = DockStyle.Top;
        path.Height = 34;

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(path);
    }

    public void LoadDocument()
    {
        var path = _getPath();
        try
        {
            var result = ConfigDocument.Load(path, _inputFile);
            _entries.RaiseListChangedEvents = false;
            _entries.Clear();
            foreach (var entry in result.Entries.Where(entry => entry.IsSetting)) _entries.Add(entry);
            _entries.RaiseListChangedEvents = true;
            _entries.ResetBindings();
            _encoding = result.Encoding;
            _setStatus($"已载入 {Path.GetFileName(path)}，共 {_entries.Count} 个可编辑项");
        }
        catch (Exception ex) { _setStatus($"无法载入 {path}：{ex.Message}"); }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !_loaded) { _loaded = true; LoadDocument(); }
    }

    private void SaveDocument()
    {
        var path = _getPath();
        try
        {
            _grid.EndEdit();
            var original = ConfigDocument.Load(path, _inputFile);
            var settings = original.Entries.Where(entry => entry.IsSetting).ToList();
            for (var i = 0; i < Math.Min(settings.Count, _entries.Count); i++)
            {
                settings[i].Enabled = _entries[i].Enabled;
                settings[i].Value = _entries[i].Value;
                settings[i].Comment = _entries[i].Comment;
            }
            ConfigDocument.Save(path, original.Entries, _encoding, _inputFile);
            _setStatus($"已保存 {path}，原文件已自动备份");
            MessageBox.Show(this, "配置已保存，备份位于配置目录下的 EmbyMpvClient_Backups 文件夹。", "保存完成");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private DataGridView CreateGrid(bool inputFile)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            DataSource = _entries,
            BackgroundColor = UiTheme.Surface,
            ForeColor = UiTheme.Text,
            GridColor = UiTheme.Border,
            BorderStyle = BorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowTemplate = { Height = 38 },
            EnableHeadersVisualStyles = false
        };
        grid.DefaultCellStyle.BackColor = UiTheme.Surface;
        grid.DefaultCellStyle.ForeColor = UiTheme.Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(42, 81, 70);
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.Text;
        grid.DefaultCellStyle.Padding = new Padding(6, 2, 6, 2);
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceRaised;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.TextMuted;
        grid.ColumnHeadersHeight = 40;
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(ConfigEntry.Enabled), HeaderText = "启用", Width = 62 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConfigEntry.Key), HeaderText = inputFile ? "按键" : "选项", Width = 210, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConfigEntry.Value), HeaderText = inputFile ? "命令" : "值", Width = inputFile ? 500 : 280 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ConfigEntry.Comment), HeaderText = "说明", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        return grid;
    }

    private void ApplyFilter(string filter)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is not ConfigEntry entry) continue;
            row.Visible = string.IsNullOrWhiteSpace(filter)
                || entry.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || entry.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || entry.Comment.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }
}
