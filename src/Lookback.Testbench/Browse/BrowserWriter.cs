using System.Reflection;
using System.Text.Json;

namespace Lookback.Testbench.Browse;

/// <summary>Renders <see cref="BrowserData"/> into the self-contained browser page.</summary>
internal static class BrowserWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Render(BrowserData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // The default encoder escapes <, > and &, so no profile or manifest
        // text can close the <script> element the JSON sits in.
        return LoadTemplate().Replace("__DATA__", JsonSerializer.Serialize(data, Json), StringComparison.Ordinal);
    }

    public static async Task WriteAsync(BrowserData data, string path, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, Render(data), ct).ConfigureAwait(false);
    }

    private static string LoadTemplate()
    {
        using System.IO.Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("browser.html")
            ?? throw new InvalidOperationException("Browser template is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
