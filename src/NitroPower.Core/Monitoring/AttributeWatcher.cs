using System.Text;
using NitroPower.Core.IO;

namespace NitroPower.Core.Monitoring;

/// <summary>An attribute whose driver called sysfs_notify, with its new value.</summary>
public sealed record AttributeChange(string Path, string? Value);

/// <summary>
/// Waits for sysfs_notify() on attribute files (poll with POLLPRI|POLLERR),
/// as the Linuwu-Sense profile notifier and turbo OSD do. Values are read only
/// when the kernel signals a change: reading platform_profile or nitro_sense
/// attributes is a firmware call (SMI), so polling them in a loop is costly.
/// Works on real paths only.
/// </summary>
public sealed class AttributeWatcher : IDisposable
{
    private readonly List<(string Path, int Fd)> _files = [];
    private bool _disposed;

    /// <summary>Opens the paths that exist; missing ones are skipped (see <see cref="Paths"/>).</summary>
    public AttributeWatcher(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var fd = Libc.Open(path, Libc.ORdOnly | Libc.OCloExec);
            if (fd >= 0)
                _files.Add((path, fd));
        }
    }

    public IReadOnlyList<string> Paths => _files.Select(f => f.Path).ToList();

    /// <summary>Current value of every watched file (also arms the notification).</summary>
    public IReadOnlyList<AttributeChange> ReadAll() =>
        _files.Select(f => new AttributeChange(f.Path, ReadFd(f.Fd))).ToList();

    /// <summary>Blocks until one or more files are notified, or the timeout
    /// passes (empty result). Notified files are read again, which re-arms them.</summary>
    public IReadOnlyList<AttributeChange> Wait(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_files.Count == 0)
        {
            Thread.Sleep(timeoutMs < 0 ? Timeout.Infinite : timeoutMs);
            return [];
        }

        var fds = new Libc.PollFd[_files.Count];
        for (var i = 0; i < fds.Length; i++)
            fds[i] = new Libc.PollFd { Fd = _files[i].Fd, Events = Libc.PollPri | Libc.PollErr };
        if (!Libc.PollAll(fds, timeoutMs))
            return [];

        var changes = new List<AttributeChange>();
        for (var i = 0; i < fds.Length; i++)
        {
            if ((fds[i].Revents & (Libc.PollPri | Libc.PollErr)) != 0)
                changes.Add(new AttributeChange(_files[i].Path, ReadFd(_files[i].Fd)));
        }
        return changes;
    }

    private static unsafe string? ReadFd(int fd)
    {
        var buffer = stackalloc byte[256];
        var n = Libc.PRead(fd, buffer, 256, 0);
        return n < 0 ? null : Encoding.UTF8.GetString(buffer, (int)n).Trim();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var (_, fd) in _files)
            Libc.Close(fd);
        _files.Clear();
    }
}
