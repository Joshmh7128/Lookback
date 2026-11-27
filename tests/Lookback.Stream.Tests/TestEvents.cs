using Lookback.Contracts.V1;

namespace Lookback.Stream.Tests;

internal static class TestEvents
{
    public static Envelope Signal(ulong qpc, string session = "s1", params float[] values) => new()
    {
        Qpc = qpc,
        SessionId = session,
        Topic = "signals.video",
        SourceId = "test",
        SignalFrame = new SignalFrame { SchemaId = 1, WindowStartQpc = qpc, WindowEndQpc = qpc + 1, Values = { values } },
    };

    public static Envelope Start(string session = "s1", Retention retention = Retention.Owned, SourceKind kind = SourceKind.File, string permissionRef = "") => new()
    {
        Qpc = 0,
        SessionId = session,
        Topic = "session",
        SourceId = "test",
        SessionStarted = new SessionStarted
        {
            SourceKind = kind,
            Retention = retention,
            PermissionRef = permissionRef,
            QpcFrequency = 1000,
            Title = "fixture",
        },
    };

    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lookback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
