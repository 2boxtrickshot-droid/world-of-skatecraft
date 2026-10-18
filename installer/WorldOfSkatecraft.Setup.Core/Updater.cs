namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// Updating the project without losing anything the player owns. An update replaces the program
/// and the project's source; the state, the converted game data, the server's configuration and
/// its database volume stay exactly where they are.
/// </summary>
public static class Updater
{
    /// <summary>The folders an update must never touch, in the install root.</summary>
    public static readonly string[] Keep = { "state", "game", "server", "tool", "config", "client" };

    /// <summary>
    /// Copies a newer package's program and source over an existing install. Returns the files that
    /// changed, so the player sees what an update did.
    /// </summary>
    public static List<string> Apply(Package package, SetupPaths paths, SetupLog log)
    {
        var changed = new List<string>();
        if (!Directory.Exists(package.Source))
        {
            log.Line($"the package at {package.Root} has no src folder; nothing to update");
            return changed;
        }
        log.Line($"updating the project's source from {package.Source}");
        var before = Fingerprint(paths.Source);
        Package.CopyTree(package.Source, paths.Source);
        foreach (var (file, hash) in Fingerprint(paths.Source))
        {
            if (!before.TryGetValue(file, out var previous) || previous != hash)
            {
                changed.Add(file);
            }
        }
        // The patch the server build applies lives in the source tree; keep the package's copy.
        if (File.Exists(package.SkatecraftPatch))
        {
            var target = Path.Combine(paths.Source, "setup", "vmangos", "skatecraft.patch");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(package.SkatecraftPatch, target, overwrite: true);
            changed.Add(target);
        }
        var self = Environment.ProcessPath;
        if (self is not null
            && Path.GetFileName(self).Equals("Setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(paths.App);
            var installed = Path.Combine(paths.App, "Setup.exe");
            if (Path.GetFullPath(self) != Path.GetFullPath(installed))
            {
                File.Copy(self, installed, overwrite: true);
                changed.Add(installed);
            }
        }
        log.Line($"{changed.Count} file(s) changed");
        return changed;
    }

    /// <summary>Pulls the project's source, for an install that is a git checkout.</summary>
    public static async Task<StepOutcome> PullAsync(SetupContext ctx)
    {
        if (!Directory.Exists(Path.Combine(ctx.Paths.Source, ".git")))
        {
            return StepOutcome.Warning(
                "this install's source is not a git checkout, so it cannot pull",
                "Extract a newer package over this install instead; your games, settings and " +
                "characters are kept either way.");
        }
        var git = await ctx.Tools.LocateAsync("git").ConfigureAwait(false);
        if (git is null)
        {
            return StepOutcome.Failed("Git is not installed", "Install Git, then run the update again.");
        }
        var pull = await ctx.Run(new CommandSpec(
            git, new[] { "-C", ctx.Paths.Source, "pull", "--ff-only" }, null, ctx.ChildEnvironment,
            "pulls the project's latest source")).ConfigureAwait(false);
        return pull.Ok
            ? StepOutcome.Done("the project's source is up to date")
            : StepOutcome.Failed(
                "the update could not be pulled",
                $"Run it yourself: git -C \"{ctx.Paths.Source}\" pull");
    }

    /// <summary>A file's path and its hash, for saying what an update changed.</summary>
    private static Dictionary<string, string> Fingerprint(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return map;
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "target" or ".git"))
            {
                continue;
            }
            try
            {
                map[relative] = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            }
            catch (IOException)
            {
                // A file locked by a running build: not part of the answer.
            }
        }
        return map;
    }
}
