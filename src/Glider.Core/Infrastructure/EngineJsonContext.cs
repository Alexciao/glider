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
[JsonSerializable(typeof(GamesFile))]
[JsonSerializable(typeof(CollectionsFile))]
[JsonSerializable(typeof(OnboardingFile))]
[JsonSerializable(typeof(PersistedJobFile))]
[JsonSerializable(typeof(Library.LibraryCacheFile))]
[JsonSerializable(typeof(Migrations.SchemaFile))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
internal sealed partial class EngineJsonContext : JsonSerializerContext;
