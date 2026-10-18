namespace WorldOfSkatecraft.Setup.Core;

/// <summary>The few sockets the setup opens itself: the login port, and nothing else.</summary>
public static class Net
{
    /// <summary>Whether something is listening on a local port.</summary>
    public static async Task<bool> IsPortOpenAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception error) when (error is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Waits for a local port to answer, which is how setup knows the login service is up.</summary>
    public static async Task<bool> WaitForPortAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            if (await IsPortOpenAsync(host, port, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        return await IsPortOpenAsync(host, port, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
    }
}
