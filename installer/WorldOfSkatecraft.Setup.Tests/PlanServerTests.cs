using WorldOfSkatecraft.Setup.Core;
using Xunit;

namespace WorldOfSkatecraft.Setup.Tests;

/// <summary>
/// The checks on the server half of the plan: the map extraction's mounts, the patched image build
/// and the account. These are the two things the install was reported broken for — a deploy-version
/// mismatch that stopped the extraction, and a stock server where the Skateboarding profession
/// should have been — so each is checked against the command that is really run.
/// </summary>
public partial class PlanRehearsalTests
{
    [Fact]
    public async Task The_map_extraction_mounts_the_client_read_only_and_sets_the_deploy_version()
    {
        var context = Prepare(prebuilt: false);
        await WalkAsync(context, SetupPlan.Build(context));
        var run = _rehearsal.Runner.FindAll("run", "--entrypoint", "extract-client-data").ToList();
        var spec = Assert.Single(run);
        var mounts = spec.Arguments.Where(a => a.StartsWith("type=bind", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, mounts.Count);
        // The scratch folder holds the client's Data read-only: the extractors write beside it, and
        // the WoW folder itself must never change.
        Assert.Contains(mounts, mount =>
            mount.Contains("/opt/vmangos/storage/client-data/Data", StringComparison.Ordinal)
            && mount.EndsWith(",readonly", StringComparison.Ordinal));
        Assert.DoesNotContain(mounts, mount =>
            mount.Contains(_rehearsal.Paths.Root + "/WoW,", StringComparison.Ordinal));
        Assert.Contains("VMANGOS_DEPLOY_VERSION=1", spec.Arguments);
        Assert.Contains("VMANGOS_UID=1000", spec.Arguments);
        // The extractor asks about overwriting; nobody is there to answer it.
        Assert.Contains("--force", spec.Arguments);
    }

    [Fact]
    public async Task The_patched_server_is_built_with_the_patch_and_the_build_fails_on_a_bad_one()
    {
        var context = Prepare(prebuilt: false);
        await WalkAsync(context, SetupPlan.Build(context));
        var build = _rehearsal.Runner.Find("build", "-t");
        Assert.NotNull(build);
        Assert.Contains($"VMANGOS_REVISION={Upstreams.VmangosRev}", build!.Arguments);
        Assert.Contains("VMANGOS_CLIENT_VERSION=5875", build.Arguments);
        // Without this, a patch that does not apply leaves the stock server in place and the
        // profession silently off, which is the failure the install was reported with.
        Assert.Contains("VMANGOS_FAIL_ON_PATCH_ERROR=1", build.Arguments);
        // The deploy project's Dockerfile uses BuildKit-only instructions.
        Assert.Equal("1", build.Environment!["DOCKER_BUILDKIT"]);
        // The patch is added to a copy of the build context, never to the checkout.
        Assert.DoesNotContain(_rehearsal.Paths.Server, build.Arguments.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_compose_file_ends_up_pointing_at_the_patched_image()
    {
        var context = Prepare(prebuilt: false);
        await WalkAsync(context, SetupPlan.Build(context));
        var compose = File.ReadAllText(_rehearsal.Paths.ComposeFile);
        Assert.True(ComposeWriter.IsVersionOne(compose));
        var image = ComposeWriter.ServerImageOf(compose);
        Assert.NotNull(image);
        Assert.Contains("world-of-skatecraft/vmangos-server:5875-", image);
    }

    [Fact]
    public async Task The_account_is_created_and_read_back_through_the_server_s_own_database()
    {
        var context = Prepare(prebuilt: false);
        await WalkAsync(context, SetupPlan.Build(context));
        var statements = _rehearsal.Runner
            .FindAll("compose", "exec", "database")
            .SelectMany(spec => spec.Arguments)
            .ToList();
        Assert.Contains(statements, sql => sql.Contains("INSERT INTO account", StringComparison.Ordinal));
        Assert.Contains(statements, sql => sql.Contains("SELECT username, gmlevel", StringComparison.Ordinal));
        // The password never reaches a command line: only its salt and verifier do.
        Assert.DoesNotContain(statements, sql => sql.Contains("Skate123", StringComparison.Ordinal));
    }
}
