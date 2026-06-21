using System.Diagnostics;

namespace WindowsCleanNotifs.NotificationInspector;

internal interface IDashboardBrowserLauncher
{
    void Open(Uri dashboardUri);
}

internal sealed class ShellDashboardBrowserLauncher : IDashboardBrowserLauncher
{
    public void Open(Uri dashboardUri)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = dashboardUri.ToString(),
            UseShellExecute = true
        });

        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start a browser process.");
        }
    }
}

internal sealed class DashboardBrowserOpener
{
    private readonly IDashboardBrowserLauncher _launcher;
    private readonly TextWriter _error;
    private bool _opened;

    public DashboardBrowserOpener(IDashboardBrowserLauncher launcher, TextWriter error)
    {
        _launcher = launcher;
        _error = error;
    }

    public void OpenAfterServerStarted(bool enabled, Uri dashboardUri)
    {
        if (!enabled || _opened)
        {
            return;
        }

        _opened = true;
        try
        {
            _launcher.Open(dashboardUri);
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Warning: could not open dashboard browser: {ex.Message}");
        }
    }
}
