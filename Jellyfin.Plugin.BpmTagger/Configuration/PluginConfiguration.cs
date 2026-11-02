using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BpmTagger.Configuration;

/// <summary>
/// Settings for BPM Tagger.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the lowest tempo to report. Slower results are doubled into range.
    /// </summary>
    public int MinBpm { get; set; } = 70;

    /// <summary>
    /// Gets or sets the highest tempo to report. Faster results are halved into range.
    /// </summary>
    public int MaxBpm { get; set; } = 180;

    /// <summary>
    /// Gets or sets a value indicating whether tracks that already have a BPM are re-analysed and their Jellyfin tag replaced.
    /// </summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    /// Gets or sets the prefix used for the Jellyfin tag, such as "BPM:128", which lets smart playlists
    /// filter on tempo. "BPM:" matches the MusicTags plugin's format.
    /// </summary>
    public string TagPrefix { get; set; } = "BPM:";

    /// <summary>
    /// Gets or sets how many seconds of each track are analysed.
    /// </summary>
    public int AnalyseSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the minimum confidence (0–1) needed before a result is saved.
    /// </summary>
    public double MinConfidence { get; set; } = 0.3;

    /// <summary>
    /// Gets or sets how many tracks are analysed at once.
    /// </summary>
    public int Parallelism { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of tracks analysed per task run (0 = no limit).
    /// </summary>
    public int MaxTracksPerRun { get; set; }
}
