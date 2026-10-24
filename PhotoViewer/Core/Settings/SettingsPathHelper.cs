namespace PhotoViewer.Core.Settings;

public static class SettingsPathHelper
{
    public static string GetDefaultPath(string appFolderName)
    {
        var baseDir = GetBaseDirectory();
        var dir = Path.Combine(baseDir, appFolderName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static string GetBaseDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS())
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (OperatingSystem.IsMacOS())
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig)) return xdgConfig;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config");
    }
}
