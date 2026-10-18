using WorldOfSkatecraft.Setup.Core;
using Xunit;

namespace WorldOfSkatecraft.Setup.Tests;

/// <summary>The checks on the player's own game files, which are the first thing setup does.</summary>
public class GameFilesTests : IDisposable
{
    private readonly Rehearsal _rehearsal = new(Path.Combine(
        Path.GetTempPath(), "wosc-test-" + Guid.NewGuid().ToString("N")[..8]));

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

    [Fact]
    public void A_client_with_the_four_archives_is_accepted()
    {
        var check = GameFiles.CheckWow(_rehearsal.MakeWowFolder());
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Contains(check.Facts, fact => fact.StartsWith("Data archives:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_data_folder_itself_is_the_same_thing_as_the_client_folder()
    {
        var wow = _rehearsal.MakeWowFolder();
        Assert.Equal(
            Path.GetFullPath(wow),
            GameFiles.NormalizeWowFolder(Path.Combine(wow, "Data")));
    }

    [Fact]
    public void A_missing_archive_is_named()
    {
        var wow = _rehearsal.MakeWowFolder();
        File.Delete(Path.Combine(wow, "Data", "terrain.MPQ"));
        var check = GameFiles.CheckWow(wow);
        Assert.False(check.Ok);
        Assert.Contains("terrain.MPQ", string.Join(" ", check.Errors));
    }

    [Fact]
    public void A_file_that_is_not_an_mpq_is_refused()
    {
        var wow = _rehearsal.MakeWowFolder();
        File.WriteAllText(Path.Combine(wow, "Data", "dbc.MPQ"), "this is not an archive");
        var check = GameFiles.CheckWow(wow);
        Assert.False(check.Ok);
        Assert.Contains("not a readable MPQ", string.Join(" ", check.Errors));
    }

    [Fact]
    public void A_folder_without_data_says_what_to_pick_instead()
    {
        var check = GameFiles.CheckWow(_rehearsal.Root);
        Assert.False(check.Ok);
        Assert.Contains("No Data folder", string.Join(" ", check.Errors));
    }

    [Fact]
    public void An_expansion_archive_is_a_note_and_not_a_refusal()
    {
        var wow = _rehearsal.MakeWowFolder();
        File.WriteAllText(Path.Combine(wow, "Data", "expansion.MPQ"), "MPQ\x1a");
        var check = GameFiles.CheckWow(wow);
        Assert.True(check.Ok);
        Assert.Contains(check.Warnings, warning => warning.Contains("expansion.MPQ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_extracted_skate_3_folder_is_accepted()
    {
        var check = GameFiles.CheckSkate(_rehearsal.MakeSkateFolder());
        Assert.True(check.Ok, string.Join("; ", check.Errors));
    }

    [Fact]
    public void An_iso_is_refused_with_the_command_that_extracts_it()
    {
        var iso = Path.Combine(_rehearsal.Root, "Skate 3.iso");
        File.WriteAllText(iso, "not really an iso");
        var check = GameFiles.CheckSkate(iso);
        Assert.False(check.Ok);
        Assert.Contains("extract-xiso", string.Join(" ", check.Errors));
    }

    [Fact]
    public void A_skate_folder_missing_a_game_archive_names_it()
    {
        var skate = _rehearsal.MakeSkateFolder();
        File.Delete(Path.Combine(skate, "data", "big", "db.big"));
        var check = GameFiles.CheckSkate(skate);
        Assert.False(check.Ok);
        Assert.Contains("data\\big\\db.big", string.Join(" ", check.Errors));
    }

    [Fact]
    public void The_four_skate_archives_are_the_converter_s_own_list()
    {
        // The converter is not in this repository, so its REQUIRED list is mirrored here and in the
        // check; if the engine's list changes, this test is where the mismatch is seen.
        Assert.Equal(
            new[]
            {
                "data/big/miscload.big",
                "data/big/miscboot.big",
                "data/big/db.big",
                "data/content/createacharacter.big",
            },
            GameFiles.RequiredSkateFiles);
    }
}
