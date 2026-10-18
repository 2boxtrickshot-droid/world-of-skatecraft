using System.Globalization;
using System.Numerics;
using WorldOfSkatecraft.Setup.Core;
using Xunit;

namespace WorldOfSkatecraft.Setup.Tests;

/// <summary>
/// The account: the SRP6 verifier the server compares against, and the SQL that writes it. The
/// vector is `tools/make_account.py`'s own, so an account made here logs in the same as one made by
/// the repository's Python tool.
/// </summary>
public class AccountTests
{
    /// <summary>The salt the vector was computed with, as the Python tool would store it.</summary>
    private static readonly BigInteger Salt = Account.FromHex(
        "B79B3A2D1F5C0C3E5A4B3C2D1E0F9A8B7C6D5E4F3A2B1C0D9E8F7A6B5C4D3E2F");

    [Fact]
    public void The_verifier_matches_the_python_tool_s_own_vector()
    {
        // python3 -c "from tools.make_account import verifier; ..." prints this for SKATEPROBE /
        // Skate123 and the salt above.
        var expected = Account.FromHex("707e0075688826a5320e8e31e1db079f34d0ade46b94bbae89d6bf87b125aba3");
        Assert.Equal(expected, Account.Verifier("SKATEPROBE", "Skate123", Salt));
    }

    [Fact]
    public void The_names_are_upper_cased_the_way_the_server_does()
    {
        Assert.Equal(
            Account.Verifier("SKATEPROBE", "Skate123", Salt),
            Account.Verifier("skateprobe", "skate123", Salt));
    }

    [Fact]
    public void A_different_password_is_a_different_verifier()
    {
        Assert.NotEqual(
            Account.Verifier("SKATEPROBE", "Skate123", Salt),
            Account.Verifier("SKATEPROBE", "Skate124", Salt));
    }

    [Fact]
    public void The_stored_salt_is_sixty_four_upper_case_hex_digits()
    {
        var (saltHex, verifierHex) = Account.Credentials("SKATEPROBE", "Skate123", Salt);
        Assert.Equal(64, saltHex.Length);
        Assert.Equal(saltHex.ToUpperInvariant(), saltHex);
        Assert.Equal(verifierHex.ToUpperInvariant(), verifierHex);
    }

    [Fact]
    public void A_fresh_salt_is_a_different_one_every_time()
    {
        Assert.NotEqual(Account.NewSalt(), Account.NewSalt());
        Assert.True(Account.NewSalt() > 0);
    }

    [Theory]
    [InlineData("skater", true)]
    [InlineData("Skater99", true)]
    [InlineData("", false)]
    [InlineData("with space", false)]
    [InlineData("seventeen-chars!!", false)]
    public void An_account_name_is_letters_and_digits(string name, bool valid) =>
        Assert.Equal(valid, Account.ValidName(name));

    [Theory]
    [InlineData("Skate123", true)]
    [InlineData("", false)]
    [InlineData("seventeen-chars!!", false)]
    public void A_password_is_up_to_sixteen_characters(string password, bool valid) =>
        Assert.Equal(valid, Account.ValidPassword(password));

    [Fact]
    public void The_sql_makes_the_account_a_gm_on_every_realm()
    {
        var sql = Account.CreateAccountSql("skateprobe", "Skate123", gmLevel: 3);
        Assert.Contains("INSERT INTO account", sql, StringComparison.Ordinal);
        Assert.Contains("'SKATEPROBE'", sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE account SET gmlevel=3", sql, StringComparison.Ordinal);
        // vmangos reads the GM level from account_access with realm -1, which covers every realm.
        Assert.Contains("INSERT INTO account_access (id, gmlevel, RealmID) SELECT id, 3, -1", sql,
            StringComparison.Ordinal);
        Assert.Contains("REPLACE INTO realmcharacters", sql, StringComparison.Ordinal);
        // The password itself is never in the SQL: only its salt and verifier are.
        Assert.DoesNotContain("'Skate123'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_name_or_password_is_refused_rather_than_written()
    {
        Assert.Throws<ArgumentException>(() => Account.CreateAccountSql("bad name", "Skate123"));
        Assert.Throws<ArgumentException>(() => Account.CreateAccountSql("skater", ""));
    }

    [Fact]
    public void The_database_command_runs_the_sql_in_the_project_s_container()
    {
        var paths = new SetupPaths(Path.Combine(Path.GetTempPath(), "wosc-account"));
        var docker = new Docker(paths, new RecordingProcessRunner());
        var command = Account.DatabaseCommand(docker, "SELECT 1");
        Assert.Equal("docker", command.File);
        Assert.Contains("compose", command.Arguments);
        Assert.Contains("world-of-skatecraft", command.Arguments);
        Assert.Contains("database", command.Arguments);
        Assert.Contains("-e", command.Arguments);
        Assert.Contains("SELECT 1", command.Arguments);
    }
}
