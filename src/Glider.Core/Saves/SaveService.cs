// Phase 2 stubs: they become stateful services when their feature lands.
#pragma warning disable CA1822
using Glider.Core.Contracts;
using Glider.Core.Library;
using Glider.Core.Services;

namespace Glider.Core.Saves;

/// <summary>
/// Cloud saves (Phase 2: 3-way conflict detection, backups, explicit `sync-saves` flags, the S lane). For now: an honest
/// state per game (NotSupported when the catalog declares no CloudSaveFolder, Disabled when switched off, Unknown otherwise)
/// and "Not available yet" for every action.
/// </summary>
internal sealed class SaveService
{
    private readonly ServiceContext _c;

    public SaveService(ServiceContext context) => _c = context;

    public SaveState Get(string app)
    {
        var summary = _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        var entry = _c.Games.Get(app);
        var status = GameCatalog.SaveStatus(summary.CloudSaves, _c.Settings.CloudSaves, entry);
        return new SaveState(summary.CloudSaves, status != SaveSyncStatus.Disabled && summary.CloudSaves, status, null, null, null, null, null, 0);
    }

    // Phase 2
    public Task<SaveState> SyncAsync(SaveSyncRequest request) => throw new GliderException(Errors.NotAvailableYet("cloud saves"));

    // Phase 2
    public Task<LaunchResult?> ResolveAsync(SaveResolveRequest request) => throw new GliderException(Errors.NotAvailableYet("cloud saves"));

    // Phase 2
    public Task OpenBackupsAsync(string app) => throw new GliderException(Errors.NotAvailableYet("save backups"));
}
