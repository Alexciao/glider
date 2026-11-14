using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Overlay;

namespace Glider.Core.Tests.Services;

/// <summary>In-memory stand-in for the HKCU overlay entries. Tests must never reach the real registry.</summary>
public sealed class FakeOverlayRegistry : IOverlayRegistry
{
    public string? Path { get; set; }
    public List<string> Layers { get; } = [];
    public List<string> MachineWide { get; } = [];
    public List<string> Calls { get; } = [];

    public string? GetOverlayPath() => Path;

    public void Enable(string overlayFolder)
    {
        Calls.Add("enable:" + overlayFolder);
        Path = overlayFolder.Replace('\\', '/');
        Layers.Clear();
        Layers.Add(System.IO.Path.Combine(overlayFolder, "EOSOverlayVkLayer-Win32.json"));
        Layers.Add(System.IO.Path.Combine(overlayFolder, "EOSOverlayVkLayer-Win64.json"));
    }

    public void Disable()
    {
        Calls.Add("disable");
        Path = null;
        Layers.Clear();
    }

    public IReadOnlyList<string> MachineLayers() => MachineWide;
}

public sealed class FakeLaunchers : IThirdPartyLaunchers
{
    public HashSet<ThirdPartyStore> Installed { get; } = [ThirdPartyStore.Ubisoft, ThirdPartyStore.Ea];

    public bool IsInstalled(ThirdPartyStore store) => Installed.Contains(store);
}
