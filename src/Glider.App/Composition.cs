using Glider.Core;
using Glider.Core.Contracts;

namespace Glider.App;

/// <summary>
/// THE integration point with Glider.Core. Replace the body with Core's service layer when the branches merge, e.g.
/// <c>return new GliderService(paths, host, events, ...)</c> (plus whatever startup it needs). Called once, on a thread-pool
/// thread, in parallel with window creation — it must not touch the UI thread. Anything IAsyncDisposable/IDisposable returned
/// is disposed on exit.
/// </summary>
internal static class Composition
{
    public static IGliderRpc Build(AppPaths paths, IHostPlatform host, IEventSink events) =>
        new PlaceholderRpc(paths, host, events); // TODO(integration): replace with Core's GliderService
}
