using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Nightwave.Tuners.Dvb.LinuxDvb;

/// <summary>
/// The numbers of the Linux DVB API, from <c>include/uapi/linux/dvb/frontend.h</c> and
/// <c>dmx.h</c>. The request codes are those of x86-64 and arm64; other architectures encode them
/// differently, and 32 bit ones lay out <c>dtv_properties</c> differently.
/// </summary>
internal static class DvbApi
{
    public const int PropertySize = 76;
    public const uint FrontendInfoSize = 168;
    public const int PidAll = 0x2000;

    public const uint Tune = 1;
    public const uint Clear = 2;
    public const uint Frequency = 3;
    public const uint Modulation = 4;
    public const uint BandwidthHz = 5;
    public const uint Inversion = 6;
    public const uint SymbolRate = 8;
    public const uint InnerFec = 9;
    public const uint Pilot = 12;
    public const uint RollOff = 13;
    public const uint DeliverySystem = 17;
    public const uint CodeRateHigh = 36;
    public const uint CodeRateLow = 37;
    public const uint GuardInterval = 38;
    public const uint TransmissionMode = 39;
    public const uint Hierarchy = 40;
    public const uint StreamId = 42;
    public const uint EnumerateSystems = 44;
    public const uint StatSignalStrength = 62;
    public const uint StatCarrierToNoise = 63;
    public const uint StatPostErrorBits = 66;
    public const uint StatPostTotalBits = 67;

    public const uint SysDvbCAnnexA = 1;
    public const uint SysDvbCAnnexB = 2;
    public const uint SysDvbCAnnexC = 18;
    public const uint SysDvbT = 3;
    public const uint SysDvbT2 = 16;
    public const uint SysDvbS = 5;
    public const uint SysDvbS2 = 6;

    public const uint FecAuto = 9;
    public const uint InversionAuto = 2;
    public const uint ModulationQamAuto = 6;
    public const uint RollOffAuto = 3;
    public const uint TransmissionModeAuto = 2;
    public const uint GuardIntervalAuto = 4;
    public const uint HierarchyAuto = 4;
    public const uint PilotOn = 0;
    public const uint PilotOff = 1;
    public const uint PilotAuto = 2;

    public const int Voltage13 = 0;
    public const int Voltage18 = 1;
    public const int ToneOn = 0;
    public const int ToneOff = 1;

    public const uint StatusSignal = 0x01;
    public const uint StatusLock = 0x10;

    public const byte ScaleDecibel = 1;
    public const byte ScaleRelative = 2;
    public const byte ScaleCounter = 3;

    public static readonly ulong FeGetInfo = Read(61, FrontendInfoSize);
    public static readonly ulong FeSendDiseqc = Write(63, 7);
    public static readonly ulong FeSendBurst = None(65);
    public static readonly ulong FeSetTone = None(66);
    public static readonly ulong FeSetVoltage = None(67);
    public static readonly ulong FeReadStatus = Read(69, 4);
    public static readonly ulong FeSetProperty = Write(82, 16);
    public static readonly ulong FeGetProperty = Read(83, 16);
    public static readonly ulong DmxStop = None(42);
    public static readonly ulong DmxSetPesFilter = Write(44, 20);
    public static readonly ulong DmxSetBufferSize = None(45);
    public static readonly ulong DmxAddPid = Write(51, 2);
    public static readonly ulong DmxRemovePid = Write(52, 2);

    private static ulong None(uint number) => Code(0, number, 0);

    private static ulong Write(uint number, uint size) => Code(1, number, size);

    private static ulong Read(uint number, uint size) => Code(2, number, size);

    private static ulong Code(ulong direction, uint number, uint size) =>
        (direction << 30) | ((ulong)size << 16) | ((ulong)'o' << 8) | number;

    /// <summary>Lays out <c>struct dtv_property</c> entries, which are packed, with command and value.</summary>
    public static byte[] Properties(IReadOnlyList<(uint Command, uint Value)> entries)
    {
        var bytes = new byte[entries.Count * PropertySize];

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = bytes.AsSpan(index * PropertySize);

            BinaryPrimitives.WriteUInt32LittleEndian(entry, entries[index].Command);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], entries[index].Value);
        }

        return bytes;
    }

    /// <summary>The first measurement of a property that carries a <c>dtv_fe_stats</c>, or null without one.</summary>
    public static (byte Scale, long Value)? FirstStat(ReadOnlySpan<byte> property)
    {
        if (property[16] == 0 || property[17] == 0)
        {
            return null;
        }

        return (property[17], BinaryPrimitives.ReadInt64LittleEndian(property[18..]));
    }

    /// <summary>The delivery systems of a <c>DTV_ENUM_DELSYS</c> answer.</summary>
    public static IReadOnlyList<byte> Systems(ReadOnlySpan<byte> property)
    {
        var length = Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(property[48..]), 32);

        return property.Slice(16, length).ToArray();
    }

    /// <summary><c>struct dmx_pes_filter_params</c> for a pid passed to the DVR device as transport stream.</summary>
    public static byte[] PesFilter(int pid)
    {
        const int OutputTsTap = 2;
        const int PesOther = 20;
        const int ImmediateStart = 4;

        var filter = new byte[20];

        BinaryPrimitives.WriteUInt16LittleEndian(filter, (ushort)pid);
        BinaryPrimitives.WriteInt32LittleEndian(filter.AsSpan(8), OutputTsTap);
        BinaryPrimitives.WriteInt32LittleEndian(filter.AsSpan(12), PesOther);
        BinaryPrimitives.WriteInt32LittleEndian(filter.AsSpan(16), ImmediateStart);

        return filter;
    }

    /// <summary>The name in a <c>struct dvb_frontend_info</c>.</summary>
    public static string FrontendName(ReadOnlySpan<byte> info)
    {
        var name = info[..128];
        var end = name.IndexOf((byte)0);

        return Encoding.UTF8.GetString(end < 0 ? name : name[..end]).Trim();
    }
}

/// <summary>The system calls a DVB card is driven with, so what is done with them can be tested without one.</summary>
internal interface IDvbKernel
{
    /// <summary>Opens a device without blocking. A file descriptor, or a negative errno.</summary>
    int Open(string path, bool writable);

    void Close(int descriptor);

    /// <summary>An ioctl with a number for its argument. Zero, or a negative errno.</summary>
    int Control(int descriptor, ulong request, nint value);

    /// <summary>An ioctl with a structure for its argument. Zero, or a negative errno.</summary>
    int Control(int descriptor, ulong request, byte[] data);

    /// <summary><c>FE_SET_PROPERTY</c> or <c>FE_GET_PROPERTY</c> on packed entries. Zero, or a negative errno.</summary>
    int Properties(int descriptor, ulong request, byte[] entries);

    /// <summary>Reads what is there. A byte count, or a negative errno.</summary>
    int Read(int descriptor, Span<byte> buffer);

    /// <summary>Waits for something to read.</summary>
    bool WaitReadable(int descriptor, int milliseconds);
}

/// <summary>The real thing, on Linux with glibc.</summary>
internal sealed unsafe partial class LinuxDvbKernel : IDvbKernel
{
    private const string Libc = "libc.so.6";
    private const int ReadOnly = 0;
    private const int ReadWrite = 2;
    private const int NonBlocking = 0x800;
    private const int CloseOnExec = 0x80000;
    private const short PollIn = 1;

    public static readonly LinuxDvbKernel Instance = new();

    /// <summary>Whether this machine can drive a card at all, and what stands in the way if not.</summary>
    public static string? Unsupported =>
        !OperatingSystem.IsLinux() ? "DVB cards need Linux."
        : RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64) ? "DVB cards need a 64 bit x86 or ARM system."
        : null;

    public int Open(string path, bool writable)
    {
        var descriptor = NativeOpen(path, (writable ? ReadWrite : ReadOnly) | NonBlocking | CloseOnExec);

        return descriptor >= 0 ? descriptor : -Marshal.GetLastPInvokeError();
    }

    public void Close(int descriptor) => _ = NativeClose(descriptor);

    public int Control(int descriptor, ulong request, nint value) =>
        NativeControl(descriptor, request, value) >= 0 ? 0 : -Marshal.GetLastPInvokeError();

    public int Control(int descriptor, ulong request, byte[] data)
    {
        fixed (byte* pointer = data)
        {
            return Control(descriptor, request, (nint)pointer);
        }
    }

    public int Properties(int descriptor, ulong request, byte[] entries)
    {
        fixed (byte* pointer = entries)
        {
            var properties = new NativeProperties { Count = (uint)(entries.Length / DvbApi.PropertySize), Entries = pointer };

            return Control(descriptor, request, (nint)(&properties));
        }
    }

    public int Read(int descriptor, Span<byte> buffer)
    {
        fixed (byte* pointer = buffer)
        {
            var count = NativeRead(descriptor, pointer, (nuint)buffer.Length);

            return count >= 0 ? (int)count : -Marshal.GetLastPInvokeError();
        }
    }

    public bool WaitReadable(int descriptor, int milliseconds)
    {
        var entry = new PollEntry { Descriptor = descriptor, Events = PollIn };

        return NativePoll(&entry, 1, milliseconds) > 0;
    }

    [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeOpen(string path, int flags);

    [LibraryImport(Libc, EntryPoint = "close")]
    private static partial int NativeClose(int descriptor);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int NativeControl(int descriptor, ulong request, nint argument);

    [LibraryImport(Libc, EntryPoint = "read", SetLastError = true)]
    private static partial nint NativeRead(int descriptor, byte* buffer, nuint count);

    [LibraryImport(Libc, EntryPoint = "poll")]
    private static partial int NativePoll(PollEntry* entries, nuint count, int milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProperties
    {
        public uint Count;
        public byte* Entries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollEntry
    {
        public int Descriptor;
        public short Events;
        public short Returned;
    }
}
