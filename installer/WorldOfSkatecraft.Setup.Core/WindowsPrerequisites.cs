namespace WorldOfSkatecraft.Setup.Core;

/// <summary>The Windows prerequisites, and the winget or vendor-installer command for each.</summary>
public static class WindowsPrerequisites
{
    /// <summary>How long to wait for the Docker engine after starting Docker Desktop.</summary>
    public static readonly TimeSpan DockerStartTimeout = TimeSpan.FromMinutes(6);
    private static CommandSpec Tool(SetupContext ctx, string program, params string[] arguments) =>
        new(program, arguments, null, ctx.ChildEnvironment);

    /// <summary>A tool's version line, for the report; empty when the probe did not answer.</summary>
    private static async Task<string> CapturePath(SetupContext ctx, string program, params string[] arguments)
    {
        var result = await ctx.Capture(Tool(ctx, program, arguments)).ConfigureAwait(false);
        var line = result.Output.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 120 ? line[..120] : line;
    }

    /// <summary>Every prerequisite, in the order setup installs them.</summary>
    public static IReadOnlyList<Prerequisite> All(bool needsCompiler) => new List<Prerequisite>
    {
        Docker(),
        DockerEngine(),
        Compose(),
        Buildx(),
        Git(),
        Python(),
        Rust(needsCompiler),
        Compiler(needsCompiler),
    };

    private static Prerequisite Docker() => new(
        "docker",
        "Docker Desktop",
        "Runs the local game server: vmangos, its database and the map extraction all run in " +
        "containers, which is how the server project is distributed. Nothing about it is bundled " +
        "here — it is installed from Docker's own download.",
        Required: true,
        Probe: async ctx => await ctx.Tools.HasAsync("docker").ConfigureAwait(false)
            ? PrerequisiteStatus.Yes("docker found", await CapturePath(ctx, "docker", "--version").ConfigureAwait(false))
            : PrerequisiteStatus.No("Docker Desktop is not installed"),
        InstallFix: _ => "winget install -e --id Docker.DockerDesktop, then restart Windows and start " +
                         "Docker Desktop once",
        Install: InstallDockerDesktop);

    private static Prerequisite DockerEngine() => new(
        "docker-engine",
        "Docker Desktop running",
        "The Docker engine has to be running before the server's containers can start.",
        Required: true,
        Probe: async ctx => await ctx.Docker.IsRunningAsync(ctx.Cancellation).ConfigureAwait(false)
            ? PrerequisiteStatus.Yes("the Docker engine answers")
            : PrerequisiteStatus.No("Docker is installed but its engine is not answering"),
        InstallFix: _ => "Start Docker Desktop from the Start menu, wait until it says the engine is " +
                         "running, then run setup again.",
        Install: StartDockerDesktop);

    private static Prerequisite Compose() => new(
        "docker-compose",
        "Docker Compose",
        "The server is one Compose project: its database, its login service and its world service.",
        Required: true,
        Probe: async ctx => (await ctx.Capture("docker", "compose", "version").ConfigureAwait(false)).Ok
            ? PrerequisiteStatus.Yes("docker compose answers")
            : PrerequisiteStatus.No("the docker compose plugin is missing"),
        InstallFix: _ => "Docker Desktop ships it; update Docker Desktop.",
        Install: async ctx => (await ctx.Capture("docker", "compose", "version").ConfigureAwait(false)).Ok
            ? StepOutcome.Done("docker compose is available")
            : StepOutcome.Failed("docker compose is still missing", "Update Docker Desktop."));

    private static Prerequisite Buildx() => new(
        "docker-buildx",
        "Docker Buildx",
        "The server image is built with the deploy project's Dockerfile, which uses BuildKit " +
        "instructions (COPY --chmod and --exclude), so only Buildx can build it.",
        Required: true,
        Probe: async ctx => (await ctx.Capture("docker", "buildx", "version").ConfigureAwait(false)).Ok
            ? PrerequisiteStatus.Yes("docker buildx answers")
            : PrerequisiteStatus.No("the docker buildx plugin is missing"),
        InstallFix: _ => "Docker Desktop ships it; update Docker Desktop.",
        Install: async ctx => (await ctx.Capture("docker", "buildx", "version").ConfigureAwait(false)).Ok
            ? StepOutcome.Done("docker buildx is available")
            : StepOutcome.Failed("docker buildx is still missing", "Update Docker Desktop."));

    private static Prerequisite Git() => new(
        "git",
        "Git",
        "Fetches the server project and the Skate 3 converters, each at the revision this setup pins.",
        Required: true,
        Probe: async ctx => await ctx.Tools.HasAsync("git").ConfigureAwait(false)
            ? PrerequisiteStatus.Yes("git found", await CapturePath(ctx, "git", "--version").ConfigureAwait(false))
            : PrerequisiteStatus.No("git is not installed"),
        InstallFix: _ => "winget install -e --id Git.Git",
        Install: InstallGit);

    private static Prerequisite Python() => new(
        "python",
        "Python 3",
        "Runs the Skate 3 converters, which read the player's own Skate 3 files and write the " +
        "skater, animations and physics the skate engine loads.",
        Required: true,
        Probe: async ctx => (await ctx.Capture(Tool(ctx, "python", "-c", "import venv, ensurepip"))
                .ConfigureAwait(false)).Ok
            ? PrerequisiteStatus.Yes("python 3 with venv",
                await CapturePath(ctx, "python", "--version").ConfigureAwait(false))
            : PrerequisiteStatus.No("Python 3 with venv is missing"),
        InstallFix: _ => "winget install -e --id Python.Python.3.12 — and if \"python\" opens the " +
                         "Microsoft Store, turn its app execution alias off in Settings.",
        Install: InstallPython);

    private static Prerequisite Rust(bool needed) => new(
        "rust",
        "Rust toolchain",
        needed
            ? "Builds the game client from the project's source. This package has no prebuilt client, " +
              "so the build happens here, on your machine."
            : "Not needed: this package ships a prebuilt client.",
        Required: needed,
        Probe: async ctx => await ctx.Tools.HasAsync("cargo").ConfigureAwait(false)
            ? PrerequisiteStatus.Yes("cargo found", await CapturePath(ctx, "cargo", "--version").ConfigureAwait(false))
            : PrerequisiteStatus.No("Rust (cargo) is not installed"),
        InstallFix: _ => "winget install -e --id Rustlang.Rustup, then run setup again (a new shell " +
                         "may be needed for cargo to be on the PATH).",
        Install: InstallRust) { Optional = !needed, FatalWhenMissing = needed };

    private static Prerequisite Compiler(bool needed) => new(
        "msvc",
        "Visual Studio C++ build tools",
        needed
            ? "The linker the Rust client build needs on Windows. It is a few GB and installs with " +
              "an administrator's approval."
            : "Not needed: this package ships a prebuilt client.",
        Required: needed,
        Probe: async ctx => !string.IsNullOrWhiteSpace(await VcToolsPath(ctx).ConfigureAwait(false))
            ? PrerequisiteStatus.Yes("the MSVC x64 tools are installed",
                await VcToolsPath(ctx).ConfigureAwait(false))
            : PrerequisiteStatus.No("the MSVC C++ build tools are missing"),
        InstallFix: _ => "winget install -e --id Microsoft.VisualStudio.2022.BuildTools --override \"" +
                         Prerequisites.BuildToolsArguments + "\"",
        Install: InstallCompiler) { Optional = !needed, FatalWhenMissing = needed };

    /// <summary>The MSVC toolchain's install folder as vswhere reports it, or an empty string.</summary>
    private static async Task<string> VcToolsPath(SetupContext ctx)
    {
        var vswhere = await ctx.Tools.FindAsync("vswhere").ConfigureAwait(false);
        if (vswhere is null)
        {
            return "";
        }
        var result = await ctx.Capture(new CommandSpec(
            vswhere,
            new[]
            {
                "-products", "*",
                "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "-property", "installationPath",
            })).ConfigureAwait(false);
        return result.Ok ? result.StdOut.Trim() : "";
    }

    /// <summary>Where setup keeps the installers it downloads.</summary>
    public static string Downloads(SetupContext ctx) => Path.Combine(ctx.Paths.Tool, "downloads");

    /// <summary>Installs a package with winget; false when winget is absent or declined.</summary>
    private static async Task<bool> Winget(SetupContext ctx, string id, string? overrideArguments = null)
    {
        if (!await ctx.Tools.HasAsync("winget").ConfigureAwait(false))
        {
            ctx.Say("winget is not available on this PC; using the vendor's own installer instead");
            return false;
        }
        var arguments = new List<string>
        {
            "install", "-e", "--id", id,
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (overrideArguments is not null)
        {
            arguments.Add("--override");
            arguments.Add(overrideArguments);
        }
        await ctx.Run(new CommandSpec(
            "winget", arguments, null, ctx.ChildEnvironment, $"installs {id}")).ConfigureAwait(false);
        // winget reports "already installed" as a failure exit code, so the probe decides, not this.
        ctx.Tools.ForgetAll();
        return true;
    }

    private static async Task<StepOutcome> InstallDockerDesktop(SetupContext ctx)
    {
        ctx.Say("Installing Docker Desktop (several hundred MB, and it asks for administrator)");
        await Winget(ctx, "Docker.DockerDesktop").ConfigureAwait(false);
        if (!await ctx.Tools.HasAsync("docker").ConfigureAwait(false))
        {
            var installer = Path.Combine(Downloads(ctx), "DockerDesktopInstaller.exe");
            if (!await ctx.Download(Prerequisites.DockerDesktopUrl, installer, "Docker Desktop")
                    .ConfigureAwait(false))
            {
                return StepOutcome.Failed(
                    "Docker Desktop could not be downloaded",
                    $"Install it yourself from {Prerequisites.DockerDesktopUrl}, then run setup again.");
            }
            var result = await ctx.Run(new CommandSpec(
                installer,
                new[] { "install", "--quiet", "--accept-license", "--backend=wsl-2" },
                null,
                ctx.ChildEnvironment,
                "installs Docker Desktop")).ConfigureAwait(false);
            if (!result.Ok)
            {
                return StepOutcome.Failed(
                    "Docker Desktop's installer did not finish",
                    "Run it yourself (it needs administrator rights and, the first time, WSL 2 and " +
                    "virtualization enabled in the BIOS), then run setup again.");
            }
        }
        // A fresh install cannot answer before Windows restarts; the docker-ready step reports that.
        return await ctx.Tools.HasAsync("docker").ConfigureAwait(false)
            ? StepOutcome.Done("Docker Desktop is installed")
            : StepOutcome.Warning(
                "Docker Desktop was installed and needs a Windows restart before it can run",
                "Restart Windows, start Docker Desktop once and wait for \"engine running\", then " +
                "run setup again: every finished step is skipped.");
    }

    private static async Task<StepOutcome> StartDockerDesktop(SetupContext ctx)
    {
        var exe = DockerDesktopExecutable(ctx);
        if (exe is null)
        {
            return StepOutcome.Failed(
                "Docker Desktop's program was not found",
                $"Install Docker Desktop from {Prerequisites.DockerDesktopUrl}, then run setup again.");
        }
        ctx.Say("Starting Docker Desktop and waiting for its engine");
        ctx.StartDetached(exe);
        return await WaitForEngine(ctx).ConfigureAwait(false)
            ? StepOutcome.Done("the Docker engine is running")
            : StepOutcome.Failed(
                "Docker Desktop did not report a running engine in six minutes",
                "Start Docker Desktop yourself from the Start menu, wait until it says the engine is " +
                "running, then run setup again.");
    }

    /// <summary>Where Docker Desktop's program is, or null.</summary>
    public static string? DockerDesktopExecutable(SetupContext ctx)
    {
        var candidate = Path.Combine(
            ctx.Host.GetFolder(KnownFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Polls the engine until it answers, saying how long it has been waiting.</summary>
    public static async Task<bool> WaitForEngine(SetupContext ctx)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < DockerStartTimeout)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (await ctx.Docker.IsRunningAsync(ctx.Cancellation).ConfigureAwait(false))
            {
                return true;
            }
            ctx.Report(
                null,
                $"waiting for the Docker engine ({(int)(DateTime.UtcNow - started).TotalSeconds}s)");
            await Task.Delay(TimeSpan.FromSeconds(5), ctx.Cancellation).ConfigureAwait(false);
        }
        return await ctx.Docker.IsRunningAsync(ctx.Cancellation).ConfigureAwait(false);
    }

    private static async Task<StepOutcome> InstallGit(SetupContext ctx)
    {
        ctx.Say("Installing Git");
        await Winget(ctx, "Git.Git").ConfigureAwait(false);
        if (await ctx.Tools.HasAsync("git").ConfigureAwait(false))
        {
            return StepOutcome.Done("Git is installed");
        }
        var installer = Path.Combine(Downloads(ctx), "git-installer.exe");
        if (!await ctx.Download(Prerequisites.GitUrl, installer, "Git for Windows").ConfigureAwait(false))
        {
            return StepOutcome.Failed(
                "Git could not be downloaded", $"Install it from {Prerequisites.GitUrl}, then run setup again.");
        }
        var result = await ctx.Run(new CommandSpec(
            installer,
            new[] { "/VERYSILENT", "/NORESTART", "/NOCANCEL", "/SP-" },
            null,
            ctx.ChildEnvironment,
            "installs Git")).ConfigureAwait(false);
        ctx.Tools.ForgetAll();
        return result.Ok && await ctx.Tools.HasAsync("git").ConfigureAwait(false)
            ? StepOutcome.Done("Git is installed")
            : StepOutcome.Failed(
                "Git's installer did not finish", $"Install Git yourself from {Prerequisites.GitUrl}.");
    }

    private static async Task<StepOutcome> InstallPython(SetupContext ctx)
    {
        ctx.Say("Installing Python 3");
        await Winget(ctx, "Python.Python.3.12").ConfigureAwait(false);
        if (!(await ctx.Capture(Tool(ctx, "python", "-c", "import venv, ensurepip")).ConfigureAwait(false)).Ok)
        {
            var installer = Path.Combine(Downloads(ctx), "python-installer.exe");
            if (!await ctx.Download(Prerequisites.PythonUrl, installer, "Python 3.12").ConfigureAwait(false))
            {
                return StepOutcome.Failed(
                    "Python could not be downloaded",
                    $"Install Python 3.12 yourself from {Prerequisites.PythonUrl} (tick \"Add python.exe " +
                    "to PATH\"), then run setup again.");
            }
            // Per-user and on the PATH: no administrator, and the converters find python themselves.
            var result = await ctx.Run(new CommandSpec(
                installer,
                new[] { "/quiet", "InstallAllUsers=0", "PrependPath=1", "Include_launcher=1", "Include_pip=1" },
                null,
                ctx.ChildEnvironment,
                "installs Python 3.12")).ConfigureAwait(false);
            if (!result.Ok)
            {
                return StepOutcome.Failed(
                    "Python's installer did not finish", $"Install Python 3.12 from {Prerequisites.PythonUrl}.");
            }
        }
        ctx.Tools.ForgetAll();
        return (await ctx.Capture(Tool(ctx, "python", "-c", "import venv, ensurepip")).ConfigureAwait(false)).Ok
            ? StepOutcome.Done("Python 3 is installed")
            : StepOutcome.Failed(
                "Python is installed but its venv module does not answer",
                "If \"python\" opens the Microsoft Store, turn its app execution alias off in Settings " +
                "(Apps > Advanced app settings > App execution aliases), then run setup again.");
    }

    private static async Task<StepOutcome> InstallRust(SetupContext ctx)
    {
        ctx.Say("Installing the Rust toolchain (per-user, no administrator needed)");
        var installer = Path.Combine(Downloads(ctx), "rustup-init.exe");
        if (!await ctx.Download(Prerequisites.RustupInitUrl, installer, "rustup").ConfigureAwait(false))
        {
            return StepOutcome.Failed(
                "rustup could not be downloaded",
                $"Install Rust from {Prerequisites.RustupInitUrl} (or winget install -e --id Rustlang.Rustup), " +
                "then run setup again.");
        }
        var result = await ctx.Run(new CommandSpec(
            installer,
            new[]
            {
                "-y",
                "--no-modify-path",
                "--default-toolchain", "stable-x86_64-pc-windows-msvc",
                "--profile", "minimal",
                "--component", "rustfmt",
                "--component", "clippy",
            },
            null,
            ctx.ChildEnvironment,
            "installs Rust")).ConfigureAwait(false);
        ctx.Tools.ForgetAll();
        if (!result.Ok)
        {
            return StepOutcome.Failed("rustup's installer did not finish", "Run it yourself, then run setup again.");
        }
        return await ctx.Tools.HasAsync("cargo").ConfigureAwait(false)
            ? StepOutcome.Done("Rust is installed")
            : StepOutcome.Failed(
                "Rust installed but cargo was not found",
                $"cargo should be at {Path.Combine(ctx.Host.GetFolder(KnownFolder.UserProfile), ".cargo", "bin")}; " +
                "if it is not, install Rust with winget install -e --id Rustlang.Rustup and run setup again.");
    }

    private static async Task<StepOutcome> InstallCompiler(SetupContext ctx)
    {
        ctx.Say("Installing the C++ build tools (a few GB; Windows will ask for administrator)");
        await Winget(ctx, "Microsoft.VisualStudio.2022.BuildTools", Prerequisites.BuildToolsArguments)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(await VcToolsPath(ctx).ConfigureAwait(false)))
        {
            var installer = Path.Combine(Downloads(ctx), "vs_BuildTools.exe");
            if (!await ctx.Download(Prerequisites.BuildToolsUrl, installer, "the C++ build tools")
                    .ConfigureAwait(false))
            {
                return StepOutcome.Failed(
                    "The C++ build tools could not be downloaded",
                    $"Install them yourself: {Prerequisites.BuildToolsUrl} with the arguments " +
                    $"\"{Prerequisites.BuildToolsArguments}\", then run setup again.");
            }
            var result = await ctx.Run(new CommandSpec(
                installer,
                Prerequisites.BuildToolsArguments.Split(' '),
                null,
                ctx.ChildEnvironment,
                "installs the MSVC x64 tools")).ConfigureAwait(false);
            if (!result.Ok)
            {
                return StepOutcome.Failed(
                    "The C++ build tools' installer did not finish",
                    $"Run {Prerequisites.BuildToolsUrl} with \"{Prerequisites.BuildToolsArguments}\", " +
                    "then run setup again.");
            }
        }
        return !string.IsNullOrWhiteSpace(await VcToolsPath(ctx).ConfigureAwait(false))
            ? StepOutcome.Done("the C++ build tools are installed")
            : StepOutcome.Failed(
                "The C++ build tools are still missing after installing them",
                $"Install them with winget install -e --id Microsoft.VisualStudio.2022.BuildTools --override " +
                $"\"{Prerequisites.BuildToolsArguments}\", then run setup again.");
    }
}
