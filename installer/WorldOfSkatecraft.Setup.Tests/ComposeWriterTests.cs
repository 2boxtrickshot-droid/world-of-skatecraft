using WorldOfSkatecraft.Setup.Core;
using Xunit;

namespace WorldOfSkatecraft.Setup.Tests;

/// <summary>
/// The generated Compose file. These are the checks that the deploy-version mismatch, which stopped
/// every fresh install at the map extraction, cannot come back: the version and the image digests
/// come from one place, and the file that runs is generated from them.
/// </summary>
public class ComposeWriterTests
{
    private static readonly string Patched = Upstreams.PatchedImage("abcdef012345");

    [Fact]
    public void Every_service_carries_the_deploy_version_the_images_check()
    {
        var compose = ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC");
        // One per service that checks it: database, realmd and mangosd. The tool service reuses
        // mangosd's environment anchor, so those lines are shared.
        var lines = compose.Split('\n').Count(line =>
            line.TrimStart('-', ' ').TrimEnd() == $"VMANGOS_DEPLOY_VERSION={Upstreams.DeployVersion}");
        Assert.Equal(3, lines);
        Assert.True(ComposeWriter.IsVersionOne(compose));
    }

    [Fact]
    public void The_images_are_pinned_by_digest_and_never_by_a_floating_tag()
    {
        var compose = ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC");
        Assert.Contains(Upstreams.DatabaseImage, compose, StringComparison.Ordinal);
        Assert.Contains("@sha256:", Upstreams.DatabaseImage, StringComparison.Ordinal);
        Assert.Contains("@sha256:", Upstreams.ServerImage, StringComparison.Ordinal);
        Assert.DoesNotContain("vmangos-database:latest", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("image: ghcr.io/mserajnik/vmangos-server:5875\n", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_world_service_carries_the_anchor_the_tool_service_needs()
    {
        var compose = ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC");
        Assert.Contains("image: &mangosd-image " + Patched, compose, StringComparison.Ordinal);
        Assert.Contains("image: *mangosd-image", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_extractors_get_the_client_s_data_read_only()
    {
        var compose = ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC");
        Assert.Contains(
            "./storage/mangosd/client-data:/opt/vmangos/storage/client-data",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "./storage/mangosd/extracted-data:/opt/vmangos/storage/data:ro",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_has_no_carriage_returns()
    {
        var compose = ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC");
        Assert.DoesNotContain('\r', compose);
    }

    [Fact]
    public void The_ports_are_the_ones_the_client_speaks()
    {
        var compose = ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC");
        Assert.Contains("- 3724:3724", compose, StringComparison.Ordinal);
        Assert.Contains("- 8085:8085", compose, StringComparison.Ordinal);
        Assert.Contains("VMANGOS_REALMLIST_PORT=8085", compose, StringComparison.Ordinal);
        Assert.Contains("VMANGOS_REALMLIST_ADDRESS=127.0.0.1", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_is_read_back_out_of_the_generated_file()
    {
        var compose = ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC");
        Assert.Equal(Patched, ComposeWriter.ServerImageOf(compose));
    }

    [Fact]
    public void A_stock_compose_file_is_recognized_as_not_being_a_version_one_one()
    {
        // vmangos-deploy before its relaunch: no VMANGOS_DEPLOY_VERSION anywhere, a floating tag,
        // and `user:` instead of VMANGOS_UID.
        const string old = """
            services:
              mangosd:
                image: ghcr.io/mserajnik/vmangos-server:5875
                user: 1000:1000
            """;
        Assert.False(ComposeWriter.IsVersionOne(old));
        Assert.True(ComposeWriter.IsVersionOne(
            ComposeWriter.Render(Patched, Upstreams.DatabaseImage, "Etc/UTC")));
    }

    [Fact]
    public void The_patched_image_tag_is_the_patch_s_and_the_dockerfile_s_hashes()
    {
        Assert.Matches("^world-of-skatecraft/vmangos-server:5875-[0-9a-f]{12}$", Patched);
    }
}
