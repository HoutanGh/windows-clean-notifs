namespace WindowsCleanNotifs.NotificationInspector;

public static class StoragePaths
{
    public static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Could not resolve the per-user LocalApplicationData directory.");
        }

        return Path.Combine(localAppData, "WindowsCleanNotifs", "notifications.db");
    }
}
