using System.Net.Sockets;
using System.Text;

namespace NitroPower.Gui.Services;

/// <summary>
/// One window per user: the first instance listens on a socket in
/// $XDG_RUNTIME_DIR; later launches ask it to show itself and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    /// <summary>In $XDG_RUNTIME_DIR (private to the user). Without it, a private
    /// directory in the user's cache, never the shared /tmp, where another
    /// user could squat the name.</summary>
    public static string SocketPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run)
                return Path.Join(run, $"nitropower-gui-{Environment.UserName}.sock");
            var dir = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "nitropower");
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return Path.Join(dir, "gui.sock");
        }
    }

    /// <summary>Sends <paramref name="command"/> to a running instance; true when one answered.</summary>
    public static bool Signal(string command)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            s.Send(Encoding.UTF8.GetBytes(command + "\n"));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Becomes the running instance; null if the socket cannot be made.</summary>
    public static SingleInstance? Claim(Action<string> onCommand)
    {
        var path = SocketPath;
        try
        {
            if (File.Exists(path))
                File.Delete(path); // stale: Signal() found nobody listening
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            var instance = new SingleInstance(listener, path);
            _ = instance.AcceptLoop(onCommand);
            return instance;
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task AcceptLoop(Action<string> onCommand)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token);
                var buffer = new byte[256];
                var n = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                var command = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                onCommand(command.Length == 0 ? "show" : command);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // a client that went away; keep listening
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try { File.Delete(_path); }
        catch (IOException) { }
    }
}
