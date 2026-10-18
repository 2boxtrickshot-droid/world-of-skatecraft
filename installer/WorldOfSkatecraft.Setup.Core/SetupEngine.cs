namespace WorldOfSkatecraft.Setup.Core;

/// <summary>A step starting or finishing, for the wizard's list and its progress bar.</summary>
public sealed record SetupProgress(SetupStep Step, int Index, int Total, StepReport? Result);

/// <summary>
/// Runs a plan, one step at a time, and keeps going only past the steps that are optional or
/// already done. State is written after every step, so a setup that is stopped — by the player, by
/// a power cut, or by a step that fails — continues from where it stopped.
/// </summary>
public sealed class SetupEngine
{
    private readonly SetupContext _ctx;

    public SetupEngine(SetupContext context) => _ctx = context;

    /// <summary>
    /// The steps a repair reruns even though they look done: the cheap ones that check rather than
    /// build. The expensive ones (the Skate 3 conversion, the map extraction, the server build) are
    /// judged by their output being there, which is what a repair is for.
    /// </summary>
    public static readonly string[] RepairSteps =
    {
        "layout", "disk", "prereqs", "docker-ready", "server-fetch", "server-images", "shortcut",
        "verify",
    };

    /// <summary>Runs the plan; the report says what happened to every step.</summary>
    public async Task<SetupReport> RunAsync(
        IReadOnlyList<SetupStep> plan,
        IProgress<SetupProgress>? progress = null)
    {
        var reports = new List<StepReport>();
        var stopped = false;
        for (var index = 0; index < plan.Count; index++)
        {
            var step = plan[index];
            _ctx.Cancellation.ThrowIfCancellationRequested();
            _ctx.EnterStep(step.Id);
            _ctx.Log.Section($"{step.Id} — {step.Title}");
            _ctx.Log.Line(step.What);
            progress?.Report(new SetupProgress(step, index, plan.Count, null));
            _ctx.Report(0, $"{step.Title}");

            var started = DateTime.UtcNow;
            if (!ShouldRun(step))
            {
                _ctx.Log.Line("already done; skipping");
            }
            else
            {
                StepOutcome outcome;
                try
                {
                    outcome = await step.Run(_ctx).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _ctx.Log.Line("stopped by the player");
                    stopped = true;
                    reports.Add(new StepReport(step, StepOutcome.Failed("stopped"), DateTime.UtcNow - started, false));
                    break;
                }
                catch (Exception error)
                {
                    // A step that throws is a bug, but the player still deserves a usable message
                    // and a log rather than a stack trace in a dialog.
                    _ctx.Log.Line($"unexpected failure: {error}");
                    outcome = StepOutcome.Failed(
                        "the step failed unexpectedly: " + error.Message,
                        $"See {_ctx.Paths.LogFile} and {_ctx.Paths.DiagnosticsFile}, then report it.",
                        error.ToString());
                }

                var report = new StepReport(step, outcome, DateTime.UtcNow - started, false);
                reports.Add(report);
                progress?.Report(new SetupProgress(step, index, plan.Count, report));
                _ctx.Log.Line($"{step.Id}: {outcome.Message} ({report.Took.TotalSeconds:F0}s)");

                if (outcome.Ok)
                {
                    if (!_ctx.State.CompletedSteps.Contains(step.Id))
                    {
                        _ctx.State.CompletedSteps.Add(step.Id);
                    }
                    _ctx.State.Save(_ctx.Paths);
                    continue;
                }
                if (outcome.Fix is not null)
                {
                    _ctx.Log.Line($"next: {outcome.Fix}");
                }
                if (outcome.Detail is not null)
                {
                    _ctx.Log.Output(outcome.Detail);
                }
                if (step.Optional || _ctx.Options.KeepGoingAfterFailure)
                {
                    continue;
                }
                stopped = true;
                break;
            }

            var skippedReport = new StepReport(
                step,
                StepOutcome.Skipped("already done"),
                DateTime.UtcNow - started,
                true);
            reports.Add(skippedReport);
            progress?.Report(new SetupProgress(step, index, plan.Count, skippedReport));
        }
        return new SetupReport(reports, stopped);
    }

    private bool ShouldRun(SetupStep step)
    {
        if (_ctx.Options.Repair && RepairSteps.Contains(step.Id))
        {
            return true;
        }
        try
        {
            return !step.IsDone(_ctx).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            // A check that throws cannot say "done", so the step runs and reports properly.
            _ctx.Log.Line($"could not check whether {step.Id} is done ({error.Message}); running it");
            return true;
        }
    }
}
