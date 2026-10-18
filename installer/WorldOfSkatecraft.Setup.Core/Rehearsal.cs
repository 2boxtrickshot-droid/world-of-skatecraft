namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// A package laid out on disk the way the real one is, plus the fake machine a rehearsal walks the
/// plan on: a recording process runner stands in for every program, so no Docker, no compiler and
/// no Windows PC are needed to check the plan's order, its commands and its idempotence.
/// </summary>
public sealed class Rehearsal
{
    private readonly List<string> _log = new();

    public Rehearsal(string root)
    {
        Root = Path.GetFullPath(root);
        Host = new TestHostEnvironment(isWindows: true, Path.Combine(Root, "user"));
        Paths = new SetupPaths(Path.Combine(Root, "install"));
        Runner = new RecordingProcessRunner();
        Downloader = new RecordingDownloader();
        Shortcuts = new NullShortcuts();
        Log = new SetupLog(null);
    }

    public string Root { get; }

    public TestHostEnvironment Host { get; }

    public SetupPaths Paths { get; }

    public RecordingProcessRunner Runner { get; }

    public RecordingDownloader Downloader { get; }

    public NullShortcuts Shortcuts { get; }

    public SetupLog Log { get; }

    /// <summary>The steps a rehearsal pretended to produce output for.</summary>
    public IReadOnlyList<string> Simulated => _log;

    /// <summary>A package folder with the source tree, the patch and (optionally) a prebuilt client.</summary>
    public Package MakePackage(bool withPrebuiltClient)
    {
        var package = new Package(Path.Combine(Root, "package"));
        Directory.CreateDirectory(Path.Combine(package.Source, "crates"));
        File.WriteAllText(Path.Combine(package.Source, "Cargo.toml"), "[workspace]");
        File.WriteAllText(Path.Combine(package.Source, "Cargo.lock"), "");
        Directory.CreateDirectory(Path.Combine(package.Source, "setup", "vmangos"));
        File.Copy(SkatecraftPatch(), Path.Combine(package.Source, "setup", "vmangos", "skatecraft.patch"));
        Directory.CreateDirectory(Path.Combine(package.Source, "tools"));
        File.WriteAllText(Path.Combine(package.Source, "tools", "skate_audio.py"), "# stub");
        Directory.CreateDirectory(Path.Combine(package.Payload, "vmangos"));
        File.Copy(SkatecraftPatch(), package.SkatecraftPatch, overwrite: true);
        File.WriteAllText(Path.Combine(package.Payload, "version.txt"), package.Version);
        if (withPrebuiltClient)
        {
            Directory.CreateDirectory(Path.Combine(package.Payload, "bin"));
            File.WriteAllText(package.PrebuiltClient, "not really a client");
        }
        return package;
    }

    /// <summary>The real skatecraft patch, out of the repository this installer lives in.</summary>
    public static string SkatecraftPatch()
    {
        var path = Path.Combine(RepositoryRoot(), "setup", "vmangos", "skatecraft.patch");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("setup/vmangos/skatecraft.patch was not found", path);
        }
        return path;
    }

    /// <summary>The repository's root: the nearest folder up holding the project's workspace.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "crates", "benilla-formats")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "the repository root was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A WoW folder holding the four archives, each with a real MPQ tag.</summary>
    public string MakeWowFolder()
    {
        var wow = Path.Combine(Root, "WoW");
        var data = Path.Combine(wow, "Data");
        Directory.CreateDirectory(data);
        foreach (var archive in GameFiles.RequiredWowArchives)
        {
            File.WriteAllBytes(
                Path.Combine(data, archive),
                new byte[] { 0x4D, 0x50, 0x51, 0x1A, 0, 0, 0, 0 });
        }
        File.WriteAllText(Path.Combine(wow, "WoW.exe"), "");
        return wow;
    }

    /// <summary>An extracted Skate 3 folder with the files the converter reads.</summary>
    public string MakeSkateFolder()
    {
        var skate = Path.Combine(Root, "Skate 3");
        Directory.CreateDirectory(skate);
        File.WriteAllBytes(Path.Combine(skate, "default.xex"), new byte[] { 0x58, 0x45, 0x58, 0x32 });
        foreach (var needed in GameFiles.RequiredSkateFiles)
        {
            var path = Path.Combine(skate, needed.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "game data");
        }
        Directory.CreateDirectory(Path.Combine(skate, "data", "audio"));
        return skate;
    }

    /// <summary>A server checkout with the files the patched image build reads.</summary>
    public void MakeServerCheckout()
    {
        var docker = Path.Combine(Paths.Server, "docker");
        Directory.CreateDirectory(Path.Combine(docker, "server"));
        Directory.CreateDirectory(Path.Combine(docker, "patches"));
        File.WriteAllText(
            Path.Combine(docker, "server", "Dockerfile"),
            "FROM ubuntu\nRUN make -j\"$(nproc)\"\n");
        Directory.CreateDirectory(Path.Combine(Paths.Server, ".git"));
        Directory.CreateDirectory(Path.Combine(Paths.Server, "config"));
        foreach (var name in new[] { "mangosd.conf", "realmd.conf" })
        {
            File.WriteAllText(Path.Combine(Paths.Server, "config", name + ".example"), name);
        }
    }

    /// <summary>The context a rehearsal walks the plan with.</summary>
    public SetupContext Context(
        SetupOptions options,
        string wowFolder,
        string skateFolder,
        Package? package = null,
        string account = "skateprobe",
        string password = "Skate123")
    {
        var state = new SetupState
        {
            InstallRoot = Paths.Root,
            WowFolder = wowFolder,
            SkateFolder = skateFolder,
            Account = account,
            BuildJobs = 4,
        };
        return new SetupContext(
            state,
            Paths,
            Host,
            Runner,
            Downloader,
            Log,
            options,
            progress: null,
            shortcuts: Shortcuts)
        {
            Package = package,
            PackageVersion = package?.Version ?? "0.0.0",
            AccountPassword = password,
        };
    }

    /// <summary>
    /// Teaches the recording runner the few answers a whole plan needs: an image that exists only
    /// once its build was "run", a container that answers health checks, an account row that appears
    /// only after its insert, and the file each converter or build produces. Without these, a
    /// rehearsal waits out real timeouts or stops at a check only a real run could pass.
    /// </summary>
    /// <param name="freshMachine">
    /// Rehearses a PC that has none of the programs yet: the probes fail until the install command
    /// for each has "run", which is how the dependency installation path is checked.
    /// </param>
    public void PrimeRunner(bool freshMachine = false)
    {
        var builtImages = new HashSet<string>(StringComparer.Ordinal);
        var accountCreated = false;
        var installed = new HashSet<string>(StringComparer.Ordinal);
        PrimeAnswers(builtImages, installed, () => accountCreated, value => accountCreated = value, freshMachine);
        PrimeEffects();
    }

    /// <summary>Whether a command is one of the probes setup uses to ask a program its version.</summary>
    private static bool IsProbe(CommandSpec spec) => spec.Arguments.Any(
        argument => argument is "--version" or "-c" or "version");
    private static string? ProbeTool(CommandSpec spec)
    {
        var name = Path.GetFileNameWithoutExtension(spec.File).ToLowerInvariant();
        if (name.Contains("vswhere"))
        {
            return "vswhere";
        }
        if (name.Contains("python"))
        {
            return "python";
        }
        foreach (var tool in new[] { "docker", "cargo", "git" })
        {
            if (name == tool)
            {
                return tool;
            }
        }
        return null;
    }

    /// <summary>The answers: what each command returns on this fake machine.</summary>
    private void PrimeAnswers(
        HashSet<string> builtImages,
        HashSet<string> installed,
        Func<bool> accountCreated,
        Action<bool> setAccountCreated,
        bool freshMachine)
    {
        if (freshMachine)
        {
            // A probe for a program that is not installed yet says so; the install command is what
            // makes it answer afterwards, which is the dependency path being rehearsed. Only probes
            // are affected: a real use of the program is not what the probe asks about.
            Runner.Answer(spec =>
                IsProbe(spec) && ProbeTool(spec) is { } tool && !installed.Contains(tool)
                    ? new CommandResult(1, "", $"{spec.File}: not found")
                    : null);
            Runner.OnCommand(spec => spec.Arguments.Contains("Docker.DockerDesktop"), _ => installed.Add("docker"));
            Runner.OnCommand(spec => spec.Arguments.Contains("Git.Git"), _ => installed.Add("git"));
            Runner.OnCommand(spec => spec.Arguments.Contains("Python.Python.3.12"), _ => installed.Add("python"));
            Runner.OnCommand(spec => spec.Arguments.Contains("VisualStudio.2022.BuildTools"), _ =>
            {
                installed.Add("msvc");
                // vswhere ships with the build tools, and is the probe that finds them.
                installed.Add("vswhere");
            });
            Runner.OnCommand(spec => spec.Arguments.Contains("Rustlang.Rustup"), _ => installed.Add("cargo"));
            Runner.OnCommand(spec => Path.GetFileName(spec.File) == "rustup-init.exe", _ => installed.Add("cargo"));
            Runner.OnCommand(spec => Path.GetFileName(spec.File) == "DockerDesktopInstaller.exe", _ => installed.Add("docker"));
            Runner.OnCommand(spec => Path.GetFileName(spec.File) == "git-installer.exe", _ => installed.Add("git"));
            Runner.OnCommand(spec => Path.GetFileName(spec.File) == "python-installer.exe", _ => installed.Add("python"));
            Runner.OnCommand(spec => Path.GetFileName(spec.File) == "vs_BuildTools.exe", _ =>
            {
                installed.Add("msvc");
                installed.Add("vswhere");
            });
        }

        // A build "produces" its image, so everything after it finds the image.
        Runner.Answer(spec =>
        {
            if (!spec.Arguments.Contains("build"))
            {
                return null;
            }
            var arguments = spec.Arguments.ToList();
            var index = arguments.IndexOf("-t");
            if (index >= 0 && index + 1 < arguments.Count)
            {
                builtImages.Add(arguments[index + 1]);
            }
            return CommandResult.Success();
        });
        // A pull "produces" the image too, so the step that follows finds it.
        Runner.OnCommand(
            spec => spec.Arguments.Contains("pull") && spec.Arguments.Count > 1,
            spec => builtImages.Add(spec.Arguments.Last()));
        // `docker image inspect` answers only for an image that exists.
        Runner.Answer(spec => spec.Arguments.Contains("image") && spec.Arguments.Contains("inspect")
            ? builtImages.Contains(spec.Arguments.Last())
                ? CommandResult.Success()
                : new CommandResult(1, "", "No such image")
            : null);
        Runner.AnswerWith(spec => spec.Arguments.Any(a => a.Contains("Health.Status")), "healthy");
        Runner.AnswerWith(spec => spec.Arguments.Any(a => a.Contains("State.Status")), "running (exit 0)");
        Runner.AnswerWith(spec => spec.Arguments.Contains("ps") && spec.Arguments.Contains("-q"), "container-id");
        // vswhere names a path, so the C++ toolchain looks installed.
        Runner.AnswerWith(
            spec => ProbeTool(spec) == "vswhere" && spec.Arguments.Contains("-property"),
            @"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools");
        // The account row exists once its insert was "run", and not before.
        Runner.Answer(spec =>
        {
            if (spec.Arguments.Any(a => a.Contains("INSERT INTO account", StringComparison.Ordinal)))
            {
                setAccountCreated(true);
                return CommandResult.Success();
            }
            if (!spec.Arguments.Any(a => a.Contains("SELECT username", StringComparison.Ordinal)))
            {
                return null;
            }
            return CommandResult.Success(accountCreated() ? "SKATEPROBE\t3" : "");
        });
    }

    /// <summary>The effects: the file each command leaves behind, so the checks after it pass.</summary>
    private void PrimeEffects()
    {
        Runner.OnCommand(
            spec => spec.Arguments.Any(a => a.EndsWith("iw4l_skate_convert.py", StringComparison.Ordinal)),
            _ =>
            {
                var privateFolder = Path.Combine(Paths.SkateAssets, "private");
                Directory.CreateDirectory(Path.Combine(privateFolder, "stock"));
                foreach (var name in new[]
                {
                    "skater.glb", "game.json",
                    Path.Combine("stock", "physics-skeletons.json"),
                    Path.Combine("stock", "skater-collections.json"),
                })
                {
                    File.WriteAllText(Path.Combine(privateFolder, name), "converted");
                }
            });
        Runner.OnCommand(
            spec => spec.Arguments.Any(a => a.EndsWith("skate_audio.py", StringComparison.Ordinal)),
            _ =>
            {
                Directory.CreateDirectory(Paths.SkateAudio);
                File.WriteAllText(Path.Combine(Paths.SkateAudio, "pop_1.wav"), "converted");
            });
        Runner.OnCommand(
            spec => spec.Arguments.Contains("-m") && spec.Arguments.Contains("venv"),
            _ =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Paths.VenvPython)!);
                File.WriteAllText(Paths.VenvPython, "stub");
            });
        Runner.OnCommand(
            spec => spec.Arguments.Contains("build") && spec.Arguments.Contains("benilla")
                    && spec.Arguments.Contains("--release"),
            _ =>
            {
                var built = Path.Combine(Paths.Source, "target", "release", "benilla.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(built)!);
                File.WriteAllText(built, "the built client");
            });
        Runner.OnCommand(
            spec => spec.Arguments.Contains("extract-client-data"),
            _ => Directory.CreateDirectory(Path.Combine(Paths.ExtractedData, "maps")));
    }

    /// <summary>
    /// The files a step's output is, written after it "ran", so the next step's checks see them.
    /// This is what lets a rehearsal walk the whole plan on a machine with neither game.
    /// </summary>
    public void SimulateArtifacts(string stepId)
    {
        switch (stepId)
        {
            case "layout":
                Directory.CreateDirectory(Path.Combine(Paths.Source, "crates"));
                break;
            case "skate-data":
                var assets = Path.Combine(Paths.SkateAssets, "private");
                Directory.CreateDirectory(assets);
                File.WriteAllText(Path.Combine(assets, "skater.glb"), "glb");
                break;
            case "skate-audio":
                Directory.CreateDirectory(Paths.SkateAudio);
                File.WriteAllText(Path.Combine(Paths.SkateAudio, "pop_1.wav"), "wav");
                break;
            case "client":
                Directory.CreateDirectory(Paths.Client);
                File.WriteAllText(Paths.ClientExe, "exe");
                break;
            case "server-extract":
                Directory.CreateDirectory(Path.Combine(Paths.ExtractedData, "maps"));
                break;
        }
        _log.Add(stepId);
    }
}
