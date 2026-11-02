using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ATL;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.BpmTagger.Analysis;
using Jellyfin.Plugin.BpmTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BpmTagger.ScheduledTasks;

/// <summary>
/// Scheduled task that analyses music tracks and saves their tempo.
/// </summary>
public class DetectBpmTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<DetectBpmTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectBpmTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaEncoder">Media encoder, used for its ffmpeg path.</param>
    /// <param name="logger">Logger.</param>
    public DetectBpmTask(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, ILogger<DetectBpmTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Detect BPM of music";

    /// <inheritdoc />
    public string Key => "BpmTaggerDetectBpm";

    /// <inheritdoc />
    public string Description => "Analyses music tracks without a BPM, then adds a BPM tag in Jellyfin.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Nightly at 03:00, after the usual library scan.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var prefix = string.IsNullOrWhiteSpace(config.TagPrefix) ? "BPM:" : config.TagPrefix;
        var minBpm = Math.Clamp(config.MinBpm, 30, 300);
        var maxBpm = Math.Clamp(config.MaxBpm, minBpm * 2, 400); // the range must span an octave for folding to work

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false,
        });

        // Tracks already carrying our Jellyfin tag were handled on an earlier run; skip them without opening the file.
        var work = items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .Where(i => config.OverwriteExisting || !HasBpmTag(i, prefix))
            .ToList();
        if (config.MaxTracksPerRun > 0 && work.Count > config.MaxTracksPerRun)
        {
            work = work.Take(config.MaxTracksPerRun).ToList();
        }

        _logger.LogInformation("BPM Tagger: {Count} of {Total} tracks to process", work.Count, items.Count);
        if (work.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var detector = new BpmDetector(_mediaEncoder.EncoderPath);
        var dbLock = new SemaphoreSlim(1, 1);
        int done = 0, detected = 0, fromFile = 0, failed = 0;

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(config.Parallelism, 1, 8), CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                try
                {
                    var outcome = await ProcessAsync(item, detector, config, prefix, minBpm, maxBpm, dbLock, ct).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case Outcome.Detected: Interlocked.Increment(ref detected); break;
                        case Outcome.FromFile: Interlocked.Increment(ref fromFile); break;
                        case Outcome.Failed: Interlocked.Increment(ref failed); break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "BPM Tagger: could not process {Path}", item.Path);
                }

                progress.Report(100.0 * Interlocked.Increment(ref done) / work.Count);
            }).ConfigureAwait(false);

        _logger.LogInformation(
            "BPM Tagger finished: {Detected} detected, {FromFile} already had a BPM in the file, {Failed} skipped or failed",
            detected,
            fromFile,
            failed);
    }

    private async Task<Outcome> ProcessAsync(
        BaseItem item,
        BpmDetector detector,
        PluginConfiguration config,
        string prefix,
        int minBpm,
        int maxBpm,
        SemaphoreSlim dbLock,
        CancellationToken cancellationToken)
    {
        var path = item.Path;
        if (!File.Exists(path))
        {
            return Outcome.Failed;
        }

        var existing = (double?)new Track(path).BPM;
        int bpm;
        Outcome outcome;

        if (existing is > 0 && !config.OverwriteExisting)
        {
            bpm = (int)Math.Round(existing.Value, MidpointRounding.AwayFromZero);
            outcome = Outcome.FromFile;
        }
        else
        {
            var result = await detector.DetectAsync(path, minBpm, maxBpm, Math.Clamp(config.AnalyseSeconds, 15, 600), cancellationToken).ConfigureAwait(false);
            if (result is null || result.Confidence < config.MinConfidence)
            {
                _logger.LogInformation(
                    "BPM Tagger: no clear tempo in {Path} (confidence {Confidence:F2})",
                    path,
                    result?.Confidence ?? 0);
                return Outcome.Failed;
            }

            bpm = (int)Math.Round(result.Bpm, MidpointRounding.AwayFromZero);
            outcome = Outcome.Detected;
            _logger.LogDebug("BPM Tagger: {Path} = {Bpm} BPM (confidence {Confidence:F2})", path, bpm, result.Confidence);
        }

        await SetJellyfinTagAsync(item, prefix, bpm, dbLock, cancellationToken).ConfigureAwait(false);

        return outcome;
    }

    private async Task SetJellyfinTagAsync(BaseItem item, string prefix, int bpm, SemaphoreSlim dbLock, CancellationToken cancellationToken)
    {
        var newTag = prefix + bpm.ToString(CultureInfo.InvariantCulture);
        var tags = (item.Tags ?? [])
            .Where(t => !t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Append(newTag)
            .ToArray();

        if (item.Tags is not null && tags.Length == item.Tags.Length && item.Tags.Contains(newTag, StringComparer.Ordinal))
        {
            return; // already correct
        }

        item.Tags = tags;
        await dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            dbLock.Release();
        }
    }

    private static bool HasBpmTag(BaseItem item, string prefix)
        => item.Tags?.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true;

    private enum Outcome
    {
        Detected,
        FromFile,
        Failed,
    }
}
