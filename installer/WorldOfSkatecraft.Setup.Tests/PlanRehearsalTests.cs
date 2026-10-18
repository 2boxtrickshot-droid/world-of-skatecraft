using WorldOfSkatecraft.Setup.Core;
using Xunit;

namespace WorldOfSkatecraft.Setup.Tests;

/// <summary>
/// The plan as a whole, walked twice on a machine with neither game. The first walk runs every step
/// that produces something; the second skips all of them, which is what makes a setup that was
/// interrupted — or a repair — cheap.
/// </summary>
public partial class PlanRehearsalTests : IDisposable
{
    private readonly Rehearsal _rehearsal = new(Path.Combine(
        Path.GetTempPath(), "wosc-plan-" + Guid.NewGuid().ToString("N")[..8]));

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
    public async Task The_whole_plan_walks_and_the_second_walk_skips_what_it_holds()
    {
        var context = Prepare(prebuilt: false);
        var plan = SetupPlan.Build(context);
        var first = await WalkAsync(context, plan);
        Assert.True(first.Ok, first.Summary());
        Assert.Equal(16, plan.Count);

        var second = await WalkAsync(context, plan);
        Assert.True(second.Ok, second.Summary());
        // Only the steps that check rather than build may run again.
        foreach (var id in second.Steps.Where(step => !step.Skipped).Select(step => step.Step.Id))
        {
            Assert.Contains(id, SetupEngine.RepairSteps);
        }
        Assert.True(
            second.Steps.Count(step => step.Skipped) >= 10,
            $"only {second.Steps.Count(step => step.Skipped)} of {second.Steps.Count} were skipped");
    }

    [Fact]
    public async Task The_steps_run_in_the_order_that_leaves_the_least_work_to_redo()
    {
        var context = Prepare(prebuilt: false);
        var plan = SetupPlan.Build(context);
        var report = await WalkAsync(context, plan);
        Assert.True(report.Ok, report.Summary());
        var order = plan.Select(step => step.Id).ToList();
        // The client is built before the map extraction, so a failed build does not come after hours
        // of extraction.
        Assert.True(order.IndexOf("client") < order.IndexOf("server-extract"));
        // The player's own files are checked before anything is built out of them.
        Assert.True(order.IndexOf("wow") < order.IndexOf("skate-data"));
        Assert.Equal("layout", order[0]);
        Assert.Equal("verify", order[^1]);
    }

    [Fact]
    public async Task A_package_that_carries_a_client_needs_no_compiler_and_no_rust()
    {
        var context = Prepare(prebuilt: true);
        Assert.True(SetupPlan.HasPrebuiltClient(context));
        var report = await WalkAsync(context, SetupPlan.Build(context));
        Assert.True(report.Ok, report.Summary());
        Assert.True(File.Exists(_rehearsal.Paths.ClientExe));
        Assert.False(_rehearsal.Runner.Ran("cargo", "build"));
    }

    [Fact]
    public async Task The_server_steps_are_left_out_when_the_server_is_skipped()
    {
        var context = Prepare(prebuilt: true, new SetupOptions { DryRun = true, SkipServer = true });
        var plan = SetupPlan.Build(context);
        Assert.DoesNotContain(plan, step => step.Id == "server-extract");
        Assert.DoesNotContain(plan, step => step.Id == "account");
        var report = await WalkAsync(context, plan);
        Assert.True(report.Ok, report.Summary());
    }

    /// <summary>A progress sink that runs on the calling thread, so a walk stays sequential.</summary>
    private sealed class InlineProgress : IProgress<SetupProgress>
    {
        private readonly Action<SetupProgress> _action;

        public InlineProgress(Action<SetupProgress> action) => _action = action;

        public void Report(SetupProgress value) => _action(value);
    }
}
