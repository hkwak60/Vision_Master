using System.Diagnostics;
using System.Net.Sockets;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class ConnectionProbe : IConnectionProbe
{
    private static readonly char[] Drives = ['D', 'E', 'F', 'G'];
    private readonly ISharePathResolver _shares;

    public ConnectionProbe(ISharePathResolver shares)
    {
        _shares = shares;
    }

    public async Task<IReadOnlyList<ShareConnectionResult>> ProbeAsync(
        WeldingMachine machine,
        TimeSpan timeoutPerShare,
        CancellationToken cancellationToken)
    {
        if (!await CanReachSmbAsync(machine.IpAddress, timeoutPerShare, cancellationToken))
        {
            return Drives.Select(drive => new ShareConnectionResult(
                drive,
                ConnectionState.PcUnreachable,
                machine.ShareRoot(drive),
                TimeSpan.Zero,
                "PC or SMB port 445 is unreachable.")).ToArray();
        }

        var tasks = Drives.Select(drive =>
            ProbeDriveAsync(machine, drive, timeoutPerShare, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<ShareConnectionResult> ProbeDriveAsync(
        WeldingMachine machine,
        char drive,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var named = machine.ShareRoot(drive);
        var namedResult = await ProbePathAsync(drive, named, timeout, cancellationToken);
        if (namedResult.State == ConnectionState.Accessible)
        {
            _shares.RecordAccessibleRoot(machine, drive, namedResult.Path);
            return namedResult;
        }

        var administrative = machine.ShareRoot(drive, administrative: true);
        var adminResult = await ProbePathAsync(drive, administrative, timeout, cancellationToken);
        if (adminResult.State == ConnectionState.Accessible)
        {
            _shares.RecordAccessibleRoot(machine, drive, adminResult.Path);
            return adminResult with { Message = "Accessible through administrative share fallback." };
        }
        return namedResult;
    }

    private static async Task<ShareConnectionResult> ProbePathAsync(
        char drive,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var state = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        return (ConnectionState.Missing, "Share was not found.");
                    }

                    using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                    _ = enumerator.MoveNext();
                    return (ConnectionState.Accessible, "Accessible.");
                }
                catch (UnauthorizedAccessException)
                {
                    return (ConnectionState.AccessDenied, "Access denied.");
                }
                catch (IOException exception)
                {
                    return (ConnectionState.Error, exception.Message);
                }
            }, cancellationToken).WaitAsync(timeout, cancellationToken);

            return new(drive, state.Item1, path, stopwatch.Elapsed, state.Item2);
        }
        catch (TimeoutException)
        {
            return new(drive, ConnectionState.TimedOut, path, stopwatch.Elapsed, "Connection timed out.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(drive, ConnectionState.TimedOut, path, stopwatch.Elapsed, "Connection timed out.");
        }
        catch (Exception exception)
        {
            return new(drive, ConnectionState.Error, path, stopwatch.Elapsed, exception.Message);
        }
    }

    private static async Task<bool> CanReachSmbAsync(
        string ipAddress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ipAddress, 445, cancellationToken)
                .AsTask()
                .WaitAsync(timeout, cancellationToken);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
