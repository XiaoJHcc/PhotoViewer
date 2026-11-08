#if PHOTOVIEWER_DINO
using System.Diagnostics;
using PhotoViewer.Core.AI;
using PhotoViewer.Core.Database;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Apple;

internal static class AppleBatchThroughputChecks
{
    private const int FixtureCount = 8;

    internal static async Task RunAsync(string sourcePath, string outputDirectory, IMemoryBudgetProvider budget)
    {
        var lines = new List<string>();
        string reportPath = Path.Combine(outputDirectory, "native-batch-throughput.txt");
        string fixtureDirectory = Path.Combine(outputDirectory, $"batch-fixtures-{Guid.NewGuid():N}");
        var pressure = new SuiteBudgetProvider(budget);
        var stopwatch = new Stopwatch();
        var sampler = CancellationTokenSource.CreateLinkedTokenSource(BatchAnalysisContext.ApplicationToken);
        long startFootprint = 0;
        long peakFootprint = 0;
        long peakReserved = 0;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(fixtureDirectory);
            for (int index = 0; index < FixtureCount; index++)
                File.Copy(sourcePath, Path.Combine(fixtureDirectory, $"batch-{index:00}.jpg"));

            await PhotoDatabase.DeleteDatabaseAsync();
            pressure.AvailablePermille = 300;
            MemoryGovernor.Initialize(pressure, Path.Combine(outputDirectory, "memory-events-batch.jsonl"));
            if (MemoryGovernor.CurrentLevel is not (PressureLevel.Caution or PressureLevel.Warning))
                throw new InvalidOperationException($"Caution 起跑失败：{MemoryGovernor.CurrentLevel}");

            var files = Directory.GetFiles(fixtureDirectory, "*.jpg")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new ImageFile(new LocalFileRef(path)))
                .ToArray();
            var groups = await FolderFeatureIndexer.GroupByFingerprintAsync(files);
            if (groups.Count != FixtureCount)
                throw new InvalidOperationException($"指纹组数 {groups.Count}，预期 {FixtureCount}");

            var indexer = new FolderFeatureIndexer();
            var completed = new List<int>();
            int switched = 0;
            indexer.ProgressChanged += progress =>
            {
                lock (completed) completed.Add(progress.Completed);
                if (progress.Completed == 1 && Interlocked.Exchange(ref switched, 1) == 0)
                {
                    pressure.AvailablePermille = 200;
                    MemoryGovernor.EvaluateNow();
                }
            };

            startFootprint = budget.GetFootprintBytes();
            peakFootprint = startFootprint;
            var sampling = Task.Run(async () =>
            {
                while (!sampler.IsCancellationRequested)
                {
                    peakFootprint = Math.Max(peakFootprint, budget.GetFootprintBytes());
                    peakReserved = Math.Max(peakReserved, MemoryGovernor.ReservedBytes);
                    try { await Task.Delay(20, sampler.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });

            stopwatch.Start();
            await indexer.RunAsync(files);
            stopwatch.Stop();
            await sampler.CancelAsync();
            await sampling;

            if (indexer.LastError != null) throw new InvalidOperationException(indexer.LastError);
            if (switched == 0 || MemoryGovernor.CurrentLevel != PressureLevel.Warning)
                throw new InvalidOperationException($"首组后未进入 Warning：{MemoryGovernor.CurrentLevel}");
            lock (completed)
            {
                if (completed.Count != FixtureCount + 1
                    || completed[0] != 0
                    || !completed.Skip(1).SequenceEqual(Enumerable.Range(1, FixtureCount)))
                    throw new InvalidOperationException($"进度序列异常：{string.Join(",", completed)}");
            }

            foreach (var group in groups)
            {
                var missing = await FolderFeatureIndexer.EvaluateMissingPartsAsync(group.Fingerprint);
                if (missing.AnyMissing)
                    throw new InvalidOperationException($"三路数据不齐：{group.Representative.Name}");
                var cv = await PhotoDatabase.ReadCvGridAsync(group.Fingerprint);
                if (cv == null || cv.Value.ImageWidth <= 0 || cv.Value.ImageHeight <= 0)
                    throw new InvalidOperationException($"CV 尺寸未落库：{group.Representative.Name}");
            }

            lines.Add("PASS Caution 起跑，首组后进入 Warning，8 组 CLS/patch/CV 全部落库，进度每组一次");
            lines.Add($"wallMs={stopwatch.Elapsed.TotalMilliseconds:F1} groupsPerSecond={FixtureCount / stopwatch.Elapsed.TotalSeconds:F2} ep={(DinoFeatureExtractor.UsesGpuExecutionProvider ? "CoreML" : "CPU")} processors={Environment.ProcessorCount}");
            lines.Add($"footprintMB={startFootprint / 1048576.0:F0}->{peakFootprint / 1048576.0:F0}->{budget.GetFootprintBytes() / 1048576.0:F0} peakReservedMB={peakReserved / 1048576.0:F0} pressure={MemoryGovernor.CurrentLevel}");
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL {ex}");
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await sampler.CancelAsync();
            sampler.Dispose();
            MemoryGovernor.Initialize(budget, Path.Combine(outputDirectory, "memory-events-batch.jsonl"));
            await File.WriteAllLinesAsync(reportPath, lines);
            if (Directory.Exists(fixtureDirectory)) Directory.Delete(fixtureDirectory, true);
            Console.WriteLine($"[BatchThroughput] {reportPath}");
        }
    }

    private sealed class SuiteBudgetProvider(IMemoryBudgetProvider inner) : IMemoryBudgetProvider
    {
        private int _availablePermille = -1;

        internal int AvailablePermille
        {
            get => Volatile.Read(ref _availablePermille);
            set => Volatile.Write(ref _availablePermille, value);
        }

        public long GetBudgetBytes() => inner.GetBudgetBytes();
        public long GetFootprintBytes() => inner.GetFootprintBytes();
        public SystemMemoryInfo GetSystemMemoryInfo() => inner.GetSystemMemoryInfo();

        public long GetAvailableBytes()
        {
            long budget = inner.GetBudgetBytes();
            long real = inner.GetAvailableBytes();
            int permille = Volatile.Read(ref _availablePermille);
            if (permille < 0 || budget <= 0) return real;
            long cap = (long)((double)budget * permille / 1000.0);
            return real == long.MaxValue ? cap : Math.Min(real, cap);
        }
    }
}
#endif
