namespace WorldOfSkatecraft.Setup.Core;

/// <summary>What a check found: what is wrong, what to do about it, and what only deserves a note.</summary>
public sealed record ValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Facts)
{
    public bool Ok => Errors.Count == 0;

    public static ValidationResult From(params (string Kind, string Text)[] items) =>
        new(
            items.Where(i => i.Kind == "error").Select(i => i.Text).ToList(),
            items.Where(i => i.Kind == "warn").Select(i => i.Text).ToList(),
            items.Where(i => i.Kind == "fact").Select(i => i.Text).ToList());

    public string Summary() =>
        Errors.Count == 0
            ? Warnings.Count == 0 ? "OK" : $"OK with {Warnings.Count} note(s)"
            : $"{Errors.Count} problem(s)";
}

/// <summary>
/// Checks the player's own game files. Nothing here is redistributed: the setup reads the files the
/// player already has and refuses a folder that cannot work, before anything is installed.
/// </summary>
public static class GameFiles
{
    /// <summary>The 1.12.1 data archives every English client has; a missing one is fatal.</summary>
    public static readonly string[] RequiredWowArchives =
        { "dbc.MPQ", "patch.MPQ", "model.MPQ", "terrain.MPQ" };

    /// <summary>
    /// The Skate 3 archives the converter reads; a missing one is fatal. Keep in step with the
    /// converter's own REQUIRED list (`tools/asset_pipeline` of the Skate 3 Rust Engine).
    /// </summary>
    public static readonly string[] RequiredSkateFiles =
    {
        "data/big/miscload.big",
        "data/big/miscboot.big",
        "data/big/db.big",
        "data/content/createacharacter.big",
    };

    /// <summary>
    /// The player's WoW folder, checked: the four archives, each a real MPQ, and the build the
    /// client speaks (1.12.1, 5875) as far as the executable's version resource says.
    /// </summary>
    public static ValidationResult CheckWow(string folder)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var facts = new List<string>();

        folder = NormalizeWowFolder(folder);
        if (!Directory.Exists(folder))
        {
            errors.Add($"That folder does not exist: {folder}");
            return new ValidationResult(errors, warnings, facts);
        }

        var data = Path.Combine(folder, "Data");
        if (!Directory.Exists(data))
        {
            errors.Add(
                $"No Data folder in {folder}. Pick the World of Warcraft folder itself — the one " +
                "that contains Data, WoW.exe and the .dll files.");
            return new ValidationResult(errors, warnings, facts);
        }

        foreach (var archive in RequiredWowArchives)
        {
            var path = FindCaseInsensitive(data, archive);
            if (path is null)
            {
                errors.Add($"Data\\{archive} is missing. Use an untouched English 1.12.1 client (build 5875).");
                continue;
            }
            if (ReadArchiveMagic(path) is null)
            {
                errors.Add(
                    $"Data\\{archive} is not a readable MPQ archive (it may be a partial download or " +
                    "a re-packed client). Use an untouched English 1.12.1 client.");
            }
        }

        var exe = FindCaseInsensitive(folder, "WoW.exe");
        if (exe is null)
        {
            warnings.Add(
                "No WoW.exe beside Data. That is only needed by the client's own launcher, so setup " +
                "can continue, but check you picked the right folder.");
        }
        else
        {
            var version = TryFileVersion(exe);
            if (version is not null)
            {
                facts.Add($"WoW.exe file version: {version}");
                if (!version.StartsWith("1.12", StringComparison.Ordinal))
                {
                    warnings.Add(
                        $"WoW.exe reports version {version}, not 1.12.1 (build 5875). This client reads " +
                        "1.12.1 data; another version's data will not load.");
                }
            }
        }

        // A client whose patch chain carries The Burning Crusade is newer than 1.12.1.
        foreach (var expansion in new[] { "expansion.MPQ", "patch-2.MPQ" })
        {
            if (FindCaseInsensitive(data, expansion) is not null)
            {
                warnings.Add(
                    $"Data\\{expansion} is present: this looks like a client newer than 1.12.1, which " +
                    "is not what this client reads.");
            }
        }

        var gigabytes = ArchiveSizes(data) / (1024.0 * 1024.0 * 1024.0);
        facts.Add($"Data archives: {gigabytes:F1} GB");
        return new ValidationResult(errors, warnings, facts);
    }

    /// <summary>
    /// The player's extracted Skate 3 folder, checked against exactly what the converter reads: a
    /// file named <c>default.xex</c> with a <c>data</c> folder holding the four game archives.
    /// </summary>
    public static ValidationResult CheckSkate(string folder)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var facts = new List<string>();

        if (string.IsNullOrWhiteSpace(folder))
        {
            errors.Add("No Skate 3 folder given.");
            return new ValidationResult(errors, warnings, facts);
        }
        if (File.Exists(folder) && folder.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                "That is a disc image. The converter reads an extracted copy, not an ISO: extract it " +
                "first (for example `extract-xiso -x \"Skate 3.iso\"`) and pick the folder that comes " +
                "out, the one with default.xex in it.");
            return new ValidationResult(errors, warnings, facts);
        }
        if (!Directory.Exists(folder))
        {
            errors.Add($"That folder does not exist: {folder}");
            return new ValidationResult(errors, warnings, facts);
        }

        var xex = FindCaseInsensitive(folder, "default.xex");
        if (xex is null)
        {
            var iso = Directory.EnumerateFiles(folder, "*.iso").FirstOrDefault();
            errors.Add(iso is null
                ? $"{folder} has no default.xex. Pick your extracted Skate 3 (Xbox 360) folder — a disc " +
                  "image has to be extracted first."
                : $"{folder} holds {Path.GetFileName(iso)}, not an extracted game. Extract the disc image " +
                  "first, then pick the folder with default.xex in it.");
            return new ValidationResult(errors, warnings, facts);
        }

        foreach (var needed in RequiredSkateFiles)
        {
            if (!File.Exists(Path.Combine(folder, needed.Replace('/', Path.DirectorySeparatorChar))))
            {
                errors.Add(
                    $"Missing {needed.Replace('/', '\\')}. Keep the data folder beside default.xex; a " +
                    "complete extraction has several GB of files under data\\.");
            }
        }

        if (!Directory.Exists(Path.Combine(folder, "data", "audio")))
        {
            warnings.Add(
                "No data\\audio folder: the skate sounds cannot be converted. Everything else works, " +
                "just silently.");
        }

        facts.Add($"default.xex: {new FileInfo(xex).Length / (1024.0 * 1024.0):F1} MB");
        return new ValidationResult(errors, warnings, facts);
    }

    /// <summary>The WoW folder itself, given either it or its <c>Data</c> folder.</summary>
    public static string NormalizeWowFolder(string folder)
    {
        var full = Path.GetFullPath(folder);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), "Data", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(trimmed)!
            : full;
    }

    /// <summary>The path of a name in a folder, whatever case the file system stored it in.</summary>
    public static string? FindCaseInsensitive(string folder, string name)
    {
        var direct = Path.Combine(folder, name);
        if (File.Exists(direct) || Directory.Exists(direct))
        {
            return direct;
        }
        if (!Directory.Exists(folder))
        {
            return null;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>The MPQ tag at the head of an archive, or null when the file does not start with one.</summary>
    private static string? ReadArchiveMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 4)
            {
                return null;
            }
            Span<byte> head = stackalloc byte[4];
            stream.ReadExactly(head);
            var tag = System.Text.Encoding.ASCII.GetString(head);
            return tag.StartsWith("MPQ", StringComparison.Ordinal) ? tag : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long ArchiveSizes(string data)
    {
        long total = 0;
        foreach (var pattern in new[] { "*.MPQ", "*.mpq" })
        {
            foreach (var file in Directory.EnumerateFiles(data, pattern, SearchOption.TopDirectoryOnly))
            {
                total += new FileInfo(file).Length;
            }
        }
        return total;
    }

    /// <summary>The executable's version resource, or null where there is none to read.</summary>
    private static string? TryFileVersion(string exe)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
