using System.Text.Json;
using System.Text.Json.Serialization;
using Glider.Core.Jobs;

namespace Glider.Core.Infrastructure;

/// <summary>Source-generated serializer for Core's own on-disk files (not part of the IPC contract).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(GameRecord))]
[JsonSerializable(typeof(PersistedJobFile))]
internal sealed partial class EngineJsonContext : JsonSerializerContext;
