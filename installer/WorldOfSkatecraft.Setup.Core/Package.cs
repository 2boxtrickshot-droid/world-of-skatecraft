namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// What the package the player extracted holds, and where. Setup.exe sits at the package's root
/// beside <c>src/</c> (the project's source) and <c>payload/</c> (the files setup hands out); after
/// installing, a copy of Setup.exe lives in <c>&lt;install&gt;/app/</c> as the launcher.
/// </summary>
public sealed class Package
{
    public Package(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>Where the running Setup.exe was started from.</summary>
    public string Root { get; }

    /// <summary>The project source tree, copied into the install and built there.</summary>
    public string Source => Path.Combine(Root, "src");

    /// <summary>The extra files setup hands out.</summary>
    public string Payload => Path.Combine(Root, "payload");

    /// <summary>The vmangos patch that carries the Skateboarding profession.</summary>
    public string SkatecraftPatch => Path.Combine(Payload, "vmangos", "skatecraft.patch");

    /// <summary>
    /// A prebuilt Windows client, when the package carries one: then no Rust toolchain and no C++
    /// linker are needed at all. Absent in a source package, which builds the client instead.
    /// </summary>
    public string PrebuiltClient => Path.Combine(Payload, "bin", "benilla.exe");

    /// <summary>The package's version, or the assembly's when there is no stamp file.</summary>
    public string Version { get; set; } = "0.0.0";

    /// <summary>Whether this is a package root (rather than an installed launcher's folder).</summary>
    public bool LooksLikePackage() => Directory.Exists(Source) || Directory.Exists(Payload);

    /// <summary>The package the running program belongs to, or null when it is an installed copy.</summary>
    public static Package? Discover()
    {
        var candidate = new Package(SetupPaths.ProgramFolder);
        if (File.Exists(Path.Combine(candidate.Payload, "version.txt")))
        {
            candidate.Version = File.ReadAllText(Path.Combine(candidate.Payload, "version.txt")).Trim();
        }
        if (File.Exists(Path.Combine(candidate.Root, "VERSION.txt")))
        {
            candidate.Version = File.ReadAllText(Path.Combine(candidate.Root, "VERSION.txt")).Trim();
        }
        return candidate.LooksLikePackage() ? candidate : null;
    }

    /// <summary>Copies the source tree into the install, unless it is already the same tree.</summary>
    public void CopySourceInto(SetupPaths paths)
    {
        CopyTree(Source, paths.Source);
    }

    /// <summary>
    /// Copies a folder over another, keeping files that are newer in the destination only when the
    /// source does not have them. Robocopy is not used: it is an external program with its own
    /// exit codes, and this is a few hundred small files.
    /// </summary>
    public static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }
        Directory.CreateDirectory(to);
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(folder);
            // Build output is regenerated, and the player's own copies are never in the package.
            if (name is "target" or ".git" or "node_modules")
            {
                continue;
            }
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "target" or ".git" or "node_modules"))
            {
                continue;
            }
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
