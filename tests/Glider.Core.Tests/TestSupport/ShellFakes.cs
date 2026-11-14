using System.Collections.Concurrent;
using Glider.Core.Links;

namespace Glider.Core.Tests.TestSupport;

/// <summary>In-memory HKCU: the tests never touch the machine's registry. Reads and writes copy, like the real one.</summary>
public sealed class FakeRegistry : IRegistryAccess
{
    private readonly Dictionary<string, RegistryNode> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, <see cref="Replace"/> and <see cref="Delete"/> throw it (a registry that refuses writes).</summary>
    public Exception? FailWrites { get; set; }

    public List<string> Writes { get; } = [];

    public RegistryNode? Read(string keyPath) => _keys.TryGetValue(keyPath, out var n) ? Clone(n) : null;

    public void Replace(string keyPath, RegistryNode node)
    {
        if (FailWrites is not null)
        {
            throw FailWrites;
        }

        Writes.Add("replace " + keyPath);
        _keys[keyPath] = Clone(node);
    }

    public void Delete(string keyPath)
    {
        if (FailWrites is not null)
        {
            throw FailWrites;
        }

        Writes.Add("delete " + keyPath);
        _keys.Remove(keyPath);
    }

    /// <summary>Test setup: puts a key in place without counting as a Glider write.</summary>
    public void Seed(string keyPath, RegistryNode node) => _keys[keyPath] = Clone(node);

    public static RegistryNode Clone(RegistryNode n)
    {
        var copy = new RegistryNode();
        foreach (var (name, v) in n.Values)
        {
            copy.Values[name] = new RegValue
            {
                Kind = v.Kind,
                Text = v.Text,
                Number = v.Number,
                Bytes = v.Bytes?.ToArray(),
                Multi = v.Multi?.ToArray(),
            };
        }

        foreach (var (name, child) in n.Keys)
        {
            copy.Keys[name] = Clone(child);
        }

        return copy;
    }

    /// <summary>EGL-style registration: default value, URL Protocol, an icon and the open command, plus typed oddities a restore must keep.</summary>
    public static RegistryNode EglKey(string command = "\"C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win32\\EpicGamesLauncher.exe\" %1")
    {
        var node = new RegistryNode();
        node.Set("", "URL:com.epicgames.launcher Protocol");
        node.Set("URL Protocol", "");
        node.Values["EditFlags"] = new RegValue { Kind = RegKind.DWord, Number = 0x00000002 };
        node.Values["Blob"] = new RegValue { Kind = RegKind.Binary, Bytes = [1, 2, 3, 0, 255] };
        node.Values["Multi"] = new RegValue { Kind = RegKind.MultiString, Multi = ["a", "b"] };
        node.Values["Expand"] = new RegValue { Kind = RegKind.ExpandString, Text = "%ProgramFiles%\\Epic" };
        node.Values["Big"] = new RegValue { Kind = RegKind.QWord, Number = 1L << 40 };
        node.Child("DefaultIcon").Set("", "C:\\Epic\\icon.ico,0");
        node.Child("shell").Child("open").Child("command").Set("", command);
        return node;
    }
}

/// <summary>In-memory .lnk files. Paths are compared case-insensitively like Windows.</summary>
public sealed class FakeShellLinks : IShellLinks
{
    private readonly ConcurrentDictionary<string, ShellLinkSpec> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ShellLinkSpec> Files => _files;

    public int Writes { get; private set; }

    public bool Exists(string linkPath) => _files.ContainsKey(linkPath);

    public ShellLinkSpec? Read(string linkPath) => _files.GetValueOrDefault(linkPath);

    public void Write(string linkPath, ShellLinkSpec spec)
    {
        Writes++;
        _files[linkPath] = spec;
    }

    public bool Delete(string linkPath) => _files.TryRemove(linkPath, out _);

    public IReadOnlyList<string> Enumerate(string folder) =>
        [.. _files.Keys.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Test setup: a link another program made.</summary>
    public void Put(string linkPath, ShellLinkSpec spec) => _files[linkPath] = spec;
}

public sealed class FakeShell : IShellIntegration
{
    public FakeShell(string root, bool managesGliderScheme = false)
    {
        DesktopFolder = Path.Combine(root, "Desktop");
        StartMenuFolder = Path.Combine(root, "Start Menu", "Programs");
        LaunchExePath = Path.Combine(root, "Glider", "Glider.exe");
        ManagesGliderScheme = managesGliderScheme;
    }

    public string LaunchExePath { get; set; }

    public string? DesktopFolder { get; set; }

    public string? StartMenuFolder { get; set; }

    public FakeShellLinks FakeLinks { get; } = new();

    public FakeRegistry FakeRegistry { get; } = new();

    public IShellLinks Links => FakeLinks;

    public IRegistryAccess Registry => FakeRegistry;

    public bool ManagesGliderScheme { get; }

    public string DesktopLink(string name) => Path.Combine(DesktopFolder!, name + ".lnk");

    public string StartLink(string name) => Path.Combine(StartMenuFolder!, name + ".lnk");
}
