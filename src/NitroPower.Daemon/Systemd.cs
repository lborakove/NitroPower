using System.Net.Sockets;
using System.Text;

namespace NitroPower.Daemon;

/// <summary>Logging for journald (syslog priority prefixes) and sd_notify.</summary>
internal static class Systemd
{
    private static readonly bool UnderJournal = Environment.GetEnvironmentVariable("JOURNAL_STREAM") is not null;
    private static readonly object Gate = new();

    public static bool Verbose { get; set; }

    public static void Info(string message) => Write(6, message);
    public static void Warn(string message) => Write(4, message);
    public static void Error(string message) => Write(3, message);

    public static void Debug(string message)
    {
        if (Verbose)
            Write(7, message);
    }

    private static void Write(int priority, string message)
    {
        // One record per call: a newline in a message (device names, exception
        // text) would otherwise start a new journal line with its own priority
        message = message.ReplaceLineEndings(" | ");
        lock (Gate)
        {
            if (UnderJournal)
                Console.Out.WriteLine($"<{priority}>{message}");
            else
                Console.Out.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {Level(priority)} {message}");
            Console.Out.Flush();
        }
    }

    private static string Level(int priority) => priority switch
    {
        <= 3 => "error",
        4 => "warn ",
        6 => "info ",
        _ => "debug",
    };

    /// <summary>sd_notify(3): "READY=1", "STOPPING=1", "STATUS=...". No-op outside systemd.</summary>
    public static void Notify(string state)
    {
        var path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(path))
            return;
        if (path[0] == '@')
            path = "\0" + path[1..];
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Encoding.UTF8.GetBytes(state));
        }
        catch (SocketException e)
        {
            Warn($"sd_notify failed: {e.Message}");
        }
    }
}
