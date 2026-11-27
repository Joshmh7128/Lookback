using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Lookback.Testbench.Report;

/// <summary>Renders <see cref="TimelineData"/> into the self-contained HTML viewer.</summary>
internal static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Render(TimelineData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string template = LoadTemplate();

        // The default encoder escapes <, > and & as \uXXXX, so the JSON cannot
        // close the <script> element it sits in, whatever a title contains.
        string json = JsonSerializer.Serialize(data, Json);
        return template
            .Replace("__TITLE__", WebUtility.HtmlEncode(data.Title), StringComparison.Ordinal)
            .Replace("__DATA__", json, StringComparison.Ordinal);
    }

    public static async Task WriteAsync(TimelineData data, string path, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, Render(data), ct).ConfigureAwait(false);

    private static string LoadTemplate()
    {
        using System.IO.Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("report.html")
            ?? throw new InvalidOperationException("Report template is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
