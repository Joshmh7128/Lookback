using Lookback.Testbench.Report;

namespace Lookback.Testbench.Tests;

public class ReportWriterTests
{
    [Fact]
    public void TitleCannotBreakOutOfThePage()
    {
        var data = new TimelineData
        {
            Title = "</script><script>alert(1)</script>",
            SessionId = "s",
            SourceKind = "File",
            Retention = "Owned",
        };

        string html = ReportWriter.Render(data);
        Assert.DoesNotContain("<script>alert(1)", html, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(html, "</script>")); // only the template's own two script elements
        Assert.Contains("&lt;/script&gt;", html, StringComparison.Ordinal); // the <title> copy is HTML-encoded
    }

    private static int CountOf(string s, string value)
    {
        int n = 0;
        for (int i = s.IndexOf(value, StringComparison.Ordinal); i >= 0; i = s.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
