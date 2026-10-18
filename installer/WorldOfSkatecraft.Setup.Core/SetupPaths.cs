namespace WorldOfSkatecraft.Setup.Core;

/// <summary>Every folder the install is made of, resolved from one root.</summary>
/// <remarks>
/// The layout is flat and every folder's role is in its name, so a player can look inside, and so
/// an update can replace <see cref="App"/> and <see cref="Source"/> and keep everything else.
/// Nothing is written outside the root except the desktop shortcut, the Start Menu entry and — by
/// Docker — the container images.
/// </remarks>
public sealed class SetupPaths
{
    public SetupPaths(string installRoot)
    {
        Root = Path.GetFullPath(installRoot);
    }

    /// <summary>The install root, chosen by the player (default: <c>%LOCALAPPDATA%\WorldOfSkatecraft</c>).</summary>
    public string Root { get; }

    /// <summary>This installer and its launcher: replaced wholesale by an update.</summary>
    public string App => Path.Combine(Root, "app");

    /// <summary>The project's source tree, the client is built from here. Replaced by an update.</summary>
    public string Source => Path.Combine(Root, "src");

    /// <summary>The client binary and its local state, once built.</summary>
    public string Client => Path.Combine(Root, "client");

    /// <summary>The built client.</summary>
    public string ClientExe => Path.Combine(Client, "benilla.exe");

    /// <summary>Game data converted from the player's own copies: <c>skate-data</c>, <c>skate-audio</c>.</summary>
    public string Game => Path.Combine(Root, "game");

    /// <summary>The skate engine's converted assets (<c>WOW_SKATE_ASSETS</c>).</summary>
    public string SkateAssets => Path.Combine(Game, "skate-data", "assets");

    /// <summary>The converted skate sounds (<c>WOW_SKATE_AUDIO</c>).</summary>
    public string SkateAudio => Path.Combine(Game, "skate-audio");

    /// <summary>The vmangos-deploy checkout, its config, its database volume and its map data.</summary>
    public string Server => Path.Combine(Root, "server");

    /// <summary>The generated Compose file.</summary>
    public string ComposeFile => Path.Combine(Server, "compose.yaml");

    /// <summary>The server's extracted map data (hours to produce; never deleted by a repair).</summary>
    public string ExtractedData => Path.Combine(Server, "storage", "mangosd", "extracted-data");

    /// <summary>The client data the extraction reads, so the WoW folder itself stays read-only.</summary>
    public string ClientDataScratch => Path.Combine(Server, "storage", "mangosd", "client-data");

    /// <summary>Setup's own tools: the Python venv, the engine checkout, vgmstream.</summary>
    public string Tool => Path.Combine(Root, "tool");

    /// <summary>The Python interpreter of setup's venv.</summary>
    public string VenvPython => Path.Combine(Tool, "venv", "Scripts", "python.exe");

    /// <summary>The Skate 3 Rust Engine checkout the converters run out of.</summary>
    public string Engine => Path.Combine(Tool, "engine");

    /// <summary>vgmstream, for the skate sounds.</summary>
    public string Vgmstream => Path.Combine(Tool, "vgmstream");

    /// <summary>Setup's state: what it was told, and how far it got.</summary>
    public string State => Path.Combine(Root, "state");

    /// <summary>The persisted answers.</summary>
    public string StateFile => Path.Combine(State, "setup.json");

    /// <summary>The setup log, appended to by every run.</summary>
    public string LogFile => Path.Combine(State, "setup.log");

    /// <summary>The diagnostic report, written by the doctor and by a failed run.</summary>
    public string DiagnosticsFile => Path.Combine(State, "diagnostics.txt");

    /// <summary>The client's settings (<c>BENILLA_HOME</c>), so nothing lands in the source tree.</summary>
    public string ClientConfig => Path.Combine(Root, "config");

    /// <summary>Folders the setup creates up front.</summary>
    public IEnumerable<string> AllDirectories()
    {
        yield return Root;
        yield return App;
        yield return Source;
        yield return Client;
        yield return Game;
        yield return Server;
        yield return Tool;
        yield return State;
        yield return ClientConfig;
    }

    /// <summary>The default install root: per-user, so no administrator is needed to install.</summary>
    public static string DefaultRoot(IHostEnvironment host) =>
        Path.Combine(host.GetFolder(KnownFolder.LocalApplicationData), "WorldOfSkatecraft");

    /// <summary>The folder of the running program: the package the player extracted.</summary>
    public static string ProgramFolder => AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>The folders setup asks the OS for, named rather than taken as an enum, so a test can fake them.</summary>
public enum KnownFolder
{
    LocalApplicationData,
    Desktop,
    StartMenuPrograms,
    UserProfile,
    ProgramFiles,
}

/// <summary>Everything setup asks the machine about, so a test can answer without a Windows PC.</summary>
public interface IHostEnvironment
{
    /// <summary>True on the machine a player installs on.</summary>
    bool IsWindows { get; }

    /// <summary>The folder's path, per-user where the OS allows it.</summary>
    string GetFolder(KnownFolder folder);

    /// <summary>The environment variable, or null; setup reads PATH and writes the launcher's own.</summary>
    string? GetEnvironment(string name);

    /// <summary>Free space in gigabytes at or above a path, for the disk check.</summary>
    double FreeSpaceGigabytes(string path);
}
