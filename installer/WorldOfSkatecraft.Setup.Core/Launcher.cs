namespace WorldOfSkatecraft.Setup.Core;

/// <summary>What the launcher found when it started, for its window and its log.</summary>
public sealed record PlayReport(bool Started, string Message, string? Fix = null);

/// <summary>
/// The Play button: it brings the server up, waits until it can accept a login, starts the game
/// with the paths it needs, and stops the server again when the game exits.
/// </summary>
public sealed class Launcher
{
    private readonly SetupContext _ctx;

    public Launcher(SetupContext context) => _ctx = context;

    /// <summary>The environment the client reads its data from; the launcher's whole job.</summary>
    public IReadOnlyDictionary<string, string> ClientEnvironment()
    {
        var environment = new Dictionary<string, string>(_ctx.ChildEnvironment)
        {
            // The 1.12.1 install, read in place and never written to.
            ["WOW_DATA"] = Path.Combine(_ctx.State.WowFolder, "Data"),
            ["WOW_HOST"] = "127.0.0.1",
            // The skate assets the setup converted from the player's own Skate 3.
            ["WOW_SKATE_ASSETS"] = _ctx.Paths.SkateAssets,
            ["WOW_SKATE_AUDIO"] = _ctx.Paths.SkateAudio,
            // The client's own settings folder, inside the install rather than in the source tree.
            ["BENILLA_HOME"] = _ctx.Paths.ClientConfig,
        };
        return environment;
    }

    /// <summary>Starts the server and waits for it to be able to serve a login.</summary>
    public async Task<PlayReport> StartServerAsync()
    {
        if (!File.Exists(_ctx.Paths.ComposeFile))
        {
            return new PlayReport(
                false,
                "the game has not been installed yet: its server files are missing",
                $"Run {Path.Combine(_ctx.Paths.App, "Setup.exe")} first.");
        }
        if (!await _ctx.Docker.IsRunningAsync(_ctx.Cancellation).ConfigureAwait(false))
        {
            var started = await WindowsPrerequisites.WaitForEngine(_ctx).ConfigureAwait(false);
            if (!started)
            {
                if (WindowsPrerequisites.DockerDesktopExecutable(_ctx) is { } exe)
                {
                    _ctx.Say("starting Docker Desktop");
                    _ctx.StartDetached(exe);
                    started = await WindowsPrerequisites.WaitForEngine(_ctx).ConfigureAwait(false);
                }
            }
            if (!started)
            {
                return new PlayReport(
                    false,
                    "Docker Desktop is not running",
                    "Start Docker Desktop and wait until it says the engine is running, then press Play again.");
            }
        }

        if (await _ctx.Docker.HealthAsync("mangosd", _ctx.Cancellation).ConfigureAwait(false) != "healthy")
        {
            _ctx.Say("starting the local server");
            var up = await _ctx.Run(_ctx.Docker.Compose("up", "-d"), "compose:").ConfigureAwait(false);
            if (!up.Ok)
            {
                return new PlayReport(
                    false,
                    "the local server did not start",
                    "Run Setup.exe --repair (or look at state\\setup.log) and try again.");
            }
            var healthy = await _ctx.Docker
                .WaitHealthyAsync(
                    "mangosd",
                    TimeSpan.FromMinutes(10),
                    waited => _ctx.Report(null, $"waiting for the world server ({waited.TotalSeconds:F0}s)"),
                    _ctx.Cancellation)
                .ConfigureAwait(false);
            if (!healthy)
            {
                return new PlayReport(
                    false,
                    "the world server did not come up",
                    "Run Setup.exe --repair; its log says what the server printed.");
            }
        }
        var login = await Net.WaitForPortAsync("127.0.0.1", 3724, TimeSpan.FromMinutes(2), _ctx.Cancellation)
            .ConfigureAwait(false);
        return login
            ? new PlayReport(true, "the server is up")
            : new PlayReport(
                false,
                "the login service is not answering on port 3724",
                "Wait a few seconds and press Play again; if it repeats, run Setup.exe --repair.");
    }

    /// <summary>Starts the game and waits for it to exit; true when it ran.</summary>
    public async Task<bool> RunClientAsync()
    {
        var exe = _ctx.Paths.ClientExe;
        if (!File.Exists(exe))
        {
            return false;
        }
        _ctx.Say("starting the game");
        var environment = ClientEnvironment();
        _ctx.Log.Line($"client environment: WOW_DATA={environment["WOW_DATA"]}");
        var start = new System.Diagnostics.ProcessStartInfo(exe)
        {
            WorkingDirectory = _ctx.Paths.Client,
            UseShellExecute = false,
        };
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return false;
        }
        await process.WaitForExitAsync(_ctx.Cancellation).ConfigureAwait(false);
        _ctx.Log.Line($"the game exited with {process.ExitCode}");
        return true;
    }

    /// <summary>Stops the server's containers, keeping the database and the map data.</summary>
    public async Task StopServerAsync()
    {
        if (!File.Exists(_ctx.Paths.ComposeFile))
        {
            return;
        }
        _ctx.Say("stopping the local server");
        await _ctx.Docker.StopAsync(_ctx.Cancellation).ConfigureAwait(false);
    }
}
