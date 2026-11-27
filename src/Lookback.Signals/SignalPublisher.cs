using Lookback.Contracts.V1;
using Lookback.Core.Bus;

namespace Lookback.Signals;

/// <summary>
/// Publishes one family of signals: the <c>SignalSchema</c> once, then a
/// <c>SignalFrame</c> per window. Schema ids are stable per family so a
/// session log can be decoded without the producer.
/// </summary>
public sealed class SignalPublisher
{
    public const uint VideoSchemaId = 1;
    public const uint AudioSchemaId = 2;

    private readonly IEventBus _bus;
    private readonly EnvelopeFactory _factory;
    private readonly string _topic;
    private readonly SignalSchema _schema;

    public SignalPublisher(IEventBus bus, EnvelopeFactory factory, string topic, uint schemaId, SignalFamily family, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(names);
        _bus = bus;
        _factory = factory;
        _topic = topic;
        _schema = new SignalSchema { SchemaId = schemaId, Family = family, Names = { names } };
    }

    public uint SchemaId => _schema.SchemaId;

    public int Width => _schema.Names.Count;

    public void PublishSchema(long qpc)
    {
        Envelope e = _factory.Create(_topic, qpc);
        e.SignalSchema = _schema.Clone();
        _bus.Publish(e);
    }

    /// <summary>
    /// Publishes one window. This allocates the message objects the bus
    /// carries (the extractors themselves do not allocate); batching several
    /// windows per envelope is the planned optimisation if it ever shows up
    /// in profiles.
    /// </summary>
    public void Publish(long startQpc, long endQpc, ReadOnlySpan<float> values)
    {
        if (values.Length != Width)
        {
            throw new ArgumentException($"Expected {Width} values, got {values.Length}.", nameof(values));
        }

        var frame = new SignalFrame
        {
            SchemaId = _schema.SchemaId,
            WindowStartQpc = (ulong)startQpc,
            WindowEndQpc = (ulong)endQpc,
        };
        frame.Values.Capacity = values.Length;
        foreach (float v in values)
        {
            frame.Values.Add(v);
        }

        Envelope e = _factory.Create(_topic, startQpc);
        e.SignalFrame = frame;
        _bus.Publish(e);
    }
}
