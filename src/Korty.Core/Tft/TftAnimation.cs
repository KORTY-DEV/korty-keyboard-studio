namespace Korty.Core.Tft;

public sealed record TftFrame(byte[] Bgra, int DelayMs);

/// <summary>A 128×128 animation (or a single still = one frame). Pixels are BGRA32, row-major.</summary>
public sealed class TftAnimation
{
    public const int Width = 128;
    public const int Height = 128;
    public const int FrameBytesBgra = Width * Height * 4;

    public IReadOnlyList<TftFrame> Frames { get; }

    public TftAnimation(IReadOnlyList<TftFrame> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one frame is required.", nameof(frames));
        foreach (var f in frames)
            if (f.Bgra.Length != FrameBytesBgra) throw new ArgumentException("Frame must be 128x128 BGRA32.", nameof(frames));
        Frames = frames;
    }

    public bool IsStill => Frames.Count == 1;
    public int TotalDurationMs => Frames.Sum(f => f.DelayMs);

    /// <summary>
    /// Reduces to at most <paramref name="maxFrames"/> frames while preserving total duration: kept frames absorb the
    /// delay of the frames dropped after them (uniform sampling).
    /// </summary>
    public TftAnimation Decimate(int maxFrames)
    {
        if (maxFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (Frames.Count <= maxFrames) return this;
        var kept = new List<TftFrame>(maxFrames);
        double step = (double)Frames.Count / maxFrames;
        for (int i = 0; i < maxFrames; i++)
        {
            int from = (int)Math.Round(i * step);
            int to = i == maxFrames - 1 ? Frames.Count : (int)Math.Round((i + 1) * step);
            if (to <= from) to = from + 1;
            int delay = 0;
            for (int k = from; k < to && k < Frames.Count; k++) delay += Frames[k].DelayMs;
            kept.Add(new TftFrame(Frames[from].Bgra, delay));
        }
        return new TftAnimation(kept);
    }
}
