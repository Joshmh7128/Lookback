using Lookback.Core.Time;

namespace Lookback.Governor;

/// <summary>Pressure the recorder reports to the governor.</summary>
public readonly record struct PressureSample(bool MemoryLow, long StoreFreeBytes, double EncoderLoad);

/// <summary>
/// Runtime adaptation: steps the tier down while memory, disk or encoder
/// pressure persists, and back up (never above the planned tier) once it has
/// been clear for a while. The hysteresis keeps it from flapping. The new
/// tier takes effect at the encoder's next keyframe.
/// </summary>
public sealed class GovernorController
{
    private readonly IClock _clock;
    private readonly MachineProfile _machine;
    private readonly RecordingPlan _base;
    private readonly long _downAfter;
    private readonly long _upAfter;
    private long _pressureSince = -1;
    private long _clearSince = -1;

    public GovernorController(IClock clock, MachineProfile machine, RecordingPlan plan, TimeSpan? stepDownAfter = null, TimeSpan? stepUpAfter = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(plan);
        _clock = clock;
        _machine = machine;
        _base = plan;
        Current = plan;
        _downAfter = QpcTime.FromTimeSpan(stepDownAfter ?? TimeSpan.FromSeconds(2), clock.Frequency);
        _upAfter = QpcTime.FromTimeSpan(stepUpAfter ?? TimeSpan.FromSeconds(60), clock.Frequency);
    }

    /// <summary>Encoder load above this (busy fraction of each frame interval) counts as overload.</summary>
    public const double EncoderOverload = 0.9;

    public RecordingPlan Current { get; private set; }

    /// <summary>Feeds one sample; returns the new plan when the tier changes, otherwise null.</summary>
    public RecordingPlan? Observe(PressureSample sample)
    {
        long now = _clock.Now;
        bool pressure = sample.MemoryLow || sample.StoreFreeBytes < Current.FreeSpaceFloorBytes || sample.EncoderLoad > EncoderOverload;
        if (pressure)
        {
            _clearSince = -1;
            if (_pressureSince < 0)
            {
                _pressureSince = now;
            }

            if (now - _pressureSince >= _downAfter && GovernorPolicy.StepDown(Current.Tier, _machine) is QualityTier lower)
            {
                _pressureSince = now; // another step needs another full interval
                string why = sample.MemoryLow ? "memory pressure" : sample.EncoderLoad > EncoderOverload ? "encoder overload" : "low disk space";
                Current = Current with
                {
                    Tier = lower,
                    RingBytes = sample.MemoryLow ? Current.RingBytes / 2 : Current.RingBytes,
                    Reasons = [.. Current.Reasons, $"stepped down to {lower.Name}: {why}"],
                };
                return Current;
            }

            return null;
        }

        _pressureSince = -1;
        if (Current.Tier == _base.Tier)
        {
            return null;
        }

        if (_clearSince < 0)
        {
            _clearSince = now;
        }

        if (now - _clearSince >= _upAfter)
        {
            _clearSince = now;
            int i = GovernorPolicy.Index(Current.Tier);
            QualityTier? higher = null;
            for (int j = i - 1; j >= GovernorPolicy.Index(_base.Tier); j--)
            {
                QualityTier t = QualityTier.Ladder[j];
                if (_machine.Gpu is null ? t == QualityTier.Software : _machine.Gpu.HardwareEncoders.Contains(t.Codec))
                {
                    higher = t;
                    break;
                }
            }

            if (higher is not null)
            {
                Current = Current with { Tier = higher, RingBytes = _base.RingBytes, Reasons = [.. Current.Reasons, $"stepped back up to {higher.Name}"] };
                return Current;
            }
        }

        return null;
    }
}
