namespace WorldOfSkatecraft.Setup.Core;

/// <summary>A program the rest of the setup needs: how to see it, and how to get it.</summary>
public sealed record Prerequisite(
    string Id,
    string Title,
    string Why,
    bool Required,
    Func<SetupContext, Task<PrerequisiteStatus>> Probe,
    Func<SetupContext, string?> InstallFix,
    Func<SetupContext, Task<StepOutcome>>? Install = null)
{
    /// <summary>Whether a missing one, that cannot be installed automatically, stops the install.</summary>
    public bool FatalWhenMissing { get; init; } = true;

    /// <summary>Notes an unmet prerequisite only in the report rather than failing the step.</summary>
    public bool Optional { get; init; }
}

/// <summary>What a probe found.</summary>
public sealed record PrerequisiteStatus(bool Present, string Detail, string? Version = null)
{
    public static PrerequisiteStatus Yes(string detail, string? version = null) => new(true, detail, version);

    public static PrerequisiteStatus No(string detail) => new(false, detail);
}

/// <summary>
/// The programs the install needs, and how to install each. Everything here is installed from its
/// authors' own source or a package manager, never redistributed in the package: Docker Desktop,
/// the compiler toolchain, Rust and Python are all downloaded from the vendor on the player's own
/// machine, per-user where the vendor allows it.
/// </summary>
public static class Prerequisites
{
    /// <summary>The Docker Desktop download, used when winget is unavailable or declines.</summary>
    public const string DockerDesktopUrl =
        "https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe";

    /// <summary>The C++ build tools' bootstrapper, the one INSTALL.md documents.</summary>
    public const string BuildToolsUrl = "https://aka.ms/vs/17/release/vs_BuildTools.exe";

    /// <summary>Rustup's installer, which installs Rust per-user with no administrator.</summary>
    public const string RustupInitUrl =
        "https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe";

    /// <summary>Python's per-user installer, used when winget is unavailable.</summary>
    public const string PythonUrl =
        "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";

    /// <summary>Git for Windows' installer, used when winget is unavailable.</summary>
    public const string GitUrl =
        "https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/Git-2.47.1-64-bit.exe";

    /// <summary>The VS Build Tools workload arguments.</summary>
    public const string BuildToolsArguments =
        "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended";

    /// <summary>
    /// The list for this install. <paramref name="needsCompiler"/> is false when the package carries
    /// a prebuilt client, which is the point of shipping one: no Rust and no C++ toolchain then.
    /// </summary>
    public static IReadOnlyList<Prerequisite> For(bool needsCompiler) =>
        WindowsPrerequisites.All(needsCompiler);
}
