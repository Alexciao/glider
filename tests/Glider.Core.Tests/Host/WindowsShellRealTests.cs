using System.Runtime.Versioning;
using Glider.App;
using Glider.Core.Links;
using Glider.Core.Shortcuts;
using Microsoft.Win32;

namespace Glider.Core.Tests.Host;

/// <summary>
/// The real Windows implementations (IShellLink COM, HKCU) checked end to end, but only inside sandboxes: .lnk files in a temp folder
/// and a throwaway registry subtree <c>HKCU\Software\GliderTests\&lt;guid&gt;</c> that every test deletes. The real Desktop, Start menu and
/// <c>Software\Classes</c> are never touched. Skipped off Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellRealTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "glider-tests", Guid.NewGuid().ToString("N"));
    private readonly string _prefix = @"Software\GliderTests\" + Guid.NewGuid().ToString("N");

    public WindowsShellRealTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_prefix, throwOnMissingSubKey: false);
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");

    [Fact]
    public void ShellLinkRoundTripsEveryField_AndRewritesInPlace()
    {
        RequireWindows();
        var links = new WindowsShellLinks();
        var path = Path.Combine(_temp, "Desktop", "Alpha Quest.lnk");
        var spec = new ShellLinkSpec(@"C:\Windows\System32\notepad.exe", "--launch Alpha", @"C:\Windows\System32", @"C:\Windows\System32\shell32.dll", 3, "Play Alpha Quest");

        links.Write(path, spec);

        Assert.True(links.Exists(path));
        Assert.Equal(spec, links.Read(path));
        Assert.Equal([path], links.Enumerate(Path.GetDirectoryName(path)!));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp")); // the temp file is gone

        var changed = spec with { IconPath = @"C:\Windows\System32\imageres.dll", IconIndex = 11, Arguments = "--launch Alpha" };
        links.Write(path, changed);
        Assert.Equal(changed, links.Read(path));

        Assert.True(links.Delete(path));
        Assert.False(links.Delete(path));
        Assert.Null(links.Read(path));
    }

    [Fact]
    public void AFileThatIsNotAShortcutReadsAsNull_NotAnException()
    {
        RequireWindows();
        var links = new WindowsShellLinks();
        var path = Path.Combine(_temp, "fake.lnk");
        File.WriteAllText(path, "not a shell link");

        Assert.Null(links.Read(path));
        Assert.True(links.Exists(path));
        Assert.Empty(links.Enumerate(Path.Combine(_temp, "missing")));
    }

    [Fact]
    public void UnicodeAndSpacesSurviveInTitlesAndPaths()
    {
        RequireWindows();
        var links = new WindowsShellLinks();
        var name = ShortcutNames.FileName("Caf\u00e9 \u30b2\u30fc\u30e0: Part 2", "app");
        var path = Path.Combine(_temp, "Start Menu", name + ".lnk");
        var spec = new ShellLinkSpec(@"C:\Program Files\Glider App\Glider.exe", "--launch Caf3", @"C:\Program Files\Glider App", @"C:\Program Files\Glider App\Glider.exe", 0, "Play Caf\u00e9 \u30b2\u30fc\u30e0");

        links.Write(path, spec);

        Assert.Equal(spec, links.Read(path));
    }

    [Fact]
    public void RegistryTreesRoundTripWithEveryValueKind()
    {
        RequireWindows();
        var reg = new WindowsRegistry(_prefix);
        var node = FakeEgl();
        const string key = @"Software\Classes\com.example.test";

        Assert.Null(reg.Read(key));
        reg.Replace(key, node);

        var back = reg.Read(key)!;
        Assert.True(node.SameAs(back));
        Assert.Equal(RegKind.DWord, back.Values["EditFlags"].Kind);
        Assert.Equal(0xFFFFFFFEL, back.Values["High"].Number); // a DWORD above int.MaxValue keeps its bits
        Assert.Equal("%ProgramFiles%\\Epic", back.Values["Expand"].Text); // not expanded

        reg.Replace(key, new RegistryNode().Set("", "replaced"));
        Assert.Equal("replaced", reg.Read(key)!.GetString());
        Assert.Empty(reg.Read(key)!.Keys); // the old subkeys are gone

        reg.Delete(key);
        reg.Delete(key); // no error when missing
        Assert.Null(reg.Read(key));
    }

    [Fact]
    public void ThePrefixKeepsEverythingUnderTheThrowawayKey()
    {
        RequireWindows();
        new WindowsRegistry(_prefix).Replace(@"Software\Classes\glider-sandbox-check", new RegistryNode().Set("", "x"));

        using var real = Registry.CurrentUser.OpenSubKey(@"Software\Classes\glider-sandbox-check");
        Assert.Null(real);
        using var sandboxed = Registry.CurrentUser.OpenSubKey(_prefix + @"\Software\Classes\glider-sandbox-check");
        Assert.NotNull(sandboxed);
    }

    [Fact]
    public void EpicHandlerTakeoverAndRestoreOnTheRealRegistryApi()
    {
        RequireWindows();
        var reg = new WindowsRegistry(_prefix);
        var backup = Path.Combine(_temp, "protocol-backup.json");
        var exe = Path.Combine(_temp, "Glider.exe");
        var registrar = new ProtocolRegistrar(reg, exe, backup);
        const string epic = @"Software\Classes\com.epicgames.launcher";
        var original = FakeEgl();
        reg.Replace(epic, original);

        Assert.True(registrar.EnsureGliderScheme());
        Assert.False(registrar.EnsureGliderScheme());
        var enabled = registrar.EnableEpicHandler();
        Assert.True(enabled.GliderIsHandler);
        Assert.Equal($"\"{exe}\" \"%1\"", reg.Read(epic)!.Find("shell", "open", "command")!.GetString());

        var disabled = registrar.DisableEpicHandler();

        Assert.False(disabled.GliderIsHandler);
        Assert.True(original.SameAs(reg.Read(epic)), "the key is exactly what it was");
        Assert.False(File.Exists(backup));
        Assert.True(registrar.RemoveGliderScheme());
        Assert.Null(reg.Read(@"Software\Classes\glider"));
    }

    private static RegistryNode FakeEgl()
    {
        var node = new RegistryNode();
        node.Set("", "URL:test Protocol");
        node.Set("URL Protocol", "");
        node.Values["EditFlags"] = new RegValue { Kind = RegKind.DWord, Number = 2 };
        node.Values["High"] = new RegValue { Kind = RegKind.DWord, Number = 0xFFFFFFFEL };
        node.Values["Big"] = new RegValue { Kind = RegKind.QWord, Number = 1L << 40 };
        node.Values["Blob"] = new RegValue { Kind = RegKind.Binary, Bytes = [0, 1, 2, 255] };
        node.Values["Multi"] = new RegValue { Kind = RegKind.MultiString, Multi = ["one", "two"] };
        node.Values["Expand"] = new RegValue { Kind = RegKind.ExpandString, Text = "%ProgramFiles%\\Epic" };
        node.Child("DefaultIcon").Set("", @"C:\Epic\icon.ico,0");
        node.Child("shell").Child("open").Child("command").Set("", @"""C:\Epic\EpicGamesLauncher.exe"" %1");
        return node;
    }
}
