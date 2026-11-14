namespace Glider.Core.Links;

/// <summary>Everything a Windows shell link (.lnk) holds that Glider sets.</summary>
public sealed record ShellLinkSpec(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex, string Description);

/// <summary>.lnk files. The real implementation (IShellLink COM) lives in Glider.App; tests use an in-memory fake and never touch a real Desktop or Start menu.</summary>
public interface IShellLinks
{
    bool Exists(string linkPath);

    /// <summary>The link's settings, or null when the file is missing or isn't a readable shell link.</summary>
    ShellLinkSpec? Read(string linkPath);

    /// <summary>Creates or replaces the link (atomically where the platform allows).</summary>
    void Write(string linkPath, ShellLinkSpec spec);

    /// <summary>Deletes the file; false when there was none.</summary>
    bool Delete(string linkPath);

    /// <summary>Top-level *.lnk files of the folder (no recursion).</summary>
    IReadOnlyList<string> Enumerate(string folder);
}

/// <summary>What Core needs from the host to integrate with the shell: shortcuts and URL protocols. Null in Core options = feature unavailable.</summary>
public interface IShellIntegration
{
    /// <summary>The exe that shortcuts and protocol handlers start: Velopack's stable root stub when installed (survives updates), else the running exe.</summary>
    string LaunchExePath { get; }

    /// <summary>The user's Desktop folder; null when it can't be determined.</summary>
    string? DesktopFolder { get; }

    /// <summary>The user's Start menu "Programs" folder (per-user, no elevation).</summary>
    string? StartMenuFolder { get; }

    IShellLinks Links { get; }

    IRegistryAccess Registry { get; }

    /// <summary>
    /// True for an installed build: Glider keeps its <c>glider://</c> registration current at every start. Portable and development
    /// runs leave the registry alone unless the user turns on an Epic link handler.
    /// </summary>
    bool ManagesGliderScheme { get; }
}
