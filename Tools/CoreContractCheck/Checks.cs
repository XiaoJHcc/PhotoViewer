using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

/// <summary>逐项断言的轻量检查器：通过项即时输出，失败项累计并附具体证据。</summary>
internal sealed class Checks
{
    public int Failures { get; private set; }

    public int Passed { get; private set; }

    public int Skipped { get; private set; }

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title);
    }

    public void Pass(string item)
    {
        Passed++;
        Console.WriteLine("  PASS " + item);
    }

    public void Skip(string item, string reason)
    {
        Skipped++;
        Console.WriteLine($"  SKIP {item}（{reason}）");
    }

    public void Fail(string item, string detail)
    {
        Failures++;
        Console.WriteLine($"  FAIL {item}：{detail}");
    }

    public void True(string item, bool condition, string detail = "条件不成立")
    {
        if (condition) Pass(item);
        else Fail(item, detail);
    }

    public void Equal<T>(string item, T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) Pass(item);
        else Fail(item, $"期望 {Format(expected)}，实际 {Format(actual)}");
    }

    public void SameId(string item, StorageItemId expected, StorageItemId actual) => Equal(item, expected, actual);

    public void Bytes(string item, byte[] expected, byte[] actual)
    {
        if (expected.AsSpan().SequenceEqual(actual))
        {
            Pass(item);
            return;
        }

        var common = Math.Min(expected.Length, actual.Length);
        var index = 0;
        while (index < common && expected[index] == actual[index]) index++;
        Fail(item,
            $"长度 期望 {expected.Length} 实际 {actual.Length}，首个差异 @{index}；"
            + $"期望 {Hex(expected)}，实际 {Hex(actual)}");
    }

    public void ThrowsExact<TException>(string item, Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            if (exception.GetType() == typeof(TException)) Pass(item);
            else Fail(item, $"期望 {typeof(TException).Name}，实际 {Describe(exception)}");
            return;
        }

        Fail(item, $"期望 {typeof(TException).Name}，实际未抛异常");
    }

    public async Task ThrowsExactAsync<TException>(string item, Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception.GetType() == typeof(TException)) Pass(item);
            else Fail(item, $"期望 {typeof(TException).Name}，实际 {Describe(exception)}");
            return;
        }

        Fail(item, $"期望 {typeof(TException).Name}，实际未抛异常");
    }

    public async Task ThrowsAssignableAsync<TException>(string item, Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is TException) Pass(item);
            else Fail(item, $"期望 {typeof(TException).Name} 或其子类，实际 {Describe(exception)}");
            return;
        }

        Fail(item, $"期望 {typeof(TException).Name}，实际未抛异常");
    }

    public async Task StorageFailsAsync(string item, StorageError expected, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportStorageFailure(item, expected, exception);
            return;
        }

        Fail(item, $"期望 StorageException({expected})，实际未抛异常");
    }

    /// <summary>目录内不得残留原子写临时文件（.tmp）或备份（.bak）。</summary>
    public void NoTempArtifacts(string item, string directory)
    {
        var leftovers = Directory.GetFileSystemEntries(directory)
            .Where(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();
        True(item, leftovers.Length == 0,
            leftovers.Length == 0 ? string.Empty : "残留 " + string.Join(", ", leftovers));
    }

    private void ReportStorageFailure(string item, StorageError expected, Exception exception)
    {
        if (exception is StorageException storage)
        {
            if (storage.Error == expected) Pass(item);
            else Fail(item, $"期望 StorageException({expected})，实际 StorageException({storage.Error})：{storage.Message}");
            return;
        }

        Fail(item, $"期望 StorageException({expected})，实际 {Describe(exception)}");
    }

    private static string Describe(Exception exception) => exception switch
    {
        StorageException storage => $"StorageException({storage.Error})：{storage.Message}",
        _ => $"{exception.GetType().Name}：{exception.Message}",
    };

    private static string Format<T>(T value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        byte[] bytes => Hex(bytes),
        _ => value.ToString() ?? "null",
    };

    private static string Hex(byte[] bytes) => bytes.Length <= 32
        ? Convert.ToHexString(bytes)
        : Convert.ToHexString(bytes.AsSpan(0, 32)) + "…";
}
