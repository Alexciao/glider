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
[JsonSerializable(typeof(GameState))]
[JsonSerializable(typeof(JobInfo))]
[JsonSerializable(typeof(JobProgress))]
[JsonSerializable(typeof(EglState))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(GliderError))]
[JsonSerializable(typeof(Notice))]
[JsonSerializable(typeof(WindowInfo))]
[JsonSerializable(typeof(CaptionEvent))]
[JsonSerializable(typeof(InstallPlanRequest))]
[JsonSerializable(typeof(InstallPlan))]
[JsonSerializable(typeof(InstallRequest))]
[JsonSerializable(typeof(AdoptRequest))]
[JsonSerializable(typeof(AdoptResult))]
[JsonSerializable(typeof(ValidateFolderRequest))]
[JsonSerializable(typeof(FolderValidation))]
[JsonSerializable(typeof(MoveRequest))]
[JsonSerializable(typeof(UninstallRequest))]
[JsonSerializable(typeof(JobRef))]
[JsonSerializable(typeof(PathRequest))]
[JsonSerializable(typeof(UrlRequest))]
[JsonSerializable(typeof(PickFolderRequest))]
[JsonSerializable(typeof(PickFolderResult))]
[JsonSerializable(typeof(UpdateCheckResult))]
[JsonSerializable(typeof(LogsRequest))]
[JsonSerializable(typeof(LogsResult))]
[JsonSerializable(typeof(LaunchResult))]
[JsonSerializable(typeof(LicensesResult))]
public sealed partial class GliderJsonContext : JsonSerializerContext;

public static class GliderJson
{
    public static JsonSerializerOptions Options => GliderJsonContext.Default.Options;
}
