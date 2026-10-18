using System.Diagnostics;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>One program to run; no shell, so a path with spaces or an ampersand is never reinterpreted.</summary>
public sealed record CommandSpec(
    string File,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Why = null)
{
    /// <summary>The command as a player would type it, for the log and for a dry run.</summary>
    public string Display()
    {
        var args = Arguments.Select(Quote);
        var dir = WorkingDirectory is null ? "" : $" (in {WorkingDirectory})";
        var env = Environment is null || Environment.Count == 0
            ? ""
            : " " + string.Join(" ", Environment.Select(e => $"{e.Key}={Quote(e.Value)}"));
        return $"{Quote(File)} {string.Join(" ", args)}{dir}{env}".TrimEnd();
    }

    private static string Quote(string value) =>
        value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"') ? value : $"\"{value}\"";
}

/// <summary>What a program printed and returned.</summary>
public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Everything it printed, for the log.</summary>
    public string Output => (StdOut + (StdErr.Length > 0 ? Environment.NewLine + StdErr : "")).Trim();

    public static CommandResult Success(string output = "") => new(0, output, "");
}

/// <summary>Runs programs. Faked by the tests and by a dry run, so no plan needs a real machine.</summary>
public interface IProcessRunner
{
    /// <summary>Runs to completion, handing every output line to <paramref name="onLine"/> as it arrives.</summary>
    Task<CommandResult> RunAsync(
        CommandSpec spec,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Runs programs for real, with no window of its own and the environment setup needs.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>The longest a single command may take before it is killed (map extraction runs for hours).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(12);

    public async Task<CommandResult> RunAsync(
        CommandSpec spec,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(spec.File)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (spec.WorkingDirectory is not null)
        {
            start.WorkingDirectory = spec.WorkingDirectory;
        }
        foreach (var argument in spec.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                start.Environment[key] = value;
            }
        }
        // Keep tools quiet and non-interactive: this runs behind a progress bar, not a terminal.
        start.Environment["CLICOLOR"] = "0";
        start.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = start };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(127, "", $"{spec.File}: {error.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new CommandResult(-1, stdout.ToString(), "cancelled or timed out");
        }

        // Drain the redirected streams: WaitForExitAsync returns before the last lines are raised.
        process.WaitForExit();
        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Nothing left to kill.
        }
    }
}

/// <summary>
/// A runner that records what it was asked to run instead of running it: what a dry run uses, and
/// what the tests use to assert a plan's commands without a Windows PC or Docker.
/// </summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly List<CommandSpec> _commands = new();
    private readonly List<Func<CommandSpec, CommandResult?>> _answers = new();
    private readonly List<(Func<CommandSpec, bool> Match, Action<CommandSpec> Effect)> _effects = new();

    /// <summary>Every command handed to it, in order.</summary>
    public IReadOnlyList<CommandSpec> Commands => _commands;

    /// <summary>Answers a command needs: the first rule that answers wins, and none answers success.</summary>
    public void Answer(Func<CommandSpec, bool> match, CommandResult result) =>
        Answer(spec => match(spec) ? result : null);

    /// <summary>Answers a command needs: the first rule that answers wins, and none answers success.</summary>
    public void Answer(Func<CommandSpec, CommandResult?> answer) => _answers.Add(answer);

    /// <summary>
    /// What a command *does*, for a rehearsal: the file a converter writes, the binary a build
    /// produces. Registering an effect is how a plan can be walked on a machine that cannot run the
    /// commands at all.
    /// </summary>
    public void OnCommand(Func<CommandSpec, bool> match, Action<CommandSpec> effect) =>
        _effects.Add((match, effect));

    /// <summary>Answers every command whose file and arguments contain all the given words.</summary>
    public void AnswerWith(Func<CommandSpec, bool> match, string stdout) =>
        Answer(match, CommandResult.Success(stdout));

    public Task<CommandResult> RunAsync(
        CommandSpec spec,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        _commands.Add(spec);
        var result = _answers
            .Select(answer => answer(spec))
            .FirstOrDefault(answer => answer is not null) ?? CommandResult.Success();
        foreach (var (match, effect) in _effects.Where(e => e.Match(spec)))
        {
            effect(spec);
        }
        foreach (var line in result.Output.Split('\n'))
        {
            onLine?.Invoke(line);
        }
        return Task.FromResult(result);
    }

    /// <summary>True when a recorded command contains every given word in its file or arguments.</summary>
    public bool Ran(params string[] words) => _commands.Any(c => Matches(c, words));

    /// <summary>The recorded command containing every given word, or null.</summary>
    public CommandSpec? Find(params string[] words) => _commands.FirstOrDefault(c => Matches(c, words));

    /// <summary>Every recorded command containing all the given words, in order.</summary>
    public IEnumerable<CommandSpec> FindAll(params string[] words) => _commands.Where(c => Matches(c, words));

    private static bool Matches(CommandSpec spec, string[] words) =>
        words.All(w => spec.File.Contains(w, StringComparison.OrdinalIgnoreCase)
                       || spec.Arguments.Any(a => a.Contains(w, StringComparison.OrdinalIgnoreCase)));
}
