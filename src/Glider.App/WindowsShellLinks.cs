using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Glider.Core.Links;

namespace Glider.App;

/// <summary>
/// <see cref="IShellLinks"/> over the shell's IShellLink / IPersistFile COM objects (the only supported way to write a .lnk). Writes go to a
/// temp file next to the target and are moved into place, so a half-written shortcut is never visible. Self-contained so the tests can
/// compile it and exercise real .lnk files in a temp folder.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsShellLinks : IShellLinks
{
    private const int MaxPath = 1024;
    private const uint SlgpRawPath = 0x4;

    public bool Exists(string linkPath) => File.Exists(linkPath);

    public ShellLinkSpec? Read(string linkPath)
    {
        if (!File.Exists(linkPath))
        {
            return null;
        }

        object? com = null;
        try
        {
            com = new ShellLinkCoClass();
            ((IPersistFile)com).Load(linkPath, 0);
            var link = (IShellLinkW)com;
            var target = new StringBuilder(MaxPath);
            link.GetPath(target, MaxPath, nint.Zero, SlgpRawPath);
            var args = new StringBuilder(MaxPath);
            link.GetArguments(args, MaxPath);
            var dir = new StringBuilder(MaxPath);
            link.GetWorkingDirectory(dir, MaxPath);
            var icon = new StringBuilder(MaxPath);
            link.GetIconLocation(icon, MaxPath, out var index);
            var desc = new StringBuilder(MaxPath);
            link.GetDescription(desc, MaxPath);
            return new ShellLinkSpec(target.ToString(), args.ToString(), dir.ToString(), icon.ToString(), index, desc.ToString());
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (com is not null)
            {
                Marshal.FinalReleaseComObject(com);
            }
        }
    }

    public void Write(string linkPath, ShellLinkSpec spec)
    {
        var dir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = linkPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        object? com = null;
        try
        {
            com = new ShellLinkCoClass();
            var link = (IShellLinkW)com;
            link.SetPath(spec.Target);
            link.SetArguments(spec.Arguments);
            link.SetWorkingDirectory(spec.WorkingDirectory);
            link.SetIconLocation(spec.IconPath, spec.IconIndex);
            link.SetDescription(spec.Description);
            ((IPersistFile)com).Save(temp, true);
            File.Move(temp, linkPath, overwrite: true);
        }
        finally
        {
            if (com is not null)
            {
                Marshal.FinalReleaseComObject(com);
            }

            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public bool Delete(string linkPath)
    {
        if (!File.Exists(linkPath))
        {
            return false;
        }

        File.Delete(linkPath);
        return true;
    }

    public IReadOnlyList<string> Enumerate(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, nint findData, uint flags);

        void GetIDList(out nint pidl);

        void SetIDList(nint pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(nint hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
