using Glider.Core.Infrastructure;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

public class OptionsDefaultsTests
{
    [Fact]
    public void AutoWorkerDefaultsComeFromAutoTune() // review L12
    {
        var tune = AutoTune.ForCurrentMachine();
        var o = new GliderServiceOptions();
        Assert.Equal(tune.Workers, o.AutoWorkers);
        Assert.Equal(tune.SharedMemoryMb, o.AutoSharedMemoryMb);
    }
}
