using WorldOfSkatecraft.Setup.Core;

namespace WorldOfSkatecraft.Setup.Cli;

/// <summary>
/// The headless driver for the setup engine. It is how the plan is checked on a machine that is not
/// Windows: `replay` walks the whole plan against fake WoW and Skate 3 folders with a runner that
/// records commands instead of running them, and prints what would happen.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var command = args.Length == 0 ? "help" : args[0];
        var options = Parse(args.Skip(1));
        try
        {
            return command switch
            {
                "replay" => await ReplayAsync(options).ConfigureAwait(false),
                "check" => Check(options),
                "compose" => Compose(options),
                "credentials" => Credentials(options),
                "plan" => await PlanAsync(options).ConfigureAwait(false),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command),
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"failed: {error.Message}");
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            wosc-setup — the World of Skatecraft setup engine, headless.

              replay                                   walk the whole plan with fake game folders
              check --wow DIR --skate DIR              validate the player's own game folders
              compose                                  print the Compose file the setup generates
              credentials --user NAME --password PW    print the SRP6 salt, verifier and account SQL
              plan [--root DIR] [--wow DIR] [--skate DIR] [--dry-run] [--repair]
                                                       walk the plan for real (the machine being
                                                       installed on; Windows only)

            replay takes --root DIR, --prebuilt (the package carries a client) and --fresh
            (rehearse a PC that has none of the required programs yet).
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command \"{command}\"; try `wosc-setup help`");
        return 2;
    }

    private static int Check(Dictionary<string, string> options)
    {
        var wowCheck = GameFiles.CheckWow(options.GetValueOrDefault("wow") ?? "");
        var skateCheck = GameFiles.CheckSkate(options.GetValueOrDefault("skate") ?? "");
        Report("World of Warcraft", wowCheck);
        Report("Skate 3", skateCheck);
        return wowCheck.Ok && skateCheck.Ok ? 0 : 1;
    }

    private static void Report(string what, ValidationResult check)
    {
        Console.WriteLine($"{what}: {check.Summary()}");
        foreach (var fact in check.Facts)
        {
            Console.WriteLine($"  {fact}");
        }
        foreach (var warning in check.Warnings)
        {
            Console.WriteLine($"  note: {warning}");
        }
        foreach (var error in check.Errors)
        {
            Console.WriteLine($"  problem: {error}");
        }
    }

    private static int Compose(Dictionary<string, string> options)
    {
        var image = options.GetValueOrDefault("image") ?? Upstreams.PatchedImage("0123456789ab");
        Console.Write(ComposeWriter.Render(image, Upstreams.DatabaseImage, "Etc/UTC"));
        return 0;
    }

    private static int Credentials(Dictionary<string, string> options)
    {
        var user = options.GetValueOrDefault("user") ?? "SKATEPROBE";
        var password = options.GetValueOrDefault("password") ?? "Skate123";
        var sql = Account.CreateAccountSql(user, password);
        var values = sql.Split("VALUES (")[1];
        Console.WriteLine($"user     : {user}");
        Console.WriteLine($"salt     : {values.Split("', '")[1]}");
        Console.WriteLine($"verifier : {values.Split("', '")[2].Split('\'')[0]}");
        Console.WriteLine($"sql      : {sql}");
        return 0;
    }

    /// <summary>The rehearsal: the whole plan, twice, on a machine with neither game.</summary>
    private static async Task<int> ReplayAsync(Dictionary<string, string> options)
    {
        var root = options.GetValueOrDefault("root")
                   ?? Path.Combine(Path.GetTempPath(), "wosc-replay-" + Guid.NewGuid().ToString("N")[..8]);
        var withPrebuilt = options.ContainsKey("prebuilt");
        var fresh = options.ContainsKey("fresh");
        Console.WriteLine($"replaying the plan in {root}{(withPrebuilt ? " (the package carries a client)" : "")}");
        Console.WriteLine();

        var rehearsal = new Rehearsal(root);
        rehearsal.PrimeRunner(fresh);
        var package = rehearsal.MakePackage(withPrebuilt);
        var wow = rehearsal.MakeWowFolder();
        var skate = rehearsal.MakeSkateFolder();
        rehearsal.MakeServerCheckout();
        var context = rehearsal.Context(
            new SetupOptions { DryRun = true, KeepGoingAfterFailure = true },
            wow,
            skate,
            package);
        var plan = SetupPlan.Build(context);

        Console.WriteLine($"{plan.Count} steps:");
        foreach (var step in plan)
        {
            Console.WriteLine($"  {step.Id,-16} {step.Phase,-14} {step.Title}");
        }
        Console.WriteLine();

        var engine = new SetupEngine(context);
        var progress = new InlineProgress(update =>
        {
            if (update.Result is null)
            {
                return;
            }
            var status = update.Result.Skipped
                ? "already done"
                : update.Result.Outcome.Ok ? "ok" : "FAILED";
            var fix = update.Result.Outcome.Fix is { } text ? $"  fix: {text}" : "";
            Console.WriteLine(
                $"[{update.Index + 1}/{update.Total}] {update.Step.Id}: {status} — {update.Result.Outcome.Message}{fix}");
            if (update.Result.Outcome.Ok && !update.Result.Skipped)
            {
                rehearsal.SimulateArtifacts(update.Step.Id);
            }
        });

        var first = await engine.RunAsync(plan, progress).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("commands the setup would run:");
        foreach (var spec in rehearsal.Runner.Commands)
        {
            Console.WriteLine("  " + spec.Display());
        }
        Console.WriteLine();
        Console.WriteLine("files the setup would download:");
        foreach (var (url, destination) in rehearsal.Downloader.Requests)
        {
            Console.WriteLine($"  {url} -> {Path.GetFileName(destination)}");
        }
        Console.WriteLine($"shortcuts: {string.Join(", ", rehearsal.Shortcuts.Created.Select(s => $"{s.Name} ({s.Place})"))}");
        Console.WriteLine();

        // A second walk: anything now in place must be skipped, which is what makes a re-run after
        // a failure cheap and a repair honest.
        Console.WriteLine("second walk (everything already in place):");
        var again = await engine.RunAsync(plan, progress).ConfigureAwait(false);
        foreach (var step in again.Steps.Where(step => !step.Skipped))
        {
            Console.WriteLine($"  re-ran {step.Step.Id}: {step.Outcome.Message}");
        }
        Console.WriteLine($"  {again.Steps.Count(step => step.Skipped)}/{again.Steps.Count} steps skipped");
        Console.WriteLine();
        Console.WriteLine($"first walk : {first.Summary()}");
        Console.WriteLine($"second walk: {again.Summary()}");

        var failed = first.Failures.Concat(again.Failures).ToList();
        if (failed.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("REPLAY OK");
            return 0;
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine("REPLAY FAILED:");
        foreach (var report in failed)
        {
            Console.Error.WriteLine($"  {report.Step.Id}: {report.Outcome.Message}");
            if (report.Outcome.Fix is { } fix)
            {
                Console.Error.WriteLine($"    fix: {fix}");
            }
        }
        return 1;
    }
    /// <summary>The plan, for real, on the machine being installed on.</summary>
    private static async Task<int> PlanAsync(Dictionary<string, string> options)
    {
        var host = new HostEnvironment();
        if (!host.IsWindows && !options.ContainsKey("root"))
        {
            Console.Error.WriteLine("a real plan is for the machine being installed on; use replay here");
            return 2;
        }
        var paths = new SetupPaths(options.GetValueOrDefault("root") ?? SetupPaths.DefaultRoot(host));
        var state = SetupState.Load(paths);
        state.WowFolder = options.GetValueOrDefault("wow") ?? state.WowFolder;
        state.SkateFolder = options.GetValueOrDefault("skate") ?? state.SkateFolder;
        var dryRun = options.ContainsKey("dry-run");
        var context = new SetupContext(
            state,
            paths,
            host,
            dryRun ? new RecordingProcessRunner() : new ProcessRunner(),
            dryRun ? new RecordingDownloader() : new HttpDownloader(),
            new SetupLog(paths.LogFile, Console.WriteLine),
            new SetupOptions
            {
                DryRun = dryRun,
                Repair = options.ContainsKey("repair"),
            })
        {
            Package = Package.Discover(),
            PackageVersion = "cli",
        };
        if (options.TryGetValue("account", out var account))
        {
            state.Account = account;
            context.AccountPassword = options.GetValueOrDefault("password") ?? "";
        }
        var plan = SetupPlan.Build(context);
        var engine = new SetupEngine(context);
        var report = await engine.RunAsync(
            plan,
            new InlineProgress(update => Console.WriteLine(update.Result is null
                ? $"==> {update.Step.Title}"
                : $"    {update.Result.Outcome.Message}"))).ConfigureAwait(false);
        await new Doctor(context).WriteAsync().ConfigureAwait(false);
        Console.WriteLine(report.Summary());
        return report.Ok ? 0 : 1;
    }

    private sealed class InlineProgress : IProgress<SetupProgress>
    {
        private readonly Action<SetupProgress> _action;

        public InlineProgress(Action<SetupProgress> action) => _action = action;

        public void Report(SetupProgress value) => _action(value);
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        foreach (var argument in args)
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    options[pending] = "";
                }
                pending = argument[2..];
                continue;
            }
            if (pending is not null)
            {
                options[pending] = argument;
                pending = null;
            }
        }
        if (pending is not null)
        {
            options[pending] = "";
        }
        return options;
    }
}
