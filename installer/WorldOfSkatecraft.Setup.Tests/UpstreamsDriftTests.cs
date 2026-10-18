using WorldOfSkatecraft.Setup.Core;
using Xunit;

namespace WorldOfSkatecraft.Setup.Tests;

/// <summary>
/// The pins must agree everywhere: the installer's constants, the repository's Linux script, its
/// Windows script and the patch. A pin bumped in one place and not another is exactly how the
/// deploy-version mismatch happened, so the mismatch is a failing test rather than a broken install.
/// </summary>
public class UpstreamsDriftTests
{
    private static string RepositoryFile(string relative) =>
        Path.Combine(Rehearsal.RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    public static readonly TheoryData<string> SetupScripts = new() { "setup/setup.sh", "setup/setup.ps1" };

    public static readonly TheoryData<string> ServerBuildScripts = new()
    {
        "setup/build-server.sh",
        "setup/build-server.ps1",
    };

    [Theory]
    [MemberData(nameof(SetupScripts))]
    public void The_setup_scripts_pin_the_same_upstreams(string script)
    {
        var text = File.ReadAllText(RepositoryFile(script));
        Assert.Contains(Upstreams.ServerRev, text);
        Assert.Contains(Upstreams.ServerImage, text);
        Assert.Contains(Upstreams.DatabaseImage, text);
        Assert.Contains(Upstreams.EngineRev, text);
        Assert.Contains(Upstreams.EngineUrl, text);
        Assert.Contains(Upstreams.ServerUrl, text);
        Assert.Contains(Upstreams.ConverterUrl, text);
    }

    [Theory]
    [MemberData(nameof(ServerBuildScripts))]
    public void The_server_build_scripts_pin_the_vmangos_revision(string script)
    {
        var text = File.ReadAllText(RepositoryFile(script));
        Assert.Contains(Upstreams.VmangosRev, text);
        Assert.Contains(Upstreams.PatchedImageRepository, text);
        Assert.Contains(Upstreams.ClientBuild, text);
    }

    [Theory]
    [MemberData(nameof(SetupScripts))]
    [MemberData(nameof(ServerBuildScripts))]
    public void No_setup_script_still_pins_the_pre_relaunch_deploy_revision(string script)
    {
        // f0692574 is the vmangos-deploy checkout from before the 2026-10-04 relaunch: with it the
        // images refuse to start, because that checkout's compose file has no
        // VMANGOS_DEPLOY_VERSION — the mismatch a fresh install was dying of.
        Assert.DoesNotContain("f0692574", File.ReadAllText(RepositoryFile(script)));
    }

    [Fact]
    public void The_skatecraft_patch_is_lf_only()
    {
        // git apply runs in a Linux container; a CRLF patch fails there, the build then ships the
        // stock server, and the Skateboarding profession silently does nothing.
        var patch = File.ReadAllText(RepositoryFile("setup/vmangos/skatecraft.patch"));
        Assert.DoesNotContain('\r', patch);
    }

    [Fact]
    public void The_patch_adds_the_profession_where_the_server_loads_it()
    {
        var patch = File.ReadAllText(RepositoryFile("setup/vmangos/skatecraft.patch"));
        Assert.Contains("src/game/Skatecraft.cpp", patch, StringComparison.Ordinal);
        Assert.Contains("Skatecraft::LoadSkillLine()", patch, StringComparison.Ordinal);
        Assert.Contains("Skatecraft::LoadSpells()", patch, StringComparison.Ordinal);
        Assert.Contains("Skatecraft::OnLogin", patch, StringComparison.Ordinal);
        // The profession's own skill line, and the spells the client already knows.
        Assert.Contains("SKILL_SKATEBOARDING = 760", patch, StringComparison.Ordinal);
        Assert.Contains("SPELL_SUMMON_SKATEBOARD = 60001", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gitattributes_keeps_the_patch_and_the_dockerfile_lf()
    {
        var attributes = File.ReadAllText(RepositoryFile(".gitattributes"));
        Assert.Contains("*.patch", attributes, StringComparison.Ordinal);
        Assert.Contains("eol=lf", attributes, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_and_the_repository_agree_on_the_patched_image_tag()
    {
        // Three implementations of one tag: the installer's, the Linux script's and Windows'.
        foreach (var script in new[] { "setup/build-server.sh", "setup/build-server.ps1" })
        {
            var text = File.ReadAllText(RepositoryFile(script));
            Assert.Contains(Upstreams.PatchedImageRepository, text, StringComparison.Ordinal);
            Assert.Contains("5875-", text, StringComparison.Ordinal);
            Assert.Contains("12", text, StringComparison.Ordinal);
        }
    }
}
