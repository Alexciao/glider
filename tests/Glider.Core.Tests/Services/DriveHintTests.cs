using Glider.Core.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Services;

/// <summary>Install-picker warnings for slow or nearly full drives, and the remembered "this drive proved slow" speeds.</summary>
public class DriveHintTests
{
    private const long Gb = 1_000_000_000;

    [Fact]
    public void WarnsAboutProvenSlowHardDiskAndNearlyFullDrivesInThatOrder()
    {
        Assert.Contains("only wrote about 8 MB/s", DriveHints.For(true, 200 * Gb, 1000 * Gb, 8e6, @"D:\"), StringComparison.Ordinal);
        Assert.Contains("hard disk", DriveHints.For(false, 500 * Gb, 1000 * Gb, null, @"E:\"), StringComparison.Ordinal);
        Assert.Contains("92% full", DriveHints.For(true, 80 * Gb, 1000 * Gb, null, @"C:\"), StringComparison.Ordinal);
        Assert.Null(DriveHints.For(true, 500 * Gb, 1000 * Gb, 900e6, @"C:\")); // a fast measured drive
        Assert.Null(DriveHints.For(null, 500 * Gb, 1000 * Gb, null, @"Z:\"));   // unknown media, plenty of space
        Assert.Null(DriveHints.For(true, 0, 0, null, @"C:\"));                   // no size info
    }

    [Fact]
    public void DescribeUsesTheRememberedSpeedPerDrive()
    {
        using var dir = new TempDir();
        var paths = new Glider.Core.AppPaths(dir.Path, "x.exe");
        DriveSpeeds.Record(paths, "D:", 8e6);
        DriveSpeeds.Record(paths, "D:", 6e6); // averaged, not replaced
        var described = DriveHints.Describe([new FixedDrive(@"D:\", 190 * Gb, 1000 * Gb, true), new FixedDrive(@"C:\", 500 * Gb, 1000 * Gb, true)], DriveSpeeds.Read(paths));

        Assert.Contains("7 MB/s", described[0].Hint, StringComparison.Ordinal);
        Assert.Null(described[1].Hint);
        Assert.True(described[0].IsSsd);
    }

    [Fact]
    public void ARealVolumeAnswersWithoutThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows storage query");
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        _ = DriveMedia.IsSsd(root); // true/false/null depending on the machine; must never throw
        Assert.Null(DriveMedia.IsSsd(@"\server\share"));
    }
}
