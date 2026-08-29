using CskinNative.Services;

namespace CskinNative;

internal sealed class GameSettingsForm : Form
{
    private readonly TextBox _pathInput = new();
    private readonly TextBox _engineInput = new();
    private readonly Label _detectStatus = new();

    public string? SelectedDirectory { get; private set; }
    public string? SelectedEngineExecutable { get; private set; }

    public GameSettingsForm(string? currentDirectory, string? currentEngineExecutable)
    {
        Text = "游戏设置";
        ClientSize = new Size(640, 360);
        MinimumSize = new Size(640, 360);
        MaximumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Palette.Canvas;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 9F);
        Icon = LoadIcon();
        BuildLayout(currentDirectory, currentEngineExecutable);
    }

    private void BuildLayout(string? currentDirectory, string? currentEngineExecutable)
    {
        var surface = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(26), BackColor = Palette.Surface };
        Controls.Add(surface);

        var title = new Label { Text = "游戏设置", AutoSize = true, Location = new Point(26, 22), Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold), ForeColor = Palette.Text };
        var subtitle = new Label { Text = "安装器已包含本地引擎和 Git。游戏目录支持自动识别。", AutoSize = true, Location = new Point(28, 56), ForeColor = Palette.Muted };
        surface.Controls.Add(title);
        surface.Controls.Add(subtitle);

        _pathInput.Location = new Point(28, 103);
        _pathInput.Size = new Size(468, 35);
        _pathInput.Font = new Font("Segoe UI", 10F);
        _pathInput.BorderStyle = BorderStyle.FixedSingle;
        _pathInput.PlaceholderText = @"例如：C:\Riot Games\League of Legends\Game 或 wegameapps\英雄联盟\Game";
        _pathInput.Text = currentDirectory ?? "";
        _pathInput.AccessibleName = "英雄联盟游戏目录";
        surface.Controls.Add(_pathInput);

        var browse = new RoundedButton("浏览…", Palette.SurfaceSoft) { Location = new Point(507, 103), Size = new Size(82, 35), AccessibleName = "浏览游戏目录" };
        browse.Click += (_, _) => BrowseDirectory();
        surface.Controls.Add(browse);

        var engineLabel = new Label { Text = "替代引擎（可选）", AutoSize = true, Location = new Point(28, 153), ForeColor = Palette.Text };
        surface.Controls.Add(engineLabel);
        _engineInput.Location = new Point(28, 176);
        _engineInput.Size = new Size(468, 35);
        _engineInput.Font = new Font("Segoe UI", 10F);
        _engineInput.BorderStyle = BorderStyle.FixedSingle;
        _engineInput.PlaceholderText = @"留空，使用安装器提供的 Cskin.exe";
        _engineInput.Text = currentEngineExecutable ?? "";
        _engineInput.AccessibleName = "本地 Cskin 引擎";
        surface.Controls.Add(_engineInput);

        var browseEngine = new RoundedButton("浏览…", Palette.SurfaceSoft) { Location = new Point(507, 176), Size = new Size(82, 35), AccessibleName = "浏览本地引擎" };
        browseEngine.Click += (_, _) => BrowseEngine();
        surface.Controls.Add(browseEngine);

        var detect = new RoundedButton("自动识别", Palette.AccentSoft) { Location = new Point(28, 226), Size = new Size(104, 34), AccessibleName = "自动识别游戏目录" };
        detect.Click += (_, _) => DetectDirectory();
        surface.Controls.Add(detect);

        var clear = new RoundedButton("恢复自动识别", Palette.SurfaceSoft) { Location = new Point(142, 226), Size = new Size(116, 34), AccessibleName = "清除手动游戏目录" };
        clear.Click += (_, _) => { _pathInput.Clear(); SetDetectStatus("已清空手动路径，将在启动时自动识别", Palette.Muted); };
        surface.Controls.Add(clear);

        _detectStatus.AutoSize = false;
        _detectStatus.Location = new Point(274, 226);
        _detectStatus.Size = new Size(315, 34);
        _detectStatus.TextAlign = ContentAlignment.MiddleLeft;
        _detectStatus.ForeColor = Palette.Muted;
        _detectStatus.AutoEllipsis = true;
        surface.Controls.Add(_detectStatus);
        SetDetectStatus(currentDirectory is null ? "当前：自动识别" : "当前：使用手动路径", Palette.Muted);

        var cancel = new RoundedButton("取消", Palette.SurfaceSoft) { Location = new Point(402, 287), Size = new Size(86, 35) };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        var save = new RoundedButton("保存设置", Palette.Accent) { Location = new Point(503, 287), Size = new Size(86, 35) };
        save.Click += (_, _) => { if (SaveAndClose()) DialogResult = DialogResult.OK; };
        surface.Controls.Add(cancel);
        surface.Controls.Add(save);
    }

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 League of Legends.exe 的 Game 文件夹，也可以选择英雄联盟安装根目录。",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_pathInput.Text.Trim()) ? _pathInput.Text.Trim() : ""
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pathInput.Text = dialog.SelectedPath;
            SetDetectStatus("已选择目录，点击保存后生效", Palette.Success);
        }
    }

    private void BrowseEngine()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择替代 Cskin 引擎",
            Filter = "Cskin 引擎 (Cskin.exe)|Cskin.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
            FileName = File.Exists(_engineInput.Text.Trim()) ? _engineInput.Text.Trim() : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _engineInput.Text = dialog.FileName;
        SetDetectStatus("已选择本地引擎，点击保存后生效", Palette.Success);
    }

    private void DetectDirectory()
    {
        var path = EngineClient.TryDetectLeagueGameDirectory();
        if (path is null)
        {
            SetDetectStatus("未找到游戏，请先启动客户端或手动浏览", Palette.Error);
            return;
        }
        _pathInput.Text = path;
        SetDetectStatus("已识别：" + path, Palette.Success);
    }

    private bool SaveAndClose()
    {
        var value = _pathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedDirectory = null;
        }
        else
        {
            var normalized = EngineClient.NormalizeGameDirectory(value);
            if (normalized is null)
            {
                MessageBox.Show(this, "所选目录中没有找到 League of Legends.exe。请选择 Game 文件夹或英雄联盟安装根目录。", "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            SelectedDirectory = normalized;
        }

        var engine = _engineInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(engine))
        {
            SelectedEngineExecutable = null;
            return true;
        }

        SelectedEngineExecutable = EngineClient.NormalizeEngineExecutable(engine);
        if (SelectedEngineExecutable is null)
        {
            MessageBox.Show(this, "请选择存在的 Cskin.exe。留空会使用安装器提供的引擎。", "引擎无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private void SetDetectStatus(string text, Color color)
    {
        _detectStatus.Text = text;
        _detectStatus.ForeColor = color;
    }

    private static Icon? LoadIcon()
    {
        try
        {
            var path = AppPaths.Asset("cskin.ico");
            return File.Exists(path) ? new Icon(path) : null;
        }
        catch { return null; }
    }
}
