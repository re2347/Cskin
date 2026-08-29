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
}
