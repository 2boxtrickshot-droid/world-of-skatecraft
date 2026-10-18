namespace WorldOfSkatecraft.Setup.Core;

/// <summary>Where a step sits in the install, so the wizard can group them into phases.</summary>
public enum SetupPhase
{
    /// <summary>Folders, state and the checks before anything is installed.</summary>
    Prepare,

    /// <summary>The programs the rest needs: Docker Desktop, Rust, a C++ toolchain, Python, Git.</summary>
    Prerequisites,

    /// <summary>The player's own game files: validation and conversion.</summary>
    GameFiles,

    /// <summary>The local vmangos server: its checkout, its image, its maps, its account.</summary>
    Server,

    /// <summary>The game client itself.</summary>
    Client,

    /// <summary>The shortcut, the final checks and the state write.</summary>
    Finish,
}

/// <summary>What a step says when it is done, and how long it took.</summary>
public sealed record StepOutcome(bool Ok, string Message, string? Fix = null, string? Detail = null)
{
    public static StepOutcome Done(string message, string? detail = null) => new(true, message, null, detail);

    public static StepOutcome Finished(string message, string? detail = null) => new(true, message, null, detail);

    public static StepOutcome Skipped(string message) => new(true, message);

    public static StepOutcome Failed(string message, string? fix = null, string? detail = null) =>
        new(false, message, fix, detail);

    /// <summary>A failure that does not stop the setup, because the game works without it.</summary>
    public static StepOutcome Warning(string message, string? fix = null, string? detail = null) =>
        new(true, message, fix, detail);
}

/// <summary>
/// One step of the install. Every step is written to be safe to run twice: it asks
/// <paramref name="IsDone"/> first, and does nothing when its result is already on disk, which is
/// what makes a stopped setup continue and a repair redo only what is missing.
/// </summary>
public sealed record SetupStep(
    string Id,
    SetupPhase Phase,
    string Title,
    string What,
    Func<SetupContext, Task<bool>> IsDone,
    Func<SetupContext, Task<StepOutcome>> Run,
    bool Optional = false,
    bool NeedsDocker = false,
    string? Note = null,
    TimeSpan Expected = default)
{
    /// <summary>How long a player should expect this step to take, for the wizard's estimate.</summary>
    public TimeSpan Estimate => Expected == default ? TimeSpan.FromSeconds(15) : Expected;
}

/// <summary>How a step's run ended, and how the wizard reports it.</summary>
public sealed record StepReport(SetupStep Step, StepOutcome Outcome, TimeSpan Took, bool Skipped);

/// <summary>Everything the plan produced: what ran, what it said, and whether the install is playable.</summary>
public sealed record SetupReport(IReadOnlyList<StepReport> Steps, bool Stopped)
{
    /// <summary>True when every step either finished or was skipped.</summary>
    public bool Ok => !Stopped && Steps.All(s => s.Outcome.Ok);

    /// <summary>The steps that failed, in order.</summary>
    public IReadOnlyList<StepReport> Failures => Steps.Where(s => !s.Outcome.Ok).ToList();

    /// <summary>The steps that finished with something to say but did not fail.</summary>
    public IReadOnlyList<StepReport> Warnings => Steps
        .Where(s => s.Outcome.Ok && s.Outcome.Fix is not null)
        .ToList();

    /// <summary>The one-line summary the wizard's last page and the log's tail both use.</summary>
    public string Summary()
    {
        if (Failures.Count > 0)
        {
            var first = Failures[0];
            var fix = first.Outcome.Fix is null ? "" : $" Fix: {first.Outcome.Fix}";
            return $"Stopped at \"{first.Step.Title}\": {first.Outcome.Message}{fix}";
        }
        var skipped = Steps.Count(s => s.Skipped);
        var warnings = Warnings.Count;
        var tail = warnings == 0 ? "" : $", {warnings} with a note";
        return $"Done: {Steps.Count - skipped} step(s) run, {skipped} already done{tail}.";
    }
}
