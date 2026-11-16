using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NitroPower.Core.IO;

namespace NitroPower.Core.Api;

/// <summary>
/// The daemon side of the API: a Unix stream socket, one JSON-RPC message per
/// line. The socket is root:<see cref="NitroApiProtocol.Group"/> 0660, so only
/// that group (and root) can connect. Every call runs under
/// <paramref name="hardwareLock"/>, the lock the daemon's own loops also take.
/// A connection that calls events.subscribe receives event notifications.
/// </summary>
public sealed class RpcServer(
    string socketPath,
    RpcDispatcher dispatcher,
    object hardwareLock,
    Action<string> log) : IDisposable
{
    /// <summary>Longest request accepted; the largest real one (a fan curve) is about 1 KiB.</summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>Connections at once; more are closed at accept, so a client
    /// cannot exhaust the daemon's descriptors or threads.</summary>
    public const int MaxClients = 32;

    private const int SendTimeoutMs = 2000;

    private sealed class Connection(Socket socket)
    {
        public Socket Socket { get; } = socket;
        public object WriteLock { get; } = new();
        public bool Subscribed { get; set; }
    }

    private readonly List<Connection> _connections = [];
    private Socket? _listener;

    /// <summary>Called after each call that may have changed something (with the method name).</summary>
    public Action<string>? Mutated { get; set; }

    public int ClientCount
    {
        get { lock (_connections) return _connections.Count; }
    }

    /// <summary>Creates the socket. <paramref name="groupId"/> null: root only (0600).</summary>
    public void Start(int? groupId)
    {
        var dir = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        if (OperatingSystem.IsLinux())
        {
            if (groupId is { } gid && Environment.IsPrivilegedProcess)
            {
                if (Libc.Chown(socketPath, 0, gid) != 0)
                    log($"chown {socketPath} failed (errno {Marshal.GetLastPInvokeError()})");
                File.SetUnixFileMode(socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            }
            else if (groupId is null)
            {
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        _listener.Listen(16);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = _listener ?? throw new InvalidOperationException("Start first");
        using var registration = ct.Register(() => listener.Close());
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
            var connection = new Connection(client);
            bool accepted;
            lock (_connections)
            {
                accepted = _connections.Count < MaxClients;
                if (accepted)
                    _connections.Add(connection);
            }
            if (!accepted)
            {
                log($"more than {MaxClients} clients; connection refused");
                client.Dispose();
                continue;
            }
            client.SendTimeout = SendTimeoutMs;
            _ = Task.Run(() => Serve(connection, ct), ct);
        }
    }

    private async Task Serve(Connection c, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var pending = new MemoryStream();
        try
        {
            using var stream = new NetworkStream(c.Socket, ownsSocket: true);
            while (!ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0)
                    break;
                // Lines are read with a size limit: ReadLine would buffer an
                // endless line until the daemon runs out of memory
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n')
                        continue;
                    pending.Write(buffer, start, i - start);
                    start = i + 1;
                    if (pending.Length > MaxLineBytes)
                    {
                        log($"request longer than {MaxLineBytes} bytes; connection closed");
                        return;
                    }
                    var line = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length).TrimEnd('\r');
                    pending.SetLength(0);
                    Handle(c, line);
                }
                pending.Write(buffer, start, n - start);
                if (pending.Length > MaxLineBytes)
                {
                    log($"request longer than {MaxLineBytes} bytes; connection closed");
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_connections)
                _connections.Remove(c);
        }
    }

    private void Handle(Connection c, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        RpcReply reply;
        if (IsSubscribe(line, out var id))
        {
            c.Subscribed = true;
            reply = new RpcReply(RpcDispatcher.Result(id, true), RpcDispatcher.SubscribeMethod, false);
        }
        else
        {
            lock (hardwareLock)
                reply = dispatcher.Handle(line);
        }
        if (reply.Response is not null)
            Send(c, reply.Response);
        if (reply.Mutating && reply.Method is not null)
            Mutated?.Invoke(reply.Method);
    }

    private static bool IsSubscribe(string line, out System.Text.Json.JsonElement? id)
    {
        id = null;
        if (!line.Contains(RpcDispatcher.SubscribeMethod, StringComparison.Ordinal))
            return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("method", out var m) && m.GetString() == RpcDispatcher.SubscribeMethod)
            {
                id = root.TryGetProperty("id", out var i) ? i.Clone() : null;
                return true;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return false;
    }

    /// <summary>Sends an event to every subscribed connection; a client that
    /// does not read is dropped.</summary>
    public void Broadcast(NitroEvent e)
    {
        var line = RpcDispatcher.Event(e);
        List<Connection> targets;
        lock (_connections)
            targets = _connections.Where(c => c.Subscribed).ToList();
        foreach (var c in targets)
        {
            try
            {
                Send(c, line);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                c.Socket.Dispose();
            }
        }
    }

    private static void Send(Connection c, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (c.WriteLock)
            c.Socket.Send(bytes);
    }

    public void Dispose()
    {
        _listener?.Dispose();
        lock (_connections)
        {
            foreach (var c in _connections)
                c.Socket.Dispose();
            _connections.Clear();
        }
        try
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>gid of a group from /etc/group, or null.</summary>
    public static int? FindGroupId(string name, string groupFile = "/etc/group")
    {
        try
        {
            foreach (var line in File.ReadLines(groupFile))
            {
                var parts = line.Split(':');
                if (parts.Length >= 3 && parts[0] == name && int.TryParse(parts[2], out var gid))
                    return gid;
            }
        }
        catch (IOException)
        {
        }
        return null;
    }
}
