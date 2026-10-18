using System.Text;

namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// Setup's log: one file, appended to by every run, with the same lines echoed to whoever is
/// watching (the wizard's log pane, or the console). Nothing about the player's games is hidden in
/// it — the diagnostic report and the log together are what a bug report carries.
/// </summary>
public sealed class SetupLog : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly object _lock = new();
    private readonly Action<string>? _echo;

    public SetupLog(string? path, Action<string>? echo = null)
    {
        _echo = echo;
        if (path is null)
        {
            return;
        }
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>Where the log is going, for the wizard's "open the log" button.</summary>
    public string? LogPath => _file?.BaseStream is FileStream stream ? stream.Name : null;

    /// <summary>Writes a line with the time it was written.</summary>
    public void Line(string message)
    {
        var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (_lock)
        {
            _file?.WriteLine(stamped);
        }
        _echo?.Invoke(stamped);
    }

    /// <summary>Writes a section heading.</summary>
    public void Section(string title)
    {
        Line("");
        Line($"=== {title} ===");
    }

    /// <summary>Writes a program's output, indented, so the log reads as a transcript.</summary>
    public void Output(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            Line("    " + line.TrimEnd('\r'));
        }
    }

    /// <summary>Writes the command before it runs, which is what makes a log actionable.</summary>
    public void Command(CommandSpec spec, bool dryRun)
    {
        Line($"{(dryRun ? "would run: " : "run: ")}{spec.Display()}");
        if (spec.Why is not null)
        {
            Line($"    ({spec.Why})");
        }
    }

    public void Dispose() => _file?.Dispose();
}

/// <summary>Fetches the files setup downloads, so a dry run and the tests can answer without a network.</summary>
public interface IDownloader
{
    /// <summary>Downloads a URL to a path, reporting bytes so far, and returns what was written.</summary>
    Task<long> DownloadAsync(
        string url,
        string destination,
        Action<long, long?>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Downloads over HTTP, following redirects, as the setup's upstreams need.</summary>
public sealed class HttpDownloader : IDownloader, IDisposable
{
    private readonly HttpClient _client;

    public HttpDownloader(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "WorldOfSkatecraftSetup/1.0 (+https://github.com/2boxtrickshot-droid/world-of-skatecraft)");
    }

    public async Task<long> DownloadAsync(
        string url,
        string destination,
        Action<long, long?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        // A download that dies halfway must not leave a file that looks complete.
        var partial = destination + ".partial";
        await using (var file = File.Create(partial))
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                progress?.Invoke(written, total);
            }
        }
        File.Move(partial, destination, overwrite: true);
        return new FileInfo(destination).Length;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>A downloader for a dry run and the tests: it records the URLs and writes nothing.</summary>
public sealed class RecordingDownloader : IDownloader
{
    /// <summary>Every URL asked for, in order, with the path it would have been written to.</summary>
    public List<(string Url, string Destination)> Requests { get; } = new();

    public Task<long> DownloadAsync(
        string url,
        string destination,
        Action<long, long?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add((url, destination));
        progress?.Invoke(1, 1);
        return Task.FromResult(1L);
    }
}
