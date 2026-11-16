using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NitroPower.Core.IO;

namespace NitroPower.Core.Input;

/// <summary>An input device as listed in /proc/bus/input/devices.</summary>
/// <param name="EventNode">"event3" → /dev/input/event3.</param>
public sealed record InputDeviceInfo(string Name, string EventNode);

public static class InputDevices
{
    public const string ProcPath = "/proc/bus/input/devices";

    /// <summary>Devices whose key bitmap has <paramref name="keyCode"/>, limited
    /// to <paramref name="names"/> (exact names) when given.</summary>
    public static IReadOnlyList<InputDeviceInfo> WithKey(string procText, int keyCode, IReadOnlyCollection<string>? names)
    {
        var found = new List<InputDeviceInfo>();
        foreach (var block in procText.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null, node = null, keys = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("N: Name=", StringComparison.Ordinal))
                    name = line["N: Name=".Length..].Trim().Trim('"');
                else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
                    node = line["H: Handlers=".Length..]
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(h => h.StartsWith("event", StringComparison.Ordinal));
                else if (line.StartsWith("B: KEY=", StringComparison.Ordinal))
                    keys = line["B: KEY=".Length..].Trim();
            }
            if (name is null || node is null || keys is null || !HasBit(keys, keyCode))
                continue;
            if (names is not null && !names.Contains(name))
                continue;
            found.Add(new InputDeviceInfo(name, node));
        }
        return found;
    }

    /// <summary>The kernel prints bitmaps as hex longs, most significant first.</summary>
    public static bool HasBit(string hexWords, int bit)
    {
        var words = hexWords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = bit / 64;
        if (index >= words.Length)
            return false;
        var word = words[words.Length - 1 - index];
        return ulong.TryParse(word, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) &&
               (v >> (bit % 64) & 1) == 1;
    }
}

/// <summary>
/// Kernel-side event filter (EVIOCSMASK, Linux 4.4+): the descriptor receives
/// only one key, so the daemon never sees the rest of what is typed.
/// </summary>
public static class EvdevFilter
{
    private const int EvCount = 0x20;
    private const int KeyCount = 0x300;

    /// <summary>Lets through only <paramref name="keyCode"/> (and EV_SYN, which
    /// the kernel never filters). Returns the errno, or 0 on success.</summary>
    public static unsafe int OnlyKey(int fd, int keyCode)
    {
        Span<byte> types = stackalloc byte[MaskBytes(EvCount)];
        types.Clear();
        types[InputEvent.EvKey / 8] |= (byte)(1 << (InputEvent.EvKey % 8));
        Span<byte> keys = stackalloc byte[MaskBytes(KeyCount)];
        keys.Clear();
        keys[keyCode / 8] |= (byte)(1 << (keyCode % 8));

        // The type mask (set through EV_SYN) drops scancodes (EV_MSC), LEDs and repeats
        fixed (byte* t = types)
        fixed (byte* k = keys)
        {
            var typeMask = new Libc.InputMask { Type = 0, CodesSize = (uint)types.Length, CodesPtr = (ulong)t };
            if (Libc.Ioctl(fd, Libc.EvIocSMask, ref typeMask) != 0)
                return Marshal.GetLastPInvokeError();
            var keyMask = new Libc.InputMask { Type = InputEvent.EvKey, CodesSize = (uint)keys.Length, CodesPtr = (ulong)k };
            if (Libc.Ioctl(fd, Libc.EvIocSMask, ref keyMask) != 0)
                return Marshal.GetLastPInvokeError();
        }
        return 0;
    }

    /// <summary>Bitmap size for <paramref name="bits"/> codes. The kernel copies
    /// whole longs and answers EINVAL to any other size.</summary>
    internal static int MaskBytes(int bits) => (bits + 8 * nint.Size - 1) / (8 * nint.Size) * nint.Size;
}

/// <summary>struct input_event on 64-bit Linux: timeval (16 bytes), type, code, value.</summary>
public readonly record struct InputEvent(ushort Type, ushort Code, int Value)
{
    public const int Size = 24;
    public const ushort EvKey = 1;
    public const int KeyPress = 1;

    public static InputEvent Parse(ReadOnlySpan<byte> raw) => new(
        BitConverter.ToUInt16(raw[16..18]),
        BitConverter.ToUInt16(raw[18..20]),
        BitConverter.ToInt32(raw[20..24]));

    public bool IsPressOf(int keyCode) => Type == EvKey && Code == keyCode && Value == KeyPress;
}

/// <summary>
/// Reports presses of one key from the matching /dev/input devices, without
/// grabbing them. Devices that disappear (module reload) are dropped and found
/// again by the periodic rescan. Presses closer than <see cref="DebounceMs"/>
/// count once, in case two devices report the same key.
/// </summary>
public sealed class KeyListener(int keyCode, IReadOnlyCollection<string>? deviceNames, Action<string> log)
{
    public const int DebounceMs = 400;
    private const int RescanMs = 5000;

    private readonly Dictionary<string, (int Fd, string Name)> _open = [];
    private readonly HashSet<string> _failed = [];
    private readonly Stopwatch _sinceLast = new();

    public IReadOnlyList<string> OpenDevices => _open.Values.Select(v => v.Name).ToList();

    /// <summary>Blocks until cancelled, calling <paramref name="pressed"/> with the device name.</summary>
    public unsafe void Run(Action<string> pressed, CancellationToken ct)
    {
        var lastScan = Stopwatch.StartNew();
        Rescan();
        var buffer = new byte[InputEvent.Size * 32];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (lastScan.ElapsedMilliseconds >= RescanMs)
                {
                    Rescan();
                    lastScan.Restart();
                }
                if (_open.Count == 0)
                {
                    ct.WaitHandle.WaitOne(1000);
                    continue;
                }

                var nodes = _open.Keys.ToArray();
                var fds = nodes.Select(n => new Libc.PollFd { Fd = _open[n].Fd, Events = Libc.PollIn }).ToArray();
                if (!Libc.PollAll(fds, 1000))
                    continue;
                for (var i = 0; i < fds.Length; i++)
                {
                    var r = fds[i].Revents;
                    if (r == 0)
                        continue;
                    var (fd, name) = _open[nodes[i]];
                    if ((r & Libc.PollIn) == 0)
                    {
                        Drop(nodes[i], "hung up");
                        continue;
                    }
                    nint n;
                    fixed (byte* p = buffer)
                        n = Libc.Read(fd, p, (nuint)buffer.Length);
                    if (n < 0)
                    {
                        if (Marshal.GetLastPInvokeError() != Libc.EAgain)
                            Drop(nodes[i], "read failed");
                        continue;
                    }
                    for (var off = 0; off + InputEvent.Size <= n; off += InputEvent.Size)
                    {
                        if (!InputEvent.Parse(buffer.AsSpan(off, InputEvent.Size)).IsPressOf(keyCode))
                            continue;
                        if (_sinceLast.IsRunning && _sinceLast.ElapsedMilliseconds < DebounceMs)
                            continue;
                        _sinceLast.Restart();
                        pressed(name);
                    }
                }
            }
        }
        finally
        {
            foreach (var node in _open.Keys.ToList())
                Drop(node, null);
        }
    }

    private void Rescan()
    {
        string text;
        try
        {
            text = File.ReadAllText(InputDevices.ProcPath);
        }
        catch (IOException)
        {
            return;
        }
        var wanted = InputDevices.WithKey(text, keyCode, deviceNames);
        foreach (var gone in _open.Keys.Where(n => wanted.All(w => w.EventNode != n)).ToList())
            Drop(gone, "removed");
        foreach (var dev in wanted)
        {
            if (_open.ContainsKey(dev.EventNode))
                continue;
            var fd = Libc.Open($"/dev/input/{dev.EventNode}", Libc.ORdOnly | Libc.ONonBlock | Libc.OCloExec);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (_failed.Add(dev.EventNode))
                    log($"cannot open /dev/input/{dev.EventNode} ({dev.Name}): errno {errno}");
                continue;
            }
            _failed.Remove(dev.EventNode);
            _open[dev.EventNode] = (fd, dev.Name);
            var filterError = EvdevFilter.OnlyKey(fd, keyCode);
            log(filterError == 0
                ? $"listening for key {keyCode} on {dev.Name} (/dev/input/{dev.EventNode}), other keys filtered by the kernel"
                : $"listening for key {keyCode} on {dev.Name} (/dev/input/{dev.EventNode}); kernel filter unavailable (errno {filterError}), other keys are read and ignored");
        }
    }

    private void Drop(string node, string? why)
    {
        if (!_open.Remove(node, out var entry))
            return;
        Libc.Close(entry.Fd);
        if (why is not null)
            log($"stopped listening on {entry.Name} (/dev/input/{node}): {why}");
    }
}
