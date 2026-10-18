using System.Runtime.InteropServices;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>The folders and environment of the machine the setup runs on.</summary>
public sealed class HostEnvironment : IHostEnvironment
{
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string GetFolder(KnownFolder folder) => folder switch
    {
        KnownFolder.LocalApplicationData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        KnownFolder.Desktop =>
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        KnownFolder.StartMenuPrograms =>
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        KnownFolder.UserProfile =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        KnownFolder.ProgramFiles =>
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "unknown folder"),
    };

    public string? GetEnvironment(string name) => Environment.GetEnvironmentVariable(name);

    public double FreeSpaceGigabytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return double.MaxValue;
            }
            return new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        }
        catch (Exception)
        {
            return double.MaxValue; // an unknown figure is not a reason to refuse an install
        }
    }
}

/// <summary>A host for a machine that is not Windows: the tests and the dry run use it.</summary>
public sealed class TestHostEnvironment : IHostEnvironment
{
    private readonly Dictionary<KnownFolder, string> _folders = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.OrdinalIgnoreCase);

    public TestHostEnvironment(bool isWindows, string root)
    {
        IsWindows = isWindows;
        _folders[KnownFolder.LocalApplicationData] = Path.Combine(root, "AppData", "Local");
        _folders[KnownFolder.Desktop] = Path.Combine(root, "Desktop");
        _folders[KnownFolder.StartMenuPrograms] = Path.Combine(root, "StartMenu");
        _folders[KnownFolder.UserProfile] = root;
        _folders[KnownFolder.ProgramFiles] = Path.Combine(root, "ProgramFiles");
        foreach (var folder in _folders.Values)
        {
            Directory.CreateDirectory(folder);
        }
    }

    public bool IsWindows { get; }

    /// <summary>Free space the tests answer with, in gigabytes.</summary>
    public double FreeSpace { get; set; } = 500;

    public string GetFolder(KnownFolder folder) => _folders[folder];

    public string? GetEnvironment(string name) => _environment.GetValueOrDefault(name);

    public double FreeSpaceGigabytes(string path) => FreeSpace;

    /// <summary>Sets an environment variable the setup will read.</summary>
    public void SetEnvironment(string name, string value) => _environment[name] = value;
}
