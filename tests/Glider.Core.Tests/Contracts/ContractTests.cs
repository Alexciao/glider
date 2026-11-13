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
        var fake = new FakeRpc();
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(fake, transport);
        await d.HandleAsync("""{"type":"request","id":7,"method":"game.validateFolder","params":{"path":"D:\\Games\\Fortnite"}}""");
        Assert.True(transport.Sent.TryDequeue(out var msg));
        using var doc = JsonDocument.Parse(msg!);
        Assert.Equal("response", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("error").ValueKind);
        Assert.Equal(@"D:\Games\Fortnite", doc.RootElement.GetProperty("result").GetProperty("gamePath").GetString());
    }

    [Fact]
    public async Task GliderExceptionBecomesStructuredError()
    {
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(new FakeRpc(), transport);
        await d.HandleAsync("""{"type":"request","id":1,"method":"game.update","params":null}""");
        Assert.True(transport.Sent.TryDequeue(out var msg));
        using var doc = JsonDocument.Parse(msg!);
        Assert.Equal("notInstalled", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownMethodAndBadJsonNeverThrow()
    {
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(new FakeRpc(), transport);
        await d.HandleAsync("""{"type":"request","id":2,"method":"nope"}""");
        await d.HandleAsync("{not json");
        Assert.Single(transport.Sent);
        Assert.Contains("invalidRequest", transport.Sent.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void PublishWritesEventEnvelope()
    {
        var transport = new CaptureTransport();
        var d = new RpcDispatcher(new FakeRpc(), transport);
        d.Publish(RpcEvents.Window, new WindowInfo(true, false));
        Assert.Equal("""{"type":"event","event":"window","data":{"maximized":true,"focused":false}}""", transport.Sent.Single());
    }

    /// <summary>Minimal implementation; unimplemented members throw.</summary>
    private sealed class FakeRpc : IGliderRpc
    {
        public Task<FolderValidation> ValidateFolder(ValidateFolderRequest request) =>
            Task.FromResult(new FolderValidation(true, request.Path, "1.0", null));

        public Task<JobInfo> Update() => throw new GliderException(Errors.NotInstalled());

        public Task<AppSnapshot> GetSnapshot() => throw new NotImplementedException();
        public Task OpenExternal(UrlRequest request) => throw new NotImplementedException();
        public Task OpenPath(PathRequest request) => throw new NotImplementedException();
        public Task<PickFolderResult> PickFolder(PickFolderRequest request) => throw new NotImplementedException();
        public Task<LogsResult> GetLogs(LogsRequest request) => throw new NotImplementedException();
        public Task CopyLogs() => throw new NotImplementedException();
        public Task<LicensesResult> GetLicenses() => throw new NotImplementedException();
        public Task CompleteOnboarding() => throw new NotImplementedException();
        public Task Ready() => throw new NotImplementedException();
        public Task Quit() => throw new NotImplementedException();
        public Task Minimize() => throw new NotImplementedException();
        public Task ToggleMaximize() => throw new NotImplementedException();
        public Task Close() => throw new NotImplementedException();
        public Task<WindowInfo> GetWindowInfo() => throw new NotImplementedException();
        public Task<AuthState> SignIn() => throw new NotImplementedException();
        public Task<AuthState> SignOut() => throw new NotImplementedException();
        public Task<InstallPlan> PlanInstall(InstallPlanRequest request) => throw new NotImplementedException();
        public Task<JobInfo> Install(InstallRequest request) => throw new NotImplementedException();
        public Task<AdoptResult> Adopt(AdoptRequest request) => throw new NotImplementedException();
        public Task<UpdateCheckResult> CheckForUpdates() => throw new NotImplementedException();
        public Task<JobInfo> Verify() => throw new NotImplementedException();
        public Task<JobInfo> Move(MoveRequest request) => throw new NotImplementedException();
        public Task<JobInfo> Uninstall(UninstallRequest request) => throw new NotImplementedException();
        public Task<LaunchResult> Launch() => throw new NotImplementedException();
        public Task PrepareLaunch() => throw new NotImplementedException();
        public Task CloseGame() => throw new NotImplementedException();
        public Task OpenInstallFolder() => throw new NotImplementedException();
        public Task<JobInfo> PauseJob(JobRef request) => throw new NotImplementedException();
        public Task<JobInfo> ResumeJob(JobRef request) => throw new NotImplementedException();
        public Task<JobInfo> CancelJob(JobRef request) => throw new NotImplementedException();
        public Task DismissJob(JobRef request) => throw new NotImplementedException();
        public Task<EglState> RescanEgl() => throw new NotImplementedException();
        public Task<EglState> QuickScanEgl() => throw new NotImplementedException();
        public Task<Settings> UpdateSettings(Settings settings) => throw new NotImplementedException();
        public Task<Settings> ResetSettings() => throw new NotImplementedException();
    }
}
