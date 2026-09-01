using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using CskinNative.Services;

namespace CskinNative;

internal static class Palette
{
    public static readonly Color Canvas = Color.FromArgb(245, 246, 244);
    public static readonly Color Sidebar = Color.FromArgb(237, 240, 237);
    public static readonly Color Surface = Color.FromArgb(255, 255, 255);
    public static readonly Color SurfaceSoft = Color.FromArgb(249, 250, 248);
    public static readonly Color SurfaceRaised = Color.FromArgb(232, 236, 232);
    public static readonly Color Border = Color.FromArgb(218, 223, 218);
    public static readonly Color Text = Color.FromArgb(31, 37, 33);
    public static readonly Color Muted = Color.FromArgb(101, 111, 104);
    public static readonly Color Faint = Color.FromArgb(144, 154, 146);
    public static readonly Color Accent = Color.FromArgb(226, 103, 84);
    public static readonly Color AccentSoft = Color.FromArgb(255, 239, 235);
    public static readonly Color AccentDark = Color.FromArgb(176, 67, 52);
    public static readonly Color Error = Color.FromArgb(181, 70, 70);
    public static readonly Color Success = Color.FromArgb(43, 130, 91);
}

internal sealed record SkinGroup(Skin BaseSkin, IReadOnlyList<Skin> Chromas)
{
    public bool Contains(int skinId) => BaseSkin.Id == skinId || Chromas.Any(skin => skin.Id == skinId);
}

public sealed class MainForm : Form
{
    private const int SkinPageSize = 24;
    private static readonly StringComparer PinyinComparer = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: false);
    private readonly EngineClient _engine = new();
    private readonly AuthorizationService _authorization = new();
    private readonly AppSettings _appSettings = AppSettings.Load();
    private readonly LeagueClient _leagueClient = new();
    private readonly SkinRepository _repository = new(null);
    private readonly SelectionMemory _selectionMemory = SelectionMemory.Load();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AuthorizationHeartbeat? _authorizationHeartbeat = AuthorizationService.IsEnabled ? new AuthorizationHeartbeat() : null;
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 160 };
    // Champion select changes quickly while hovering/picking. Keep this
    // independent from the skin engine startup so the UI can sync as soon as
    // the League client is ready.
    private readonly System.Windows.Forms.Timer _clientSelectionTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _repositoryRefreshTimer = new() { Interval = 5 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer _authorizationDisplayTimer = new() { Interval = 60 * 1000 };
    private readonly ChampionListPanel _championList = new();
    private readonly FlowLayoutPanel _skinGrid = new();
    private readonly RoundedSearchBox _championSearch = new();
    private readonly Label _heroName = new();
    private readonly Label _heroMeta = new();
    private readonly Label _skinCount = new();
    private readonly Label _selectedName = new();
    private readonly Label _selectedMeta = new();
    private readonly Label _applyStatus = new();
    private readonly Label _licenseStatus = new();
    private readonly Label _pageLabel = new();
    private readonly Label _catalogHint = new();
    private readonly CoverImage _heroImage = new();
    private readonly CoverImage _previewImage = new();
    private readonly RoundedButton _applyButton;
    private readonly RoundedButton _switchLicenseButton;
    private readonly RoundedButton _exitCleanupButton;
    private readonly RoundedButton _settingsButton;
    private readonly RoundedButton _prevPageButton;
    private readonly RoundedButton _nextPageButton;
    private readonly LoadingBar _applyProgress = new();
    private readonly TableLayoutPanel _workspace = new();
    private readonly Panel _inspectorPane = new();
    private readonly Panel _catalogPane = new();
    private LocalCatalog _catalog = new();
    private List<Champion> _champions = [];
    private readonly Dictionary<int, string> _championSearchIndex = [];
    private readonly HashSet<int> _championAvatarLoading = [];
    private Champion? _selectedChampion;
    private Skin? _selectedSkin;
    private CancellationTokenSource? _skinLoadCancellation;
    private int _skinPage;
    private int _clientSelectionInFlight;
    private int _repositorySyncInFlight;
    private readonly object _repositorySyncGate = new();
    private Task<bool>? _repositorySyncTask;
    private int _applyInFlight;
    private bool _clientSelectionPollCompleted;
    private readonly HashSet<int> _autoApplyAttemptedChampions = [];
    private int _lastClientChampionId;
    private int _lastClientSkinId;
    private DateTime _lastClientSelectionUtc;
    private int _clientSelectionMisses;
    private readonly ClientSelectionStabilizer _clientSelectionStabilizer = new(requiredSamples: 2);
    private int _lastAppliedSkinId;
    private int _lastAppliedTargetSkinId;
    private int _disposed;

    public bool RequestLicenseChange { get; private set; }

    public MainForm()
    {
        AppPaths.EnsureRuntime();
        AppLog.Info($"应用启动，根目录：{AppPaths.Root}");
        _engine.SetGameDirectory(_appSettings.GameDirectory);
        _leagueClient.SetGameDirectory(_appSettings.GameDirectory);
        _engine.SetEngineExecutable(_appSettings.EngineExecutable);
        Text = "PortableCskin";
        ClientSize = new Size(1360, 820);
        MinimumSize = new Size(1040, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Palette.Canvas;
        ForeColor = Palette.Text;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = LoadIcon();

        _applyButton = new RoundedButton("应用皮肤", Palette.Accent) { Height = 46, Dock = DockStyle.Fill, Enabled = false, AccessibleName = "应用当前选中的皮肤" };
        _switchLicenseButton = new RoundedButton("切换密钥", Palette.SurfaceSoft)
        {
            Width = 92,
            Height = 34,
            Visible = AuthorizationService.IsEnabled,
            AccessibleName = "返回激活界面并输入新的密钥"
        };
        _exitCleanupButton = new RoundedButton("退出并清理", Palette.SurfaceSoft) { Width = 108, Height = 34, AccessibleName = "退出应用并清理已下载资源" };
        _settingsButton = new RoundedButton("设置", Palette.SurfaceSoft) { Width = 68, Height = 34, AccessibleName = "设置游戏目录和本地引擎" };
        _prevPageButton = new RoundedButton("上一页", Palette.SurfaceSoft) { Width = 72, Height = 30, AccessibleName = "上一页皮肤" };
        _nextPageButton = new RoundedButton("下一页", Palette.SurfaceSoft) { Width = 72, Height = 30, AccessibleName = "下一页皮肤" };
        _applyButton.TabIndex = 6;
        _prevPageButton.TabIndex = 2;
        _nextPageButton.TabIndex = 3;

        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            FilterChampions();
        };
        _clientSelectionTimer.Tick += async (_, _) => await PollClientSelectionAsync();
        _repositoryRefreshTimer.Tick += async (_, _) => await SyncRepositoryAsync();
        _authorizationDisplayTimer.Tick += (_, _) => UpdateAuthorizationStatus();
        _exitCleanupButton.Click += async (_, _) => await ExitAndCleanupAsync();
        _switchLicenseButton.Click += async (_, _) => await SwitchLicenseAsync();
        _settingsButton.Click += async (_, _) => await OpenSettingsAsync();
        BuildLayout();
        Resize += (_, _) => UpdateResponsiveLayout();
        Shown += (_, _) =>
        {
            _authorizationHeartbeat?.Start();
            UpdateAuthorizationStatus();
            _authorizationDisplayTimer.Start();
            _ = InitializeAsync();
        };
        FormClosed += (_, _) =>
        {
            _repositoryRefreshTimer.Stop();
            _clientSelectionTimer.Stop();
            _authorizationDisplayTimer.Stop();
            CancelLifetime();
            _authorizationHeartbeat?.Dispose();
        };
        if (_authorizationHeartbeat is not null)
            _authorizationHeartbeat.AuthorizationLost += AuthorizationHeartbeatLost;
    }

    private void AuthorizationHeartbeatLost(object? sender, string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            MessageBox.Show(this, message, "授权已失效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        });
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Palette.Canvas, ColumnCount = 1, RowCount = 2, Padding = new Padding(0) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);
        root.Controls.Add(BuildTopBar(), 0, 0);

        _workspace.Dock = DockStyle.Fill;
        _workspace.BackColor = Palette.Canvas;
        _workspace.Padding = new Padding(16, 0, 16, 16);
        _workspace.ColumnCount = 3;
        _workspace.RowCount = 1;
        _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 248));
        _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 344));
        root.Controls.Add(_workspace, 0, 1);

        _workspace.Controls.Add(BuildChampionPane(), 0, 0);
        _workspace.Controls.Add(BuildCatalogPane(), 1, 0);
        _workspace.Controls.Add(BuildInspectorPane(), 2, 0);
        UpdateResponsiveLayout();
    }

    private Control BuildTopBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(22, 0, 22, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AuthorizationService.IsEnabled ? 284 : 184));
        bar.Controls.Add(layout);

        var brand = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var mark = new Label { Text = "C", AutoSize = false, Size = new Size(34, 34), Location = new Point(0, 19), TextAlign = ContentAlignment.MiddleCenter, BackColor = Palette.AccentSoft, ForeColor = Palette.AccentDark, Font = new Font("Segoe UI", 16F, FontStyle.Bold) };
        mark.Region = new Region(SurfacePanel.RoundedPath(new Rectangle(0, 0, mark.Width, mark.Height), 9));
        var name = new Label { Text = "PortableCskin", AutoSize = true, Location = new Point(46, 17), ForeColor = Palette.Text, Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold) };
        var subtitle = new Label { Text = "native skin library", AutoSize = true, Location = new Point(47, 41), ForeColor = Palette.Faint, Font = new Font("Segoe UI", 8.5F) };
        brand.Controls.AddRange([mark, name, subtitle]);
        layout.Controls.Add(brand, 0, 0);

        var context = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(22, 0, 22, 0),
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        context.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        context.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        var title = LabelText("选择皮肤并应用到本地引擎", 10F, Palette.Muted, FontStyle.Regular);
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        _licenseStatus.AutoSize = false;
        _licenseStatus.Dock = DockStyle.Fill;
        _licenseStatus.TextAlign = ContentAlignment.MiddleRight;
        _licenseStatus.ForeColor = Palette.Success;
        _licenseStatus.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular);
        _licenseStatus.AutoEllipsis = true;
        context.Controls.Add(title, 0, 0);
        context.Controls.Add(_licenseStatus, 1, 0);
        layout.Controls.Add(context, 1, 0);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 20, 0, 20),
            Margin = Padding.Empty
        };
        _exitCleanupButton.Margin = new Padding(8, 0, 0, 0);
        _switchLicenseButton.Margin = new Padding(8, 0, 0, 0);
        _settingsButton.Margin = new Padding(0, 0, 0, 0);
        actions.Controls.Add(_exitCleanupButton);
        actions.Controls.Add(_switchLicenseButton);
        actions.Controls.Add(_settingsButton);
        layout.Controls.Add(actions, 2, 0);
        return bar;
    }

    private void UpdateAuthorizationStatus()
    {
        if (!AuthorizationService.IsEnabled)
        {
            _licenseStatus.Text = "授权未启用";
            _licenseStatus.ForeColor = Palette.Faint;
            return;
        }

        var lease = new LeaseStore().Load();
        if (lease is null || lease.ExpiresAt <= 0)
        {
            _licenseStatus.Text = "授权状态未知";
            _licenseStatus.ForeColor = Palette.Error;
            return;
        }

        var remaining = lease.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= 0)
        {
            _licenseStatus.Text = "授权已到期";
            _licenseStatus.ForeColor = Palette.Error;
            return;
        }

        var days = remaining / 86400;
        var hours = remaining % 86400 / 3600;
        var minutes = remaining % 3600 / 60;
        var expiry = DateTimeOffset.FromUnixTimeSeconds(lease.ExpiresAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        _licenseStatus.Text = days > 0
            ? $"授权剩余 {days}天 {hours}小时 · {expiry} 到期"
            : $"授权剩余 {hours}小时 {minutes}分 · {expiry} 到期";
        _licenseStatus.ForeColor = remaining <= 24 * 3600 ? Palette.AccentDark : Palette.Success;
    }

    private async Task OpenSettingsAsync()
    {
        using var dialog = new GameSettingsForm(_appSettings.GameDirectory, _appSettings.EngineExecutable);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var oldPath = _appSettings.GameDirectory;
        var oldEngine = _appSettings.EngineExecutable;
        var wasReady = _engine.IsReady;
        _appSettings.GameDirectory = dialog.SelectedDirectory;
        _appSettings.EngineExecutable = dialog.SelectedEngineExecutable;
        if (!_appSettings.Save())
        {
            MessageBox.Show(this, "设置无法保存，请检查程序目录权限。", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _appSettings.GameDirectory = oldPath;
            _appSettings.EngineExecutable = oldEngine;
            return;
        }

        _engine.SetGameDirectory(_appSettings.GameDirectory);
        _leagueClient.SetGameDirectory(_appSettings.GameDirectory);
        var engineChanged = !string.Equals(oldEngine, _appSettings.EngineExecutable, StringComparison.OrdinalIgnoreCase);
        if (engineChanged) _engine.SetEngineExecutable(_appSettings.EngineExecutable);
        if (!wasReady && !engineChanged)
        {
            SetStatus(_applyStatus, _appSettings.GameDirectory is null ? "已恢复自动识别游戏目录" : "游戏目录已保存", Palette.Success);
            return;
        }

        SetStatus(_applyStatus, "正在应用设置并启动本地引擎…", Palette.Muted);
        try
        {
            var ready = await _engine.RestartForGameDirectoryAsync(_lifetime.Token);
            SetStatus(_applyStatus, ready
                ? "游戏和本地引擎设置已应用"
                : "本地引擎启动失败，请检查游戏目录和引擎文件", ready ? Palette.Success : Palette.Error);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Error("设置应用失败", ex);
            SetStatus(_applyStatus, $"设置应用失败：{ex.Message}", Palette.Error);
        }
    }

    private async Task ExitAndCleanupAsync()
    {
        if (IsDisposed || _exitCleanupButton is { Enabled: false }) return;
        var answer = MessageBox.Show(
            this,
            "将退出应用并删除已下载的皮肤、仓库缓存和引擎临时文件。\n随包引擎、索引和授权信息不会删除。是否继续？",
            "退出并清理",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;

        _exitCleanupButton.Enabled = false;
        _applyButton.Enabled = false;
        _repositoryRefreshTimer.Stop();
        _clientSelectionTimer.Stop();
        CancelLifetime();
        SetStatus(_applyStatus, "正在停止引擎并清理本地资源", Palette.Muted);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while ((Volatile.Read(ref _repositorySyncInFlight) != 0 || Volatile.Read(ref _applyInFlight) != 0) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        _repository.Stop();
        _engine.Stop();
        var error = AppPaths.CleanupRuntime();
        if (error is not null)
        {
            AppLog.Error($"一键清理未完成，应用仍将退出：{error}");
            MessageBox.Show(this, $"部分资源未能删除，应用仍将退出：\n{error}\n\n详情已写入：{AppLog.FilePath}", "清理提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            AppLog.Info("一键清理完成");
        }
        var logError = AppPaths.CleanupLogs();
        if (logError is not null)
        {
            AppLog.Error($"日志文件清理未完成，应用仍将退出：{logError}");
            MessageBox.Show(this, $"日志文件未能全部删除，应用仍将退出：\n{logError}\n\n详情已写入：{AppLog.FilePath}", "日志清理提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        try { Close(); }
        catch (Exception ex)
        {
            AppLog.Error("清理后关闭窗口失败", ex);
            Application.ExitThread();
        }
    }

    private async Task SwitchLicenseAsync()
    {
        if (IsDisposed || _switchLicenseButton is { Enabled: false }) return;
        var answer = MessageBox.Show(
            this,
            "将退出当前授权并返回激活界面。已下载的皮肤和本地资源会保留，是否继续？",
            "切换密钥",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;

        _switchLicenseButton.Enabled = false;
        _exitCleanupButton.Enabled = false;
        _applyButton.Enabled = false;
        _repositoryRefreshTimer.Stop();
        _clientSelectionTimer.Stop();
        _authorizationDisplayTimer.Stop();
        CancelLifetime();
        SetStatus(_applyStatus, "正在停止引擎并切换授权…", Palette.Muted);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while ((Volatile.Read(ref _repositorySyncInFlight) != 0 || Volatile.Read(ref _applyInFlight) != 0) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        _repository.Stop();
        _engine.Stop();

        _authorization.ClearCurrentLease();
        _authorization.ForgetRememberedCode();
        RequestLicenseChange = true;
        Close();
    }

    private Control BuildChampionPane()
    {
        var pane = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(16, 16, 12, 12), BackColor = Palette.Sidebar, AccessibleName = "英雄列表" };
        // Keep the header, search field, and list in explicit rows. Docking
        // several Top/Fill controls in one parent makes their z-order affect
        // layout and can place the list over the search field.
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pane.Controls.Add(content);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        var titleStack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var title = LabelText("英雄", 13F, Palette.Text, FontStyle.Bold);
        title.Location = new Point(0, 0);
        var hint = LabelText("从名称或编号开始", 8.5F, Palette.Faint);
        hint.Location = new Point(0, 25);
        titleStack.Controls.AddRange([title, hint]);
        header.Controls.Add(titleStack, 0, 0);
        _catalogHint.Text = "0";
        _catalogHint.Dock = DockStyle.Fill;
        _catalogHint.TextAlign = ContentAlignment.TopRight;
        _catalogHint.ForeColor = Palette.Faint;
        _catalogHint.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        header.Controls.Add(_catalogHint, 1, 0);
        content.Controls.Add(header, 0, 0);

        _championSearch.Dock = DockStyle.Fill;
        _championSearch.Height = 32;
        _championSearch.Margin = new Padding(0, 6, 0, 8);
        _championSearch.BackColor = Palette.SurfaceSoft;
        _championSearch.ForeColor = Palette.Text;
        _championSearch.Font = new Font("Segoe UI", 9F);
        _championSearch.PlaceholderText = "查找英雄或编号";
        _championSearch.AccessibleName = "英雄搜索";
        _championSearch.TabIndex = 0;
        _championSearch.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        content.Controls.Add(_championSearch, 0, 1);

        _championList.Dock = DockStyle.Fill;
        _championList.Padding = new Padding(0, 2, 10, 0);
        _championList.BackColor = Palette.Sidebar;
        _championList.Resize += (_, _) => UpdateChampionRowWidths();
        _championList.ViewportChanged += (_, _) => LoadVisibleChampionImages();
        content.Controls.Add(_championList, 0, 2);
        return pane;
    }

    private Control BuildCatalogPane()
    {
        _catalogPane.Dock = DockStyle.Fill;
        _catalogPane.BackColor = Palette.Canvas;
        _catalogPane.Padding = new Padding(20, 16, 16, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _catalogPane.Controls.Add(layout);

        var hero = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _heroImage.Location = new Point(0, 8);
        _heroImage.Size = new Size(88, 88);
        _heroImage.CornerRadius = 16;
        hero.Controls.Add(_heroImage);
        _heroName.AutoSize = true;
        _heroName.Location = new Point(108, 19);
        _heroName.Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold);
        _heroName.ForeColor = Palette.Text;
        hero.Controls.Add(_heroName);
        _heroMeta.AutoSize = true;
        _heroMeta.Location = new Point(110, 59);
        _heroMeta.Font = new Font("Segoe UI", 9F);
        _heroMeta.ForeColor = Palette.Muted;
        hero.Controls.Add(_heroMeta);
        var source = LabelText("本地索引 · 按编号匹配", 8.5F, Palette.AccentDark, FontStyle.Bold);
        source.Location = new Point(110, 83);
        hero.Controls.Add(source);
        layout.Controls.Add(hero, 0, 0);

        var toolbar = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var skinsTitle = LabelText("可用皮肤", 13F, Palette.Text, FontStyle.Bold);
        skinsTitle.AutoSize = false;
        skinsTitle.Bounds = new Rectangle(0, 0, 132, 50);
        skinsTitle.TextAlign = ContentAlignment.MiddleLeft;
        toolbar.Controls.Add(skinsTitle);
        _skinCount.AutoSize = false;
        _skinCount.Bounds = new Rectangle(132, 0, 116, 50);
        _skinCount.TextAlign = ContentAlignment.MiddleLeft;
        _skinCount.ForeColor = Palette.Faint;
        _skinCount.Font = new Font("Segoe UI", 9F);
        _skinCount.Text = "0 张基础皮肤";
        toolbar.Controls.Add(_skinCount);
        toolbar.Resize += (_, _) =>
        {
            _skinCount.Bounds = new Rectangle(
                skinsTitle.Right,
                0,
                Math.Max(116, toolbar.ClientSize.Width - skinsTitle.Right),
                toolbar.ClientSize.Height);
        };
        layout.Controls.Add(toolbar, 0, 1);

        _skinGrid.Dock = DockStyle.Fill;
        _skinGrid.FlowDirection = FlowDirection.LeftToRight;
        _skinGrid.WrapContents = true;
        _skinGrid.AutoScroll = true;
        _skinGrid.Padding = new Padding(0, 4, 4, 10);
        _skinGrid.BackColor = Palette.Canvas;
        _skinGrid.BorderStyle = BorderStyle.None;
        _skinGrid.Resize += (_, _) => UpdateSkinCardWidths();
        layout.Controls.Add(_skinGrid, 0, 2);

        var pager = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _prevPageButton.Location = new Point(0, 3);
        _prevPageButton.Click += (_, _) => ChangeSkinPage(-1);
        pager.Controls.Add(_prevPageButton);
        _pageLabel.AutoSize = false;
        _pageLabel.Size = new Size(84, 30);
        _pageLabel.Location = new Point(78, 3);
        _pageLabel.TextAlign = ContentAlignment.MiddleCenter;
        _pageLabel.ForeColor = Palette.Muted;
        _pageLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        pager.Controls.Add(_pageLabel);
        _nextPageButton.Location = new Point(164, 3);
        _nextPageButton.Click += (_, _) => ChangeSkinPage(1);
        pager.Controls.Add(_nextPageButton);
        layout.Controls.Add(pager, 0, 3);
        return _catalogPane;
    }

    private Control BuildInspectorPane()
    {
        _inspectorPane.Dock = DockStyle.Fill;
        _inspectorPane.BackColor = Color.Transparent;
        var pane = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18, 18, 18, 16), BackColor = Palette.Surface, AccessibleName = "皮肤预览与应用" };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 222));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        pane.Controls.Add(layout);
        var heading = LabelText("应用预览", 13F, Palette.Text, FontStyle.Bold);
        heading.Dock = DockStyle.Fill;
        heading.TextAlign = ContentAlignment.TopLeft;
        layout.Controls.Add(heading, 0, 0);
        _previewImage.Dock = DockStyle.Fill;
        _previewImage.CornerRadius = 12;
        _previewImage.PreserveAspectRatio = true;
        layout.Controls.Add(_previewImage, 0, 1);

        var copy = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 12, 0, 0) };
        _selectedName.AutoSize = false;
        _selectedName.Dock = DockStyle.Top;
        _selectedName.Height = 34;
        _selectedName.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold);
        _selectedName.ForeColor = Palette.Text;
        _selectedName.Text = "未选择皮肤";
        _selectedName.AutoEllipsis = true;
        _selectedMeta.AutoSize = false;
        _selectedMeta.Dock = DockStyle.Top;
        _selectedMeta.Height = 24;
        _selectedMeta.ForeColor = Palette.Muted;
        _selectedMeta.Font = new Font("Segoe UI", 8.5F);
        _selectedMeta.Text = "从中间目录选择一张皮肤";
        copy.Controls.Add(_selectedMeta);
        copy.Controls.Add(_selectedName);
        layout.Controls.Add(copy, 0, 2);

        var note = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(12, 11, 12, 8), BackColor = Palette.SurfaceSoft };
        _applyStatus.Dock = DockStyle.Fill;
        _applyStatus.ForeColor = Palette.Muted;
        _applyStatus.Font = new Font("Segoe UI", 8.5F);
        _applyStatus.Text = "选择皮肤后，资源会按编号缓存";
        _applyStatus.AutoEllipsis = true;
        note.Controls.Add(_applyStatus);
        layout.Controls.Add(note, 0, 3);

        _applyProgress.Dock = DockStyle.Fill;
        _applyProgress.AccessibleName = "正在处理皮肤资源";
        _applyProgress.Visible = false;
        layout.Controls.Add(_applyProgress, 0, 4);
        _applyButton.Margin = new Padding(0, 10, 0, 0);
        _applyButton.Click += async (_, _) => await ApplySelectedSkinAsync();
        layout.Controls.Add(_applyButton, 0, 5);
        _inspectorPane.Controls.Add(pane);
        return _inspectorPane;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _catalog = await LocalCatalog.LoadAsync(_lifetime.Token);
            await RefreshCatalogViewAsync();
            // LCU polling is useful before the local skin engine has finished
            // booting. Start it as soon as the catalog exists; the poller will
            // use the engine endpoint only as a compatibility fallback.
            _clientSelectionTimer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化失败", ex);
            _clientSelectionTimer.Stop();
            SetStatus(_applyStatus, ex.Message, Palette.Error);
            return;
        }

        var engineReady = await _engine.EnsureReadyAsync(
            null,
            new Progress<string>(message => SetStatus(_applyStatus, message, Palette.Muted)),
            _lifetime.Token);
        if (engineReady)
        {
            _repository.SetPortableRoot(_engine.PortableRoot);
            // The initial champion/skin selection is rendered before the
            // engine finishes booting. Refresh it now so the apply action is
            // enabled as soon as the local API is ready.
            RenderSelectedSkin();
            await PollClientSelectionAsync();
        }
        else
        {
            AppLog.Error("本地引擎未连接，启动或健康检查失败");
            SetStatus(_applyStatus, "本地引擎未连接", Palette.Error);
            _applyButton.Enabled = false;
        }
        await SyncRepositoryAsync();
        _repositoryRefreshTimer.Start();
        await AutoApplyRememberedSelectionAsync();
    }

    private async Task RefreshCatalogViewAsync()
    {
        var selectedChampionId = _selectedChampion?.Id;
        var selectedSkinId = _selectedSkin?.Id;
        _champions = _catalog.Champions
            .OrderBy(c => c.Name, PinyinComparer)
            .ThenBy(c => c.Id)
            .ToList();
        _championSearchIndex.Clear();
        foreach (var champion in _champions)
        {
            var searchable = new List<string>(4 + champion.Skins.Count * 4)
            {
                champion.Name,
                champion.Slug,
                champion.Id.ToString(CultureInfo.InvariantCulture)
            };
            searchable.AddRange(champion.Skins.SelectMany(skin => new[]
            {
                skin.DisplayName,
                skin.Name,
                skin.Id.ToString(CultureInfo.InvariantCulture)
            }));
            _championSearchIndex[champion.Id] = string.Join('\u001f', searchable);
        }

        _catalogHint.Text = _champions.Count.ToString("N0");
        RenderChampionList();
        var preferred = _champions.FirstOrDefault(champion => champion.Id == selectedChampionId)
            ?? _champions.FirstOrDefault(champion => champion.Id == _selectionMemory.LastChampionId)
            ?? _champions.FirstOrDefault(champion => champion.Id == 134)
            ?? _champions.FirstOrDefault();
        if (preferred is null) return;
        await SelectChampionAsync(preferred);
        var restored = preferred.Skins.FirstOrDefault(skin => skin.Id == selectedSkinId);
        if (restored is not null) SelectSkin(restored, remember: false);
    }

    private void RenderChampionList()
    {
        _championAvatarLoading.Clear();
        _championList.SuspendLayout();
        try
        {
            foreach (var control in _championList.Controls.Cast<Control>().ToArray()) control.Dispose();
            _championList.Controls.Clear();
            foreach (var champion in _champions) AddChampionRow(champion);
        }
        finally
        {
            _championList.ResumeLayout(false);
        }
        UpdateChampionRowWidths();
        LoadVisibleChampionImages();
    }

    private void AddChampionRow(Champion champion)
    {
        var row = new ChampionRow(champion)
        {
            Width = Math.Max(190, _championList.ClientSize.Width - 10),
            Selected = _selectedChampion?.Id == champion.Id
        };
        row.Clicked += async (_, _) => await SelectChampionAsync(champion);
        _championList.Controls.Add(row);
    }

    private void UpdateChampionRowWidths()
    {
        _championList.RelayoutRows();
        LoadVisibleChampionImages();
    }

    private void LoadVisibleChampionImages()
    {
        foreach (var row in _championList.VisibleRows)
        {
            if (!_championAvatarLoading.Add(row.Champion.Id)) continue;
            _ = LoadChampionRowImageAsync(row, row.Champion);
        }
    }

    private async Task SelectChampionAsync(Champion champion)
    {
        _selectedChampion = champion;
        _skinPage = 0;
        var rememberedSkinId = _selectionMemory.GetSkinId(champion.Id);
        _selectedSkin = champion.Skins.FirstOrDefault(skin => skin.Id == rememberedSkinId)
            ?? champion.Skins.FirstOrDefault(skin => !skin.Chroma)
            ?? champion.Skins.FirstOrDefault();
        foreach (ChampionRow row in _championList.Controls) row.Selected = row.Champion.Id == champion.Id;
        _heroName.Text = champion.Name;
        _heroMeta.Text = $"英雄编号 {champion.Id}  ·  {champion.SkinCount:N0} 个皮肤";
        RenderSelectedSkin();
        RenderSkins();
        _ = LoadHeroImageAsync(champion);
        await Task.CompletedTask;
    }

    private void RenderSkins()
    {
        _skinLoadCancellation?.Cancel();
        _skinLoadCancellation?.Dispose();
        _skinLoadCancellation = new CancellationTokenSource();
        var groups = BuildSkinGroups(_selectedChampion?.Skins ?? []);
        var pageCount = Math.Max(1, (int)Math.Ceiling(groups.Count / (double)SkinPageSize));
        _skinPage = Math.Clamp(_skinPage, 0, pageCount - 1);
        var page = groups.Skip(_skinPage * SkinPageSize).Take(SkinPageSize).ToList();
        _skinCount.Text = $"{groups.Count:N0} 张基础皮肤";
        _pageLabel.Text = groups.Count == 0 ? "无结果" : $"{_skinPage + 1} / {pageCount}";
        _prevPageButton.Enabled = _skinPage > 0;
        _nextPageButton.Enabled = _skinPage < pageCount - 1;
        if (_selectedSkin is null || !groups.Any(group => group.Contains(_selectedSkin.Id)))
        {
            _selectedSkin = groups.FirstOrDefault()?.BaseSkin;
            RenderSelectedSkin();
        }

        _skinGrid.SuspendLayout();
        foreach (Control control in _skinGrid.Controls) control.Dispose();
        _skinGrid.Controls.Clear();
        if (groups.Count == 0)
        {
            var emptyState = new RoundedLabel
            {
                Text = "当前英雄暂无可用皮肤",
                AutoSize = false,
                Width = Math.Max(260, _skinGrid.ClientSize.Width - 20),
                Height = 74,
                Margin = new Padding(0, 18, 0, 0),
                ForeColor = Palette.Muted,
                BackColor = Palette.SurfaceSoft,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F)
            };
            _skinGrid.Controls.Add(emptyState);
        }
        foreach (var group in page)
        {
            var card = new SkinCard(group.BaseSkin, group.Chromas) { Selected = _selectedSkin is not null && group.Contains(_selectedSkin.Id) };
            card.Clicked += (_, _) => SelectSkin(group.BaseSkin);
            card.ChromaSelected += (_, skin) => SelectSkin(skin);
            _skinGrid.Controls.Add(card);
            _ = LoadSkinImageAsync(card, group.BaseSkin, _skinLoadCancellation.Token);
        }
        _skinGrid.ResumeLayout(true);
        UpdateSkinCardWidths();
    }

    private static List<SkinGroup> BuildSkinGroups(IReadOnlyList<Skin> skins)
    {
        var groups = skins
            .GroupBy(skin => skin.Chroma ? skin.BaseSkinId : skin.Id)
            .Select(group =>
            {
                var baseSkin = group.FirstOrDefault(skin => !skin.Chroma) ?? group.First();
        var chromas = group
            .Where(skin => skin.Chroma && skin.Id != baseSkin.Id)
            .OrderBy(skin => skin.Id)
            .ToList();
                return new SkinGroup(baseSkin, chromas);
            })
            .OrderBy(group => group.BaseSkin.Id)
            .ToList();

        return groups;
    }

    private void UpdateSkinCardWidths()
    {
        if (_skinGrid.ClientSize.Width <= 0) return;
        const int minWidth = 184;
        const int gap = 12;
        var usable = Math.Max(minWidth, _skinGrid.ClientSize.Width - _skinGrid.Padding.Horizontal - 4);
        var columns = Math.Max(1, (usable + gap) / (minWidth + gap));
        var width = Math.Max(minWidth, (usable - columns * gap) / columns);
        foreach (var card in _skinGrid.Controls.OfType<SkinCard>()) card.Width = width;
    }

    private void ChangeSkinPage(int delta)
    {
        _skinPage += delta;
        RenderSkins();
        _skinGrid.Focus();
    }

    private void FilterChampions()
    {
        var query = _championSearch.Text.Trim();
        var matchingIds = string.IsNullOrWhiteSpace(query)
            ? null
            : _championSearchIndex
                .Where(pair => pair.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Select(pair => pair.Key)
                .ToHashSet();

        _championList.SuspendLayout();
        try
        {
            foreach (ChampionRow row in _championList.Controls)
                row.Visible = matchingIds is null || matchingIds.Contains(row.Champion.Id);
        }
        finally
        {
            _championList.ResumeLayout(false);
        }
        _championList.ScrollToTop();
    }

    private async Task PollClientSelectionAsync()
    {
        if (Interlocked.Exchange(ref _clientSelectionInFlight, 1) != 0) return;
        try
        {
            // LCU is the authoritative source for the live champion-select
            // session. The engine endpoint remains a fallback for older
            // clients that do not expose LCU data. Do not wait for the skin
            // engine here: the client can already be in champion select while
            // the local engine is still starting.
            // Keep the two observers independent. LCU is the preferred source
            // for the live client slot, while the local engine is a fallback
            // only when LCU has no champion yet. A valid engine response must
            // not be discarded just because LCU returned an empty session.
            ChampionSelection? lcuSelection;
            using (var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    lcuSelection = await _leagueClient.GetSelectionAsync(pollTimeout.Token);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    // A single blocked client probe must not occupy the
                    // WinForms timer until the application closes.
                    lcuSelection = null;
                }
            }
            var selection = lcuSelection;
            // A 404/empty LCU session is a valid "not in champion select"
            // response. Only use the engine fallback when LCU itself is
            // unavailable; otherwise a stale fallback could resurrect an old
            // champion and slot.
            if ((selection?.ResolvedChampionId ?? 0) <= 0
                && _engine.IsReady
                && (lcuSelection is null || !lcuSelection.Available))
                selection = await ReadEngineSelectionSafeAsync();

            _clientSelectionPollCompleted = lcuSelection is not null || selection is not null;
            var championId = selection?.ResolvedChampionId ?? 0;
            if (championId <= 0)
            {
                // LCU briefly returns an empty session while the picker is
                // switching phases. Keep the last real champion for a few
                // polls so the UI does not jump back or block synchronization.
                if (++_clientSelectionMisses >= 3)
                {
                    _lastClientChampionId = 0;
                    _lastClientSkinId = 0;
                    _clientSelectionStabilizer.Reset();
                    if (lcuSelection is { Available: true })
                    {
                        var phase = string.IsNullOrWhiteSpace(lcuSelection.Phase) ? "未知" : lcuSelection.Phase;
                        AppLog.InfoThrottled("lcu-selection-waiting", $"LCU 已连接，当前阶段={phase}，等待进入英雄选择", TimeSpan.FromSeconds(10));
                        SetStatus(_applyStatus, $"等待进入英雄选择 · 当前阶段 {phase}", Palette.Muted);
                    }
                    else
                    {
                        AppLog.WarnThrottled("lcu-selection-unavailable", $"未读取到当前英雄选择，详情见 {AppLog.FilePath}", TimeSpan.FromSeconds(10));
                        SetStatus(_applyStatus, $"等待英雄选择 · LCU 暂不可用（详情见 {AppLog.FilePath}）", Palette.Muted);
                    }
                }
                return;
            }
            _clientSelectionMisses = 0;
            var selectedSkinId = selection?.SelectedSkinId ?? 0;
            var normalizedSkinId = selectedSkinId > 0
                && selectedSkinId / 1000 == championId
                ? selectedSkinId
                : championId * 1000;
            _lastClientSelectionUtc = DateTime.UtcNow;
            var source = ReferenceEquals(selection, lcuSelection) ? "LCU" : "engine";
            var observation = _clientSelectionStabilizer.Observe(championId, normalizedSkinId);
            if (!observation.IsStable)
            {
                AppLog.Info(
                    $"客户端英雄候选等待稳定：source={source} championId={championId} skinId={normalizedSkinId} " +
                    $"phase={selection?.Phase} samples={observation.ConsecutiveSamples}/2");
                return;
            }

            // Keep the target skin current after it is stable, but emit a UI
            // synchronization only once per actual LCU champion change. This
            // lets the user browse another champion in the app without the
            // 700 ms poller repeatedly pulling the UI back.
            _lastClientSkinId = normalizedSkinId;
            if (!observation.ShouldSynchronize)
            {
                AppLog.InfoThrottled(
                    "lcu-selection-steady",
                    $"客户端英雄保持不变，不重复覆盖界面：source={source} championId={championId} skinId={normalizedSkinId} phase={selection?.Phase}",
                    TimeSpan.FromSeconds(10));
                return;
            }

            _lastClientChampionId = championId;
            var champion = _champions.FirstOrDefault(item => item.Id == championId);
            if (champion is null)
            {
                AppLog.Warn($"客户端英雄已稳定但本地目录未找到：source={source} championId={championId} phase={selection?.Phase}");
                return;
            }
            var championChanged = _selectedChampion?.Id != championId;
            if (championChanged)
            {
                await SelectChampionAsync(champion);
                SetStatus(_applyStatus, $"已同步客户端英雄 · {champion.Name}", Palette.Success);
                AppLog.Info(
                    $"客户端英雄同步已接受：source={source} championId={championId} champion={champion.Name} " +
                    $"skinId={normalizedSkinId} phase={selection?.Phase} samples={observation.ConsecutiveSamples}");
                // Applying a remembered skin is deliberately detached from
                // the polling loop. A slow download or engine request must
                // never prevent the next champion selection from being read.
                QueueRememberedSelectionApply(championId);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex)
        {
            AppLog.WarnThrottled("lcu-http", $"LCU 轮询网络错误：{ex.Message}", TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            AppLog.Error("客户端选择轮询异常", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _clientSelectionInFlight, 0);
        }
    }

    private void QueueRememberedSelectionApply(int championId)
    {
        _ = ApplyRememberedSelectionInBackgroundAsync(championId);
    }

    private async Task<ChampionSelection?> ReadEngineSelectionSafeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
        try { return await _engine.GetChampionSelectionAsync(timeout.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            AppLog.WarnThrottled("engine-selection", $"读取引擎英雄选择失败：{ex.Message}", TimeSpan.FromSeconds(10));
            return null;
        }
    }

    private async Task ApplyRememberedSelectionInBackgroundAsync(int championId)
    {
        try
        {
            await AutoApplyRememberedSelectionAsync(championId);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed && _selectedChampion?.Id == championId)
                SetStatus(_applyStatus, $"自动应用未完成：{ex.Message}", Palette.Muted);
        }
    }

    private void SelectSkin(Skin skin, bool remember = true)
    {
        _selectedSkin = skin;
        if (remember && _selectedChampion is not null) _selectionMemory.Remember(_selectedChampion.Id, skin.Id);
        foreach (var card in _skinGrid.Controls.OfType<SkinCard>()) card.Selected = card.ContainsSkin(skin.Id);
        RenderSelectedSkin();
    }

    private async Task AutoApplyRememberedSelectionAsync(int? clientChampionId = null)
    {
        if (!_engine.IsReady) return;

        var championId = clientChampionId ?? (_lastClientChampionId > 0 ? _lastClientChampionId : _selectionMemory.LastChampionId);
        if (championId <= 0 || !_autoApplyAttemptedChampions.Add(championId)) return;
        var skinId = _selectionMemory.GetSkinId(championId);
        if (skinId is null) return;
        if (!_clientSelectionPollCompleted || (_lastClientChampionId > 0 && _lastClientChampionId != championId)) return;

        var champion = _champions.FirstOrDefault(item => item.Id == championId);
        var skin = champion?.Skins.FirstOrDefault(item => item.Id == skinId.Value);
        if (champion is null || skin is null) return;
        if (_selectedChampion?.Id != champion.Id) await SelectChampionAsync(champion);
        SelectSkin(skin, remember: false);
        if (_repository.GetCachedSkinPath(skin.Id) is null)
        {
            SetStatus(_applyStatus, "已恢复上次选择 · 本地资源未缓存，请手动应用", Palette.Muted);
            return;
        }
        SetStatus(_applyStatus, "正在自动应用上次选择的皮肤", Palette.Muted);
        await ApplySelectedSkinAsync(downloadIfMissing: false);
    }

    private void RenderSelectedSkin()
    {
        var skin = _selectedSkin;
        if (skin is null)
        {
            _selectedName.Text = "未选择皮肤";
            _selectedMeta.Text = "从中间目录选择一张皮肤";
            _previewImage.Image = null;
            _applyButton.Enabled = false;
            return;
        }
        _selectedName.Text = skin.DisplayName;
        _selectedMeta.Text = skin.Chroma
            ? $"编号 {skin.Id}  ·  炫彩 · {skin.DisplayName}"
            : $"编号 {skin.Id}  ·  完整皮肤";
        _applyButton.Enabled = _engine.IsReady;
        _ = LoadPreviewImageAsync(skin);
    }

    private async Task LoadHeroImageAsync(Champion champion)
    {
        var image = await ImageCache.LoadAsync(champion.Icon);
        if (IsDisposed || _selectedChampion?.Id != champion.Id)
        {
            image?.Dispose();
            return;
        }
        _heroImage.Image = image;
    }

    private async Task LoadChampionRowImageAsync(ChampionRow row, Champion champion)
    {
        var image = await ImageCache.LoadAsync(champion.Icon);
        if (IsDisposed || row.IsDisposed)
        {
            image?.Dispose();
            return;
        }
        if (image is null) return;

        Image? avatar = null;
        try
        {
            // Render above the final 30px size so the circular edge survives the
            // last downsample instead of relying on a low-resolution GDI region.
            avatar = CreateAvatarThumbnail(image, 96);
        }
        finally
        {
            image.Dispose();
        }
        row.SetAvatar(avatar);
    }

    private async Task LoadPreviewImageAsync(Skin skin)
    {
        var image = await PreviewImageResolver.LoadAsync(skin, _lifetime.Token);
        if (IsDisposed || _selectedSkin?.Id != skin.Id)
        {
            image?.Dispose();
            return;
        }
        _previewImage.Image = image;
    }

    private async Task LoadSkinImageAsync(SkinCard card, Skin skin, CancellationToken cancellationToken)
    {
        Image? image = null;
        try
        {
            image = await PreviewImageResolver.LoadAsync(skin, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (card.IsDisposed)
            {
                return;
            }
            card.SetImage(image);
            image = null;
        }
        catch (OperationCanceledException) { }
        finally { image?.Dispose(); }
    }

    private static Image CreateAvatarThumbnail(Image source, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var circle = new GraphicsPath();
        circle.AddEllipse(1, 1, Math.Max(1, size - 2), Math.Max(1, size - 2));
        graphics.SetClip(circle);
        var sourceRatio = source.Width / (double)Math.Max(1, source.Height);
        var targetRatio = 1D;
        var sourceRect = new Rectangle(0, 0, source.Width, source.Height);
        if (sourceRatio > targetRatio)
        {
            var crop = (int)Math.Round(source.Width - source.Height * targetRatio);
            sourceRect = new Rectangle(crop / 2, 0, Math.Max(1, source.Width - crop), source.Height);
        }
        else if (sourceRatio < targetRatio)
        {
            var crop = (int)Math.Round(source.Height - source.Width / targetRatio);
            sourceRect = new Rectangle(0, crop / 2, source.Width, Math.Max(1, source.Height - crop));
        }
        graphics.DrawImage(source, new Rectangle(0, 0, size, size), sourceRect, GraphicsUnit.Pixel);
        graphics.ResetClip();
        return bitmap;
    }

    private async Task ApplySelectedSkinAsync(bool downloadIfMissing = true)
    {
        var skin = _selectedSkin;
        if (skin is null || !_engine.IsReady) return;
        if (Interlocked.Exchange(ref _applyInFlight, 1) != 0) return;
        AppLog.Info($"收到皮肤应用请求：skinId={skin.Id} repositoryReady={_repository.IsReady} "
            + $"repositorySyncing={_repository.IsSyncing} downloadIfMissing={downloadIfMissing}");
        _applyButton.Enabled = false;
        _applyProgress.Visible = true;
        SetStatus(_applyStatus, $"正在缓存 {skin.Id}", Palette.Muted);
        try
        {
            if (!_repository.IsReady)
            {
                SetStatus(_applyStatus, "正在加载随包资源索引", Palette.Muted);
                if (!await _repository.EnsureIndexReadyAsync(_lifetime.Token))
                    throw new InvalidOperationException("随包资源索引不可用，请检查程序文件后重试");
            }
            var cached = downloadIfMissing
                ? await _repository.EnsureSkinCachedAsync(skin.Id, _lifetime.Token)
                : _repository.GetCachedSkinPath(skin.Id);
            if (!downloadIfMissing && cached is null)
            {
                SetStatus(_applyStatus, "本地资源未缓存，请点击应用皮肤下载资源", Palette.Muted);
                return;
            }
            if (cached is null)
            {
                throw new InvalidOperationException($"远程仓库中未找到皮肤 {skin.Id}");
            }
            AppLog.Info($"皮肤缓存已确认：skinId={skin.Id} path={cached} engineRoot={_engine.PortableRoot} repository={_repository.RepositoryPath}");
            var targetSkinId = ApplicationTargetSkinId(skin);
            AppLog.Info($"准备提交应用请求：skinId={skin.Id} targetSkinId={targetSkinId} "
                + $"cachedExists={File.Exists(cached)} cachedBytes={(File.Exists(cached) ? new FileInfo(cached).Length : 0)} "
                + $"engine={_engine.EnginePath} engineRoot={_engine.PortableRoot} game={_engine.GameDirectory}");
            SetStatus(_applyStatus, "资源已缓存，正在应用皮肤", Palette.Muted);
            if (_lastAppliedSkinId == skin.Id && _lastAppliedTargetSkinId == targetSkinId)
            {
                SetStatus(_applyStatus, "皮肤已应用 · 无需重复处理", Palette.Success);
                return;
            }
            var result = await _engine.ApplyAsync(skin.Id, _lifetime.Token, targetSkinId);
            if (result is null || !result.Ok)
            {
                // A newly cached file may not be in the engine catalog yet.
                // Rebuild only for that specific error; mod-tools failures
                // must surface immediately instead of entering another retry
                // cycle.
                if (IsCatalogMiss(result?.Error))
                {
                    SetStatus(_applyStatus, "正在刷新本地索引并重试", Palette.Muted);
                    var rebuild = await _engine.RebuildAsync(_lifetime.Token);
                    if (rebuild is null) throw new InvalidOperationException("本地引擎无法重建索引");
                    result = await _engine.ApplyAsync(skin.Id, _lifetime.Token, targetSkinId);
                }
            }
            if (result is null || !result.Ok)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result?.Error) ? "应用请求失败" : result.Error);
            _lastAppliedSkinId = skin.Id;
            _lastAppliedTargetSkinId = targetSkinId;
            var armed = string.Equals(result.OverlayStatus, "armed", StringComparison.OrdinalIgnoreCase);
            var injectionStatus = result.InjectionStatus?.Trim() ?? "";
            AppLog.Info($"皮肤应用完成：skinId={skin.Id} targetSkinId={targetSkinId} mode={result.Mode} "
                + $"overlayStatus={result.OverlayStatus} injectionStatus={injectionStatus} "
                + $"engine={_engine.EnginePath} engineRoot={_engine.PortableRoot}");
            var statusText = injectionStatus.Equals("redirected", StringComparison.OrdinalIgnoreCase)
                ? "皮肤已注入并重定向 WAD · 进入对局后生效"
                : injectionStatus.Equals("waiting-game", StringComparison.OrdinalIgnoreCase)
                    ? "覆盖层已准备 · 等待进入游戏"
                : injectionStatus.Equals("dll-initializing", StringComparison.OrdinalIgnoreCase)
                    ? "游戏进程已找到，正在加载注入 DLL · 请保持游戏运行"
                : injectionStatus.Equals("found-game", StringComparison.OrdinalIgnoreCase)
                    ? "已找到游戏进程，正在等待 DLL 重定向 · 请保持窗口运行"
                    : injectionStatus.Equals("timeout", StringComparison.OrdinalIgnoreCase)
                        ? "覆盖层已启动但未确认 DLL 重定向 · 请查看诊断日志"
                        : armed ? "覆盖层已准备 · 进入对局后生效" : "皮肤资源已准备 · 下一局开始后生效";
            SetStatus(_applyStatus,
                statusText,
                injectionStatus.Equals("redirected", StringComparison.OrdinalIgnoreCase) || !armed
                    ? Palette.Success : Palette.Muted);
        }
        catch (Exception ex)
        {
            AppLog.Error($"皮肤应用失败：{skin.Id}", ex);
            SetStatus(_applyStatus, ex.Message, Palette.Error);
        }
        finally
        {
            _applyProgress.Visible = false;
            _applyButton.Enabled = _engine.IsReady && _selectedSkin is not null;
            Interlocked.Exchange(ref _applyInFlight, 0);
        }
    }

    private static bool IsCatalogMiss(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Contains("skin is not in the local catalog", StringComparison.OrdinalIgnoreCase)
            || error.Contains("皮肤库中没有", StringComparison.Ordinal)
            || error.Contains("skin package is not cached", StringComparison.OrdinalIgnoreCase);
    }

    private int ApplicationTargetSkinId(Skin skin)
    {
        var championId = skin.ChampionId > 0 ? skin.ChampionId : skin.Id / 1000;
        return _lastClientSkinId > 0 && _lastClientSkinId / 1000 == championId
            ? _lastClientSkinId
            : championId * 1000;
    }

    private Task<bool> SyncRepositoryAsync()
    {
        lock (_repositorySyncGate)
        {
            // Startup sync and the apply action can arrive together. Share the
            // in-flight task so apply waits for the real index instead of
            // observing the temporary empty state.
            if (_repositorySyncTask is { IsCompleted: false }) return _repositorySyncTask;
            _repositorySyncTask = SyncRepositoryCoreAsync();
            return _repositorySyncTask;
        }
    }

    private async Task<bool> SyncRepositoryCoreAsync()
    {
        Interlocked.Exchange(ref _repositorySyncInFlight, 1);
        SetStatus(_applyStatus, "正在准备本地资源", Palette.Muted);
        try
        {
            var ok = await _repository.SyncAsync(new Progress<string>(message => SetStatus(_applyStatus, message, Palette.Muted)), _lifetime.Token);
            if (ok)
            {
                if (_repository.LastSyncChanged)
                {
                    ImageCache.Clear();
                    PreviewImageResolver.Clear();
                    var revision = _repository.Revision;
                    AppLog.Info($"皮肤目录 revision 已变化，清理预览图缓存：revision={revision[..Math.Min(12, revision.Length)]}");
                }
                var merged = _catalog.MergeRepository(_repository.SkinPaths, _repository.SkinNames, _repository.SkinEnglishNames, _repository.CachedPreviewPaths);
                await _repository.UpdateEngineIndexAsync(_lifetime.Token);
                if (_repository.LastSyncChanged || merged > 0)
                    await RefreshCatalogViewAsync();
                var updateLabel = _repository.LastSyncChanged ? "已更新" : "已就绪";
                SetStatus(_applyStatus, $"本地资源{updateLabel} · {_repository.RemoteSkinCount:N0} 个皮肤", Palette.Success);
            }
            else
            {
                AppLog.Warn("私有皮肤资源同步失败，请检查网络、授权状态和 Worker 服务");
                SetStatus(_applyStatus, $"本地资源准备失败，详情见 {AppLog.FilePath}", Palette.Error);
            }
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Error("私有皮肤资源同步异常", ex);
            SetStatus(_applyStatus, ex.Message, Palette.Error);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _repositorySyncInFlight, 0);
        }
    }

    private void UpdateResponsiveLayout()
    {
        var compact = ClientSize.Width < 1180;
        _inspectorPane.Visible = !compact;
        _workspace.ColumnStyles[2].Width = compact ? 0 : 344;
        _workspace.ColumnStyles[2].SizeType = SizeType.Absolute;
        _workspace.PerformLayout();
        UpdateSkinCardWidths();
    }

    private static Label LabelText(string text, float size, Color color, FontStyle style = FontStyle.Regular) => new() { Text = text, AutoSize = true, ForeColor = color, Font = new Font("Segoe UI", size, style) };
    private static void SetStatus(Control control, string text, Color color)
    {
        if (control.IsDisposed || control.Disposing || !control.IsHandleCreated) return;
        try
        {
            control.Text = text;
            control.ForeColor = color;
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
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

    private void CancelLifetime()
    {
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CancelLifetime();
            _searchDebounce.Stop();
            _clientSelectionTimer.Stop();
            _skinLoadCancellation?.Cancel();
            _skinLoadCancellation?.Dispose();
            _searchDebounce.Dispose();
            _clientSelectionTimer.Dispose();
            _leagueClient.Dispose();
            _engine.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SurfacePanel : Panel
{
    public SurfacePanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Palette.Surface;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        var radius = Math.Min(12, Math.Min(Width, Height) / 2);
        Region = new Region(RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), radius));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        using var path = RoundedPath(rect, 12);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        base.OnPaint(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var background = Parent?.BackColor ?? Palette.Canvas;
        if (background == Color.Transparent) background = Palette.Canvas;
        e.Graphics.Clear(background);
    }
    internal static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (rect.Width <= 0 || rect.Height <= 0) return path;

        radius = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
        if (radius == 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class LoadingBar : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 32 };
    private float _phase;

    public LoadingBar()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        _timer.Tick += (_, _) =>
        {
            _phase += 0.018F;
            if (_phase >= 1F) _phase -= 1F;
            Invalidate();
        };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimationState();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateAnimationState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _timer.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var parentBackColor = Parent?.BackColor ?? Color.Transparent;
        for (var parent = Parent?.Parent; parent is not null && parentBackColor == Color.Transparent; parent = parent.Parent)
            parentBackColor = parent.BackColor;
        if (parentBackColor == Color.Transparent) parentBackColor = Palette.Canvas;
        e.Graphics.Clear(parentBackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        const int trackHeight = 8;
        var track = new Rectangle(10, Math.Max(0, (Height - trackHeight) / 2), Math.Max(1, Width - 20), trackHeight);
        using var trackPath = SurfacePanel.RoundedPath(track, trackHeight / 2);
        using var trackBrush = new SolidBrush(Palette.SurfaceRaised);
        e.Graphics.FillPath(trackBrush, trackPath);
        using var trackPen = new Pen(Palette.Border, 1F);
        e.Graphics.DrawPath(trackPen, trackPath);

        var segmentWidth = Math.Max(56, (int)Math.Round(track.Width * 0.26));
        var travel = track.Width + segmentWidth;
        var segmentX = track.Left - segmentWidth + (int)Math.Round(travel * _phase);
        var segment = new Rectangle(segmentX, track.Top + 1, segmentWidth, Math.Max(1, track.Height - 2));

        var state = e.Graphics.Save();
        e.Graphics.SetClip(trackPath);
        using (var accentPath = SurfacePanel.RoundedPath(segment, segment.Height / 2))
        using (var accentBrush = new SolidBrush(Palette.Accent))
            e.Graphics.FillPath(accentBrush, accentPath);

        // A narrow, low-contrast highlight gives the indeterminate motion a
        // polished finish without adding a second accent color.
        var shineX = segment.Left + (int)Math.Round(segment.Width * (0.14 + _phase * 0.58));
        var shine = new Rectangle(shineX, segment.Top + 1, Math.Max(3, segment.Width / 12), Math.Max(1, segment.Height - 2));
        using (var shinePath = SurfacePanel.RoundedPath(shine, shine.Height / 2))
        using (var shineBrush = new SolidBrush(Color.FromArgb(58, Color.White)))
            e.Graphics.FillPath(shineBrush, shinePath);
        e.Graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateAnimationState()
    {
        if (Visible && IsHandleCreated) _timer.Start();
        else _timer.Stop();
    }
}

internal sealed class RoundedSearchBox : Panel
{
    private readonly TextBox _input = new();
    private readonly Label _placeholder = new();
    private int _cornerRadius = 10;
    private string _placeholderText = string.Empty;

    public new event EventHandler? TextChanged;

    public new string Text
    {
        get => _input.Text;
        set => _input.Text = value ?? string.Empty;
    }

    public new int TabIndex
    {
        get => _input.TabIndex;
        set => _input.TabIndex = value;
    }

    public new string? AccessibleName
    {
        get => _input.AccessibleName;
        set => _input.AccessibleName = value;
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set
        {
            _placeholderText = value ?? string.Empty;
            _placeholder.Text = _placeholderText;
            UpdatePlaceholderVisibility();
        }
    }

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(0, value);
            UpdateRegion();
            Invalidate();
        }
    }

    public RoundedSearchBox()
    {
        DoubleBuffered = true;
        TabStop = false;
        // Keep the native text box away from the curved edge. Painting the
        // rounded surface ourselves avoids the aliased WinForms Region clip.
        Padding = new Padding(10, 3, 8, 3);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _input.BorderStyle = BorderStyle.None;
        _input.Dock = DockStyle.Fill;
        _input.Margin = Padding.Empty;
        _input.Multiline = false;
        _input.BackColor = Palette.SurfaceSoft;
        _input.ForeColor = Palette.Text;
        _input.Font = Font;
        _input.TabStop = true;
        _input.TextChanged += (_, e) =>
        {
            UpdatePlaceholderVisibility();
            TextChanged?.Invoke(this, e);
        };
        _input.Enter += (_, _) => { Invalidate(); UpdatePlaceholderVisibility(); };
        _input.Leave += (_, _) => { Invalidate(); UpdatePlaceholderVisibility(); };

        _placeholder.Dock = DockStyle.Fill;
        _placeholder.AutoSize = false;
        _placeholder.TextAlign = ContentAlignment.MiddleLeft;
        _placeholder.ForeColor = Palette.Faint;
        _placeholder.BackColor = Color.Transparent;
        _placeholder.Cursor = Cursors.IBeam;
        _placeholder.Padding = Padding.Empty;
        _placeholder.Click += (_, _) => _input.Focus();

        Controls.Add(_input);
        Controls.Add(_placeholder);
        MouseDown += (_, _) => _input.Focus();
        UpdatePlaceholderVisibility();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _input.BackColor = BackColor;
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        _input.ForeColor = ForeColor;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _input.Font = Font;
        _placeholder.Font = Font;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var parentBackColor = Parent?.BackColor ?? Color.Transparent;
        for (var parent = Parent?.Parent; parent is not null && parentBackColor == Color.Transparent; parent = parent.Parent)
            parentBackColor = parent.BackColor;
        if (parentBackColor == Color.Transparent) parentBackColor = Palette.Canvas;
        e.Graphics.Clear(parentBackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = SurfacePanel.RoundedPath(
            new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)),
            Math.Min(_cornerRadius, Math.Min(Width, Height) / 2));
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using var path = SurfacePanel.RoundedPath(rect, Math.Min(_cornerRadius, Math.Min(Width, Height) / 2));
        using var pen = new Pen(_input.Focused ? Palette.Accent : Palette.Border, _input.Focused ? 1.25F : 1F);
        e.Graphics.DrawPath(pen, path);
    }

    private void UpdatePlaceholderVisibility()
    {
        _placeholder.Visible = string.IsNullOrEmpty(_input.Text) && !_input.Focused && !string.IsNullOrEmpty(_placeholderText);
        _placeholder.BringToFront();
    }

    private void UpdateRegion()
    {
        // Region uses a hard-edged GDI clip, which produces the black/white
        // corner pixels visible on scaled displays. The paint path above is
        // antialiased and leaves the parent surface outside the curve.
        if (Region is not null) Region = null;
    }
}

internal sealed class RoundedLabel : Label
{
    public int CornerRadius { get; set; } = 10;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0)
        {
            Region = null;
            return;
        }

        var radius = Math.Min(CornerRadius, Math.Min(Width, Height) / 2);
        Region = new Region(SurfacePanel.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), radius));
    }
}

internal sealed class RoundedContextMenuStrip : ContextMenuStrip
{
    private const int Radius = 10;

    public RoundedContextMenuStrip()
    {
        BackColor = Palette.Surface;
        Padding = new Padding(4);
        DropShadowEnabled = false;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0)
        {
            Region = null;
            return;
        }

        var radius = Math.Min(Radius, Math.Min(Width, Height) / 2);
        Region = new Region(SurfacePanel.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), radius));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Surface);
        base.OnPaintBackground(e);
    }
}

internal sealed class RoundedButton : Control
{
    private readonly Color _fill;
    private bool _hovered;
    private bool _pressed;
    private ContentAlignment _textAlign = ContentAlignment.MiddleCenter;

    public ContentAlignment TextAlign
    {
        get => _textAlign;
        set
        {
            if (_textAlign == value) return;
            _textAlign = value;
            Invalidate();
        }
    }

    public RoundedButton(string text, Color fill)
    {
        Text = text;
        _fill = fill;
        ForeColor = fill == Palette.Accent ? Color.White : Palette.Text;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        TabStop = true;
        Padding = new Padding(8, 0, 8, 0);
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled)
        {
            Focus();
            _pressed = true;
            Capture = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = false;
            Capture = false;
            Invalidate();
            // Control's standard mouse-up handling raises Click once. Do not
            // call OnClick here as well, otherwise custom buttons dispatch
            // the same click twice and toggle popups open then closed.
        }
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && e.KeyCode is Keys.Enter or Keys.Space)
        {
            _pressed = true;
            Invalidate();
            if (e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            var shouldClick = e.KeyCode == Keys.Space && _pressed && Enabled;
            _pressed = false;
            Invalidate();
            if (shouldClick) OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { _pressed = false; _hovered = false; }
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(GetParentBackColor());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Clear the complete invalidated rectangle before filling the path.
        // This prevents stale/native Button pixels from surviving at the
        // right and bottom edges when the control is resized in a table cell.
        e.Graphics.Clear(GetParentBackColor());
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        using var path = SurfacePanel.RoundedPath(rect, Math.Min(9, Math.Min(Width, Height) / 2));
        var fill = !Enabled ? Palette.SurfaceRaised : _fill == Palette.SurfaceSoft && _hovered ? Palette.SurfaceRaised : _fill;
        if (_pressed && Enabled) fill = _fill == Palette.Accent ? Palette.AccentDark : Palette.Border;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);
        var color = Enabled ? ForeColor : Palette.Faint;
        var textRect = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
        var textFlags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        textFlags |= _textAlign switch
        {
            ContentAlignment.TopLeft => TextFormatFlags.Left | TextFormatFlags.Top,
            ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Top,
            ContentAlignment.TopRight => TextFormatFlags.Right | TextFormatFlags.Top,
            ContentAlignment.MiddleLeft => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
            ContentAlignment.MiddleRight => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
            ContentAlignment.BottomLeft => TextFormatFlags.Left | TextFormatFlags.Bottom,
            ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom,
            ContentAlignment.BottomRight => TextFormatFlags.Right | TextFormatFlags.Bottom,
            _ => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
        };
        TextRenderer.DrawText(e.Graphics, Text, Font, textRect, color, textFlags);
        if (Focused && TabStop)
        {
            using var focusPen = new Pen(Palette.Accent) { DashStyle = DashStyle.Dot };
            e.Graphics.DrawPath(focusPen, SurfacePanel.RoundedPath(new Rectangle(3, 3, Math.Max(0, Width - 7), Math.Max(0, Height - 7)), 7));
        }
    }

    private Color GetParentBackColor()
    {
        var color = Parent?.BackColor ?? Palette.Canvas;
        for (var parent = Parent?.Parent; parent is not null && color == Color.Transparent; parent = parent.Parent)
            color = parent.BackColor;
        return color == Color.Transparent ? Palette.Canvas : color;
    }
}

internal sealed class ChampionListPanel : Panel
{
    private const int WheelStep = 52;
    private const int ScrollRailWidth = 6;
    private const int ScrollRailGutter = 14;
    private bool _layouting;
    private bool _draggingThumb;
    private int _dragStartY;
    private int _dragStartOffset;
    private int _scrollOffset;
    private int _contentHeight;

    public event EventHandler? ViewportChanged;
    public IReadOnlyList<ChampionRow> VisibleRows => Controls
        .OfType<ChampionRow>()
        .Where(row => row.Visible && row.Bottom > Padding.Top && row.Top < ClientSize.Height - Padding.Bottom)
        .ToList();

    public ChampionListPanel()
    {
        AutoScroll = false;
        TabStop = true;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    public void RelayoutRows()
    {
        if (_layouting) return;
        _layouting = true;
        try
        {
            var visibleRows = Controls
                .OfType<ChampionRow>()
                .Where(row => row.Visible)
                .ToList();

            _contentHeight = visibleRows.Sum(row => row.Height + row.Margin.Vertical);
            var viewportHeight = Math.Max(0, ClientSize.Height - Padding.Vertical);
            var maxOffset = Math.Max(0, _contentHeight - viewportHeight);
            _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);

            SuspendLayout();
            var y = Padding.Top - _scrollOffset;
            var width = Math.Max(190, ClientSize.Width - Padding.Horizontal - ScrollRailGutter);
            foreach (var row in visibleRows)
            {
                var margin = row.Margin;
                if (row.Width != width) row.Width = width;
                row.Location = new Point(Padding.Left + margin.Left, y + margin.Top);
                y += row.Height + margin.Vertical;
            }

            foreach (var row in Controls.OfType<ChampionRow>().Where(row => !row.Visible))
                row.Location = new Point(Padding.Left, -row.Height - row.Margin.Vertical);
        }
        finally
        {
            ResumeLayout(false);
            _layouting = false;
        }

        Invalidate();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScrollToTop()
    {
        _scrollOffset = 0;
        RelayoutRows();
    }

    private void ScrollByWheel(int delta)
    {
        if (delta == 0 || _contentHeight <= ClientSize.Height - Padding.Vertical) return;
        var viewportHeight = Math.Max(0, ClientSize.Height - Padding.Vertical);
        var maxOffset = Math.Max(0, _contentHeight - viewportHeight);
        var movement = (int)Math.Round(delta * WheelStep / 120d);
        if (movement == 0) movement = Math.Sign(delta);
        var nextOffset = Math.Clamp(_scrollOffset - movement, 0, maxOffset);
        if (nextOffset == _scrollOffset) return;
        _scrollOffset = nextOffset;
        RelayoutRows();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        HookMouseWheel(e.Control);
    }

    private void HookMouseWheel(Control? control)
    {
        if (control is null) return;
        control.MouseWheel += (_, e) => ScrollByWheel(e.Delta);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && TryGetScrollGeometry(out var rail, out var thumb))
        {
            if (thumb.Contains(e.Location))
            {
                _draggingThumb = true;
                _dragStartY = e.Y;
                _dragStartOffset = _scrollOffset;
                Capture = true;
                return;
            }

            if (rail.Contains(e.Location))
            {
                SetScrollOffsetFromPointer(e.Y);
                return;
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingThumb)
        {
            if (TryGetScrollGeometry(out _, out var thumb))
            {
                var viewportHeight = Math.Max(0, ClientSize.Height - Padding.Vertical);
                var maxOffset = Math.Max(0, _contentHeight - viewportHeight);
                var travel = Math.Max(1, Math.Max(0, ClientSize.Height - Padding.Vertical) - thumb.Height);
                var delta = e.Y - _dragStartY;
                _scrollOffset = Math.Clamp(
                    _dragStartOffset + (int)Math.Round(delta * maxOffset / (double)travel),
                    0,
                    maxOffset);
                RelayoutRows();
            }
            return;
        }

        if (TryGetScrollGeometry(out _, out var currentThumb))
            Cursor = currentThumb.Contains(e.Location) ? Cursors.SizeNS : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_draggingThumb && e.Button == MouseButtons.Left)
        {
            _draggingThumb = false;
            Capture = false;
            return;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RelayoutRows();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!TryGetScrollGeometry(out var rail, out var thumb)) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var railPath = SurfacePanel.RoundedPath(rail, rail.Width / 2);
        using var railBrush = new SolidBrush(Color.FromArgb(34, Palette.Muted));
        e.Graphics.FillPath(railBrush, railPath);
        using var thumbPath = SurfacePanel.RoundedPath(thumb, thumb.Width / 2);
        using var thumbBrush = new SolidBrush(Color.FromArgb(_draggingThumb ? 220 : 150, Palette.AccentDark));
        e.Graphics.FillPath(thumbBrush, thumbPath);
    }

    private bool TryGetScrollGeometry(out Rectangle rail, out Rectangle thumb)
    {
        rail = Rectangle.Empty;
        thumb = Rectangle.Empty;
        if (_contentHeight <= 0 || ClientSize.Width <= 0) return false;

        var viewportHeight = Math.Max(0, ClientSize.Height - Padding.Vertical);
        if (_contentHeight <= viewportHeight) return false;

        var railTop = Padding.Top;
        var railHeight = Math.Max(12, viewportHeight);
        var railX = ClientSize.Width - Padding.Right - ScrollRailWidth;
        rail = new Rectangle(railX - 5, railTop, ScrollRailWidth + 10, railHeight);

        var thumbHeight = Math.Max(28, (int)Math.Round(railHeight * (viewportHeight / (double)_contentHeight)));
        var maxOffset = Math.Max(1, _contentHeight - viewportHeight);
        var thumbTravel = Math.Max(0, railHeight - thumbHeight);
        var thumbY = railTop + (int)Math.Round(thumbTravel * (_scrollOffset / (double)maxOffset));
        thumb = new Rectangle(railX, thumbY, ScrollRailWidth, thumbHeight);
        return true;
    }

    private void SetScrollOffsetFromPointer(int y)
    {
        if (!TryGetScrollGeometry(out var rail, out var thumb)) return;
        var viewportHeight = Math.Max(0, ClientSize.Height - Padding.Vertical);
        var maxOffset = Math.Max(0, _contentHeight - viewportHeight);
        var travel = Math.Max(1, rail.Height - thumb.Height);
        var position = Math.Clamp(y - rail.Top - thumb.Height / 2, 0, travel);
        _scrollOffset = (int)Math.Round(position * maxOffset / (double)travel);
        RelayoutRows();
    }
}

internal sealed class ChampionRow : Panel
{
    public Champion Champion { get; }
    public event EventHandler? Clicked;
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            if (_avatarFallback is not null)
            {
                _avatarFallback.BackColor = _selected ? Palette.Accent : Palette.SurfaceRaised;
                _avatarFallback.ForeColor = _selected ? Color.White : Palette.Muted;
            }
            Invalidate();
        }
    }
    private bool _selected;
    private bool _hovered;
    private readonly CoverImage _avatarImage = new();
    private readonly Label _avatarFallback;

    public ChampionRow(Champion champion)
    {
        Champion = champion;
        Height = 48;
        Margin = new Padding(0, 0, 0, 4);
        BackColor = Palette.Sidebar;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = $"英雄 {champion.Name}，编号 {champion.Id}，{champion.SkinCount} 个皮肤";
        DoubleBuffered = true;
        _avatarFallback = new Label
        {
            Text = champion.Name[..1],
            AutoSize = false,
            Location = new Point(10, 10),
            Size = new Size(28, 28),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Palette.SurfaceRaised,
            ForeColor = Palette.Muted,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Padding = new Padding(0),
            Region = null
        };
        _avatarImage.Location = new Point(10, 10);
        _avatarImage.Size = new Size(28, 28);
        // The bitmap already contains an anti-aliased circular mask. Keeping
        // the host rectangular avoids a second low-resolution Region clip.
        _avatarImage.CornerRadius = 0;
        _avatarImage.BackColor = Palette.SurfaceRaised;
        _avatarImage.Visible = false;
        var name = new Label { Text = champion.Name, AutoSize = false, Location = new Point(46, 5), Size = new Size(145, 20), ForeColor = Palette.Text, Font = new Font("Segoe UI", 9F, FontStyle.Bold), BackColor = Color.Transparent };
        var meta = new Label { Text = $"{champion.SkinCount:N0} 个皮肤", AutoSize = false, Location = new Point(46, 26), Size = new Size(145, 16), ForeColor = Palette.Faint, Font = new Font("Segoe UI", 8F), BackColor = Color.Transparent };
        Controls.AddRange([meta, name, _avatarFallback, _avatarImage]);
        Hook(this);
        foreach (Control control in Controls) Hook(control);
    }

    public void SetAvatar(Image? image)
    {
        _avatarImage.Image = image;
        _avatarImage.Visible = image is not null;
        _avatarFallback.Visible = image is null;
    }

    private void Hook(Control control)
    {
        control.Click += (_, _) => { Focus(); Clicked?.Invoke(this, EventArgs.Empty); };
        control.MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        control.MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { Clicked?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        using var path = SurfacePanel.RoundedPath(rect, 9);
        var fill = _selected ? Palette.AccentSoft : (_hovered ? Palette.Surface : Palette.Sidebar);
        _avatarImage.BackColor = fill;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);
        if (_selected)
        {
            using var marker = new SolidBrush(Palette.Accent);
            e.Graphics.FillRectangle(marker, 1, 12, 3, Height - 24);
        }
        if (Focused)
        {
            using var pen = new Pen(Palette.Accent) { DashStyle = DashStyle.Dot };
            e.Graphics.DrawPath(pen, SurfacePanel.RoundedPath(new Rectangle(3, 3, Math.Max(0, Width - 7), Math.Max(0, Height - 7)), 7));
        }
        base.OnPaint(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // The antialiased path in OnPaint supplies the rounded silhouette.
        // A native Region is a 1-bit clip and leaves jagged dark pixels on
        // scaled displays.
        if (Region is not null) Region = null;
    }
}

internal sealed class SkinCard : Panel
{
    public Skin Skin { get; }
    public IReadOnlyList<Skin> Chromas { get; }
    public event EventHandler? Clicked;
    public event EventHandler<Skin>? ChromaSelected;
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }
    private bool _selected;
    private bool _hovered;
    private readonly CoverImage _image = new();
    private readonly ChromaPopupForm _chromaPopup;
    private readonly ChromaPickerPanel _chromaPicker;

    public SkinCard(Skin skin, IReadOnlyList<Skin> chromas)
    {
        Skin = skin;
        Chromas = chromas;
        Height = 214;
        Width = 184;
        Margin = new Padding(0, 0, 12, 12);
        BackColor = Palette.Surface;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = chromas.Count == 0
            ? $"皮肤 {skin.DisplayName}，编号 {skin.Id}"
            : $"皮肤 {skin.DisplayName}，编号 {skin.Id}，有 {chromas.Count} 个炫彩";
        DoubleBuffered = true;
        Padding = new Padding(1);
        _image.Dock = DockStyle.Top;
        _image.Height = 120;
        _image.CornerRadius = 10;
        _image.BackColor = Palette.SurfaceRaised;
        var name = new Label { Text = skin.DisplayName, Dock = DockStyle.Top, Height = 43, Padding = new Padding(10, 9, 7, 0), ForeColor = Palette.Text, Font = new Font("Segoe UI", 8.7F, FontStyle.Bold), AutoEllipsis = true, BackColor = Color.Transparent };
        var meta = new Label { Text = skin.Chroma ? $"#{skin.Id}  ·  炫彩条目" : $"#{skin.Id}  ·  可缓存", Dock = chromas.Count == 0 ? DockStyle.Fill : DockStyle.Top, Height = 21, Padding = new Padding(2, 0, 2, 0), ForeColor = Palette.Faint, Font = new Font("Segoe UI", 8F), BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft };
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(8, 0, 8, 6) };
        footer.Controls.Add(meta);

        _chromaPicker = new ChromaPickerPanel(skin, chromas);
        _chromaPopup = new ChromaPopupForm(_chromaPicker);
        _chromaPicker.Selected += (_, selected) =>
        {
            ChromaSelected?.Invoke(this, selected);
            _chromaPopup.HidePopup();
        };

        if (chromas.Count > 0)
        {
            var chromaButton = new RoundedButton($"炫彩 · {chromas.Count}", Palette.SurfaceSoft)
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = Palette.AccentDark,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                TabStop = true,
                AccessibleName = $"打开 {skin.DisplayName} 的炫彩列表"
            };
            chromaButton.Click += (_, _) =>
            {
                Focus();
                // Let the button's mouse-up message finish before activating
                // the owned popup. Showing a borderless form synchronously
                // here can immediately raise Deactivate and make it flash.
                if (IsDisposed || !IsHandleCreated || chromaButton.IsDisposed) return;
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (!IsDisposed && !chromaButton.IsDisposed)
                        _chromaPopup.ShowAt(chromaButton);
                }));
            };
            footer.Controls.Add(chromaButton);
        }

        Controls.Add(footer);
        Controls.Add(name);
        Controls.Add(_image);
        Hook(this);
        Hook(_image);
        Hook(name);
        Hook(meta);
        Hook(footer);
    }

    private void Hook(Control control)
    {
        control.Click += (_, _) => { Focus(); Clicked?.Invoke(this, EventArgs.Empty); };
        control.MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        control.MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
    }

    public bool ContainsSkin(int skinId) => Skin.Id == skinId || Chromas.Any(chroma => chroma.Id == skinId);

    public void SetImage(Image? image) { _image.Image = image; _image.Invalidate(); }


    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { Clicked?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        using var path = SurfacePanel.RoundedPath(rect, 10);
        var fill = _selected ? Palette.AccentSoft : (_hovered ? Palette.SurfaceSoft : Palette.Surface);
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);
        if (_selected)
        {
            using var pen = new Pen(Palette.Accent, 2);
            e.Graphics.DrawPath(pen, path);
        }
        else if (Focused)
        {
            using var pen = new Pen(Palette.Accent) { DashStyle = DashStyle.Dot };
            e.Graphics.DrawPath(pen, SurfacePanel.RoundedPath(new Rectangle(3, 3, Math.Max(0, Width - 7), Math.Max(0, Height - 7)), 8));
        }
        base.OnPaint(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Region is not null) Region = null;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var parentBackColor = Parent?.BackColor ?? Palette.Canvas;
        for (var parent = Parent?.Parent; parent is not null && parentBackColor == Color.Transparent; parent = parent.Parent)
            parentBackColor = parent.BackColor;
        if (parentBackColor == Color.Transparent) parentBackColor = Palette.Canvas;
        e.Graphics.Clear(parentBackColor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chromaPopup.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SkinMenuColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => Palette.Border;
    public override Color MenuItemBorder => Palette.AccentSoft;
    public override Color MenuItemSelected => Palette.AccentSoft;
    public override Color MenuItemSelectedGradientBegin => Palette.AccentSoft;
    public override Color MenuItemSelectedGradientEnd => Palette.AccentSoft;
    public override Color ToolStripDropDownBackground => Palette.Surface;
    public override Color ImageMarginGradientBegin => Palette.Surface;
    public override Color ImageMarginGradientMiddle => Palette.Surface;
    public override Color ImageMarginGradientEnd => Palette.Surface;
}

internal sealed class ChromaPopupForm : Form
{
    private readonly ChromaPickerPanel _picker;
    private readonly OutsideClickFilter _outsideClickFilter;
    private bool _closing;
    private bool _readyForDeactivate;
    private bool _filterRegistered;

    public ChromaPopupForm(ChromaPickerPanel picker)
    {
        _picker = picker;
        _outsideClickFilter = new OutsideClickFilter(this);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Padding = Padding.Empty;
        BackColor = Palette.Surface;
        DoubleBuffered = true;
        ClientSize = picker.Size;
        _picker.Dock = DockStyle.Fill;
        Controls.Add(_picker);
        _picker.TabStop = true;
        _picker.TabIndex = 0;
        Deactivate += (_, _) =>
        {
            if (!_readyForDeactivate || IsDisposed) return;
            BeginInvoke((MethodInvoker)(() =>
            {
                if (_closing || IsDisposed || ContainsFocus || ReferenceEquals(ActiveForm, this)) return;
                HidePopup();
            }));
        };
        FormClosed += (_, _) =>
        {
            _readyForDeactivate = false;
            _picker.CancelLoading();
        };
    }

    public void ShowAt(Control anchor)
    {
        if (IsDisposed || anchor.IsDisposed) return;
        var owner = anchor.FindForm();
        if (owner is null) return;

        if (Visible)
        {
            HidePopup();
            return;
        }
        _readyForDeactivate = false;
        Owner = owner;

        var workArea = Screen.FromControl(anchor).WorkingArea;
        var anchorPoint = anchor.PointToScreen(Point.Empty);
        const int gap = 6;
        const int edge = 8;
        var desiredWidth = Math.Min(_picker.Width, Math.Max(220, workArea.Width - edge * 2));
        var desiredHeight = _picker.Height;
        var belowY = anchorPoint.Y + anchor.Height + gap;
        var belowSpace = workArea.Bottom - belowY - edge;
        var aboveSpace = anchorPoint.Y - workArea.Top - gap - edge;
        var showBelow = belowSpace >= desiredHeight || belowSpace >= aboveSpace;
        var availableHeight = showBelow ? belowSpace : aboveSpace;
        var popupHeight = Math.Min(desiredHeight, Math.Max(120, availableHeight));
        var x = Math.Clamp(anchorPoint.X, workArea.Left + edge, workArea.Right - desiredWidth - edge);
        var y = showBelow
            ? belowY
            : anchorPoint.Y - popupHeight - gap;
        y = Math.Clamp(y, workArea.Top + edge, workArea.Bottom - popupHeight - edge);

        ClientSize = new Size(desiredWidth, popupHeight);
        _picker.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        UpdateRegion();
        Location = new Point(x, y);
        Show(owner);
        RegisterOutsideClickFilter();
        // Activate only after Show has completed and the original button
        // message has returned to the message loop. This prevents the first
        // Deactivate notification from closing a newly opened popup.
        BeginInvoke((MethodInvoker)(() =>
        {
            if (IsDisposed || !Visible) return;
            Activate();
            _picker.Focus();
            _readyForDeactivate = true;
        }));
        _ = _picker.LoadImagesAsync();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Surface);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnregisterOutsideClickFilter();
            _picker.CancelLoading();
        }
        base.Dispose(disposing);
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        Region = new Region(SurfacePanel.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12));
    }

    public void HidePopup()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        _readyForDeactivate = false;
        UnregisterOutsideClickFilter();
        _picker.CancelLoading();
        try { Hide(); }
        finally { _closing = false; }
    }

    private void RegisterOutsideClickFilter()
    {
        if (_filterRegistered || IsDisposed) return;
        Application.AddMessageFilter(_outsideClickFilter);
        _filterRegistered = true;
    }

    private void UnregisterOutsideClickFilter()
    {
        if (!_filterRegistered) return;
        Application.RemoveMessageFilter(_outsideClickFilter);
        _filterRegistered = false;
    }

    private sealed class OutsideClickFilter : IMessageFilter
    {
        private const int WmLButtonDown = 0x0201;
        private const int WmRButtonDown = 0x0204;
        private const int WmMButtonDown = 0x0207;
        private readonly ChromaPopupForm _popup;

        public OutsideClickFilter(ChromaPopupForm popup) => _popup = popup;

        public bool PreFilterMessage(ref Message message)
        {
            if (!_popup.Visible || _popup.IsDisposed || message.Msg is not (WmLButtonDown or WmRButtonDown or WmMButtonDown))
                return false;

            // Deactivate is not guaranteed for an owned borderless form. Use
            // the screen point from the same mouse message so a click on any
            // other control, including an empty area, dismisses the popup.
            if (_popup.Bounds.Contains(Control.MousePosition)) return false;
            if (_popup.IsHandleCreated)
                _popup.BeginInvoke((MethodInvoker)_popup.HidePopup);
            return false;
        }
    }
}

internal sealed class ChromaPickerPanel : Panel
{
    private const int PopupWidth = 340;
    private const int HeaderHeight = 44;
    private const int OptionHeight = 82;
    private const int OptionGap = 6;
    private const int PreviewWidth = 120;
    private const int PreviewHeight = 68;
    private readonly Panel _list = new();
    private readonly List<ChromaPickerOption> _options = [];
    private CancellationTokenSource? _loadCancellation;
    private bool _updatingOptionWidths;
    private int _contentHeight;
    private int _scrollOffset;

    public event EventHandler<Skin>? Selected;

    public ChromaPickerPanel(Skin baseSkin, IReadOnlyList<Skin> chromas)
    {
        var workingHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 800;
        Width = PopupWidth;
        Padding = new Padding(10, 8, 10, 8);
        // Keep the popup compact for a short list, while retaining a scrollable
        // viewport for skins with many chroma variants.
        var contentHeight = HeaderHeight + Padding.Vertical + chromas.Count * OptionHeight;
        if (chromas.Count > 1) contentHeight += (chromas.Count - 1) * OptionGap;
        Height = Math.Min(440, Math.Max(210, Math.Min(contentHeight, workingHeight - 48)));
        BackColor = Palette.Surface;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // Use explicit rows so the first option can never be laid out beneath
        // the header. Docking a Top header and a Fill list in one parent is
        // sensitive to child z-order in WinForms.
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderHeight));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(content);

        var header = new Panel { Dock = DockStyle.Fill, Height = HeaderHeight, BackColor = Color.Transparent, Margin = Padding.Empty };
        var title = new Label
        {
            Text = "炫彩预览",
            AutoSize = false,
            Location = new Point(0, 0),
            Size = new Size(220, 21),
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        var subtitle = new Label
        {
            Text = $"{baseSkin.DisplayName}  ·  {chromas.Count} 个可选炫彩",
            AutoSize = false,
            Location = new Point(0, 22),
            Size = new Size(300, 17),
            ForeColor = Palette.Muted,
            Font = new Font("Segoe UI", 8.5F),
            AutoEllipsis = true,
            BackColor = Color.Transparent
        };
        header.Controls.AddRange([subtitle, title]);
        content.Controls.Add(header, 0, 0);

        _list.Dock = DockStyle.Fill;
        // The popup owns its scroll offset so no native scrollbar arrows are
        // created. This also keeps the final option fully inside the viewport.
        _list.Padding = Padding.Empty;
        _list.Margin = Padding.Empty;
        _list.BackColor = Palette.Surface;
        _list.BorderStyle = BorderStyle.None;
        _list.Resize += (_, _) => LayoutOptions();
        content.Controls.Add(_list, 0, 1);

        for (var index = 0; index < chromas.Count; index++)
        {
            var chroma = chromas[index];
            var option = new ChromaPickerOption(chroma, PreviewWidth, PreviewHeight)
            {
                Height = OptionHeight,
                // Do not add a trailing gap after the final row. A trailing
                // margin can make FlowLayoutPanel show a needless scrollbar,
                // which in turn changes the available text width on open.
                Margin = new Padding(0, 0, 0, index == chromas.Count - 1 ? 0 : OptionGap)
            };
            option.Selected += (_, selected) => Selected?.Invoke(this, selected);
            _options.Add(option);
            _list.Controls.Add(option);
        }

        HookMouseWheel(_list);
        LayoutOptions();
        UpdateRegion();
    }

    public async Task LoadImagesAsync()
    {
        CancelLoading();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        try
        {
            await Task.WhenAll(_options.Select(option => option.LoadImageAsync(token)));
        }
        catch (OperationCanceledException) { }
    }

    public void CancelLoading()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private void LayoutOptions()
    {
        if (_list.ClientSize.Width <= 0) return;
        if (_updatingOptionWidths) return;
        _updatingOptionWidths = true;
        try
        {
            _contentHeight = _options.Sum(option => option.Height + option.Margin.Vertical);
            var maxOffset = Math.Max(0, _contentHeight - _list.ClientSize.Height);
            _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);

            var y = -_scrollOffset;
            foreach (var option in _options)
            {
                var margin = option.Margin;
                var width = Math.Max(1, _list.ClientSize.Width - margin.Horizontal);
                if (option.Width != width) option.Width = width;
                option.Location = new Point(margin.Left, y + margin.Top);
                y += margin.Vertical + option.Height;
            }
        }
        finally
        {
            _updatingOptionWidths = false;
        }
    }

    private void HookMouseWheel(Control control)
    {
        control.MouseWheel += (_, e) => ScrollByWheel(e.Delta);
        foreach (Control child in control.Controls) HookMouseWheel(child);
    }

    private void ScrollByWheel(int delta)
    {
        if (delta == 0) return;
        var maximum = Math.Max(0, _contentHeight - _list.ClientSize.Height);
        if (maximum <= 0) return;
        var step = Math.Max(32, OptionHeight / 2);
        var ticks = Math.Max(1, Math.Abs(delta) / 120);
        var target = Math.Clamp(_scrollOffset - Math.Sign(delta) * step * ticks, 0, maximum);
        if (target == _scrollOffset) return;
        _scrollOffset = target;
        LayoutOptions();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        Region = new Region(SurfacePanel.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
        LayoutOptions();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutOptions();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Surface);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelLoading();
            Region = null;
        }
        base.Dispose(disposing);
    }
}

internal sealed class ChromaPickerOption : Panel
{
    private readonly Skin _skin;
    private readonly CoverImage _preview;
    private readonly Label _name;
    private readonly Label _meta;
    private readonly Size _preferredPreviewSize;
    private bool _hovered;
    private bool _layingOut;

    public event EventHandler<Skin>? Selected;

    public ChromaPickerOption(Skin skin, int previewWidth, int previewHeight)
    {
        _skin = skin;
        _preferredPreviewSize = new Size(Math.Max(1, previewWidth), Math.Max(1, previewHeight));
        AutoSize = false;
        Margin = Padding.Empty;
        BackColor = Palette.Surface;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = $"炫彩 {skin.DisplayName}，编号 {skin.Id}";
        DoubleBuffered = true;
        Padding = new Padding(7, 6, 7, 6);

        _preview = new CoverImage
        {
            AutoSize = false,
            Margin = Padding.Empty,
            TabStop = false,
            CornerRadius = 8,
            BackColor = Palette.SurfaceRaised,
            Image = CreatePlaceholder(skin, previewWidth, previewHeight)
        };
        _name = new Label
        {
            AutoSize = false,
            Margin = Padding.Empty,
            TabStop = false,
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Text = skin.DisplayName
        };
        _meta = new Label
        {
            AutoSize = false,
            Margin = Padding.Empty,
            TabStop = false,
            ForeColor = Palette.Muted,
            Font = new Font("Segoe UI", 8F),
            Text = $"编号 {skin.Id}  ·  炫彩",
            BackColor = Color.Transparent
        };
        Controls.AddRange([_meta, _name, _preview]);
        Hook(this);
        Hook(_preview);
        Hook(_name);
        Hook(_meta);
    }

    public async Task LoadImageAsync(CancellationToken cancellationToken)
    {
        Image? image = null;
        try
        {
            var source = await PreviewImageResolver.LoadAsync(_skin, cancellationToken);
            if (source is null) return;
            try
            {
                // The menu can begin loading before WinForms has completed its
                // first layout pass. Use the preferred dimensions until the
                // preview control receives its final bounds.
                var width = _preview.Width > 0 ? _preview.Width : _preferredPreviewSize.Width;
                var height = _preview.Height > 0 ? _preview.Height : _preferredPreviewSize.Height;
                image = CreateRoundedThumbnail(source, width, height);
            }
            finally
            {
                source.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            _preview.Image = image;
            image = null;
        }
        catch (OperationCanceledException) { }
        finally { image?.Dispose(); }
    }

    private void Hook(Control control)
    {
        control.Click += (_, _) => Selected?.Invoke(this, _skin);
        control.MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        control.MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            Selected?.Invoke(this, _skin);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        using var path = SurfacePanel.RoundedPath(rect, 10);
        using var brush = new SolidBrush(_hovered || Focused ? Palette.AccentSoft : Palette.SurfaceSoft);
        e.Graphics.FillPath(brush, path);
        if (Focused)
        {
            using var pen = new Pen(Palette.Accent, 2);
            e.Graphics.DrawPath(pen, path);
        }
        base.OnPaint(e);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_layingOut)
        {
            base.OnLayout(levent);
            return;
        }

        _layingOut = true;
        try
        {
            base.OnLayout(levent);
            if (Width <= 0 || Height <= 0 || _preview is null || _name is null || _meta is null) return;

            var content = new Rectangle(
                Padding.Left,
                Padding.Top,
                Math.Max(0, ClientSize.Width - Padding.Horizontal),
                Math.Max(0, ClientSize.Height - Padding.Vertical));
            var textGap = 10;
            var minimumTextWidth = 110;
            var previewWidth = Math.Min(
                _preferredPreviewSize.Width,
                Math.Max(0, content.Width - textGap - minimumTextWidth));
            var previewHeight = Math.Min(_preferredPreviewSize.Height, content.Height);
            var previewY = content.Y + Math.Max(0, (content.Height - previewHeight) / 2);
            _preview.Bounds = new Rectangle(content.X, previewY, previewWidth, previewHeight);

            var textX = _preview.Right + textGap;
            var textWidth = Math.Max(0, content.Right - textX);
            var metaHeight = Math.Min(20, content.Height);
            var nameHeight = Math.Min(36, Math.Max(0, content.Height - metaHeight - 4));
            var textHeight = nameHeight + (metaHeight > 0 ? 4 + metaHeight : 0);
            var textY = content.Y + Math.Max(0, (content.Height - textHeight) / 2);
            _name.Bounds = new Rectangle(textX, textY, textWidth, nameHeight);
            _meta.Bounds = new Rectangle(textX, _name.Bottom + (metaHeight > 0 ? 4 : 0), textWidth, metaHeight);
        }
        finally
        {
            _layingOut = false;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Region is not null) Region = null;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Palette.Surface);
    }

    private static Image CreatePlaceholder(Skin skin, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Palette.SurfaceRaised);
        try
        {
            var color = ColorTranslator.FromHtml(skin.ChromaColor);
            using var swatch = new SolidBrush(color);
            graphics.FillEllipse(swatch, width / 2 - 12, height / 2 - 12, 24, 24);
        }
        catch (Exception) { }
        return bitmap;
    }

    private static Image CreateRoundedThumbnail(Image source, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        using var path = SurfacePanel.RoundedPath(new Rectangle(0, 0, width - 1, height - 1), 8);
        graphics.SetClip(path);
        // Chroma PNGs are not all the same aspect ratio. Fit the complete
        // source into the preview instead of cropping its top/bottom edges.
        var scale = Math.Min(
            width / (double)Math.Max(1, source.Width),
            height / (double)Math.Max(1, source.Height));
        var drawWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var drawHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        var destination = new Rectangle(
            Math.Max(0, (width - drawWidth) / 2),
            Math.Max(0, (height - drawHeight) / 2),
            drawWidth,
            drawHeight);
        graphics.DrawImage(source, destination, new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return bitmap;
    }
}

internal sealed class CoverImage : Panel
{
    private Image? _image;
    private Bitmap? _roundedImage;
    private int _cornerRadius;
    private bool _preserveAspectRatio;
    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(0, value);
            UpdateRegion();
            ClearRenderedImage();
            Invalidate();
        }
    }

    public bool PreserveAspectRatio
    {
        get => _preserveAspectRatio;
        set
        {
            if (_preserveAspectRatio == value) return;
            _preserveAspectRatio = value;
            ClearRenderedImage();
            Invalidate();
        }
    }

    public Image? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            _image?.Dispose();
            ClearRenderedImage();
            _image = value;
            Invalidate();
        }
    }

    public CoverImage()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Palette.SurfaceRaised;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
        ClearRenderedImage();
    }

    private void UpdateRegion()
    {
        // The image is clipped by the antialiased paint path below. A native
        // Region clip is intentionally avoided because its 1-bit edge leaves
        // visible stair-step pixels on high-DPI screenshots.
        if (Region is not null) Region = null;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var parentBackColor = Parent?.BackColor ?? Palette.Canvas;
        for (var parent = Parent?.Parent; parent is not null && parentBackColor == Color.Transparent; parent = parent.Parent)
            parentBackColor = parent.BackColor;
        if (parentBackColor == Color.Transparent) parentBackColor = Palette.Canvas;
        e.Graphics.Clear(parentBackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var parentBackColor = Parent?.BackColor ?? Palette.Canvas;
        for (var parent = Parent?.Parent; parent is not null && parentBackColor == Color.Transparent; parent = parent.Parent)
            parentBackColor = parent.BackColor;
        if (parentBackColor == Color.Transparent) parentBackColor = Palette.Canvas;
        e.Graphics.Clear(parentBackColor);
        var state = e.Graphics.Save();
        if (_cornerRadius > 0)
        {
            var rendered = GetRenderedImage();
            if (rendered is not null)
            {
                e.Graphics.CompositingMode = CompositingMode.SourceOver;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImageUnscaled(rendered, 0, 0);
            }
            else
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = SurfacePanel.RoundedPath(
                    new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)),
                    Math.Min(_cornerRadius, Math.Min(Width, Height) / 2));
                using var brush = new SolidBrush(BackColor);
                e.Graphics.FillPath(brush, path);
            }
            e.Graphics.Restore(state);
            base.OnPaint(e);
            return;
        }
        e.Graphics.Clear(BackColor);
        if (_image is not null)
        {
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (_preserveAspectRatio)
            {
                var scale = Math.Min(
                    Width / (double)Math.Max(1, _image.Width),
                    Height / (double)Math.Max(1, _image.Height));
                var drawWidth = Math.Max(1, (int)Math.Round(_image.Width * scale));
                var drawHeight = Math.Max(1, (int)Math.Round(_image.Height * scale));
                var destination = new Rectangle(
                    Math.Max(0, (Width - drawWidth) / 2),
                    Math.Max(0, (Height - drawHeight) / 2),
                    drawWidth,
                    drawHeight);
                e.Graphics.DrawImage(_image, destination, new Rectangle(0, 0, _image.Width, _image.Height), GraphicsUnit.Pixel);
            }
            else
            {
                var sourceRatio = _image.Width / (double)Math.Max(1, _image.Height);
                var targetRatio = Width / (double)Math.Max(1, Height);
                var source = new Rectangle(0, 0, _image.Width, _image.Height);
                if (sourceRatio > targetRatio)
                {
                    var crop = (int)(_image.Width - _image.Height * targetRatio);
                    source = new Rectangle(crop / 2, 0, _image.Width - crop, _image.Height);
                }
                else if (sourceRatio < targetRatio)
                {
                    var crop = (int)(_image.Height - _image.Width / targetRatio);
                    source = new Rectangle(0, crop / 2, _image.Width, _image.Height - crop);
                }
                e.Graphics.DrawImage(_image, ClientRectangle, source, GraphicsUnit.Pixel);
            }
        }
        e.Graphics.Restore(state);
        base.OnPaint(e);
    }

    private Bitmap? GetRenderedImage()
    {
        if (_image is null || Width <= 0 || Height <= 0) return null;
        if (_roundedImage is not null) return _roundedImage;

        const int supersample = 4;
        var largeWidth = Width * supersample;
        var largeHeight = Height * supersample;
        using var large = new Bitmap(largeWidth, largeHeight, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(large))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var radius = Math.Min(_cornerRadius, Math.Min(Width, Height) / 2) * supersample;
            using var path = SurfacePanel.RoundedPath(new Rectangle(0, 0, largeWidth - 1, largeHeight - 1), radius);
            graphics.SetClip(path);
            if (_preserveAspectRatio)
            {
                graphics.Clear(BackColor);
                var scale = Math.Min(
                    largeWidth / (double)Math.Max(1, _image.Width),
                    largeHeight / (double)Math.Max(1, _image.Height));
                var drawWidth = Math.Max(1, (int)Math.Round(_image.Width * scale));
                var drawHeight = Math.Max(1, (int)Math.Round(_image.Height * scale));
                var destination = new Rectangle(
                    Math.Max(0, (largeWidth - drawWidth) / 2),
                    Math.Max(0, (largeHeight - drawHeight) / 2),
                    drawWidth,
                    drawHeight);
                graphics.DrawImage(_image, destination, new Rectangle(0, 0, _image.Width, _image.Height), GraphicsUnit.Pixel);
            }
            else
            {
                var sourceRatio = _image.Width / (double)Math.Max(1, _image.Height);
                var targetRatio = largeWidth / (double)Math.Max(1, largeHeight);
                var source = new Rectangle(0, 0, _image.Width, _image.Height);
                if (sourceRatio > targetRatio)
                {
                    var crop = (int)Math.Round(_image.Width - _image.Height * targetRatio);
                    source = new Rectangle(crop / 2, 0, Math.Max(1, _image.Width - crop), _image.Height);
                }
                else if (sourceRatio < targetRatio)
                {
                    var crop = (int)Math.Round(_image.Height - _image.Width / targetRatio);
                    source = new Rectangle(0, crop / 2, _image.Width, Math.Max(1, _image.Height - crop));
                }
                graphics.DrawImage(_image, new Rectangle(0, 0, largeWidth, largeHeight), source, GraphicsUnit.Pixel);
            }
        }

        _roundedImage = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(_roundedImage))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(large, new Rectangle(0, 0, Width, Height), 0, 0, largeWidth, largeHeight, GraphicsUnit.Pixel);
        }
        return _roundedImage;
    }

    private void ClearRenderedImage()
    {
        _roundedImage?.Dispose();
        _roundedImage = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearRenderedImage();
            _image?.Dispose();
            _image = null;
        }
        base.Dispose(disposing);
    }
}
