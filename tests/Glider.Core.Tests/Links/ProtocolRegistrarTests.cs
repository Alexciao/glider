using Glider.Core.Links;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Links;

public sealed class ProtocolRegistrarTests : IDisposable
{
    private const string Exe = "C:\\Users\\me\\AppData\\Local\\Glider\\Glider.exe";
    private const string GliderKey = @"Software\Classes\glider";
    private const string EpicKey = @"Software\Classes\com.epicgames.launcher";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glider-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly List<string> _log = [];

    public ProtocolRegistrarTests() => Directory.CreateDirectory(_dir);

    private string BackupFile => Path.Combine(_dir, "protocol-backup.json");

    private ProtocolRegistrar Registrar(string exe = Exe) => new(_reg, exe, BackupFile, _log.Add);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- glider://

    [Fact]
    public void GliderSchemeIsRegisteredPerUserWithTheStableExe()
    {
        Assert.True(Registrar().EnsureGliderScheme());

        var key = _reg.Read(GliderKey)!;
        Assert.Equal("", key.GetString("URL Protocol"));
        Assert.Equal("1", key.GetString(ProtocolRegistrar.ManagedValue));
        Assert.Equal($"\"{Exe}\" \"%1\"", key.Find("shell", "open", "command")!.GetString());
        Assert.Equal($"\"{Exe}\",0", key.Find("DefaultIcon")!.GetString());
    }

    [Fact]
    public void RegisteringAgainWritesNothing_AndAMovedExeIsRepaired()
    {
        Registrar().EnsureGliderScheme();
        _reg.Writes.Clear();

        Assert.False(Registrar().EnsureGliderScheme());
        Assert.Empty(_reg.Writes);

        Assert.True(Registrar("D:\\Portable\\Glider.exe").EnsureGliderScheme());
        Assert.Equal("\"D:\\Portable\\Glider.exe\" \"%1\"", _reg.Read(GliderKey)!.Find("shell", "open", "command")!.GetString());
    }

    [Fact]
    public void AGliderKeyThatIsNotOursIsLeftAlone()
    {
        var foreign = new RegistryNode().Set("", "someone else");
        _reg.Seed(GliderKey, foreign);

        Assert.False(Registrar().EnsureGliderScheme());
        Assert.False(Registrar().RemoveGliderScheme());

        Assert.True(foreign.SameAs(_reg.Read(GliderKey)));
        Assert.Empty(_reg.Writes);
    }

    [Fact]
    public void UnregisteringRemovesOnlyOurKey()
    {
        Registrar().EnsureGliderScheme();
        Assert.True(Registrar().RemoveGliderScheme());
        Assert.Null(_reg.Read(GliderKey));
        Assert.False(Registrar().RemoveGliderScheme());
    }

    // ---------------------------------------------------------------- com.epicgames.launcher://

    [Fact]
    public void EnablingWithoutAnyPerUserKeyRegistersGlider_AndDisablingDeletesItAgain()
    {
        var status = Registrar().EnableEpicHandler();

        Assert.True(status.GliderIsHandler);
        Assert.True(status.BackupHeld);
        Assert.Equal($"\"{Exe}\" \"%1\"", _reg.Read(EpicKey)!.Find("shell", "open", "command")!.GetString());

        status = Registrar().DisableEpicHandler();

        Assert.False(status.GliderIsHandler);
        Assert.False(status.BackupHeld);
        Assert.Null(_reg.Read(EpicKey)); // the machine-wide EGL registration shows through again
        Assert.False(File.Exists(BackupFile));
    }

    [Fact]
    public void DisablingRestoresExactlyWhatWasThere_AllValueKindsAndSubkeys()
    {
        var egl = FakeRegistry.EglKey();
        _reg.Seed(EpicKey, egl);

        Registrar().EnableEpicHandler();
        Assert.NotEqual(egl.Find("shell", "open", "command")!.GetString(), _reg.Read(EpicKey)!.Find("shell", "open", "command")!.GetString());
        Assert.Null(_reg.Read(EpicKey)!.Values.GetValueOrDefault("EditFlags")); // Glider's key replaced it entirely

        var status = Registrar().DisableEpicHandler();

        Assert.False(status.GliderIsHandler);
        Assert.True(egl.SameAs(_reg.Read(EpicKey)), "the registry key is byte-for-byte what EGL had");
        Assert.Equal(RegKind.DWord, _reg.Read(EpicKey)!.Values["EditFlags"].Kind);
        Assert.Equal([1, 2, 3, 0, 255], _reg.Read(EpicKey)!.Values["Blob"].Bytes!);
        Assert.Equal(["a", "b"], _reg.Read(EpicKey)!.Values["Multi"].Multi!);
        Assert.Equal("%ProgramFiles%\\Epic", _reg.Read(EpicKey)!.Values["Expand"].Text);
        Assert.Equal(1L << 40, _reg.Read(EpicKey)!.Values["Big"].Number);
        Assert.False(File.Exists(BackupFile));
    }

    [Fact]
    public void EnablingTwiceKeepsTheOriginalBackup()
    {
        var egl = FakeRegistry.EglKey();
        _reg.Seed(EpicKey, egl);

        Registrar().EnableEpicHandler();
        Registrar().EnableEpicHandler(); // e.g. every start-up
        Registrar("D:\\Elsewhere\\Glider.exe").EnableEpicHandler(); // and after the exe moved

        Registrar().DisableEpicHandler();
        Assert.True(egl.SameAs(_reg.Read(EpicKey)));
    }

    [Fact]
    public void AKeyEglRewroteWhileEnabledIsTheNewOriginal()
    {
        _reg.Seed(EpicKey, FakeRegistry.EglKey("old"));
        Registrar().EnableEpicHandler();
        var updated = FakeRegistry.EglKey("EGL after an update");
        _reg.Seed(EpicKey, updated); // the launcher updated itself and re-registered

        Assert.False(Registrar().GetEpicStatus().GliderIsHandler);
        Registrar().EnableEpicHandler(); // start-up re-asserts the setting
        Registrar().DisableEpicHandler();

        Assert.True(updated.SameAs(_reg.Read(EpicKey)));
    }

    [Fact]
    public void DisablingNeverTouchesAKeySomeoneElseWroteAfterUs()
    {
        Registrar().EnableEpicHandler();
        var otherRoot = new RegistryNode().Set("", "other");
        otherRoot.Child("shell").Child("open").Child("command").Set("", "other.exe %1");
        _reg.Seed(EpicKey, otherRoot);
        _reg.Writes.Clear();

        var status = Registrar().DisableEpicHandler();

        Assert.Empty(_reg.Writes);
        Assert.True(otherRoot.SameAs(_reg.Read(EpicKey)));
        Assert.False(File.Exists(BackupFile)); // the stale backup is dropped
        Assert.False(status.GliderIsHandler);
    }

    [Fact]
    public void ALostBackupStillRemovesOurKey()
    {
        _reg.Seed(EpicKey, FakeRegistry.EglKey());
        Registrar().EnableEpicHandler();
        File.Delete(BackupFile);

        Registrar().DisableEpicHandler();

        Assert.Null(_reg.Read(EpicKey));
        Assert.Contains(_log, l => l.Contains("no backup", StringComparison.Ordinal));
    }

    [Fact]
    public void ACorruptBackupIsTreatedAsMissing()
    {
        Registrar().EnableEpicHandler();
        File.WriteAllText(BackupFile, "{ not json");

        Registrar().DisableEpicHandler();

        Assert.Null(_reg.Read(EpicKey));
        Assert.False(File.Exists(BackupFile));
    }

    [Fact]
    public void WhenTheBackupCannotBeWrittenNothingChanges()
    {
        var egl = FakeRegistry.EglKey();
        _reg.Seed(EpicKey, egl);
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "");
        var registrar = new ProtocolRegistrar(_reg, Exe, Path.Combine(blocker, "sub", "backup.json"));

        Assert.ThrowsAny<IOException>(registrar.EnableEpicHandler);

        Assert.Empty(_reg.Writes);
        Assert.True(egl.SameAs(_reg.Read(EpicKey)));
    }

    [Fact]
    public void ARegistryThatRefusesWritesPropagatesAndLeavesEglsKeyAlone()
    {
        _reg.Seed(EpicKey, FakeRegistry.EglKey());
        _reg.FailWrites = new UnauthorizedAccessException("denied");

        Assert.Throws<UnauthorizedAccessException>(() => Registrar().EnableEpicHandler());

        _reg.FailWrites = null;
        Assert.False(Registrar().GetEpicStatus().GliderIsHandler);
        Registrar().DisableEpicHandler(); // turning it off after a failed enable is harmless: our key was never written
        Assert.NotNull(_reg.Read(EpicKey)!.Values.GetValueOrDefault("EditFlags"));
    }

    [Fact]
    public void StatusReportsAForeignHandlerAndWindowsDefaultAppChoice()
    {
        Assert.Equal(new EpicHandlerStatus(false, false, null, null), Registrar().GetEpicStatus());

        _reg.Seed(EpicKey, FakeRegistry.EglKey("egl.exe %1"));
        _reg.Seed(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\com.epicgames.launcher\UserChoice", new RegistryNode().Set("ProgId", "EpicLauncher.url"));

        var status = Registrar().GetEpicStatus();

        Assert.False(status.GliderIsHandler);
        Assert.Equal("egl.exe %1", status.OtherHandlerCommand);
        Assert.Equal("EpicLauncher.url", status.UserChoiceProgId);
    }

    [Fact]
    public void TheTwoSchemesAreIndependent()
    {
        Registrar().EnsureGliderScheme();
        Registrar().EnableEpicHandler();
        Registrar().DisableEpicHandler();
        Assert.NotNull(_reg.Read(GliderKey));
        Registrar().EnableEpicHandler();
        Registrar().RemoveGliderScheme();
        Assert.NotNull(_reg.Read(EpicKey));
    }

    [Fact]
    public void RegistryNodeComparisonIsCaseInsensitiveAndTypeAware()
    {
        var a = new RegistryNode().Set("Name", "x");
        a.Child("Sub").Set("", "y");
        var b = new RegistryNode().Set("name", "x");
        b.Child("sub").Set("", "y");
        Assert.True(a.SameAs(b));

        b.Values["name"] = new RegValue { Kind = RegKind.ExpandString, Text = "x" };
        Assert.False(a.SameAs(b));
        Assert.False(a.SameAs(null));
    }
}
