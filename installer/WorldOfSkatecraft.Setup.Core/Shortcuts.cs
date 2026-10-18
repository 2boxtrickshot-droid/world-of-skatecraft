using System.Runtime.InteropServices;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>Where a shortcut goes.</summary>
public enum ShortcutPlace
{
    Desktop,
    StartMenu,
}

/// <summary>Creates and finds the launcher's shortcuts. Faked by the tests, and off on Linux.</summary>
public interface IShortcuts
{
    /// <summary>Whether a shortcut of that name exists.</summary>
    bool Exists(string name);

    /// <summary>Creates one; returns an empty string on success, and a reason otherwise.</summary>
    string Create(
        string name,
        string target,
        string arguments,
        string workingDirectory,
        string iconSource,
        ShortcutPlace place,
        SetupLog log);
}

/// <summary>The shortcut names the install is known by, and how it makes them.</summary>
public static class Shortcuts
{
    /// <summary>The name on the desktop: one Play button, which is what was asked for.</summary>
    public const string DesktopName = "World of Skatecraft";

    /// <summary>The shortcuts for this machine: Windows' own, or none at all.</summary>
    public static IShortcuts For(IHostEnvironment host) =>
        host.IsWindows ? new WindowsShortcuts(host) : new NullShortcuts();
}

/// <summary>No shortcuts: a machine that is not Windows, or a test.</summary>
public sealed class NullShortcuts : IShortcuts
{
    /// <summary>What a test asked to be created, in order.</summary>
    public List<(string Name, string Target, string Arguments, ShortcutPlace Place)> Created { get; } = new();

    public bool Exists(string name) => false;

    public string Create(
        string name,
        string target,
        string arguments,
        string workingDirectory,
        string iconSource,
        ShortcutPlace place,
        SetupLog log)
    {
        Created.Add((name, target, arguments, place));
        log.Line($"shortcut (not on this platform): {name} -> {target} {arguments}");
        return "";
    }
}

/// <summary>
/// The Windows shortcuts, through the shell's own <c>IShellLink</c>: the same thing a right-click
/// and "Create shortcut" makes, so the desktop, the Start menu and Explorer's search all see it.
/// </summary>
public sealed class WindowsShortcuts : IShortcuts
{
    private readonly IHostEnvironment _host;

    public WindowsShortcuts(IHostEnvironment host) => _host = host;

    /// <summary>Where a shortcut of this name lives, one of the two places setup writes.</summary>
    public string PathFor(string name, ShortcutPlace place) => Path.Combine(
        place == ShortcutPlace.Desktop
            ? _host.GetFolder(KnownFolder.Desktop)
            : _host.GetFolder(KnownFolder.StartMenuPrograms),
        name + ".lnk");

    public bool Exists(string name) =>
        File.Exists(PathFor(name, ShortcutPlace.Desktop))
        || File.Exists(PathFor(name, ShortcutPlace.StartMenu));

    public string Create(
        string name,
        string target,
        string arguments,
        string workingDirectory,
        string iconSource,
        ShortcutPlace place,
        SetupLog log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "the shell's shortcuts exist on Windows only";
        }
        var path = PathFor(name, place);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var classId = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
                ?? throw new InvalidOperationException("the shell link class is not registered");
            var link = (IShellLinkW)Activator.CreateInstance(classId)!;
            link.SetPath(Path.GetFullPath(target));
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription("World of Skatecraft: starts the local server and then the game");
            if (File.Exists(iconSource))
            {
                link.SetIconLocation(iconSource, 0);
            }
            ((IPersistFileW)link).Save(path, true);
            log.Line($"shortcut: {path} -> {target} {arguments}");
            return "";
        }
        catch (Exception error) when (error is COMException or FileNotFoundException or InvalidCastException)
        {
            log.Line($"shortcut failed: {error.Message}");
            return error.Message;
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr data, int flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFileW
    {
        void GetClassID(out Guid classId);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
