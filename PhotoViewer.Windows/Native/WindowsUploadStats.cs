using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PhotoViewer.Windows.Native;

internal static class WindowsUploadStats
{
    private const int Capacity = 512;
    private static readonly object Gate = new();
    private static readonly Queue<Entry> Uploads = new(Capacity);
    private static readonly Queue<(double Render, double Present)> Draws = new(Capacity);
    private static readonly Queue<double> Submissions = new(Capacity);
    private static int _hits;
    private static int _reused;

    internal readonly record struct Entry(int Width, int Height, bool Staged, double StagingCreateMs,
        double MapMs, double CreateMs, double UpdateMs, double MipsMs, double BackgroundMs,
        double WallMs)
    {
        internal double UiMs => StagingCreateMs + MapMs + CreateMs + UpdateMs + MipsMs;
    }

    internal static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    internal static void Record(Entry entry)
    {
        lock (Gate) Append(Uploads, entry);
    }

    internal static void RecordCacheHit() { lock (Gate) _hits++; }
    internal static void RecordReuse() { lock (Gate) _reused++; }
    internal static void RecordSubmission(double ms) { lock (Gate) Append(Submissions, ms); }
    internal static void RecordDraw(double renderMs, double presentMs)
    {
        lock (Gate) Append(Draws, (renderMs, presentMs));
    }

    private static void Append<T>(Queue<T> queue, T value)
    {
        if (queue.Count == Capacity) queue.Dequeue();
        queue.Enqueue(value);
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            Uploads.Clear();
            Draws.Clear();
            Submissions.Clear();
            _hits = _reused = 0;
        }
    }

    internal static string Summary()
    {
        Entry[] uploads;
        (double Render, double Present)[] draws;
        double[] submissions;
        int hits, reused;
        lock (Gate)
        {
            uploads = Uploads.ToArray();
            draws = Draws.ToArray();
            submissions = Submissions.ToArray();
            hits = _hits;
            reused = _reused;
        }

        var text = new StringBuilder(1024);
        text.Append("uploads=").Append(uploads.Length).Append(" up_hits=").Append(hits)
            .Append(" up_reused=").Append(reused);
        Add(text, "up_sync", Select(uploads, false, e => e.UiMs));
        Add(text, "up_async", Select(uploads, true, e => e.UiMs));
        Add(text, "up_async_wall", Select(uploads, true, e => e.WallMs));
        Add(text, "up_staging_create", Select(uploads, true, e => e.StagingCreateMs));
        Add(text, "up_map", Select(uploads, true, e => e.MapMs));
        Add(text, "up_create", Select(uploads, null, e => e.CreateMs));
        Add(text, "up_copy", Select(uploads, null, e => e.UpdateMs));
        Add(text, "up_mips", Select(uploads, null, e => e.MipsMs));
        Add(text, "up_bg", Select(uploads, true, e => e.BackgroundMs));
        Add(text, "frame_submit", submissions);
        var render = new double[draws.Length];
        var present = new double[draws.Length];
        for (int i = 0; i < draws.Length; i++)
        {
            render[i] = draws[i].Render;
            present[i] = draws[i].Present;
        }
        Add(text, "render", render);
        Add(text, "present", present);
        if (uploads.Length > 0) text.Append(" up_last=").Append(uploads[^1].Width).Append('x').Append(uploads[^1].Height);
        return text.ToString();
    }

    private static double[] Select(Entry[] entries, bool? staged, Func<Entry, double> selector)
    {
        var values = new List<double>(entries.Length);
        foreach (var entry in entries)
            if (!staged.HasValue || entry.Staged == staged.Value) values.Add(selector(entry));
        return values.ToArray();
    }

    private static void Add(StringBuilder text, string name, double[] values)
    {
        text.Append(' ').Append(name).Append("_n=").Append(values.Length);
        if (values.Length == 0) return;
        Array.Sort(values);
        int slow = 0;
        foreach (double value in values) if (value > 50) slow++;
        text.Append(' ').Append(name).Append("_p50=").Append(F(values[(values.Length - 1) / 2]))
            .Append(' ').Append(name).Append("_p95=").Append(F(values[Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1)]))
            .Append(' ').Append(name).Append("_max=").Append(F(values[^1]))
            .Append(' ').Append(name).Append("_over50=").Append(slow);
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
}
