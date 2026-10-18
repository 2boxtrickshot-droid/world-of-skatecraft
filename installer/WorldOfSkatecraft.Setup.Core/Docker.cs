using System.Text;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// The Docker side: the Compose project's commands and the health the setup waits for. Every
/// command is built here, so the plan and the launcher start and stop exactly the same services.
/// </summary>
public sealed class Docker
{
    private readonly SetupPaths _paths;
    private readonly IProcessRunner _runner;
    private readonly string _docker;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public Docker(
        SetupPaths paths,
        IProcessRunner runner,
        string dockerExecutable = "docker",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _paths = paths;
        _runner = runner;
        _docker = dockerExecutable;
        _environment = environment ?? new Dictionary<string, string>();
    }

    /// <summary>Where the compose file and the project's data are.</summary>
    public string ProjectDirectory => _paths.Server;

    /// <summary>A `docker compose` command for this project, whatever folder it is installed in.</summary>
    public CommandSpec Compose(params string[] arguments)
    {
        var args = new List<string>
        {
            "compose",
            "-p", Upstreams.ProjectName,
            "--project-directory", _paths.Server,
            "-f", _paths.ComposeFile,
        };
        args.AddRange(arguments);
        return new CommandSpec(_docker, args, _paths.Server, _environment);
    }

    /// <summary>A plain `docker` command.</summary>
    public CommandSpec DockerCommand(params string[] arguments) =>
        new(_docker, arguments, _paths.Server, _environment);

    /// <summary>Whether the Docker engine answers, which is also whether Docker Desktop is up.</summary>
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) =>
        (await _runner
            .RunAsync(DockerCommand("info"), null, cancellationToken)
            .ConfigureAwait(false)).Ok;

    /// <summary>Brings the project up, detached.</summary>
    public Task<CommandResult> UpAsync(CancellationToken cancellationToken = default) =>
        _runner.RunAsync(Compose("up", "-d"), null, cancellationToken);

    /// <summary>Stops the project's services, keeping the database volume and the map data.</summary>
    public Task<CommandResult> StopAsync(CancellationToken cancellationToken = default) =>
        _runner.RunAsync(Compose("stop"), null, cancellationToken);

    /// <summary>Removes the project's containers, keeping its volumes.</summary>
    public Task<CommandResult> DownAsync(CancellationToken cancellationToken = default) =>
        _runner.RunAsync(Compose("down"), null, cancellationToken);

    /// <summary>Whether an image is already in the local Docker's store.</summary>
    public async Task<bool> HasImageAsync(string image, CancellationToken cancellationToken = default) =>
        (await _runner
            .RunAsync(DockerCommand("image", "inspect", image), null, cancellationToken)
            .ConfigureAwait(false)).Ok;

    /// <summary>The id of a service's container, or null while it does not exist yet.</summary>
    public async Task<string?> ContainerIdAsync(string service, CancellationToken cancellationToken = default)
    {
        var result = await _runner
            .RunAsync(Compose("ps", "-q", service), null, cancellationToken)
            .ConfigureAwait(false);
        var id = result.StdOut.Trim();
        return result.Ok && id.Length > 0 ? id.Split('\n')[0].Trim() : null;
    }

    /// <summary>The container's health as Docker reports it: "starting", "healthy" or "unhealthy".</summary>
    public async Task<string?> HealthAsync(string service, CancellationToken cancellationToken = default)
    {
        var id = await ContainerIdAsync(service, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return null;
        }
        var result = await _runner
            .RunAsync(
                DockerCommand("inspect", "-f", "{{.State.Health.Status}}", id),
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : null;
    }

    /// <summary>A service's container state and exit code, for a failure's message.</summary>
    public async Task<string> StateAsync(string service, CancellationToken cancellationToken = default)
    {
        var id = await ContainerIdAsync(service, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return "no container";
        }
        var result = await _runner
            .RunAsync(
                DockerCommand("inspect", "-f", "{{.State.Status}} (exit {{.State.ExitCode}})", id),
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : "unknown";
    }

    /// <summary>The last lines a service logged, to put in a failure's message.</summary>
    public async Task<string> LogsAsync(
        string service,
        int lines = 40,
        CancellationToken cancellationToken = default)
    {
        var result = await _runner
            .RunAsync(Compose("logs", "--tail", lines.ToString(), service), null, cancellationToken)
            .ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>
    /// Waits for a service to report healthy, handing each poll to <paramref name="onWait"/> so a
    /// long wait can be shown. A first start runs the database migrations, which takes minutes.
    /// </summary>
    public async Task<bool> WaitHealthyAsync(
        string service,
        TimeSpan timeout,
        Action<TimeSpan>? onWait = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HealthAsync(service, cancellationToken).ConfigureAwait(false) == "healthy")
            {
                return true;
            }
            var waited = DateTime.UtcNow - started;
            if (waited >= timeout)
            {
                return false;
            }
            onWait?.Invoke(waited);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a one-off container from an image: how the setup extracts map data, and how it asks an
    /// image a question. Paths are mounted with `--mount`, whose source is a real path field rather
    /// than `-v`'s single string, which a Windows drive letter would confuse.
    /// </summary>
    public CommandSpec RunOnce(
        string image,
        string? entryPoint = null,
        IEnumerable<string>? mounts = null,
        IEnumerable<string>? environment = null,
        IEnumerable<string>? command = null)
    {
        var args = new List<string> { "run", "--rm" };
        if (entryPoint is not null)
        {
            args.Add("--entrypoint");
            args.Add(entryPoint);
        }
        foreach (var variable in environment ?? Array.Empty<string>())
        {
            args.Add("-e");
            args.Add(variable);
        }
        foreach (var mount in mounts ?? Array.Empty<string>())
        {
            args.Add("--mount");
            args.Add(mount);
        }
        args.Add(image);
        args.AddRange(command ?? Array.Empty<string>());
        return new CommandSpec(_docker, args, _paths.Server, _environment);
    }

    /// <summary>A bind mount for <see cref="RunOnce"/>, read-only when the source must not change.</summary>
    public static string Mount(string source, string target, bool readOnly = false) =>
        $"type=bind,source={source},target={target}" + (readOnly ? ",readonly" : "");
}
