namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// The install, step by step, in the order a player would want it: the folders, the programs, the
/// player's own files, the client, then the server.
/// </summary>
/// <remarks>
/// Two orderings are deliberate. The client is built *before* the map extraction, because the
/// extraction takes hours and a client build that fails should fail before that, not after. And the
/// dependencies that do not need Docker are installed before the ones that do, so a first run can
/// still produce the converted assets and the client while Docker Desktop waits for a restart.
/// </remarks>
public static class SetupPlan
{
    /// <summary>Every step, in order, for this machine and these options.</summary>
    public static IReadOnlyList<SetupStep> Build(SetupContext ctx)
    {
        var steps = new List<SetupStep>
        {
            Layout(),
            DiskSpace(),
            InstallPrerequisites(),
            Wow(),
            SkateData(),
            SkateAudio(),
            Client(),
        };
        if (!ctx.Options.SkipServer)
        {
            steps.Add(DockerReady());
            steps.Add(ServerFetch());
            steps.Add(ServerImages());
            steps.Add(ServerExtract());
            steps.Add(ServerBuild());
            steps.Add(ServerStart());
            steps.Add(GameAccount());
        }
        steps.Add(Shortcut());
        steps.Add(Verify());
        return steps;
    }

    /// <summary>Whether the package carries a client, so no Rust toolchain is needed.</summary>
    public static bool HasPrebuiltClient(SetupContext ctx) =>
        ctx.Package?.PrebuiltClient is { } exe && File.Exists(exe);

    /// <summary>The required prerequisites that do not answer on this machine.</summary>
    public static async Task<List<Prerequisite>> Missing(SetupContext ctx, bool required)
    {
        var missing = new List<Prerequisite>();
        foreach (var prerequisite in Prerequisites.For(!HasPrebuiltClient(ctx)))
        {
            if (required && !prerequisite.Required)
            {
                continue;
            }
            if (!(await prerequisite.Probe(ctx).ConfigureAwait(false)).Present)
            {
                missing.Add(prerequisite);
            }
        }
        return missing;
    }

    private static SetupStep Layout() => new(
        "layout",
        SetupPhase.Prepare,
        "Install folders",
        "Creates the install's folders and puts the project's source where it will be built.",
        IsDone: ctx => Task.FromResult(
            ctx.Paths.AllDirectories().All(Directory.Exists)
            && Directory.Exists(Path.Combine(ctx.Paths.Source, "crates"))),
        Run: ctx =>
        {
            ctx.EnsureDirectories();
            if (ctx.Package is { } package && Directory.Exists(package.Source)
                && Path.GetFullPath(package.Source) != Path.GetFullPath(ctx.Paths.Source))
            {
                ctx.Say($"copying the project's source into {ctx.Paths.Source}");
                Package.CopyTree(package.Source, ctx.Paths.Source);
            }
            // The patch is what makes the server carry the Skateboarding profession: keep it where
            // the plan and the repository's own scripts both look for it.
            var patch = Path.Combine(ctx.Paths.Source, "setup", "vmangos", "skatecraft.patch");
            if (!File.Exists(patch) && ctx.Package is { } withPatch && File.Exists(withPatch.SkatecraftPatch))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
                File.Copy(withPatch.SkatecraftPatch, patch, overwrite: true);
            }
            ctx.State.InstallRoot = ctx.Paths.Root;
            ctx.State.PackageVersion = ctx.PackageVersion;
            ctx.State.Save(ctx.Paths);
            return Task.FromResult(StepOutcome.Done(
                $"install root: {ctx.Paths.Root}",
                Directory.Exists(ctx.Paths.Source) ? "project source present" : "no package source to copy"));
        },
        Expected: TimeSpan.FromSeconds(20));

    private static SetupStep DiskSpace() => new(
        "disk",
        SetupPhase.Prepare,
        "Free disk space",
        "The server's map data, the container images and the client build need room.",
        IsDone: _ => Task.FromResult(false),
        Run: ctx =>
        {
            var free = ctx.Host.FreeSpaceGigabytes(ctx.Paths.Root);
            ctx.Say($"free space at {ctx.Paths.Root}: {free:F1} GB");
            if (free < 12)
            {
                return Task.FromResult(StepOutcome.Failed(
                    $"Only {free:F1} GB free at {ctx.Paths.Root}; about 20 GB is needed",
                    "Free some space, or choose another install folder."));
            }
            return Task.FromResult(free < 25
                ? StepOutcome.Warning(
                    $"{free:F1} GB free; about 20 GB is needed, and the map extraction is the big part",
                    "Consider freeing space before the map extraction starts.")
                : StepOutcome.Done($"{free:F1} GB free"));
        },
        Expected: TimeSpan.FromSeconds(1));

    private static SetupStep InstallPrerequisites() => new(
        "prereqs",
        SetupPhase.Prerequisites,
        "Required programs",
        "Checks for Docker Desktop, Git, Python, Rust and the C++ build tools, and installs what is missing.",
        IsDone: async ctx => (await Missing(ctx, required: true).ConfigureAwait(false)).Count == 0,
        Run: async ctx =>
        {
            var list = Prerequisites.For(!HasPrebuiltClient(ctx));
            var installed = new List<string>();
            foreach (var prerequisite in list)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var status = await prerequisite.Probe(ctx).ConfigureAwait(false);
                ctx.Say($"{prerequisite.Title}: {status.Detail}");
                if (status.Present || prerequisite.Install is null)
                {
                    continue;
                }
                var outcome = await prerequisite.Install(ctx).ConfigureAwait(false);
                ctx.Say($"{prerequisite.Title}: {outcome.Message}");
                if (outcome.Ok)
                {
                    installed.Add(prerequisite.Title);
                    continue;
                }
                // A missing Docker Desktop does not stop the conversion or the client build; the
                // docker-ready step is where that is reported, once the rest of the work is done.
                if (prerequisite.FatalWhenMissing && prerequisite.Id is not ("docker" or "docker-engine"))
                {
                    return StepOutcome.Failed(outcome.Message, outcome.Fix, outcome.Detail);
                }
                ctx.Say($"{prerequisite.Title} is not ready yet; setup continues and stops at the server step");
            }

            var stillMissing = await Missing(ctx, required: true).ConfigureAwait(false);
            var blockers = stillMissing
                .Where(p => p.Id is not ("docker" or "docker-engine"))
                .ToList();
            if (blockers.Count > 0)
            {
                return StepOutcome.Failed(
                    $"still missing: {string.Join(", ", blockers.Select(p => p.Title))}",
                    blockers[0].InstallFix(ctx));
            }
            if (stillMissing.Count > 0)
            {
                return StepOutcome.Warning(
                    $"{stillMissing[0].Title} is not ready: {stillMissing[0].InstallFix(ctx)}",
                    "The game needs Docker Desktop running to start its server; setup stops at that " +
                    "step, and every finished step is skipped when you run it again.");
            }
            return StepOutcome.Done(
                "every required program is ready",
                installed.Count == 0 ? "all were already installed" : "installed " + string.Join(", ", installed));
        },
        Expected: TimeSpan.FromMinutes(10));

    private static SetupStep DockerReady() => new(
        "docker-ready",
        SetupPhase.Server,
        "Docker Desktop running",
        "Starts Docker Desktop if it is installed but idle, and waits for its engine.",
        IsDone: async ctx => await ctx.Docker.IsRunningAsync(ctx.Cancellation).ConfigureAwait(false),
        Run: async ctx =>
        {
            if (await ctx.Docker.IsRunningAsync(ctx.Cancellation).ConfigureAwait(false))
            {
                return StepOutcome.Done("the Docker engine is answering");
            }
            var engine = Prerequisites.For(needsCompiler: false).First(p => p.Id == "docker-engine");
            var outcome = await engine.Install!(ctx).ConfigureAwait(false);
            if (outcome.Ok)
            {
                return outcome;
            }
            var docker = await ctx.Tools.LocateAsync("docker").ConfigureAwait(false);
            return docker is null
                ? StepOutcome.Failed(
                    "Docker Desktop is not installed",
                    "Install it (winget install -e --id Docker.DockerDesktop), restart Windows, start " +
                    "Docker Desktop once, then run setup again: every finished step is skipped.")
                : StepOutcome.Failed(outcome.Message, outcome.Fix);
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromMinutes(6));

    private static SetupStep Wow() => new(
        "wow",
        SetupPhase.GameFiles,
        "World of Warcraft 1.12.1 files",
        "Checks the WoW folder you chose. It is read from where it is and never written to.",
        IsDone: ctx => Task.FromResult(
            ctx.State.WowFolder.Length > 0 && GameFiles.CheckWow(ctx.State.WowFolder).Ok),
        Run: ctx =>
        {
            if (string.IsNullOrWhiteSpace(ctx.State.WowFolder))
            {
                return Task.FromResult(StepOutcome.Failed(
                    "No World of Warcraft folder has been chosen",
                    "Pick the folder that holds Data, WoW.exe and the .dll files."));
            }
            var check = GameFiles.CheckWow(ctx.State.WowFolder);
            foreach (var line in check.Facts.Concat(check.Warnings))
            {
                ctx.Say("  " + line);
            }
            if (!check.Ok)
            {
                return Task.FromResult(StepOutcome.Failed(
                    string.Join(" ", check.Errors),
                    "Point setup at your own untouched English 1.12.1 client (build 5875).",
                    check.Summary()));
            }
            ctx.State.WowFolder = GameFiles.NormalizeWowFolder(ctx.State.WowFolder);
            ctx.State.Save(ctx.Paths);
            return Task.FromResult(check.Warnings.Count > 0
                ? StepOutcome.Warning($"accepted with notes: {check.Warnings[0]}", null, check.Summary())
                : StepOutcome.Done($"WoW files accepted ({check.Summary()})", string.Join("; ", check.Facts)));
        },
        Expected: TimeSpan.FromSeconds(20));

    private static SetupStep SkateData() => new(
        "skate-data",
        SetupPhase.GameFiles,
        "Skate 3 data",
        "Converts the skater, animations and physics out of your own Skate 3 folder with the Skate 3 " +
        "Rust Engine's converters.",
        IsDone: ctx => Task.FromResult(
            File.Exists(Path.Combine(ctx.Paths.SkateAssets, "private", "skater.glb"))),
        Run: async ctx =>
        {
            var check = GameFiles.CheckSkate(ctx.State.SkateFolder);
            foreach (var line in check.Facts.Concat(check.Warnings))
            {
                ctx.Say("  " + line);
            }
            if (!check.Ok)
            {
                return StepOutcome.Failed(
                    string.Join(" ", check.Errors),
                    "Point setup at the folder holding default.xex and data (extract the disc image " +
                    "first if you have an .iso).");
            }
            var git = await ctx.Tools.LocateAsync("git").ConfigureAwait(false);
            var python = await ctx.Tools.LocateAsync("python").ConfigureAwait(false);
            if (git is null || python is null)
            {
                return StepOutcome.Failed(
                    "Git and Python are needed to convert the Skate 3 data",
                    "Let setup install them (the Required programs step), then run setup again.");
            }
            if (!Directory.Exists(Path.Combine(ctx.Paths.Engine, ".git")))
            {
                ctx.Say($"fetching the Skate 3 Rust Engine at {Upstreams.EngineRev}");
                var clone = await ctx.Run(new CommandSpec(
                    git,
                    new[] { "clone", "--quiet", Upstreams.EngineUrl, ctx.Paths.Engine },
                    null,
                    ctx.ChildEnvironment,
                    "clones the Skate 3 Rust Engine")).ConfigureAwait(false);
                if (!clone.Ok)
                {
                    return StepOutcome.Failed(
                        "the Skate 3 Rust Engine could not be downloaded",
                        $"Check your connection, then run setup again (it fetches {Upstreams.EngineUrl}).");
                }
            }
            await ctx.Run(new CommandSpec(git, new[] { "-C", ctx.Paths.Engine, "fetch", "--quiet", "origin" }))
                .ConfigureAwait(false);
            var checkout = await ctx.Run(new CommandSpec(
                git,
                new[] { "-C", ctx.Paths.Engine, "checkout", "--quiet", Upstreams.EngineRev },
                null,
                ctx.ChildEnvironment,
                $"checks out {Upstreams.EngineRev}")).ConfigureAwait(false);
            if (!checkout.Ok)
            {
                return StepOutcome.Failed(
                    $"the Skate 3 Rust Engine revision {Upstreams.EngineRev} could not be checked out",
                    "Delete the tool\\engine folder and run setup again.");
            }
            return await ConvertSkateData(ctx, python).ConfigureAwait(false);
        },
        Expected: TimeSpan.FromMinutes(8));

    /// <summary>The converter's Python environment, then the conversion itself.</summary>
    private static async Task<StepOutcome> ConvertSkateData(SetupContext ctx, string python)
    {
        ctx.Say("preparing the converter's Python environment");
        if (!File.Exists(ctx.Paths.VenvPython))
        {
            var venv = await ctx.Run(new CommandSpec(
                python,
                new[] { "-m", "venv", Path.Combine(ctx.Paths.Tool, "venv") },
                null,
                ctx.ChildEnvironment,
                "creates the converter's Python environment")).ConfigureAwait(false);
            if (!venv.Ok)
            {
                return StepOutcome.Failed(
                    "the converter's Python environment could not be created",
                    "Install Python 3 with its venv module, then run setup again.");
            }
        }
        var pip = await ctx.Run(new CommandSpec(
            ctx.Paths.VenvPython,
            new[]
            {
                "-m", "pip", "install", "--quiet", "--disable-pip-version-check",
                "numpy>=2.2,<3", "Pillow>=11.3",
            },
            null,
            ctx.ChildEnvironment,
            "installs the converter's Python packages")).ConfigureAwait(false);
        if (!pip.Ok)
        {
            return StepOutcome.Failed(
                "the converter's Python packages could not be installed",
                "Check your connection, then run setup again (it installs numpy and Pillow).");
        }

        var converter = Path.Combine(ctx.Paths.Engine, "iw4l_skate_convert.py");
        if (!await ctx.Download(Upstreams.ConverterUrl, converter, "the Skate 3 converter").ConfigureAwait(false))
        {
            return StepOutcome.Failed(
                "the Skate 3 converter could not be downloaded",
                $"Check your connection, then run setup again (it fetches {Upstreams.ConverterUrl}).");
        }

        Directory.CreateDirectory(ctx.Paths.Game);
        var run = await ctx.Run(new CommandSpec(
            ctx.Paths.VenvPython,
            new[]
            {
                converter,
                "--xex", Path.Combine(ctx.State.SkateFolder, "default.xex"),
                "--out", Path.Combine(ctx.Paths.Game, "skate-data"),
            },
            ctx.Paths.Engine,
            ctx.ChildEnvironment,
            "converts your own Skate 3 data")).ConfigureAwait(false);
        if (!run.Ok || !File.Exists(Path.Combine(ctx.Paths.SkateAssets, "private", "skater.glb")))
        {
            return StepOutcome.Failed(
                "the Skate 3 conversion failed",
                "Check that the Skate 3 folder is a complete extraction (several GB under data), then " +
                "delete game\\skate-data and run setup again.",
                run.Output);
        }
        return StepOutcome.Done("the skater, animations and physics are converted");
    }

    private static SetupStep SkateAudio() => new(
        "skate-audio",
        SetupPhase.GameFiles,
        "Skate 3 sounds",
        "Converts your own Skate 3 sounds (rolling, pops, landings, grinds) with vgmstream. Skating " +
        "works without them, just silently.",
        IsDone: ctx => Task.FromResult(File.Exists(Path.Combine(ctx.Paths.SkateAudio, "pop_1.wav"))),
        Run: async ctx =>
        {
            if (!Directory.Exists(Path.Combine(ctx.State.SkateFolder, "data", "audio")))
            {
                return StepOutcome.Skipped("your Skate 3 folder has no data\\audio, so there is nothing to convert");
            }
            var script = Path.Combine(ctx.Paths.Source, "tools", "skate_audio.py");
            if (!File.Exists(script) || !File.Exists(ctx.Paths.VenvPython))
            {
                return StepOutcome.Warning(
                    "the sounds cannot be converted without the project's tools and its Python environment",
                    "Run setup again after the Skate 3 data step has finished.");
            }
            var archive = Path.Combine(WindowsPrerequisites.Downloads(ctx), "vgmstream.zip");
            if (!await ctx.Download(Upstreams.VgmstreamUrl, archive, "vgmstream").ConfigureAwait(false))
            {
                return StepOutcome.Warning(
                    "vgmstream could not be downloaded, so the skate sounds stay silent",
                    $"Check your connection, then run setup again (it fetches {Upstreams.VgmstreamUrl}).");
            }
            ctx.ExtractZip(archive, ctx.Paths.Vgmstream);
            var environment = new Dictionary<string, string>(ctx.ChildEnvironment)
            {
                ["PATH"] = ctx.Paths.Vgmstream + Path.PathSeparator + ctx.ChildEnvironment["PATH"],
            };
            var run = await ctx.Run(new CommandSpec(
                ctx.Paths.VenvPython,
                new[]
                {
                    script,
                    Path.Combine(ctx.State.SkateFolder, "data"),
                    ctx.Paths.SkateAudio,
                },
                null,
                environment,
                "converts your own Skate 3 sounds")).ConfigureAwait(false);
            if (!run.Ok || !File.Exists(Path.Combine(ctx.Paths.SkateAudio, "pop_1.wav")))
            {
                return StepOutcome.Warning(
                    "the skate sounds could not be converted; skating works, just silently",
                    "Delete game\\skate-audio and run setup again.");
            }
            ctx.State.SoundsConverted = true;
            return StepOutcome.Done("the skate sounds are converted");
        },
        Optional: true,
        Expected: TimeSpan.FromMinutes(4));

    private static SetupStep Client() => new(
        "client",
        SetupPhase.Client,
        "Game client",
        "Uses the prebuilt client if the package carries one, and otherwise builds the client from " +
        "the project's source. Either way, the binary ends up in the client folder.",
        IsDone: ctx => Task.FromResult(
            !ctx.Options.SkipClient && File.Exists(ctx.Paths.ClientExe)),
        Run: async ctx =>
        {
            if (ctx.Options.SkipClient)
            {
                return StepOutcome.Skipped("the client step was turned off");
            }
            Directory.CreateDirectory(ctx.Paths.Client);
            if (HasPrebuiltClient(ctx))
            {
                ctx.Say("copying the prebuilt client from the package");
                File.Copy(ctx.Package!.PrebuiltClient, ctx.Paths.ClientExe, overwrite: true);
                ctx.State.ClientIsPrebuilt = true;
                return StepOutcome.Done("the prebuilt client is in place");
            }
            if (!Directory.Exists(Path.Combine(ctx.Paths.Source, "crates")))
            {
                return StepOutcome.Failed(
                    "the project's source is missing from the install",
                    "Extract the package again, then run setup: it copies src\\ into the install.");
            }
            var cargo = await ctx.Tools.LocateAsync("cargo").ConfigureAwait(false);
            if (cargo is null)
            {
                return StepOutcome.Failed(
                    "Rust is not installed, so the client cannot be built",
                    "Install Rust (setup does this for you: winget install -e --id Rustlang.Rustup), " +
                    "then run setup again.");
            }
            ctx.Say("building the client (the first build takes several minutes and says little)");
            var build = await ctx.Run(new CommandSpec(
                cargo,
                new[] { "build", "--release", "-p", "benilla" },
                ctx.Paths.Source,
                ctx.ChildEnvironment,
                "builds the game client")).ConfigureAwait(false);
            var built = Path.Combine(ctx.Paths.Source, "target", "release", "benilla.exe");
            if (!build.Ok || !File.Exists(built))
            {
                return StepOutcome.Failed(
                    "the client build failed",
                    "Read the first \"error:\" line above. A missing link.exe means the C++ build tools " +
                    "are not installed; a \"path too long\" error means the install folder is too deep.",
                    build.Output);
            }
            File.Copy(built, ctx.Paths.ClientExe, overwrite: true);
            ctx.State.ClientIsPrebuilt = false;
            return StepOutcome.Done(
                $"the client is built ({new FileInfo(ctx.Paths.ClientExe).Length / (1024 * 1024)} MB)");
        },
        Expected: TimeSpan.FromMinutes(25));

    private static SetupStep ServerFetch() => new(
        "server-fetch",
        SetupPhase.Server,
        "Local server files",
        "Fetches the server project at the pinned revision, creates its folders, and writes its " +
        "Compose file with the deploy version and image digests this setup pins.",
        IsDone: ctx => Task.FromResult(
            File.Exists(ctx.Paths.ComposeFile)
            && ComposeWriter.IsVersionOne(File.ReadAllText(ctx.Paths.ComposeFile))
            && Directory.Exists(Path.Combine(ctx.Paths.Server, ".git"))
            && Directory.Exists(Path.Combine(ctx.Paths.Server, "storage", "mangosd", "extracted-data"))),
        Run: async ctx =>
        {
            var git = await ctx.Tools.LocateAsync("git").ConfigureAwait(false);
            if (git is null)
            {
                return StepOutcome.Failed(
                    "Git is not installed, so the server project cannot be fetched",
                    "Install Git (winget install -e --id Git.Git), then run setup again.");
            }
            // An install from before vmangos-deploy's version 1: its containers have to stop, and
            // its compose file is kept aside. The database volume and the map folder keep their
            // names, so characters and the hours of extraction survive.
            if (File.Exists(ctx.Paths.ComposeFile)
                && !ComposeWriter.IsVersionOne(File.ReadAllText(ctx.Paths.ComposeFile)))
            {
                ctx.Say("updating an older server setup to vmangos-deploy version 1 (characters and maps are kept)");
                await ctx.Docker.DownAsync(ctx.Cancellation).ConfigureAwait(false);
                File.Move(ctx.Paths.ComposeFile, ctx.Paths.ComposeFile + ".pre-v1", overwrite: true);
            }

            if (!Directory.Exists(Path.Combine(ctx.Paths.Server, ".git")))
            {
                ctx.Say($"fetching the server project at {Upstreams.ServerRev[..12]}");
                var clone = await ctx.Run(new CommandSpec(
                    git,
                    new[] { "clone", "--quiet", Upstreams.ServerUrl, ctx.Paths.Server },
                    null,
                    ctx.ChildEnvironment,
                    "clones the server project")).ConfigureAwait(false);
                if (!clone.Ok)
                {
                    return StepOutcome.Failed(
                        "the server project could not be fetched",
                        $"Check your connection, then run setup again (it fetches {Upstreams.ServerUrl}).");
                }
            }
            await ctx.Run(new CommandSpec(git, new[] { "-C", ctx.Paths.Server, "fetch", "--quiet", "origin" }))
                .ConfigureAwait(false);
            // The deploy repository rewrote its history on 2026-10-04, so a reset is what works for
            // a clone from either side of that date.
            var reset = await ctx.Run(new CommandSpec(
                git,
                new[] { "-C", ctx.Paths.Server, "reset", "--quiet", "--hard", Upstreams.ServerRev },
                null,
                ctx.ChildEnvironment,
                $"moves the server project to {Upstreams.ServerRev[..12]}")).ConfigureAwait(false);
            if (!reset.Ok)
            {
                return StepOutcome.Failed(
                    $"the server project revision {Upstreams.ServerRev[..12]} could not be checked out",
                    "Delete the server folder and run setup again (your characters live in a Docker " +
                    "volume, not in that folder).");
            }
            return await WriteServerFiles(ctx).ConfigureAwait(false);
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromMinutes(3));

    /// <summary>The server's config files, its folders and its generated Compose file.</summary>
    private static async Task<StepOutcome> WriteServerFiles(SetupContext ctx)
    {
        var config = Path.Combine(ctx.Paths.Server, "config");
        foreach (var name in new[] { "mangosd.conf", "realmd.conf" })
        {
            var target = Path.Combine(config, name);
            var example = Path.Combine(config, name + ".example");
            if (!File.Exists(target) && File.Exists(example))
            {
                File.Copy(example, target);
                ctx.Say($"created config\\{name} from the project's example");
            }
        }
        foreach (var directory in new[]
        {
            Path.Combine(ctx.Paths.Server, "storage", "mangosd", "client-data"),
            Path.Combine(ctx.Paths.Server, "storage", "mangosd", "extracted-data"),
            Path.Combine(ctx.Paths.Server, "storage", "mangosd", "logs"),
            Path.Combine(ctx.Paths.Server, "storage", "mangosd", "honor"),
            Path.Combine(ctx.Paths.Server, "storage", "realmd", "logs"),
            Path.Combine(ctx.Paths.Server, "storage", "database", "custom-sql"),
        })
        {
            Directory.CreateDirectory(directory);
        }
        if (!File.Exists(Path.Combine(ctx.Paths.Server, "storage", "database", "custom-sql", ".gitkeep")))
        {
            File.WriteAllText(Path.Combine(ctx.Paths.Server, "storage", "database", "custom-sql", ".gitkeep"), "");
        }

        var patched = PatchedImage(ctx);
        var usePatched = patched is not null
                         && await ctx.Docker.HasImageAsync(patched, ctx.Cancellation).ConfigureAwait(false);
        if (ctx.DryRun)
        {
            usePatched = ctx.State.BuildPatchedServer;
        }
        var image = usePatched ? patched! : Upstreams.ServerImage;
        ComposeWriter.Write(ctx.Paths, image, Upstreams.DatabaseImage, ctx.TimeZone);
        ctx.Say(usePatched
            ? "the server runs the patched image, so the Skateboarding profession works"
            : "the server runs the stock image; the Skateboarding profession needs the patched build");
        return StepOutcome.Done(
            "the server's files and Compose file are in place",
            $"server image: {image}");
    }

    /// <summary>The patched server image's name, or null while the files it is built from are absent.</summary>
    public static string? PatchedImage(SetupContext ctx)
    {
        var patch = Path.Combine(ctx.Paths.Source, "setup", "vmangos", "skatecraft.patch");
        var dockerfile = Path.Combine(ctx.Paths.Server, "docker", "server", "Dockerfile");
        if (!File.Exists(patch) || !File.Exists(dockerfile))
        {
            return null;
        }
        return Upstreams.PatchedImage(SetupContext.Hash12(patch, dockerfile));
    }

    private static SetupStep ServerImages() => new(
        "server-images",
        SetupPhase.Server,
        "Server images",
        "Downloads the server's container images by digest, so a later upstream release cannot " +
        "change what this install runs.",
        IsDone: async ctx => ctx.DryRun
            || (await ctx.Docker.HasImageAsync(Upstreams.DatabaseImage, ctx.Cancellation).ConfigureAwait(false)
                && await ctx.Docker.HasImageAsync(Upstreams.ServerImage, ctx.Cancellation).ConfigureAwait(false)),
        Run: async ctx =>
        {
            foreach (var image in new[] { Upstreams.DatabaseImage, Upstreams.ServerImage })
            {
                ctx.Say($"downloading {image.Split('@')[0]}");
                var pull = await ctx.Run(ctx.Docker.DockerCommand(
                    "pull", image), "docker:").ConfigureAwait(false);
                if (!pull.Ok)
                {
                    return StepOutcome.Failed(
                        $"the image {image.Split('@')[0]} could not be downloaded",
                        "Check your connection and that Docker Desktop is running, then run setup again.");
                }
            }
            return StepOutcome.Done("the server's images are downloaded");
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromMinutes(8));

    private static SetupStep ServerExtract() => new(
        "server-extract",
        SetupPhase.Server,
        "Server map data",
        "Converts the maps, terrain and DBC data out of your WoW client into the server's own data. " +
        "It takes hours and happens once. Your WoW folder is mounted read-only, so nothing in it changes.",
        IsDone: ctx => Task.FromResult(Directory.Exists(Path.Combine(ctx.Paths.ExtractedData, "maps"))),
        Run: async ctx =>
        {
            if (ctx.State.WowFolder.Length == 0 || !Directory.Exists(Path.Combine(ctx.State.WowFolder, "Data")))
            {
                return StepOutcome.Failed(
                    "your WoW folder is not known yet",
                    "Run setup again and choose the World of Warcraft folder first.");
            }
            if (!File.Exists(ctx.Paths.ComposeFile))
            {
                return StepOutcome.Failed(
                    "the server's Compose file is missing",
                    "Run setup again: the Local server files step writes it.");
            }
            var image = ComposeWriter.ServerImageOf(File.ReadAllText(ctx.Paths.ComposeFile));
            if (image is null)
            {
                return StepOutcome.Failed(
                    "the Compose file does not name a server image",
                    "Delete server\\compose.yaml and run setup again.");
            }
            Directory.CreateDirectory(ctx.Paths.ClientDataScratch);
            Directory.CreateDirectory(ctx.Paths.ExtractedData);

            ctx.Say("extracting the server's map data from your WoW client (hours; leave it running)");
            // The extractors write beside the client's Data, so the client gets a scratch folder
            // holding Data read-only rather than the WoW folder itself.
            var spec = ctx.Docker.RunOnce(
                image,
                entryPoint: "extract-client-data",
                mounts: new[]
                {
                    Docker.Mount(ctx.Paths.ClientDataScratch, "/opt/vmangos/storage/client-data"),
                    Docker.Mount(
                        Path.Combine(ctx.State.WowFolder, "Data"),
                        "/opt/vmangos/storage/client-data/Data",
                        readOnly: true),
                    Docker.Mount(ctx.Paths.ExtractedData, "/opt/vmangos/storage/data"),
                },
                environment: new[]
                {
                    $"VMANGOS_DEPLOY_VERSION={Upstreams.DeployVersion}",
                    "VMANGOS_UID=1000",
                    "VMANGOS_GID=1000",
                },
                // `--force` answers the extractor's "overwrite the old data?" prompt, so a rerun
                // never waits on a question nobody is there to answer.
                command: new[] { "--force" });
            var run = await ctx.Run(spec, "extract:").ConfigureAwait(false);
            if (!Directory.Exists(Path.Combine(ctx.Paths.ExtractedData, "maps")))
            {
                return StepOutcome.Failed(
                    "the map extraction did not finish",
                    "Read the lines above for the first error. If it was interrupted, delete " +
                    "server\\storage\\mangosd\\extracted-data and run setup again.",
                    run.Output);
            }
            ctx.State.MapsExtracted = true;
            ctx.State.Save(ctx.Paths);
            return StepOutcome.Done("the server's map data is extracted");
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromHours(3),
        Note: "This is the step that takes hours. It runs once; setup continues from where it stopped.");

    private static SetupStep ServerBuild() => new(
        "server-build",
        SetupPhase.Server,
        "Server with the Skateboarding profession",
        "Builds the server image with World of Skatecraft's patch applied, which is what adds the " +
        "Skateboarding skill, its spells and the trick scoring. The stock server cannot do this.",
        IsDone: async ctx =>
        {
            var image = PatchedImage(ctx);
            if (image is null || !File.Exists(ctx.Paths.ComposeFile))
            {
                return false;
            }
            return ComposeWriter.ServerImageOf(File.ReadAllText(ctx.Paths.ComposeFile)) == image
                   && (ctx.DryRun
                       || await ctx.Docker.HasImageAsync(image, ctx.Cancellation).ConfigureAwait(false));
        },
        Run: async ctx =>
        {
            var patch = Path.Combine(ctx.Paths.Source, "setup", "vmangos", "skatecraft.patch");
            var dockerfile = Path.Combine(ctx.Paths.Server, "docker", "server", "Dockerfile");
            if (!File.Exists(patch))
            {
                return StepOutcome.Failed(
                    "the Skateboarding profession's patch is missing",
                    "Extract the package again: src\\setup\\vmangos\\skatecraft.patch is part of it.");
            }
            if (!File.Exists(dockerfile))
            {
                return StepOutcome.Failed(
                    "the server project's Dockerfile is missing",
                    "Run setup again: the Local server files step fetches it.");
            }
            if (!ctx.State.BuildPatchedServer)
            {
                ctx.Say("the patched build was turned off; the stock server it is, without the profession");
                await WriteServerFiles(ctx).ConfigureAwait(false);
                return StepOutcome.Warning(
                    "the stock server is installed, so skating works but the profession does not",
                    "Turn the patched server build on in setup's options and run it again.");
            }
            return await BuildPatchedServer(ctx, patch, dockerfile).ConfigureAwait(false);
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromMinutes(12));

    /// <summary>Builds the patched server image from the deploy project's own build context.</summary>
    private static async Task<StepOutcome> BuildPatchedServer(SetupContext ctx, string patch, string dockerfile)
    {
        var image = Upstreams.PatchedImage(SetupContext.Hash12(patch, dockerfile));
        if (await ctx.Docker.HasImageAsync(image, ctx.Cancellation).ConfigureAwait(false))
        {
            ctx.Say($"{image} is already built");
            await WriteServerFiles(ctx).ConfigureAwait(false);
            return StepOutcome.Done("the server now carries the Skateboarding profession", $"image: {image}");
        }

        ctx.Say($"building {image} (this compiles vmangos; a few minutes, and it says little)");
        var context = Path.Combine(Path.GetTempPath(), "skatecraft-build-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(context);
            Package.CopyTree(Path.Combine(ctx.Paths.Server, "docker"), Path.Combine(context, "docker"));
            var patches = Path.Combine(context, "docker", "patches");
            Directory.CreateDirectory(patches);
            File.Copy(patch, Path.Combine(patches, "zz-skatecraft.patch"), overwrite: true);
            // The build runs in a Linux container: a CRLF from a Windows checkout breaks the shell
            // scripts and the patch itself.
            foreach (var file in Directory.EnumerateFiles(context, "*", SearchOption.AllDirectories))
            {
                SetupContext.NormalizeLineEndings(file);
            }
            var source = Path.Combine(context, "docker", "server", "Dockerfile");
            var text = File.ReadAllText(source).Replace(
                "make -j\"$(nproc)\"",
                $"make -j{Math.Max(1, ctx.State.BuildJobs)}",
                StringComparison.Ordinal);
            File.WriteAllText(
                Path.Combine(context, "Dockerfile.skatecraft"), text, new System.Text.UTF8Encoding(false));

            var environment = new Dictionary<string, string>(ctx.ChildEnvironment)
            {
                // The deploy project's Dockerfile uses BuildKit-only instructions.
                ["DOCKER_BUILDKIT"] = "1",
            };
            var build = await ctx.Run(new CommandSpec(
                "docker",
                new[]
                {
                    "build", "-t", image,
                    "-f", Path.Combine(context, "Dockerfile.skatecraft"),
                    "--build-arg", $"VMANGOS_REVISION={Upstreams.VmangosRev}",
                    "--build-arg", $"VMANGOS_CLIENT_VERSION={Upstreams.ClientBuild}",
                    // A patch that does not apply must fail the build rather than quietly ship the
                    // stock server, which is what made "J does nothing" so hard to explain.
                    "--build-arg", "VMANGOS_FAIL_ON_PATCH_ERROR=1",
                    context,
                },
                null,
                environment,
                "builds the server with the Skateboarding profession")).ConfigureAwait(false);
            if (!build.Ok || !await ctx.Docker.HasImageAsync(image, ctx.Cancellation).ConfigureAwait(false))
            {
                return StepOutcome.Failed(
                    "the server build failed",
                    "Read the first error above. The build needs Docker Desktop running, an internet " +
                    "connection and a few GB of free disk. Then run setup again.",
                    build.Output);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(context, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary folder is not worth failing an install over.
            }
        }
        await WriteServerFiles(ctx).ConfigureAwait(false);
        return StepOutcome.Done("the server now carries the Skateboarding profession", $"image: {image}");
    }

    private static SetupStep ServerStart() => new(
        "server-start",
        SetupPhase.Server,
        "Start the server",
        "Starts the server's three containers and waits until the world server reports healthy.",
        IsDone: async ctx => ctx.DryRun
            || await ctx.Docker.HealthAsync("mangosd", ctx.Cancellation).ConfigureAwait(false) == "healthy",
        Run: async ctx =>
        {
            if (!File.Exists(ctx.Paths.ComposeFile))
            {
                return StepOutcome.Failed(
                    "the server's Compose file is missing",
                    "Run setup again: the Local server files step writes it.");
            }
            var up = await ctx.Run(ctx.Docker.Compose("up", "-d"), "compose:").ConfigureAwait(false);
            if (!up.Ok)
            {
                return StepOutcome.Failed(
                    "the server's containers did not start",
                    "Read the lines above. If Docker Desktop is not running, start it and run setup " +
                    "again; everything already done is skipped.",
                    await ctx.Docker.LogsAsync("mangosd", 40, ctx.Cancellation).ConfigureAwait(false));
            }
            ctx.Say("waiting for the world server (a first start runs the database migrations)");
            var healthy = await ctx.Docker
                .WaitHealthyAsync(
                    "mangosd",
                    TimeSpan.FromMinutes(20),
                    waited => ctx.Report(null, $"waiting for the world server ({waited.TotalMinutes:F0} min)"),
                    ctx.Cancellation)
                .ConfigureAwait(false);
            if (!healthy)
            {
                var state = await ctx.Docker.StateAsync("mangosd", ctx.Cancellation).ConfigureAwait(false);
                return StepOutcome.Failed(
                    $"the world server is not healthy ({state})",
                    "Look at the log lines below, or run: docker compose -p world-of-skatecraft " +
                    "-f server\\compose.yaml logs mangosd",
                    await ctx.Docker.LogsAsync("mangosd", 60, ctx.Cancellation).ConfigureAwait(false));
            }
            var login = await Net.WaitForPortAsync("127.0.0.1", 3724, TimeSpan.FromSeconds(60), ctx.Cancellation)
                .ConfigureAwait(false);
            return login
                ? StepOutcome.Done("the server is up and its login port answers")
                : StepOutcome.Warning(
                    "the world server is healthy but port 3724 did not answer yet",
                    "It usually comes up a moment later; if the game cannot log in, run setup again.");
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromMinutes(6));

    private static SetupStep GameAccount() => new(
        "account",
        SetupPhase.Server,
        "Game account",
        "Creates your game account on the server, with GM rights so chat commands like .tele work.",
        IsDone: async ctx =>
        {
            if (ctx.State.Account.Length == 0 || ctx.DryRun)
            {
                return ctx.State.Account.Length > 0;
            }
            var row = await ctx.Capture(WorldOfSkatecraft.Setup.Core.Account.DatabaseCommand(
                ctx.Docker,
                $"SELECT username FROM account WHERE username='{ctx.State.Account.ToUpperInvariant()}'"))
                .ConfigureAwait(false);
            return row.Ok && row.StdOut.Trim().Length > 0;
        },
        Run: async ctx =>
        {
            if (ctx.State.Account.Length == 0)
            {
                return StepOutcome.Skipped(
                    "no account was asked for; add one from setup's options whenever you want");
            }
            if (ctx.AccountPassword.Length == 0)
            {
                return StepOutcome.Failed(
                    "the account's password is not in this run",
                    "Run setup again and enter the account name and password.");
            }
            if (!WorldOfSkatecraft.Setup.Core.Account.ValidName(ctx.State.Account) || !WorldOfSkatecraft.Setup.Core.Account.ValidPassword(ctx.AccountPassword))
            {
                return StepOutcome.Failed(
                    "the account name or password is not one the server accepts",
                    "An account name is letters and digits, up to 16 characters; a password is up to " +
                    "16 characters.");
            }
            var sql = WorldOfSkatecraft.Setup.Core.Account.CreateAccountSql(ctx.State.Account, ctx.AccountPassword, ctx.State.AccountGmLevel);
            var result = await ctx.Run(
                WorldOfSkatecraft.Setup.Core.Account.DatabaseCommand(ctx.Docker, sql), "sql:").ConfigureAwait(false);
            if (!result.Ok)
            {
                return StepOutcome.Failed(
                    "the account could not be created",
                    "Check that the server is running (the previous step), then run setup again.",
                    result.Output);
            }
            var check = await ctx.Capture(WorldOfSkatecraft.Setup.Core.Account.DatabaseCommand(
                ctx.Docker,
                $"SELECT username, gmlevel FROM account WHERE username='{ctx.State.Account.ToUpperInvariant()}'"))
                .ConfigureAwait(false);
            if (!check.Ok || check.StdOut.Trim().Length == 0)
            {
                return StepOutcome.Failed(
                    "the account was written but cannot be read back",
                    "Run setup again; if it repeats, look at the server's database container.");
            }
            ctx.State.Save(ctx.Paths);
            return StepOutcome.Done(
                $"your account \"{ctx.State.Account.ToUpperInvariant()}\" can log in",
                "It is a GM: .tele, .levelup and .additem work.");
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromSeconds(30));

    private static SetupStep Shortcut() => new(
        "shortcut",
        SetupPhase.Finish,
        "Desktop shortcut",
        "Puts \"World of Skatecraft\" on your desktop and in the Start menu; it starts the server and " +
        "then the game.",
        IsDone: ctx => Task.FromResult(
            ctx.State.ShortcutCreated && ctx.Shortcuts.Exists(Shortcuts.DesktopName)),
        Run: ctx =>
        {
            var launcher = Path.Combine(ctx.Paths.App, "Setup.exe");
            if (!File.Exists(launcher))
            {
                launcher = "Setup.exe";
            }
            var icon = File.Exists(ctx.Paths.ClientExe) ? ctx.Paths.ClientExe : launcher;
            var made = new List<string>();
            foreach (var where in new[] { ShortcutPlace.Desktop, ShortcutPlace.StartMenu })
            {
                var outcome = ctx.Shortcuts.Create(
                    Shortcuts.DesktopName, launcher, "--play", ctx.Paths.Root, icon, where, ctx.Log);
                if (outcome.Length == 0)
                {
                    made.Add(where.ToString().ToLowerInvariant());
                }
            }
            if (made.Count == 0)
            {
                return Task.FromResult(StepOutcome.Warning(
                    "no shortcut could be created",
                    $"Start the game with {launcher} --play, or make a shortcut to it yourself."));
            }
            ctx.State.ShortcutCreated = true;
            ctx.State.Save(ctx.Paths);
            return Task.FromResult(StepOutcome.Done(
                $"\"{Shortcuts.DesktopName}\" is on your {string.Join(" and ", made)}"));
        },
        Expected: TimeSpan.FromSeconds(5));

    private static SetupStep Verify() => new(
        "verify",
        SetupPhase.Finish,
        "Final checks",
        "Checks that the client, the converted assets, the server's Compose file and the running " +
        "server are all in place before setup calls itself done.",
        IsDone: _ => Task.FromResult(false),
        Run: async ctx =>
        {
            var problems = new List<string>();
            var notes = new List<string>();
            if (!File.Exists(ctx.Paths.ClientExe))
            {
                problems.Add("the game client is missing");
            }
            if (!File.Exists(Path.Combine(ctx.Paths.SkateAssets, "private", "skater.glb")))
            {
                problems.Add("the converted Skate 3 assets are missing");
            }
            if (!File.Exists(Path.Combine(ctx.Paths.SkateAudio, "pop_1.wav")))
            {
                notes.Add("the skate sounds are not converted, so skating is silent");
            }
            if (!ctx.Options.SkipServer)
            {
                if (!File.Exists(ctx.Paths.ComposeFile))
                {
                    problems.Add("the server's Compose file is missing");
                }
                else
                {
                    var compose = File.ReadAllText(ctx.Paths.ComposeFile);
                    if (!ComposeWriter.IsVersionOne(compose))
                    {
                        problems.Add("the server's Compose file is not a version-1 deploy file");
                    }
                    if (ComposeWriter.ServerImageOf(compose)?.Contains("mserajnik", StringComparison.Ordinal) == true)
                    {
                        notes.Add("the stock server image is configured, so the Skateboarding " +
                                  "profession is off; run setup again with the patched build on");
                    }
                }
                if (!ctx.DryRun)
                {
                    if (await ctx.Docker.HealthAsync("mangosd", ctx.Cancellation).ConfigureAwait(false) != "healthy")
                    {
                        problems.Add("the world server is not healthy");
                    }
                    else if (!await Net.WaitForPortAsync("127.0.0.1", 3724, TimeSpan.FromSeconds(5), ctx.Cancellation)
                                 .ConfigureAwait(false))
                    {
                        notes.Add("port 3724 did not answer; the login service may still be starting");
                    }
                }
            }
            ctx.State.Save(ctx.Paths);
            if (problems.Count > 0)
            {
                return StepOutcome.Failed(
                    string.Join("; ", problems),
                    "Run setup again (it redoes only what is missing), or use Repair on the menu.");
            }
            var detail = notes.Count == 0
                ? "everything is in place"
                : string.Join("; ", notes);
            return notes.Count == 0
                ? StepOutcome.Done("everything is in place")
                : StepOutcome.Warning("in place, with notes: " + detail, null, detail);
        },
        NeedsDocker: true,
        Expected: TimeSpan.FromSeconds(15));
}
