using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// What the setup was told and how far it got, persisted as <c>state/setup.json</c> beside the
/// install. Every step reads this and skips itself when its result is already here, so a stopped
/// setup continues where it left off and a repair redoes only what is missing.
/// </summary>
public sealed class SetupState
{
    /// <summary>The layout version; a mismatch is a state file from another setup generation.</summary>
    [JsonPropertyName("stateVersion")]
    public int StateVersion { get; set; } = CurrentStateVersion;

    public const int CurrentStateVersion = 1;

    /// <summary>The package version that wrote this state, for the update check.</summary>
    [JsonPropertyName("packageVersion")]
    public string PackageVersion { get; set; } = "";

    [JsonPropertyName("installRoot")]
    public string InstallRoot { get; set; } = "";

    /// <summary>The player's WoW 1.12.1 folder: the one holding <c>Data/</c>.</summary>
    [JsonPropertyName("wowFolder")]
    public string WowFolder { get; set; } = "";

    /// <summary>The player's extracted Skate 3 folder: the one holding <c>default.xex</c> and <c>data/</c>.</summary>
    [JsonPropertyName("skateFolder")]
    public string SkateFolder { get; set; } = "";

    /// <summary>The game account's name. The password is never stored, only used to create the account.</summary>
    [JsonPropertyName("account")]
    public string Account { get; set; } = "";

    /// <summary>The account's GM level, given so the player can use chat commands like any GM.</summary>
    [JsonPropertyName("accountGmLevel")]
    public int AccountGmLevel { get; set; } = 3;

    /// <summary>Whether the desktop shortcut and the Start Menu entry were made.</summary>
    [JsonPropertyName("shortcutCreated")]
    public bool ShortcutCreated { get; set; }

    /// <summary>Whether the client is a prebuilt one from the package rather than a local build.</summary>
    [JsonPropertyName("clientIsPrebuilt")]
    public bool ClientIsPrebuilt { get; set; }

    /// <summary>Whether the sounds were converted; a failure there is not fatal.</summary>
    [JsonPropertyName("soundsConverted")]
    public bool SoundsConverted { get; set; }

    /// <summary>Whether the extracted map data came out of a run that reported no errors.</summary>
    [JsonPropertyName("mapsExtracted")]
    public bool MapsExtracted { get; set; }

    /// <summary>How many cores the server build and the client build may use.</summary>
    [JsonPropertyName("buildJobs")]
    public int BuildJobs { get; set; } = 4;

    /// <summary>Whether the server is stopped when the game exits (the player can keep it running).</summary>
    [JsonPropertyName("stopServerOnExit")]
    public bool StopServerOnExit { get; set; } = true;

    /// <summary>Whether setup builds the patched server that carries the Skateboarding profession.</summary>
    [JsonPropertyName("buildPatchedServer")]
    public bool BuildPatchedServer { get; set; } = true;

    /// <summary>The last time setup ran, as an ISO-8601 stamp.</summary>
    [JsonPropertyName("lastRun")]
    public string LastRun { get; set; } = "";

    /// <summary>The steps a previous run finished, so a rerun can say what it is skipping.</summary>
    [JsonPropertyName("completedSteps")]
    public List<string> CompletedSteps { get; set; } = new();

    /// <summary>The state as it is stored on disk, or a fresh one when there is none to read.</summary>
    public static SetupState Load(SetupPaths paths)
    {
        if (!File.Exists(paths.StateFile))
        {
            return new SetupState();
        }
        try
        {
            var text = File.ReadAllText(paths.StateFile);
            var state = JsonSerializer.Deserialize<SetupState>(text) ?? new SetupState();
            return state.StateVersion == CurrentStateVersion ? state : new SetupState();
        }
        catch (JsonException)
        {
            // A half-written file from a killed run is not worth failing over: start from scratch.
            return new SetupState();
        }
    }

    /// <summary>Writes the state atomically, so a killed setup never leaves half a file.</summary>
    public void Save(SetupPaths paths)
    {
        Directory.CreateDirectory(paths.State);
        StateVersion = CurrentStateVersion;
        LastRun = DateTime.UtcNow.ToString("o");
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temp = paths.StateFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, paths.StateFile, overwrite: true);
    }

    /// <summary>The install is far enough along to play: it is the check the Play button makes.</summary>
    public bool LooksInstalled(SetupPaths paths) =>
        File.Exists(paths.ClientExe)
        && File.Exists(Path.Combine(paths.SkateAssets, "private", "skater.glb"))
        && File.Exists(paths.ComposeFile);
}
