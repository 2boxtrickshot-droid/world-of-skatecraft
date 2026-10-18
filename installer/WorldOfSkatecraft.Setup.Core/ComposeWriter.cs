using System.Text;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// Writes <c>server/compose.yaml</c> from the template that ships with this engine.
/// </summary>
/// <remarks>
/// The file is generated rather than copied out of the vmangos-deploy checkout. That is the fix for
/// the deploy-version mismatch: the checkout is version 1, the images are pinned by digest, and
/// `VMANGOS_DEPLOY_VERSION` is written into every service from one constant — so an upstream
/// relaunch cannot leave an install whose compose file and images disagree. The generated file also
/// points at the patched image the Skateboarding profession needs, so nothing rewrites YAML text
/// after the fact.
/// </remarks>
public static class ComposeWriter
{
    /// <summary>The Compose file for this install, as text, with LF line endings.</summary>
    public static string Render(
        string serverImage,
        string databaseImage,
        string timeZone,
        string uid = "1000",
        string gid = "1000")
    {
        var text = Template()
            .Replace("@@SERVER_IMAGE@@", serverImage, StringComparison.Ordinal)
            .Replace("@@DATABASE_IMAGE@@", databaseImage, StringComparison.Ordinal)
            .Replace("@@DEPLOY_VERSION@@", Upstreams.DeployVersion, StringComparison.Ordinal)
            .Replace("@@DATABASE_USER@@", Upstreams.DatabaseUser, StringComparison.Ordinal)
            .Replace("@@DATABASE_PASSWORD@@", Upstreams.DatabasePassword, StringComparison.Ordinal)
            .Replace("@@DATABASE_ROOT_PASSWORD@@", Upstreams.DatabaseRootPassword, StringComparison.Ordinal)
            .Replace("@@TZ@@", timeZone, StringComparison.Ordinal)
            .Replace("@@UID@@", uid, StringComparison.Ordinal)
            .Replace("@@GID@@", gid, StringComparison.Ordinal);
        // Docker Compose reads this file on a Windows host too, and CRLF in it is needless risk.
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Writes the file, and says which image the services now run.</summary>
    public static void Write(SetupPaths paths, string serverImage, string databaseImage, string timeZone)
    {
        Directory.CreateDirectory(paths.Server);
        File.WriteAllText(paths.ComposeFile, Render(serverImage, databaseImage, timeZone), new UTF8Encoding(false));
    }

    /// <summary>The image the generated file starts, read back out of it — the repair's check.</summary>
    public static string? ServerImageOf(string composeText)
    {
        foreach (var line in composeText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("image:", StringComparison.Ordinal))
            {
                continue;
            }
            var value = trimmed["image:".Length..].Trim();
            // The mangosd service carries an anchor: `image: &mangosd-image <ref>`.
            if (value.StartsWith("&", StringComparison.Ordinal))
            {
                var after = value.IndexOf(' ');
                value = after < 0 ? "" : value[(after + 1)..].Trim();
            }
            if (value.Contains("vmangos-server", StringComparison.Ordinal))
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>Whether a Compose file is one of ours and carries the deploy version.</summary>
    public static bool IsVersionOne(string composeText) =>
        composeText.Contains($"VMANGOS_DEPLOY_VERSION={Upstreams.DeployVersion}", StringComparison.Ordinal);

    private static string Template()
    {
        using var stream = typeof(ComposeWriter).Assembly
            .GetManifestResourceStream("compose.yaml")
            ?? throw new InvalidOperationException("the composed template is missing from the assembly");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
