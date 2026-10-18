namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// Finds the programs setup drives. A program a package manager installed is not on this process's
/// PATH — a running process keeps the PATH it started with — so the usual absolute places are
/// checked too, and the folder a program was found in is remembered for the children setup starts.
/// </summary>
public sealed class ToolLocator
{
    private readonly IHostEnvironment _host;
    private readonly IProcessRunner _runner;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _extraPath = new();

    public ToolLocator(IHostEnvironment host, IProcessRunner runner)
    {
        _host = host;
        _runner = runner;
    }

    /// <summary>The folders setup found programs in, to put in front of a child's PATH.</summary>
    public IReadOnlyList<string> ExtraPath => _extraPath;

    /// <summary>Where a program is, or null. Answers are cached, so a probe runs once.</summary>
    public async Task<string?> LocateAsync(string program)
    {
        if (_cache.TryGetValue(program, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }
        var found = await FindAsync(program).ConfigureAwait(false);
        _cache[program] = found ?? "";
        if (found is not null)
        {
            Remember(found);
        }
        return found;
    }

    /// <summary>Whether setup can drive a program at all.</summary>
    public async Task<bool> HasAsync(string program) =>
        await LocateAsync(program).ConfigureAwait(false) is not null;

    /// <summary>Where a program is, or null; uncached, for a program setup may have just installed.</summary>
    public async Task<string?> FindAsync(string program)
    {
        foreach (var candidate in Candidates(program))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        // Last resort: the OS's own resolution, which covers anything already on PATH.
        foreach (var argument in new[] { "--version", "version" })
        {
            var probe = await _runner
                .RunAsync(new CommandSpec(program, new[] { argument }))
                .ConfigureAwait(false);
            if (probe.ExitCode == 0)
            {
                return program;
            }
        }
        return null;
    }

    /// <summary>The absolute places a program of this name lands, then the bare name.</summary>
    private IEnumerable<string> Candidates(string program)
    {
        var home = _host.GetFolder(KnownFolder.UserProfile);
        var local = _host.GetFolder(KnownFolder.LocalApplicationData);
        var programs = _host.GetFolder(KnownFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        switch (program.ToLowerInvariant())
        {
            case "cargo":
                yield return Path.Combine(home, ".cargo", "bin", "cargo.exe");
                break;
            case "rustup":
                yield return Path.Combine(home, ".cargo", "bin", "rustup.exe");
                break;
            case "git":
                yield return Path.Combine(programs, "Git", "cmd", "git.exe");
                break;
            case "python":
                yield return Path.Combine(local, "Programs", "Python", "Python312", "python.exe");
                yield return Path.Combine(local, "Programs", "Python", "Python311", "python.exe");
                yield return Path.Combine(programs, "Python312", "python.exe");
                break;
            case "winget":
                yield return Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
                break;
            case "docker":
                yield return Path.Combine(programs, "Docker", "Docker", "resources", "bin", "docker.exe");
                break;
            case "vswhere":
                yield return Path.Combine(programsX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
                break;
        }
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", program + ".exe");
        yield return program;
    }

    private void Remember(string found)
    {
        var folder = Path.GetDirectoryName(found);
        if (!string.IsNullOrEmpty(folder) && !_extraPath.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _extraPath.Add(folder);
        }
    }

    /// <summary>The PATH a child gets: what setup found, then what the machine has.</summary>
    public string ChildPath(string? currentPath) => string.Join(
        Path.PathSeparator,
        _extraPath.Concat(new[] { currentPath ?? "" }).Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Forgets a probe, after setup installs that program.</summary>
    public void Forget(string program) => _cache.Remove(program);

    /// <summary>Forgets every probe, after installing several programs.</summary>
    public void ForgetAll() => _cache.Clear();
}
