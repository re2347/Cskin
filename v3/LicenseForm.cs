using CskinNative.Services;

namespace CskinNative;

internal sealed class LicenseForm : Form
{
    private readonly AuthorizationService _authorization = new();
    private readonly TextBox _codeInput = new();
    private readonly CheckBox _rememberCode = new();
    private readonly Label _status = new();
    private readonly Button _activateButton = new();
    private readonly CancellationTokenSource _lifetime = new();

    public LicenseForm()
    {
        Text = "PortableCskin 授权";
        ClientSize = new Size(500, 340);
        MinimumSize = new Size(500, 340);
        MaximumSize = new Size(500, 340);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Palette.Canvas;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 9F);
        Icon = LoadIcon();
        BuildLayout();
        Shown += async (_, _) => await RestoreAsync();
        FormClosed += (_, _) => _lifetime.Cancel();
    }

    private void BuildLayout()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(32), BackColor = Palette.Surface };
        Controls.Add(panel);

        var title = new Label { Text = "激活 PortableCskin", AutoSize = true, Location = new Point(32, 28), Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold), ForeColor = Palette.Text };
        var subtitle = new Label
        {
            Text = "购买密钥途径：抖音、B站、微信、小红书：Re2347；闲鱼：Submerged，均为手动发货",
            AutoSize = false,
            Location = new Point(34, 66),
            Size = new Size(436, 32),
            ForeColor = Palette.Muted,
            AutoEllipsis = true
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);

        _codeInput.Location = new Point(32, 112);
        _codeInput.Width = 436;
        _codeInput.Height = 34;
        _codeInput.Font = new Font("Segoe UI", 11F);
        _codeInput.BorderStyle = BorderStyle.FixedSingle;
        _codeInput.PlaceholderText = "XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX";
        _codeInput.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) _ = ActivateAsync(); };
        panel.Controls.Add(_codeInput);

        _rememberCode.Text = "记住密钥（版本更新或重装后可自动恢复）";
        _rememberCode.AutoSize = true;
        _rememberCode.Location = new Point(32, 151);
        _rememberCode.ForeColor = Palette.Muted;
        _rememberCode.BackColor = Color.Transparent;
        panel.Controls.Add(_rememberCode);

        _activateButton.Text = "激活并继续";
        _activateButton.Location = new Point(32, 185);
        _activateButton.Size = new Size(132, 38);
        _activateButton.BackColor = Palette.Accent;
        _activateButton.ForeColor = Color.White;
        _activateButton.FlatStyle = FlatStyle.Flat;
        _activateButton.FlatAppearance.BorderSize = 0;
        _activateButton.Click += async (_, _) => await ActivateAsync();
        panel.Controls.Add(_activateButton);

        var cancel = new Button { Text = "退出", Location = new Point(178, 185), Size = new Size(84, 38), FlatStyle = FlatStyle.Flat, BackColor = Palette.SurfaceSoft, ForeColor = Palette.Text };
        cancel.FlatAppearance.BorderColor = Palette.Border;
        cancel.Click += (_, _) => Close();
        panel.Controls.Add(cancel);

        _status.AutoSize = false;
        _status.Location = new Point(32, 242);
        _status.Size = new Size(436, 52);
        _status.ForeColor = Palette.Muted;
        panel.Controls.Add(_status);
    }

    private async Task RestoreAsync()
    {
        var remembered = _authorization.LoadRememberedCode();
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            _codeInput.Text = remembered;
            _rememberCode.Checked = true;
        }

        if (string.IsNullOrWhiteSpace(remembered))
        {
            _authorization.ClearCurrentLease();
            SetBusy(false, "请输入激活码；勾选“记住密钥”后下次可自动恢复");
            return;
        }

        SetBusy(true, "正在检查本地授权…");
        var result = await _authorization.RestoreAsync(_lifetime.Token);
        if (result.Allowed)
        {
            _status.Text = result.Message;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        // If the local lease was removed by an uninstall/reinstall, reuse the
        // protected key once. Do not do this for network errors, which should
        // use the existing offline grace period instead.
        if (!result.Allowed && !string.IsNullOrWhiteSpace(remembered)
            && (result.ErrorCode is "NO_LOCAL_LEASE" or "DEVICE_CHANGED" or "INVALID_SIGNATURE" or "INVALID_LEASE" or "DEVICE_KEY_CHANGED"
                || result.Message.StartsWith("尚未激活", StringComparison.Ordinal)
                || result.Message.StartsWith("设备信息已变化", StringComparison.Ordinal)))
        {
            var restored = await _authorization.ActivateAsync(
                remembered,
                rememberCode: true,
                allowLegacyDeviceIdFallback: true,
                cancellationToken: _lifetime.Token);
            if (restored.Allowed)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            result = restored;
        }
        SetBusy(false, result.Message);
    }

    private async Task ActivateAsync()
    {
        if (!_activateButton.Enabled) return;
        if (!_rememberCode.Checked) _authorization.ForgetRememberedCode();
        SetBusy(true, "正在连接授权服务…");
        var result = await _authorization.ActivateAsync(
            _codeInput.Text,
            rememberCode: _rememberCode.Checked,
            allowLegacyDeviceIdFallback: true,
            cancellationToken: _lifetime.Token);
        if (result.Allowed)
        {
            _status.ForeColor = Palette.Success;
            _status.Text = "激活成功，正在启动软件…";
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        SetBusy(false, result.Message);
    }

    private void SetBusy(bool busy, string message)
    {
        _activateButton.Enabled = !busy;
        _codeInput.Enabled = !busy;
        _status.ForeColor = busy ? Palette.Muted : Palette.Error;
        _status.Text = message;
    }

    private Icon? LoadIcon()
    {
        var path = AppPaths.FindBundledFile(Path.Combine("Assets", "cskin.ico"));
        return path is not null ? new Icon(path) : null;
    }
}
