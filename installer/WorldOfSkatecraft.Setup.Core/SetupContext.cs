using System.IO.Compression;
using System.Security.Cryptography;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>A step's progress, for a progress bar and a status line.</summary>
public sealed record StepProgress(string StepId, double? Fraction, string Message);

/// <summary>What is being run, and how: a normal install, a dry run, or a repair.</summary>
public sealed class SetupOptions
{
    /// <summary>Print every command instead of running it. Used by the CLI and the tests.</summary>
    public bool DryRun { get; init; }

    /// <summary>Check every result again and redo what is missing, rather than trusting the state.</summary>
    public bool Repair { get; init; }

    /// <summary>Keep going after a failed step; only for a diagnostic run.</summary>
    public bool KeepGoingAfterFailure { get; init; }

    /// <summary>Skip the steps that need Docker, for a machine that only wants the client prepared.</summary>
    public bool SkipServer { get; init; }

    /// <summary>Skip the client build, for a machine that already has one.</summary>
    public bool SkipClient { get; init; }
}

/// <summary>
/// Everything a step needs, and the only way a step touches the machine. Every command goes through
/// <see cref="Run"/> or <see cref="Capture"/>, so a dry run records what would have happened and the
/// tests assert a whole plan without Docker, a compiler or a Windows PC.
/// </summary>
public sealed class SetupContext
{
    private readonly Action<StepProgress>? _progress;
    private string _stepId = "";

    public SetupContext(
        SetupState state,
        SetupPaths paths,
        IHostEnvironment host,
        IProcessRunner runner,
        IDownloader downloader,
        SetupLog log,
        SetupOptions options,
        Action<StepProgress>? progress = null,
        IShortcuts? shortcuts = null)
    {
        State = state;
        Paths = paths;
        Host = host;
        Runner = runner;
        Downloader = downloader;
        Log = log;
        Options = options;
        _progress = progress;
        Shortcuts = shortcuts ?? WorldOfSkatecraft.Setup.Core.Shortcuts.For(host);
        Tools = new ToolLocator(host, runner);
        Docker = new Docker(paths, runner, "docker", ChildEnvironment);
        TimeZone = ResolveTimeZone();
    }

    public SetupState State { get; }

    public SetupPaths Paths { get; }

    public IHostEnvironment Host { get; }

    public IProcessRunner Runner { get; }

    public IDownloader Downloader { get; }

    public SetupLog Log { get; }

    public SetupOptions Options { get; }

    public ToolLocator Tools { get; }

    public Docker Docker { get; }

    /// <summary>The desktop and Start-menu shortcuts.</summary>
    public IShortcuts Shortcuts { get; }

    /// <summary>The account's password for this run only: it is never written to disk.</summary>
    public string AccountPassword { get; set; } = "";

    public CancellationToken Cancellation { get; set; }

    /// <summary>The container time zone: the machine's, as an IANA name, or UTC.</summary>
    public string TimeZone { get; }

    /// <summary>True while printing commands instead of running them.</summary>
    public bool DryRun => Options.DryRun;

    /// <summary>The package the running program belongs to, or null when it is an installed copy.</summary>
    public Package? Package { get; set; }

    /// <summary>The package's version, for the state file and the update check.</summary>
    public string PackageVersion { get; set; } = "";

    /// <summary>Hands a step's progress to whoever is watching.</summary>
    public void Report(double? fraction, string message) =>
        _progress?.Invoke(new StepProgress(_stepId, fraction, message));

    /// <summary>Says what a step is doing, in the log and on the progress line.</summary>
    public void Say(string message)
    {
        Log.Line(message);
        Report(null, message);
    }

    /// <summary>The engine sets this before a step runs, so its progress lines are attributed.</summary>
    internal void EnterStep(string stepId) => _stepId = stepId;

    /// <summary>
    /// Runs a program and says so first. In a dry run the injected runner records the command and
    /// answers as a success instead of executing it, so a whole plan can be walked — and its
    /// commands printed — on a machine that has none of the programs.
    /// </summary>
    public async Task<CommandResult> Run(CommandSpec spec, string? outputPrefix = null)
    {
        Log.Command(spec, DryRun);
        var result = await Runner
            .RunAsync(spec, line => Report(null, outputPrefix is null ? line : $"{outputPrefix} {line}"), Cancellation)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            Log.Line($"    exited with {result.ExitCode}");
        }
        var output = result.Output;
        if (output.Length > 0)
        {
            Log.Output(output.Length > 4000 ? output[^4000..] : output);
        }
        return result;
    }

    /// <summary>Runs a program for a small answer, with its output kept rather than logged.</summary>
    public Task<CommandResult> Capture(string file, params string[] arguments) =>
        Capture(new CommandSpec(file, arguments));

    /// <summary>Runs a program for a small answer.</summary>
    public Task<CommandResult> Capture(CommandSpec spec) =>
        Runner.RunAsync(spec, null, Cancellation);

    /// <summary>
    /// The environment a child gets: the folders setup found tools in, in front of the machine's
    /// PATH, because a program installed by a package manager is not on this process's PATH.
    /// </summary>
    public IReadOnlyDictionary<string, string> ChildEnvironment
    {
        get
        {
            var path = Tools.ChildPath(Host.GetEnvironment("PATH"));
            // An empty PATH would be a child with no search path at all, which is worse than none.
            return path.Length == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["PATH"] = path };
        }
    }

    /// <summary>
    /// Starts a program and does not wait for it: Docker Desktop, and the game itself. A dry run
    /// only says it would.
    /// </summary>
    public void StartDetached(string executable, string arguments = "")
    {
        Log.Line($"{(DryRun ? "would start: " : "start: ")}{executable} {arguments}".TrimEnd());
        if (DryRun)
        {
            return;
        }
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            start.ArgumentList.Add(argument);
        }
        System.Diagnostics.Process.Start(start);
    }

    /// <summary>Creates the install's folders; every step may assume they exist.</summary>
    public void EnsureDirectories()
    {
        if (DryRun)
        {
            return;
        }
        foreach (var directory in Paths.AllDirectories())
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Downloads a file, showing how far it has got; skipped when it is already there.</summary>
    public async Task<bool> Download(string url, string destination, string what)
    {
        if (File.Exists(destination) && !Options.Repair)
        {
            Say($"{what} is already downloaded");
            return true;
        }
        Say($"downloading {what}");
        Log.Line($"    from {url}");
        if (DryRun)
        {
            return true;
        }
        var lastReported = 0L;
        try
        {
            await Downloader.DownloadAsync(
                url,
                destination,
                (written, total) =>
                {
                    // One line every few megabytes, not one per buffer.
                    if (written - lastReported < 4 * 1024 * 1024 && total is not null && written != total)
                    {
                        return;
                    }
                    lastReported = written;
                    var percent = total is null or 0 ? "" : $" ({written * 100 / total}%)";
                    Report(
                        total is null or 0 ? null : (double)written / total,
                        $"downloading {what}: {written / (1024.0 * 1024.0):F1} MB{percent}");
                },
                Cancellation).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Line($"download failed: {error.Message}");
            return false;
        }
    }

    /// <summary>Unpacks a zip archive, replacing what is there.</summary>
    public void ExtractZip(string archive, string destination)
    {
        Say($"unpacking {Path.GetFileName(archive)}");
        if (DryRun)
        {
            return;
        }
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            // A zip entry may name a path outside the destination; refuse rather than write it.
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>The SHA-256 of a file's bytes, the first 12 hex digits: how an image is tagged.</summary>
    public static string Hash12(params string[] files)
    {
        using var sha = SHA256.Create();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant()[..12];
    }

    /// <summary>
    /// Rewrites a text file's CRLF as LF, unless it holds a NUL byte (so it is binary). A shell
    /// script or a patch that a Windows checkout gave CRLF to does not run in a Linux container.
    /// </summary>
    public static bool NormalizeLineEndings(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var fixedText = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (fixedText == text)
        {
            return false;
        }
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(fixedText));
        return true;
    }

    /// <summary>A folder with at least one entry; how the setup tells "converted" from "empty".</summary>
    public static bool HasEntries(string folder) =>
        Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>The machine's time zone as an IANA name, which is what the containers take.</summary>
    private static string ResolveTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (!OperatingSystem.IsWindows())
        {
            return local.Id;
        }
        // Windows names its zones its own way; the containers want IANA ones. The mapping needs
        // ICU, which a Windows PC has; without it the containers stay on UTC, which is harmless.
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "Etc/UTC";
    }
}
