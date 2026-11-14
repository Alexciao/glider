using System.Collections.Concurrent;
using System.Text.Json;
using Glider.ContractGen;
using Glider.Core.Contracts;

namespace Glider.Core.Tests.Contracts;

public class ContractTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glider.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void GeneratedTypeScriptIsUpToDate()
    {
        var path = Path.Combine(RepoRoot(), TsContractGenerator.RelativeOutputPath);
        var onDisk = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(onDisk == TsContractGenerator.Generate(),
            "contract.gen.ts is stale. Run: dotnet run --project tools/Glider.ContractGen");
    }

    [Fact]
    public void EnumsSerializeCamelCase()
    {
        var json = JsonSerializer.Serialize(new AuthState(AuthStatus.SignedIn, null), GliderJsonContext.Default.AuthState);
        Assert.Equal("""{"status":"signedIn","account":null}""", json);
    }

    [Fact]
    public void EveryMethodIsRoutable() => Assert.Equal(typeof(IGliderRpc).GetMethods().Length, RpcDispatcher.MethodNames.Count);

    private sealed class CaptureTransport : IRpcTransport
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public void Send(string json) => Sent.Enqueue(json);
    }

    [Fact]
    public async Task DispatchesRequestWithParamsAndReturnsResult()
    {
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(FakeRpc.Create(), transport);
        await d.HandleAsync("""{"type":"request","id":7,"method":"egl.validateFolder","params":{"path":"D:\\Games\\Fortnite"}}""");
        Assert.True(transport.Sent.TryDequeue(out var msg));
        using var doc = JsonDocument.Parse(msg!);
        Assert.Equal("response", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("error").ValueKind);
        Assert.Equal(@"D:\Games\Fortnite", doc.RootElement.GetProperty("result").GetProperty("gamePath").GetString());
        Assert.Equal("Fortnite", doc.RootElement.GetProperty("result").GetProperty("appName").GetString());
    }

    [Fact]
    public async Task GliderExceptionBecomesStructuredError()
    {
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(FakeRpc.Create(), transport);
        await d.HandleAsync("""{"type":"request","id":1,"method":"game.update","params":{"appName":"Fortnite"}}""");
        Assert.True(transport.Sent.TryDequeue(out var msg));
        using var doc = JsonDocument.Parse(msg!);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("notInstalled", error.GetProperty("code").GetString());
        Assert.Equal("Fortnite", error.GetProperty("appName").GetString());
    }

    [Fact]
    public async Task UnknownMethodAndBadJsonNeverThrow()
    {
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(FakeRpc.Create(), transport);
        await d.HandleAsync("""{"type":"request","id":2,"method":"nope"}""");
        await d.HandleAsync("{not json");
        Assert.Single(transport.Sent);
        Assert.Contains("invalidRequest", transport.Sent.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void PublishWritesEventEnvelope()
    {
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(FakeRpc.Create(), transport);
        d.Publish(RpcEvents.Window, new WindowInfo(true, false, true));
        Assert.Equal("""{"type":"event","event":"window","data":{"maximized":true,"focused":false,"visible":true}}""", transport.Sent.Single());
    }

    [Fact]
    public void LibraryDeltaSerializesForTheWire()
    {
        var art = new GameArt("https://cdn/t.jpg", "https://cdn/t.jpg", "https://cdn/w.jpg", null, "#334455");
        var g = new GameSummary("Fortnite", "Fortnite", GameKind.Game, null, "fn", "4fe7", "Epic Games", InstallStatus.Installed, "v1", "v2", true,
            @"D:\Games\Fortnite", 10, null, null, false, SaveSyncStatus.NotSupported, true, ThirdPartyStore.None, null, 12, false, false, null, null, 0,
            art, RunState.Idle, null, null, false, false);
        var json = JsonSerializer.Serialize(new LibraryDelta(3, false, [g], ["gone"]), GliderJsonContext.Default.LibraryDelta);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal("installed", doc.RootElement.GetProperty("upserts")[0].GetProperty("installStatus").GetString());
        Assert.Equal("gone", doc.RootElement.GetProperty("removes")[0].GetString());
    }

    /// <summary>A proxy IGliderRpc: two methods answer, every other one throws NotImplemented.</summary>
    public class FakeRpc : System.Reflection.DispatchProxy
    {
        public static IGliderRpc Create() => Create<IGliderRpc, FakeRpc>();

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IGliderRpc.ValidateFolder) => Task.FromResult(new FolderValidation(true, "Fortnite", "Fortnite", ((ValidateFolderRequest)args![0]!).Path, "1.0", null)),
            nameof(IGliderRpc.Update) => Task.FromException<JobInfo>(new GliderException(Errors.NotInstalled("Fortnite", ((AppRef)args![0]!).AppName))),
            _ => throw new NotImplementedException(targetMethod?.Name),
        };
    }
}
