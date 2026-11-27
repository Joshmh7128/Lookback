namespace Lookback.Governor;

/// <summary>
/// The pure budget policy: <see cref="MachineProfile"/> in, <see cref="RecordingPlan"/> out.
/// Deterministic, so it is tested without the hardware it describes.
/// </summary>
public static class GovernorPolicy
{
    public const double RingRamShare = 0.05;
    public const long RingCeilingBytes = 1L << 30;
    public static readonly TimeSpan DefaultReplay = TimeSpan.FromMinutes(2);
    public const double DiskFreeShare = 0.5;
    public const long MinFreeFloorBytes = 10L << 30;
    public const double FreeFloorShare = 0.05;

    /// <summary>A tier is useful if the disk budget holds at least this much of a session.</summary>
    public static readonly TimeSpan UsefulSession = TimeSpan.FromHours(1);

    /// <summary>HDDs keep up with sustained writes, but seeks hurt; cap the bitrate.</summary>
    public const long HddMaxBitrate = 20_000_000;

    public static RecordingPlan Decide(MachineProfile m, GovernorOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(m);
        overrides ??= new GovernorOverrides();
        var reasons = new List<string>();

        long floor = Math.Max(MinFreeFloorBytes, (long)(m.StoreVolume.TotalBytes * FreeFloorShare));
        long usable = Math.Max(0, m.StoreVolume.FreeBytes - floor);
        long disk = (long)(usable * DiskFreeShare);
        if (overrides.DiskCapBytes is long cap && cap < disk)
        {
            disk = cap;
            reasons.Add("disk budget set by the user cap");
        }
        else
        {
            reasons.Add($"disk budget is {DiskFreeShare:P0} of free space above a {floor >> 30} GB floor");
        }

        long ring = Math.Min((long)(m.AvailableRamBytes * RingRamShare), RingCeilingBytes);
        reasons.Add($"ring gets {ring >> 20} MB ({RingRamShare:P0} of available RAM, {RingCeilingBytes >> 30} GB ceiling)");

        bool hardware = m.Gpu is { HardwareEncoders.Count: > 0 };
        IEnumerable<QualityTier> candidates = hardware
            ? QualityTier.Ladder.Where(t => m.Gpu!.HardwareEncoders.Contains(t.Codec))
            : [QualityTier.Software];
        if (!hardware)
        {
            reasons.Add("no hardware encoder: software H.264 at 720p30");
        }

        List<QualityTier> fit = [.. candidates.Where(t => t.Height <= Math.Max(m.DisplayHeight, 720) && t.Fps <= Math.Max(m.DisplayRefreshHz, 30))];
        if (!m.StoreVolume.IsSsd)
        {
            fit = [.. fit.Where(t => t.BitrateBps <= HddMaxBitrate)];
            reasons.Add("store is on an HDD: bitrate capped at 20 Mbps");
        }

        if (fit.Count == 0)
        {
            fit = [QualityTier.Software];
        }

        QualityTier tier = (overrides.Tier is not null ? fit.FirstOrDefault(t => t.Name == overrides.Tier) : null)
            ?? fit.FirstOrDefault(t => disk / t.BytesPerSecond >= UsefulSession.TotalSeconds)
            ?? fit[^1];
        reasons.Add(overrides.Tier == tier.Name ? $"tier {tier.Name} chosen by the user" : $"tier {tier.Name}: the best that fits the display and holds a useful session");

        TimeSpan want = overrides.ReplayLength ?? DefaultReplay;
        var ringLength = TimeSpan.FromSeconds(Math.Min(want.TotalSeconds, (double)ring / (tier.BytesPerSecond * 1.1)));

        return new RecordingPlan
        {
            Tier = tier,
            HardwareEncode = hardware,
            RingBytes = ring,
            RingLength = ringLength,
            DiskBudgetBytes = disk,
            FreeSpaceFloorBytes = floor,
            Reasons = reasons,
        };
    }

    /// <summary>The next tier down the ladder that the same encoder can do, or null at the bottom.</summary>
    public static QualityTier? StepDown(QualityTier tier, MachineProfile m)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentNullException.ThrowIfNull(m);
        int i = Index(tier);
        for (int j = i + 1; j < QualityTier.Ladder.Count; j++)
        {
            QualityTier t = QualityTier.Ladder[j];
            if (m.Gpu is null ? t == QualityTier.Software : m.Gpu.HardwareEncoders.Contains(t.Codec))
            {
                return t;
            }
        }

        return null;
    }

    internal static int Index(QualityTier t)
    {
        for (int i = 0; i < QualityTier.Ladder.Count; i++)
        {
            if (QualityTier.Ladder[i].Name == t.Name)
            {
                return i;
            }
        }

        return QualityTier.Ladder.Count - 1;
    }
}
