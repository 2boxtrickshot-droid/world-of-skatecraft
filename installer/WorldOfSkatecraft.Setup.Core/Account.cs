using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// The game account: the SRP6 salt and verifier vmangos stores in <c>realmd.account</c>, computed
/// exactly as vmangos's own <c>SRP6::CalculateVerifier</c> and <c>tools/make_account.py</c> do, and
/// written with the same SQL, so an account made here logs in the same as one made by the server's
/// own console.
/// </summary>
public static class Account
{
    /// <summary>The SRP6 group's modulus, from vmangos's SRP6 header.</summary>
    private static readonly BigInteger N = BigInteger.Parse(
        "894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7",
        System.Globalization.NumberStyles.HexNumber);

    /// <summary>The SRP6 group's generator.</summary>
    private static readonly BigInteger G = new(7);

    /// <summary>The longest a vmangos account name and password may be.</summary>
    public const int MaxLength = 16;

    /// <summary>Whether a name is one vmangos will accept: letters and digits, up to 16.</summary>
    public static bool ValidName(string name) =>
        name.Length is > 0 and <= MaxLength
        && name.All(char.IsAsciiLetterOrDigit);

    /// <summary>Whether a password is one vmangos will accept: up to 16 characters, no control ones.</summary>
    public static bool ValidPassword(string password) =>
        password.Length is > 0 and <= MaxLength
        && password.All(c => !char.IsControl(c));

    /// <summary>
    /// The verifier for a salt: <c>x = SHA1(salt_le || SHA1(USER:PASS))</c> read little-endian,
    /// <c>v = g^x mod N</c>. The names are upper-cased, as vmangos does.
    /// </summary>
    public static BigInteger Verifier(string user, string password, BigInteger salt)
    {
        var inner = SHA1.HashData(Encoding.ASCII.GetBytes($"{user.ToUpperInvariant()}:{password.ToUpperInvariant()}"));
        var saltLittleEndian = salt.ToByteArray(isUnsigned: true, isBigEndian: false);
        var outerInput = new byte[saltLittleEndian.Length + inner.Length];
        saltLittleEndian.CopyTo(outerInput, 0);
        inner.CopyTo(outerInput, saltLittleEndian.Length);
        var outer = SHA1.HashData(outerInput);
        var x = new BigInteger(outer, isUnsigned: true, isBigEndian: false);
        return BigInteger.ModPow(G, x, N);
    }

    /// <summary>A fresh 256-bit salt with its top bit set, as vmangos's own account creation does.</summary>
    public static BigInteger NewSalt()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        bytes[31] |= 0x80;
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>The salt and verifier as vmangos stores them: big-endian hex, upper case.</summary>
    public static (string SaltHex, string VerifierHex) Credentials(string user, string password, BigInteger salt)
    {
        var verifier = Verifier(user, password, salt);
        return (salt.ToString("X64"), verifier.ToString("X"));
    }

    /// <summary>
    /// The SQL that creates the account and makes it a GM, the statements
    /// <c>tools/make_account.py</c> runs: the account row, its GM level (vmangos reads the level
    /// from <c>account_access</c> with realm -1, which covers every realm), and the realm character
    /// counts the realm list uses.
    /// </summary>
    public static string CreateAccountSql(string user, string password, int gmLevel = 3)
    {
        if (!ValidName(user))
        {
            throw new ArgumentException(
                "an account name is letters and digits, up to 16 characters", nameof(user));
        }
        if (!ValidPassword(password))
        {
            throw new ArgumentException(
                "a password is up to 16 characters with no control characters", nameof(password));
        }
        var (salt, verifier) = Credentials(user, password, NewSalt());
        // A single quote in a password would end the literal; vmangos's own console escapes it the
        // same way, and the length limit keeps this from being a problem in practice.
        var literal = password.Replace("'", "''", StringComparison.Ordinal);
        return string.Join(
            " ",
            $"INSERT INTO account (username, s, v, joindate) VALUES ('{user.ToUpperInvariant()}', '{salt}', '{verifier}', NOW())",
            "ON DUPLICATE KEY UPDATE s=VALUES(s), v=VALUES(v);",
            $"UPDATE account SET gmlevel={gmLevel} WHERE username='{user.ToUpperInvariant()}';",
            "DELETE account_access FROM account_access JOIN account USING (id) " +
            $"WHERE username='{user.ToUpperInvariant()}';",
            $"INSERT INTO account_access (id, gmlevel, RealmID) SELECT id, {gmLevel}, -1 FROM account " +
            $"WHERE username='{user.ToUpperInvariant()}';",
            "REPLACE INTO realmcharacters (realmid, acctid, numchars) SELECT realmlist.id, account.id, 0 " +
            "FROM realmlist, account LEFT JOIN realmcharacters ON acctid=account.id WHERE acctid IS NULL;",
            $"-- password set for {user.ToUpperInvariant()} ({literal.Length} characters)");
    }

    /// <summary>The `docker compose exec` that runs that SQL in the database container.</summary>
    public static CommandSpec DatabaseCommand(Docker docker, string sql) =>
        docker.Compose(
            "exec", "-T", "database",
            "mariadb", "-uroot", $"-p{Upstreams.DatabaseRootPassword}", "-N", "-B", "realmd", "-e", sql);
}
