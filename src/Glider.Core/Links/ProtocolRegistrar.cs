namespace Glider.Core.Links;

/// <summary>Where the <c>com.epicgames.launcher://</c> handler stands for the current user.</summary>
/// <param name="GliderIsHandler">Glider's per-user registration is in place and points at the current exe.</param>
/// <param name="BackupHeld">A backup of the previous handler is stored (so turning the setting off restores it).</param>
/// <param name="OtherHandlerCommand">The current per-user handler's command when it is not Glider's (EGL's own HKCU entry, or another program).</param>
/// <param name="UserChoiceProgId">Set when Windows' "Choose a default app" overrides the registry for this scheme; Glider cannot change that.</param>
public sealed record EpicHandlerStatus(bool GliderIsHandler, bool BackupHeld, string? OtherHandlerCommand, string? UserChoiceProgId);

/// <summary>
/// Per-user URL protocol registration under <c>HKCU\Software\Classes</c> (no elevation). Owns two schemes:
/// <c>glider</c> (always, when Glider is installed) and <c>com.epicgames.launcher</c> (only while Settings.HandleEpicLinks is on).
/// Keys written by Glider carry a <c>GliderManaged</c> marker; Glider never edits or deletes a key without it, except the Epic
/// scheme right before it takes over, when the whole previous per-user key is saved to a backup file first.
/// </summary>
public sealed class ProtocolRegistrar
{
    public const string GliderScheme = "glider";
    public const string EpicScheme = EpicLauncherUri.Scheme;
    public const string ManagedValue = "GliderManaged";

    private const string ClassesPath = @"Software\Classes\";
    private const string UserChoicePath = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + EpicScheme + @"\UserChoice";

    private readonly IRegistryAccess _registry;
    private readonly string _backupFile;
    private readonly string _exePath;
    private readonly Action<string>? _log;

    /// <param name="exePath">The exe the links open: Velopack's stable root stub when installed, so the registration survives updates.</param>
    /// <param name="backupFile">Where the previous Epic handler is kept while Glider owns it (e.g. state/protocol-backup.json).</param>
    public ProtocolRegistrar(IRegistryAccess registry, string exePath, string backupFile, Action<string>? log = null)
    {
        _registry = registry;
        _exePath = exePath;
        _backupFile = backupFile;
        _log = log;
    }

    private string Command => $"\"{_exePath}\" \"%1\"";

    private RegistryNode Desired(string description)
    {
        var node = new RegistryNode();
        node.Set("", description);
        node.Set("URL Protocol", "");
        node.Set(ManagedValue, "1");
        node.Child("DefaultIcon").Set("", $"\"{_exePath}\",0");
        node.Child("shell").Child("open").Child("command").Set("", Command);
        return node;
    }

    private static bool IsManaged(RegistryNode? node) => node?.GetString(ManagedValue) == "1";

    private static string KeyOf(string scheme) => ClassesPath + scheme;

    // ---------------------------------------------------------------- glider://

    /// <summary>Registers (or repairs) <c>glider://</c> for this user. Returns true when something was written.</summary>
    public bool EnsureGliderScheme()
    {
        var current = _registry.Read(KeyOf(GliderScheme));
        var desired = Desired("URL:Glider");
        if (current is not null && !IsManaged(current))
        {
            _log?.Invoke("glider:// is registered by something else; leaving it alone");
            return false;
        }

        if (desired.SameAs(current))
        {
            return false;
        }

        _registry.Replace(KeyOf(GliderScheme), desired);
        _log?.Invoke("glider:// registered");
        return true;
    }

    /// <summary>Removes Glider's <c>glider://</c> registration (uninstall). Returns true when a key was deleted.</summary>
    public bool RemoveGliderScheme()
    {
        if (!IsManaged(_registry.Read(KeyOf(GliderScheme))))
        {
            return false;
        }

        _registry.Delete(KeyOf(GliderScheme));
        return true;
    }

    // ---------------------------------------------------------------- com.epicgames.launcher://

    public EpicHandlerStatus GetEpicStatus()
    {
        var current = _registry.Read(KeyOf(EpicScheme));
        var managed = IsManaged(current);
        var command = current?.Find("shell", "open", "command")?.GetString();
        var choice = _registry.Read(UserChoicePath)?.GetString("ProgId");
        return new EpicHandlerStatus(
            managed && string.Equals(command, Command, StringComparison.OrdinalIgnoreCase),
            ProtocolBackupFile.Load(_backupFile) is not null,
            managed ? null : command,
            string.IsNullOrEmpty(choice) ? null : choice);
    }

    /// <summary>
    /// Makes Glider the per-user handler for Epic launcher links. The previous per-user key (EGL's own, another program's, or none)
    /// is saved first; if that cannot be saved nothing is changed and the exception propagates. Calling it again only repairs the
    /// registration (the backup is not overwritten while Glider's key is in place).
    /// </summary>
    public EpicHandlerStatus EnableEpicHandler()
    {
        var current = _registry.Read(KeyOf(EpicScheme));
        if (!IsManaged(current))
        {
            ProtocolBackupFile.Save(_backupFile, new ProtocolBackup
            {
                Scheme = EpicScheme,
                SavedAt = DateTimeOffset.UtcNow,
                HkcuExisted = current is not null,
                Tree = current,
            });
        }

        var desired = Desired("URL:Epic Games Launcher (Glider)");
        if (!desired.SameAs(current))
        {
            _registry.Replace(KeyOf(EpicScheme), desired);
            _log?.Invoke("com.epicgames.launcher:// now opens Glider");
        }

        return GetEpicStatus();
    }

    /// <summary>
    /// Gives the scheme back: Glider's key is replaced by exactly what was there before (or deleted if there was no per-user key, which
    /// lets the machine-wide Epic Games Launcher registration show through again). A key someone else wrote since is never touched.
    /// </summary>
    public EpicHandlerStatus DisableEpicHandler()
    {
        var current = _registry.Read(KeyOf(EpicScheme));
        var backup = ProtocolBackupFile.Load(_backupFile);
        if (IsManaged(current))
        {
            if (backup is { HkcuExisted: true, Tree: { } tree })
            {
                _registry.Replace(KeyOf(EpicScheme), tree);
            }
            else
            {
                _registry.Delete(KeyOf(EpicScheme));
            }

            _log?.Invoke(backup is null
                ? "Glider's Epic link handler removed (no backup found; the machine-wide handler applies)"
                : "Previous Epic link handler restored");
        }

        if (backup is not null || File.Exists(_backupFile))
        {
            ProtocolBackupFile.Delete(_backupFile);
        }

        return GetEpicStatus();
    }
}
