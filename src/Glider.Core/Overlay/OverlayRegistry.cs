namespace Glider.Core.Overlay;

/// <summary>
/// The per-user registry entries that switch the EOS overlay on (lfs/eos.py): <c>HKCU\SOFTWARE\Epic Games\EOS\OverlayPath</c> for the
/// EOS SDK and the two Vulkan implicit-layer values. Glider only ever touches HKCU through this interface (machine-wide HKLM values,
/// set by Epic's EOS service, need administrator rights and belong to Epic's own software), so tests fake it and never reach the real registry.
/// </summary>
public interface IOverlayRegistry
{
    /// <summary>The overlay folder HKCU points at, as stored; null when unset.</summary>
    string? GetOverlayPath();

    /// <summary>Points HKCU at <paramref name="overlayFolder"/>: OverlayPath plus both Vulkan layer values. Idempotent.</summary>
    void Enable(string overlayFolder);

    /// <summary>Removes HKCU OverlayPath and the HKCU EOS Vulkan layer values. Never touches HKLM.</summary>
    void Disable();

    /// <summary>EOS Vulkan layers registered machine-wide (HKLM, both views). Read-only; used to explain what "off" leaves in place.</summary>
    IReadOnlyList<string> MachineLayers();
}

/// <summary>Where Epic's own software keeps an overlay Glider may reuse (Program Files (x86)); tests point it at a temp tree.</summary>
public sealed record OverlayEnvironment(string? ProgramFilesX86)
{
    /// <summary>The overlay's fixed app id (EOSOverlayApp in lfs/eos.py).</summary>
    public const string OverlayAppId = "98bc04bc842e4906993fd6d6644ffb8d";

    public static OverlayEnvironment ForMachine() => new(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    /// <summary>The folders Legendary's <c>search_overlay_installs</c> checks besides its own (core.py:2170-2200), in its order.</summary>
    public IEnumerable<string> ExternalCandidates()
    {
        if (string.IsNullOrWhiteSpace(ProgramFilesX86))
        {
            yield break;
        }

        yield return Path.Combine(ProgramFilesX86, "Epic Games", "Launcher", "Portal", "Extras", "Overlay");
        yield return Path.Combine(ProgramFilesX86, "Epic Games", "Epic Online Services", "managedArtifacts", OverlayAppId);
    }
}

/// <summary>The real registry. Never constructed by tests.</summary>
public sealed class WindowsOverlayRegistry : IOverlayRegistry
{
    private const string EosKey = @"SOFTWARE\Epic Games\EOS";
    private const string EosValue = "OverlayPath";
    private const string VulkanKey = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
    private const string LayerFilePrefix = "EOSOverlayVkLayer";

    public string? GetOverlayPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(EosKey);
        return key?.GetValue(EosValue) as string;
    }

    public void Enable(string overlayFolder)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The EOS overlay registry entries exist on Windows only.");
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(overlayFolder));
        using (var eos = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(EosKey))
        {
            // Stored with forward slashes, like Legendary (add_registry_entries).
            eos.SetValue(EosValue, folder.Replace('\\', '/'), Microsoft.Win32.RegistryValueKind.String);
        }

        using var vulkan = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(VulkanKey);
        foreach (var layer in new[] { "EOSOverlayVkLayer-Win32.json", "EOSOverlayVkLayer-Win64.json" })
        {
            vulkan.SetValue(Path.Combine(folder, layer), 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    public void Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (var eos = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(EosKey, writable: true))
        {
            eos?.DeleteValue(EosValue, throwOnMissingValue: false);
        }

        using var vulkan = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(VulkanKey, writable: true);
        if (vulkan is null)
        {
            return;
        }

        foreach (var name in vulkan.GetValueNames().Where(IsEosLayer))
        {
            vulkan.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    public IReadOnlyList<string> MachineLayers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var found = new List<string>();
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(VulkanKey);
                if (key is not null)
                {
                    found.AddRange(key.GetValueNames().Where(IsEosLayer));
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable = nothing to report.
            }
        }

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsEosLayer(string valueName) =>
        Path.GetFileName(valueName).StartsWith(LayerFilePrefix, StringComparison.OrdinalIgnoreCase);
}
