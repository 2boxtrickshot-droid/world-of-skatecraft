using System.Text;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// The doctor: one text report of what this machine is, what the install is, and what every
/// prerequisite says. It is what a bug report carries, and what the wizard shows when a step fails.
/// </summary>
public sealed class Doctor
{
    private readonly SetupContext _ctx;

    public Doctor(SetupContext context) => _ctx = context;

    /// <summary>The report, as text.</summary>
    public async Task<string> ReportAsync()
    {
        var text = new StringBuilder();
        void Line(string message) => text.AppendLine(message);

        Line("World of Skatecraft — diagnostics");
        Line($"package version : {_ctx.PackageVersion}");
        Line($"install root    : {_ctx.Paths.Root}");
        Line($"log             : {_ctx.Paths.LogFile}");
        Line($"time            : {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.Id})");
        Line($"os              : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Line($"runtime         : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Line($"free space      : {_ctx.Host.FreeSpaceGigabytes(_ctx.Paths.Root):F1} GB at the install root");
        Line($"container zone  : {_ctx.TimeZone}");
        Line($"pinned server   : vmangos-deploy {Upstreams.ServerRev[..12]}, deploy version {Upstreams.DeployVersion}");
        Line($"pinned images   : {Upstreams.ServerImage}");
        Line($"                : {Upstreams.DatabaseImage}");
        Line($"pinned engine   : skate-3-rust-engine {Upstreams.EngineRev}");
        Line("");

        Line("What the player chose");
        Line($"  WoW folder    : {_ctx.State.WowFolder}");
        Line($"  Skate 3 folder: {_ctx.State.SkateFolder}");
        Line($"  account       : {_ctx.State.Account} (GM level {_ctx.State.AccountGmLevel})");
        Line($"  patched server: {_ctx.State.BuildPatchedServer}");
        Line($"  stop on exit  : {_ctx.State.StopServerOnExit}");
        Line($"  build jobs    : {_ctx.State.BuildJobs}");
        Line("");

        Line("The player's own files");
        WriteCheck(Line, "WoW", GameFiles.CheckWow(_ctx.State.WowFolder));
        WriteCheck(Line, "Skate 3", GameFiles.CheckSkate(_ctx.State.SkateFolder));
        Line("");
        await WriteEnvironmentAsync(Line).ConfigureAwait(false);
        await WriteDockerAsync(Line).ConfigureAwait(false);
        Line("The last 40 lines of the setup log");
        if (File.Exists(_ctx.Paths.LogFile))
        {
            foreach (var line in File.ReadAllLines(_ctx.Paths.LogFile).TakeLast(40))
            {
                Line("  " + line);
            }
        }
        else
        {
            Line("  (no log yet)");
        }
        Line("");
        Line("If you are asking for help, post this file together with the log beside it.");
        return text.ToString();
    }

    /// <summary>Writes the report to <c>state/diagnostics.txt</c> and returns its path.</summary>
    public async Task<string> WriteAsync()
    {
        var text = await ReportAsync().ConfigureAwait(false);
        Directory.CreateDirectory(_ctx.Paths.State);
        await File.WriteAllTextAsync(_ctx.Paths.DiagnosticsFile, text).ConfigureAwait(false);
        return _ctx.Paths.DiagnosticsFile;
    }

    /// <summary>One of the player's own folders, with what its check found.</summary>
    private static void WriteCheck(Action<string> line, string what, ValidationResult check)
    {
        line($"  {what} files : {check.Summary()}");
        foreach (var fact in check.Facts)
        {
            line($"    {fact}");
        }
        foreach (var warning in check.Warnings)
        {
            line($"    note: {warning}");
        }
        foreach (var error in check.Errors)
        {
            line($"    problem: {error}");
        }
    }

    /// <summary>Every prerequisite, and the fix line for each that is missing.</summary>
    private async Task WriteEnvironmentAsync(Action<string> line)
    {
        line("Required programs");
        foreach (var prerequisite in Prerequisites.For(!SetupPlan.HasPrebuiltClient(_ctx)))
        {
            PrerequisiteStatus status;
            try
            {
                status = await prerequisite.Probe(_ctx).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                status = PrerequisiteStatus.No("the check failed: " + error.Message);
            }
            var label = status.Present ? "ok     " : prerequisite.Required ? "MISSING" : "absent ";
            line($"  {label} {prerequisite.Title}: {status.Detail}");
            if (!status.Present && prerequisite.Required)
            {
                line($"           fix: {prerequisite.InstallFix(_ctx)}");
            }
        }
        line("");
        line("What the install holds");
        foreach (var (name, path) in new (string, string)[]
        {
            ("client", _ctx.Paths.ClientExe),
            ("skate assets", Path.Combine(_ctx.Paths.SkateAssets, "private", "skater.glb")),
            ("skate sounds", Path.Combine(_ctx.Paths.SkateAudio, "pop_1.wav")),
            ("compose file", _ctx.Paths.ComposeFile),
            ("server maps", Path.Combine(_ctx.Paths.ExtractedData, "maps")),
            ("project source", Path.Combine(_ctx.Paths.Source, "Cargo.toml")),
        })
        {
            var exists = File.Exists(path) || Directory.Exists(path);
            line($"  {(exists ? "yes" : "no ")}  {name}: {path}");
        }
        if (File.Exists(_ctx.Paths.ComposeFile))
        {
            var compose = File.ReadAllText(_ctx.Paths.ComposeFile);
            line($"  server image : {ComposeWriter.ServerImageOf(compose)}");
            line($"  deploy version 1 present: {ComposeWriter.IsVersionOne(compose)}");
        }
        line("");
    }

    /// <summary>The Docker engine, the project's containers and the login port.</summary>
    private async Task WriteDockerAsync(Action<string> line)
    {
        line("Docker");
        var version = await _ctx.Capture("docker", "version", "--format", "{{.Server.Version}}")
            .ConfigureAwait(false);
        line($"  docker engine : {(version.Ok ? version.StdOut.Trim() : "not answering")}");
        if (version.Ok && File.Exists(_ctx.Paths.ComposeFile))
        {
            var ps = await _ctx.Capture(_ctx.Docker.Compose("ps")).ConfigureAwait(false);
            line("  containers    :");
            foreach (var entry in ps.Output.Split('\n').Take(8))
            {
                line("    " + entry.TrimEnd());
            }
            foreach (var service in new[] { "database", "realmd", "mangosd" })
            {
                var health = await _ctx.Docker.HealthAsync(service, _ctx.Cancellation).ConfigureAwait(false);
                line($"  {service,-8} health: {health ?? "no container"}");
            }
        }
        var port = await Net
            .IsPortOpenAsync("127.0.0.1", 3724, TimeSpan.FromSeconds(3), _ctx.Cancellation)
            .ConfigureAwait(false);
        line($"  login port    : {(port ? "3724 answers" : "3724 is silent")}");
        line("");
    }
}
