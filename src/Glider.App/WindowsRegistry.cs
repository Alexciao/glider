using System.Runtime.Versioning;
using Glider.Core.Links;
using Microsoft.Win32;

namespace Glider.App;

/// <summary>
/// <see cref="IRegistryAccess"/> over HKEY_CURRENT_USER. Self-contained (no host logging) so the same class is compiled into the test
/// project, where a <paramref name="prefix"/> moves every path under a throwaway key (<c>Software\GliderTests\&lt;guid&gt;</c>) and the real
/// <c>Software\Classes</c> is never touched.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistry(string? prefix = null) : IRegistryAccess
{
    private string Full(string path) => string.IsNullOrEmpty(prefix) ? path : prefix.TrimEnd('\\') + "\\" + path;

    public RegistryNode? Read(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Full(keyPath), writable: false);
        return key is null ? null : ReadKey(key);
    }

    public void Replace(string keyPath, RegistryNode node)
    {
        Delete(keyPath);
        using var key = Registry.CurrentUser.CreateSubKey(Full(keyPath), writable: true);
        WriteKey(key, node);
    }

    public void Delete(string keyPath) => Registry.CurrentUser.DeleteSubKeyTree(Full(keyPath), throwOnMissingSubKey: false);

    private static RegistryNode ReadKey(RegistryKey key)
    {
        var node = new RegistryNode();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var v = (kind, value) switch
            {
                (RegistryValueKind.String, string s) => new RegValue { Kind = RegKind.String, Text = s },
                (RegistryValueKind.ExpandString, string s) => new RegValue { Kind = RegKind.ExpandString, Text = s },
                (RegistryValueKind.DWord, int i) => new RegValue { Kind = RegKind.DWord, Number = unchecked((uint)i) },
                (RegistryValueKind.QWord, long l) => new RegValue { Kind = RegKind.QWord, Number = l },
                (RegistryValueKind.MultiString, string[] m) => new RegValue { Kind = RegKind.MultiString, Multi = m },
                (_, byte[] b) => new RegValue { Kind = RegKind.Binary, Bytes = b },
                _ => null,
            };
            if (v is not null)
            {
                node.Values[name] = v;
            }
        }

        foreach (var sub in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(sub, writable: false);
            if (child is not null)
            {
                node.Keys[sub] = ReadKey(child);
            }
        }

        return node;
    }

    private static void WriteKey(RegistryKey key, RegistryNode node)
    {
        foreach (var (name, v) in node.Values)
        {
            switch (v.Kind)
            {
                case RegKind.String:
                    key.SetValue(name, v.Text ?? "", RegistryValueKind.String);
                    break;
                case RegKind.ExpandString:
                    key.SetValue(name, v.Text ?? "", RegistryValueKind.ExpandString);
                    break;
                case RegKind.DWord:
                    key.SetValue(name, unchecked((int)(uint)v.Number), RegistryValueKind.DWord);
                    break;
                case RegKind.QWord:
                    key.SetValue(name, v.Number, RegistryValueKind.QWord);
                    break;
                case RegKind.MultiString:
                    key.SetValue(name, v.Multi ?? [], RegistryValueKind.MultiString);
                    break;
                default:
                    key.SetValue(name, v.Bytes ?? [], RegistryValueKind.Binary);
                    break;
            }
        }

        foreach (var (name, child) in node.Keys)
        {
            using var sub = key.CreateSubKey(name, writable: true);
            WriteKey(sub, child);
        }
    }
}
