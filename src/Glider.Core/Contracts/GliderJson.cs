using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glider.Core.Contracts;

/// <summary>Source-generated serializer for every contract type (fast startup, no reflection emit).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(RpcEvent))]
[JsonSerializable(typeof(AppSnapshot))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(LibraryDelta))]
[JsonSerializable(typeof(GameSummary))]
[JsonSerializable(typeof(GameDetail))]
[JsonSerializable(typeof(GameSettings))]
[JsonSerializable(typeof(GameSettingsRequest))]
[JsonSerializable(typeof(AppRef))]
[JsonSerializable(typeof(GameFlagRequest))]
[JsonSerializable(typeof(Collection))]
[JsonSerializable(typeof(CollectionCreateRequest))]
[JsonSerializable(typeof(CollectionRenameRequest))]
[JsonSerializable(typeof(CollectionRef))]
[JsonSerializable(typeof(CollectionMembershipRequest))]
[JsonSerializable(typeof(OnboardingState))]
[JsonSerializable(typeof(OnboardingStepRequest))]
[JsonSerializable(typeof(LaunchRequest))]
[JsonSerializable(typeof(LaunchResult))]
[JsonSerializable(typeof(SaveState))]
[JsonSerializable(typeof(SaveSyncRequest))]
[JsonSerializable(typeof(SaveResolveRequest))]
[JsonSerializable(typeof(JobInfo))]
[JsonSerializable(typeof(JobProgress))]
[JsonSerializable(typeof(JobHistory))]
[JsonSerializable(typeof(JobRef))]
[JsonSerializable(typeof(JobReorderRequest))]
[JsonSerializable(typeof(QueueState))]
[JsonSerializable(typeof(InstallPlanRequest))]
[JsonSerializable(typeof(InstallPlan))]
[JsonSerializable(typeof(InstallRequest))]
[JsonSerializable(typeof(ModifyRequest))]
[JsonSerializable(typeof(MoveRequest))]
[JsonSerializable(typeof(UninstallRequest))]
[JsonSerializable(typeof(UpdateAllResult))]
[JsonSerializable(typeof(EglState))]
[JsonSerializable(typeof(EglImportRequest))]
[JsonSerializable(typeof(EglImportResult))]
[JsonSerializable(typeof(ValidateFolderRequest))]
[JsonSerializable(typeof(FolderValidation))]
[JsonSerializable(typeof(LocateRequest))]
[JsonSerializable(typeof(EglReadiness))]
[JsonSerializable(typeof(OverlayState))]
[JsonSerializable(typeof(StoreShowRequest))]
[JsonSerializable(typeof(StoreBounds))]
[JsonSerializable(typeof(StoreState))]
[JsonSerializable(typeof(FreeGamesResult))]
[JsonSerializable(typeof(ShortcutRequest))]
[JsonSerializable(typeof(ShortcutState))]
[JsonSerializable(typeof(NavigateEvent))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(GliderError))]
[JsonSerializable(typeof(Notice))]
[JsonSerializable(typeof(WindowInfo))]
[JsonSerializable(typeof(CaptionEvent))]
[JsonSerializable(typeof(PathRequest))]
[JsonSerializable(typeof(UrlRequest))]
[JsonSerializable(typeof(PickFolderRequest))]
[JsonSerializable(typeof(PickFolderResult))]
[JsonSerializable(typeof(LogsRequest))]
[JsonSerializable(typeof(LogsResult))]
[JsonSerializable(typeof(LicensesResult))]
public sealed partial class GliderJsonContext : JsonSerializerContext;

public static class GliderJson
{
    public static JsonSerializerOptions Options => GliderJsonContext.Default.Options;
}
