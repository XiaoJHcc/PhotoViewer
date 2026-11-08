#if PHOTOVIEWER_DINO
using PhotoViewer.Apple;
using PhotoViewer.iOS.Core;

namespace PhotoViewer.iOS.Native;

internal static class iOSNativeBatchCheck
{
    private const string Switch = "--pv-native-batch-check";

    internal static string? DatabasePath(string[] args)
    {
        if (!args.Contains(Switch, StringComparer.Ordinal)) return null;
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer", "acceptance");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "batch-throughput.db");
    }

    public static void MaybeStart(string[] args)
    {
        string? source = null;
        for (int index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], Switch, StringComparison.Ordinal)) source = args[index + 1];
        if (source == null) return;

        string path = Path.IsPathRooted(source)
            ? source
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), source);
        _ = RunAsync(path);
    }

    private static async Task RunAsync(string source)
    {
        string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PhotoViewer");
        try
        {
            await AppleBatchThroughputChecks.RunAsync(source, output, new iOSMemoryBudgetProvider());
            Console.WriteLine("[NativeSmoke] PASS batch-throughput");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[NativeSmoke] FAIL batch-throughput " + ex);
        }
    }
}
#endif
