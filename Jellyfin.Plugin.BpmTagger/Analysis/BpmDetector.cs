using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.BpmTagger.Analysis;

/// <summary>
/// Result of a tempo analysis.
/// </summary>
/// <param name="Bpm">Detected tempo in beats per minute.</param>
/// <param name="Confidence">Rough 0–1 measure of how periodic the onsets were. Low values mean "guess".</param>
public sealed record BpmResult(double Bpm, double Confidence);

/// <summary>
/// Detects the tempo of an audio file.
/// Audio is decoded with ffmpeg (Jellyfin's own copy), turned into an onset-strength envelope via
/// spectral flux, and the tempo is chosen from the envelope's autocorrelation using a harmonic
/// comb and a gentle preference for tempos around 120 BPM.
/// </summary>
public sealed class BpmDetector
{
    private const int SampleRate = 11025;
    private const int FrameSize = 1024;
    private const int HopSize = 128;
    private const double FramesPerSecond = (double)SampleRate / HopSize; // ~86 Hz

    private readonly string _ffmpegPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="BpmDetector"/> class.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg executable.</param>
    public BpmDetector(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Analyses a file and returns its tempo, or null when there is not enough audio.
    /// </summary>
    /// <param name="path">Audio file path.</param>
    /// <param name="minBpm">Lowest tempo to report; results are folded into the range by doubling or halving.</param>
    /// <param name="maxBpm">Highest tempo to report.</param>
    /// <param name="analyseSeconds">How many seconds of audio to analyse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detected tempo, or null.</returns>
    public async Task<BpmResult?> DetectAsync(string path, double minBpm, double maxBpm, int analyseSeconds, CancellationToken cancellationToken)
    {
        // Skip the intro: start 30 s in, which is usually past the build-up. Fall back to the start for short tracks.
        var samples = await DecodeAsync(path, 30, analyseSeconds, cancellationToken).ConfigureAwait(false);
        if (samples.Length < SampleRate * 20)
        {
            samples = await DecodeAsync(path, 0, analyseSeconds, cancellationToken).ConfigureAwait(false);
        }

        return Analyse(samples, minBpm, maxBpm);
    }

    /// <summary>
    /// Estimates the tempo of mono PCM audio sampled at 11025 Hz.
    /// </summary>
    /// <param name="samples">Mono samples.</param>
    /// <param name="minBpm">Lowest tempo to report.</param>
    /// <param name="maxBpm">Highest tempo to report.</param>
    /// <returns>The detected tempo, or null when there is too little audio or no rhythm.</returns>
    public static BpmResult? Analyse(float[] samples, double minBpm, double maxBpm)
    {
        if (samples.Length < SampleRate * 8)
        {
            return null;
        }

        var envelope = OnsetEnvelope(samples);

        // Square-root compression evens out kick/snare accents, which otherwise pull the estimate to half-time.
        for (var i = 0; i < envelope.Length; i++)
        {
            envelope[i] = Math.Sqrt(envelope[i]);
        }

        var acf = Autocorrelate(envelope, (int)(FramesPerSecond * 60 / 15));
        if (acf[0] <= 0)
        {
            return null; // silence
        }

        // Search 50–220 BPM in 0.05 BPM steps. Each candidate is scored on its own period plus the
        // period of half-notes and eighth-notes, so a real beat is backed up by its neighbours.
        double bestBpm = 0, bestScore = double.MinValue;
        for (var bpm = 50.0; bpm <= 220.0; bpm += 0.05)
        {
            var lag = FramesPerSecond * 60.0 / bpm;
            var score = Interp(acf, lag)
                        + (0.5 * Interp(acf, lag * 2))
                        + (0.5 * Interp(acf, lag / 2));

            // Log-normal prior centred on 130 BPM, one octave wide, to settle half/double-time ties.
            // Ambiguous tracks lean towards half-time, so a slow song is not mistaken for a fast one.
            var octaves = Math.Log2(bpm / 130.0);
            score *= Math.Exp(-0.5 * octaves * octaves);

            if (score > bestScore)
            {
                bestScore = score;
                bestBpm = bpm;
            }
        }

        if (bestScore <= 0)
        {
            return null;
        }

        var confidence = Math.Clamp(Interp(acf, FramesPerSecond * 60.0 / bestBpm) / acf[0], 0, 1);
        bestBpm = Refine(acf, bestBpm);

        // Fold into the requested range.
        while (bestBpm < minBpm && bestBpm * 2 <= maxBpm * 1.0001)
        {
            bestBpm *= 2;
        }

        while (bestBpm > maxBpm && bestBpm / 2 >= minBpm * 0.9999)
        {
            bestBpm /= 2;
        }

        return new BpmResult(bestBpm, confidence);
    }

    /// <summary>
    /// Sharpens a tempo estimate. A single beat period is only a few dozen envelope frames long, so it
    /// is measured imprecisely; the peak several beats later gives the same period with far less error.
    /// </summary>
    private static double Refine(double[] acf, double bpm)
    {
        var lag = FramesPerSecond * 60.0 / bpm;
        var estimates = new List<double>();
        for (var k = 2; (k * lag * 1.03) < acf.Length - 1; k++)
        {
            var centre = k * lag;
            var from = Math.Max(1, (int)Math.Floor(centre * 0.97));
            var to = Math.Min(acf.Length - 2, (int)Math.Ceiling(centre * 1.03));
            var peak = from;
            for (var i = from; i <= to; i++)
            {
                if (acf[i] > acf[peak])
                {
                    peak = i;
                }
            }

            if (peak == from || peak == to)
            {
                continue; // no clear peak inside the window
            }

            // Parabolic interpolation around the integer peak.
            double a = acf[peak - 1], b = acf[peak], c = acf[peak + 1];
            var denominator = a - (2 * b) + c;
            var offset = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
            estimates.Add((peak + offset) / k);
        }

        if (estimates.Count == 0)
        {
            return bpm;
        }

        estimates.Sort();
        var refinedLag = estimates[estimates.Count / 2]; // median resists the odd stray peak
        var refined = FramesPerSecond * 60.0 / refinedLag;
        return Math.Abs(refined - bpm) / bpm < 0.03 ? refined : bpm;
    }

    internal async Task<float[]> DecodeAsync(string path, int startSeconds, int durationSeconds, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-nostdin", "-hide_banner", "-v", "error",
            "-ss", startSeconds.ToString(CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
            "-i", path,
            "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "pipe:1",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stderr so ffmpeg never blocks on a full pipe.
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var buffer = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0 && buffer.Length == 0)
        {
            throw new IOException($"ffmpeg could not decode the file: {stderr.Trim()}");
        }

        var bytes = buffer.GetBuffer();
        var count = (int)(buffer.Length / 4);
        var samples = new float[count];
        Buffer.BlockCopy(bytes, 0, samples, 0, count * 4);
        return samples;
    }

    /// <summary>Spectral-flux onset strength, locally normalised and half-wave rectified.</summary>
    internal static double[] OnsetEnvelope(float[] samples)
    {
        var frames = ((samples.Length - FrameSize) / HopSize) + 1;
        var bins = FrameSize / 2;
        var window = new double[FrameSize];
        for (var i = 0; i < FrameSize; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / FrameSize));
        }

        var re = new double[FrameSize];
        var im = new double[FrameSize];
        var previous = new double[bins];
        var current = new double[bins];
        var flux = new double[frames];

        for (var f = 0; f < frames; f++)
        {
            var offset = f * HopSize;
            for (var i = 0; i < FrameSize; i++)
            {
                re[i] = samples[offset + i] * window[i];
                im[i] = 0;
            }

            Fft(re, im);

            double sum = 0;
            for (var k = 1; k < bins; k++)
            {
                // Log compression makes quiet hi-hats count alongside loud kicks.
                current[k] = Math.Log(1 + (100 * Math.Sqrt((re[k] * re[k]) + (im[k] * im[k]))));
                var d = current[k] - previous[k];
                if (d > 0)
                {
                    sum += d;
                }
            }

            flux[f] = f == 0 ? 0 : sum;
            (previous, current) = (current, previous);
        }

        // Subtract a ~0.5 s moving average so dynamics changes don't swamp the rhythm.
        var half = (int)(FramesPerSecond / 4);
        var result = new double[frames];
        double running = 0;
        int lo = 0, hi = -1;
        for (var f = 0; f < frames; f++)
        {
            while (hi < Math.Min(frames - 1, f + half))
            {
                running += flux[++hi];
            }

            while (lo < f - half)
            {
                running -= flux[lo++];
            }

            var mean = running / (hi - lo + 1);
            result[f] = Math.Max(0, flux[f] - mean);
        }

        return result;
    }

    internal static double[] Autocorrelate(double[] x, int maxLag)
    {
        maxLag = Math.Min(maxLag, x.Length - 1);
        var acf = new double[maxLag + 1];
        for (var lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var i = lag; i < x.Length; i++)
            {
                sum += x[i] * x[i - lag];
            }

            // Unbiased: compensate for fewer overlapping samples at longer lags.
            acf[lag] = sum / (x.Length - lag);
        }

        return acf;
    }

    internal static double Interp(double[] a, double index)
    {
        if (index < 0 || index >= a.Length - 1)
        {
            return 0;
        }

        var i = (int)index;
        var t = index - i;
        return (a[i] * (1 - t)) + (a[i + 1] * t);
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + (len / 2);
                    var tr = (re[b] * cr) - (im[b] * ci);
                    var ti = (re[b] * ci) + (im[b] * cr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = (cr * wr) - (ci * wi);
                    ci = (cr * wi) + (ci * wr);
                    cr = ncr;
                }
            }
        }
    }
}
