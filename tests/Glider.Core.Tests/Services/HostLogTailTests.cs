using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

public class HostLogTailTests
{
    [Fact]
    public void Returns_the_newest_lines_of_the_newest_host_log()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glider-hostlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "host-20260101.log"), "old\n");
            File.WriteAllText(Path.Combine(dir, "host-20260102.log"), "a\nb\nc\nd\n");
            var tail = Diagnostics.HostLogTail(dir, 2);
            Assert.Contains("c", tail, StringComparison.Ordinal);
            Assert.Contains("d", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("b", tail.Replace("--- host log ---", ""), StringComparison.Ordinal);
            Assert.DoesNotContain("old", tail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Is_empty_without_a_host_log_or_directory()
    {
        Assert.Equal("", Diagnostics.HostLogTail(Path.Combine(Path.GetTempPath(), "glider-nope-" + Guid.NewGuid().ToString("N")), 10));
    }
}
