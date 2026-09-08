using System.Diagnostics;
using CskinNative.Services;

namespace CskinNative;

internal sealed class UpdateRequiredForm : Form
{
    private readonly UpdateClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _currentVersion;
    private UpdateManifest _manifest;
    private readonly Label _status = new();
    private readonly Button _recheckButton = new();

    public UpdateRequiredForm(UpdateManifest manifest, string currentVersion)
    {
        _manifest = manifest;
        _currentVersion = currentVersion;
        Text = "PortableCskin 需要更新";
        ClientSize = new Size(560, 390);
        MinimumSize = new Size(560, 390);
        MaximumSize = new Size(560, 390);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Palette.Canvas;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 9F);
        Icon = LoadIcon();
        BuildLayout();
        FormClosed += (_, _) =>
        {
            _lifetime.Cancel();
            _client.Dispose();
        };
    }

    private void BuildLayout()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(34), BackColor = Palette.Surface };
        Controls.Add(panel);

        var title = new Label { Text = _manifest.Title, AutoSize = false, Location = new Point(34, 28), Size = new Size(490, 34), Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold), ForeColor = Palette.Text };
        panel.Controls.Add(title);

        var version = new Label { Text = $"当前版本：{_currentVersion}    最新版本：{_manifest.LatestVersion}", AutoSize = true, Location = new Point(36, 76), ForeColor = Palette.AccentDark, Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold) };
        panel.Controls.Add(version);

        var message = new Label { Text = _manifest.Message, AutoSize = false, Location = new Point(36, 112), Size = new Size(480, 48), ForeColor = Palette.Muted };
        panel.Controls.Add(message);

        var link = new LinkLabel { Text = "打开蓝奏云下载页面", AutoSize = true, Location = new Point(36, 177), LinkColor = Palette.AccentDark, ActiveLinkColor = Palette.Accent };
        link.Click += (_, _) => OpenUrl(_manifest.DownloadUrl);
        panel.Controls.Add(link);

        var copyUrl = CreateButton("复制下载链接", new Point(230, 170), 120, () => CopyText(_manifest.DownloadUrl, "下载链接已复制"));
        panel.Controls.Add(copyUrl);

        var password = new Label { Text = $"提取码：{_manifest.DownloadPassword}", AutoSize = true, Location = new Point(36, 218), ForeColor = Palette.Text };
        panel.Controls.Add(password);
        panel.Controls.Add(CreateButton("复制提取码", new Point(230, 211), 120, () => CopyText(_manifest.DownloadPassword, "提取码已复制")));

        var group = new Label { Text = $"QQ 群备用下载：{_manifest.GroupNumber}", AutoSize = true, Location = new Point(36, 259), ForeColor = Palette.Text };
        panel.Controls.Add(group);
        panel.Controls.Add(CreateButton("复制群号", new Point(230, 252), 120, () => CopyText(_manifest.GroupNumber, "QQ群号已复制")));

        _recheckButton.Text = "重新检查";
        _recheckButton.Location = new Point(36, 304);
        _recheckButton.Size = new Size(112, 38);
        _recheckButton.BackColor = Palette.Accent;
        _recheckButton.ForeColor = Color.White;
        _recheckButton.FlatStyle = FlatStyle.Flat;
        _recheckButton.FlatAppearance.BorderSize = 0;
        _recheckButton.Click += async (_, _) => await RecheckAsync();
        panel.Controls.Add(_recheckButton);

        var exit = CreateButton("退出", new Point(162, 304), 84, Close);
        panel.Controls.Add(exit);

        _status.AutoSize = false;
        _status.Location = new Point(265, 304);
        _status.Size = new Size(250, 42);
        _status.ForeColor = Palette.Muted;
        panel.Controls.Add(_status);
    }

    private async Task RecheckAsync()
    {
        if (!_recheckButton.Enabled) return;
        _recheckButton.Enabled = false;
        _status.ForeColor = Palette.Muted;
        _status.Text = "正在检查版本…";
        try
        {
            var result = await _client.CheckAsync(UpdatePolicy.CurrentVersionText, _lifetime.Token);
            if (result.Status == UpdateGateStatus.Allowed)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            if (result.Manifest is not null && result.Status == UpdateGateStatus.Required)
                _manifest = result.Manifest;
            _status.ForeColor = result.Status == UpdateGateStatus.Unavailable ? Palette.Error : Palette.Muted;
            _status.Text = result.Decision.Message;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _status.ForeColor = Palette.Error;
            _status.Text = "检查失败，请稍后重试";
            AppLog.Error("强制更新重新检查失败", ex);
        }
        finally
        {
            if (!IsDisposed) _recheckButton.Enabled = true;
        }
    }

    private static Button CreateButton(string text, Point location, int width, Action action)
    {
        var button = new Button { Text = text, Location = location, Size = new Size(width, 38), FlatStyle = FlatStyle.Flat, BackColor = Palette.SurfaceSoft, ForeColor = Palette.Text };
        button.FlatAppearance.BorderColor = Palette.Border;
        button.Click += (_, _) => action();
        return button;
    }

    private void CopyText(string text, string success)
    {
        try
        {
            Clipboard.SetText(text);
            _status.ForeColor = Palette.Success;
            _status.Text = success;
        }
        catch (Exception ex)
        {
            _status.ForeColor = Palette.Error;
            _status.Text = "复制失败，请手动复制";
            AppLog.Error("更新页面复制内容失败", ex);
        }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            _status.ForeColor = Palette.Error;
            _status.Text = "无法打开下载页面，请复制链接";
            AppLog.Error("打开更新下载链接失败", ex);
        }
    }

    private Icon? LoadIcon()
    {
        var path = AppPaths.FindBundledFile(Path.Combine("Assets", "cskin.ico"));
        return path is not null ? new Icon(path) : null;
    }
}
