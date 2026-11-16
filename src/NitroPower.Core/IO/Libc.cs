using System.Runtime.InteropServices;

namespace NitroPower.Core.IO;

/// <summary>The few libc calls NitroPower needs (sysfs poll, evdev, socket ownership).</summary>
internal static partial class Libc
{
    public const int ORdOnly = 0;
    public const int ONonBlock = 0x800;
    public const int OCloExec = 0x80000;
    public const short PollIn = 0x001;
    public const short PollPri = 0x002;
    public const short PollErr = 0x008;
    public const short PollHup = 0x010;
    public const short PollNval = 0x020;
    public const int EIntr = 4;
    public const int EAgain = 11;

    /// <summary>EVIOCSMASK = _IOW('E', 0x93, struct input_mask).</summary>
    public const uint EvIocSMask = 0x40104593;

    /// <summary>struct input_mask from linux/input.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InputMask
    {
        public uint Type;
        public uint CodesSize;
        public ulong CodesPtr;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint Read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "pread", SetLastError = true)]
    public static unsafe partial nint PRead(int fd, byte* buffer, nuint count, nint offset);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static unsafe partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    [LibraryImport("libc", EntryPoint = "close")]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, ref InputMask mask);

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Chown(string path, int owner, int group);

    /// <summary>poll() on the given descriptors; returns false on EINTR.</summary>
    public static unsafe bool PollAll(PollFd[] fds, int timeoutMs)
    {
        int ready;
        fixed (PollFd* p = fds)
            ready = Poll(p, (nuint)fds.Length, timeoutMs);
        if (ready >= 0)
            return true;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == EIntr)
            return false;
        throw new IOException($"poll failed (errno {errno})");
    }
}
