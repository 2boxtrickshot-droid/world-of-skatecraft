using WorldOfSkatecraft.Setup.Core;
using Xunit;

namespace WorldOfSkatecraft.Setup.Tests;

/// <summary>
/// What setup does when something is missing or wrong: it stops with a fix line rather than a stack
/// trace, and a repair never repeats the hours-long work.
/// </summary>
public partial class PlanRecoveryTests : IDisposable
{
    private readonly Rehearsal _rehearsal = new(Path.Combine(
        Path.GetTempPath(), "wosc-recover-" + Guid.NewGuid().ToString("N")[..8]));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rehearsal.Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder is not a test failure.
        }
        GC.SuppressFinalize(this);
    }

    private async Task<SetupReport> WalkAsync(SetupContext context, IReadOnlyList<SetupStep> plan)
    {
        var progress = new InlineProgress(update =>
        {
            if (update.Result is { Outcome.Ok: true, Skipped: false })
            {
                _rehearsal.SimulateArtifacts(update.Step.Id);
            }
        });
        return await new SetupEngine(context).RunAsync(plan, progress);
    }

    private SetupContext Prepare(bool prebuilt, SetupOptions? options = null)
    {
        _rehearsal.PrimeRunner();
        var package = _rehearsal.MakePackage(prebuilt);
        var wow = _rehearsal.MakeWowFolder();
        var skate = _rehearsal.MakeSkateFolder();
        _rehearsal.MakeServerCheckout();
        return _rehearsal.Context(options ?? new SetupOptions { DryRun = true }, wow, skate, package);
    }

    [Fact]
    public async Task A_missing_wow_folder_stops_the_plan_with_a_fix_and_not_a_stack_trace()
    {
        _rehearsal.PrimeRunner();
        var context = _rehearsal.Context(
            new SetupOptions { DryRun = true },
            wowFolder: Path.Combine(_rehearsal.Root, "not-a-client"),
            skateFolder: _rehearsal.MakeSkateFolder(),
            package: _rehearsal.MakePackage(withPrebuiltClient: true));
        var report = await WalkAsync(context, SetupPlan.Build(context));
        Assert.False(report.Ok);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("wow", failure.Step.Id);
        Assert.NotNull(failure.Outcome.Fix);
        Assert.Contains("does not exist", failure.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repair_reruns_the_cheap_checks_and_never_the_hours_long_extraction()
    {
        var context = Prepare(prebuilt: true);
        var plan = SetupPlan.Build(context);
        await WalkAsync(context, plan);
        Assert.DoesNotContain("server-extract", SetupEngine.RepairSteps);
        Assert.DoesNotContain("skate-data", SetupEngine.RepairSteps);

        var repairing = _rehearsal.Context(
            new SetupOptions { DryRun = true, Repair = true },
            context.State.WowFolder,
            context.State.SkateFolder,
            context.Package);
        var repaired = await WalkAsync(repairing, plan);
        var ranAgain = repaired.Steps.Where(step => !step.Skipped).Select(step => step.Step.Id).ToList();
        Assert.DoesNotContain("server-extract", ranAgain);
        Assert.DoesNotContain("skate-data", ranAgain);
        Assert.Contains("verify", ranAgain);
        Assert.Contains("prereqs", ranAgain);
    }

    [Fact]
    public async Task A_fresh_pc_gets_the_programs_installed_before_the_work_that_needs_them()
    {
        var rehearsal = new Rehearsal(Path.Combine(
            Path.GetTempPath(), "wosc-fresh-" + Guid.NewGuid().ToString("N")[..8]));
        try
        {
            rehearsal.PrimeRunner(freshMachine: true);
            var package = rehearsal.MakePackage(withPrebuiltClient: false);
            rehearsal.MakeServerCheckout();
            var context = rehearsal.Context(
                new SetupOptions { DryRun = true },
                rehearsal.MakeWowFolder(),
                rehearsal.MakeSkateFolder(),
                package);
            var report = await new SetupEngine(context).RunAsync(SetupPlan.Build(context));
            Assert.True(report.Ok, report.Summary());
            Assert.True(rehearsal.Runner.Ran("winget", "Docker.DockerDesktop"));
            Assert.True(rehearsal.Runner.Ran("winget", "Git.Git"));
            Assert.True(rehearsal.Runner.Ran("winget", "Python.Python.3.12"));
            Assert.True(rehearsal.Runner.Ran("winget", "VisualStudio.2022.BuildTools"));
            // Rust comes from Rustup's own installer: per-user, and no administrator needed.
            var rustup = rehearsal.Runner.Find("rustup-init.exe");
            Assert.NotNull(rustup);
            Assert.Contains("--no-modify-path", rustup!.Arguments);
            Assert.Contains("stable-x86_64-pc-windows-msvc", rustup.Arguments);
        }
        finally
        {
            try
            {
                Directory.Delete(rehearsal.Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary folder is not a test failure.
            }
        }
    }

    private sealed class InlineProgress : IProgress<SetupProgress>
    {
        private readonly Action<SetupProgress> _action;

        public InlineProgress(Action<SetupProgress> action) => _action = action;

        public void Report(SetupProgress value) => _action(value);
    }
}
