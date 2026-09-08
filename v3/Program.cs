using System.Windows.Forms;
using CskinNative.Services;

namespace CskinNative;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var updateResult = CheckForUpdate();
        while (updateResult.Status == UpdateGateStatus.Required && updateResult.Manifest is not null)
        {
            using var updateForm = new UpdateRequiredForm(updateResult.Manifest, UpdatePolicy.CurrentVersionText);
            if (updateForm.ShowDialog() != DialogResult.OK) return;
            updateResult = CheckForUpdate();
        }

        var requireLicense = AuthorizationService.IsEnabled;
        while (true)
        {
            if (requireLicense)
            {
                using (var licenseForm = new LicenseForm())
                {
                    if (licenseForm.ShowDialog() != DialogResult.OK) return;
                }
            }

            using var mainForm = new MainForm();
            Application.Run(mainForm);
            if (!mainForm.RequestLicenseChange) return;
            requireLicense = true;
        }
    }

    private static UpdateCheckResult CheckForUpdate()
    {
        try
        {
            using var client = new UpdateClient();
            var result = client.CheckAsync(UpdatePolicy.CurrentVersionText).GetAwaiter().GetResult();
            if (result.Status == UpdateGateStatus.Required)
                AppLog.Warn($"强制更新门槛命中：current={UpdatePolicy.CurrentVersionText} minimum={result.Manifest?.MinimumVersion} endpoint={result.Endpoint}");
            else if (result.Status == UpdateGateStatus.Unavailable)
                AppLog.Warn($"版本检查不可用，继续启动：{result.Error}");
            else
                AppLog.Info($"版本检查通过：current={UpdatePolicy.CurrentVersionText} endpoint={result.Endpoint}");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error("启动版本检查异常，继续启动", ex);
            return UpdateCheckResult.Unavailable("版本检查异常");
        }
    }
}
