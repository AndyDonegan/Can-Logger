using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CanLogger;

/// <summary>
/// Windows-side bridge entry point for the Waveshare USB-CAN-FD vendor API.
/// The Linux GUI launches this assembly through Windows .NET and communicates
/// using a small line-oriented protocol over stdin/stdout.
/// </summary>
internal static class WaveshareBridgeProgram
{
    private const uint DeviceType = 41; // ZCAN_USBCANFD_200U
    private const uint StatusOk = 1;
    private const uint CanFd = 1;
    private const byte ClassicReceiveQueue = 0;
    private const byte CanFdReceiveQueue = 1;

    public static int Run(string[] args)
    {
        IntPtr device = IntPtr.Zero;
        IntPtr channel = IntPtr.Zero;
        try
        {
            int channelIndex = ReadIntArgument(args, "--channel", 0);
            int bitrate = ReadIntArgument(args, "--bitrate", 125_000);
            bool listenOnly = args.Contains("--listen-only", StringComparer.Ordinal);

            if (channelIndex is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(channelIndex), "Channel must be 0 or 1.");
            if (bitrate <= 0)
                throw new ArgumentOutOfRangeException(nameof(bitrate));

            device = WaveshareNative.ZCAN_OpenDevice(DeviceType, 0, 0);
            if (device == IntPtr.Zero)
                throw new IOException(
                    "Waveshare USB-CAN-FD could not be opened. Close CANFDToolPro and check the WinUSB driver.");

            if (WaveshareNative.ZCAN_SetAbitBaud(device, (uint)channelIndex, (uint)bitrate) != StatusOk)
                throw new IOException($"The analyser rejected arbitration bitrate {bitrate}.");
            // The USB-CAN-FD hardware must be initialized as a CAN-FD controller even
            // when the connected bus only carries classic CAN frames. The vendor API
            // still places classic frames in its separate TYPE_CAN receive queue.
            if (WaveshareNative.ZCAN_SetDbitBaud(device, (uint)channelIndex, (uint)bitrate) != StatusOk)
                throw new IOException($"The analyser rejected data bitrate {bitrate}.");

            var config = new WaveshareChannelInitConfig
            {
                CanType = CanFd,
                AcceptanceCode = 0,
                AcceptanceMask = uint.MaxValue,
                CanFdFilter = 1,
                CanFdMode = listenOnly ? (byte)1 : (byte)0,
            };

            channel = WaveshareNative.ZCAN_InitCAN(device, (uint)channelIndex, ref config);
            if (channel == IntPtr.Zero)
                throw new IOException($"Could not initialize Waveshare CAN{channelIndex + 1}.");
            if (WaveshareNative.ZCAN_StartCAN(channel) != StatusOk)
                throw new IOException($"Could not start Waveshare CAN{channelIndex + 1}.");

            WaveshareNative.ZCAN_ClearBuffer(channel);
            string serial = ReadSerial(device);
            Console.WriteLine($"READY|{serial}|{channelIndex + 1}|{bitrate}");
            Console.Out.Flush();

            return RunBridgeLoop(channel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR|{OneLine(ex.Message)}");
            Console.Out.Flush();
            return 1;
        }
        finally
        {
            if (channel != IntPtr.Zero)
                WaveshareNative.ZCAN_ResetCAN(channel);
            if (device != IntPtr.Zero)
                WaveshareNative.ZCAN_CloseDevice(device);
        }
    }

    private static int RunBridgeLoop(IntPtr channel)
    {
        var commands = new ConcurrentQueue<string>();
        int inputClosed = 0;
        var inputThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = Console.ReadLine()) != null)
                    commands.Enqueue(line);
            }
            finally
            {
                Volatile.Write(ref inputClosed, 1);
            }
        })
        {
            IsBackground = true,
            Name = "Waveshare-Command-Reader",
        };
        inputThread.Start();

        var classicFrames = new WaveshareReceiveData[100];
        var canFdFrames = new WaveshareReceiveFdData[100];
        while (Volatile.Read(ref inputClosed) == 0 || !commands.IsEmpty)
        {
            while (commands.TryDequeue(out string? command))
            {
                if (command == "STOP")
                    return 0;
                if (command.StartsWith("SEND|", StringComparison.Ordinal))
                    SendFrame(channel, command);
            }

            bool receivedAny = ReceiveClassicFrames(channel, classicFrames);
            receivedAny |= ReceiveCanFdFrames(channel, canFdFrames);
            if (!receivedAny)
                Thread.Sleep(2);
        }

        return 0;
    }

    private static bool ReceiveClassicFrames(IntPtr channel, WaveshareReceiveData[] frames)
    {
        uint pending = WaveshareNative.ZCAN_GetReceiveNum(channel, ClassicReceiveQueue);
        if (pending == 0)
            return false;

        uint wanted = Math.Min(pending, (uint)frames.Length);
        uint received = WaveshareNative.ZCAN_Receive(channel, frames, wanted, 0);
        if (received > frames.Length)
            return false;

        for (int i = 0; i < received; i++)
            WriteFrame(frames[i]);
        Console.Out.Flush();
        return received > 0;
    }

    private static bool ReceiveCanFdFrames(IntPtr channel, WaveshareReceiveFdData[] frames)
    {
        uint pending = WaveshareNative.ZCAN_GetReceiveNum(channel, CanFdReceiveQueue);
        if (pending == 0)
            return false;

        uint wanted = Math.Min(pending, (uint)frames.Length);
        uint received = WaveshareNative.ZCAN_ReceiveFD(channel, frames, wanted, 0);
        if (received > frames.Length)
            return false;

        for (int i = 0; i < received; i++)
            WriteFrame(frames[i]);
        Console.Out.Flush();
        return received > 0;
    }

    private static unsafe void SendFrame(IntPtr channel, string command)
    {
        string[] parts = command.Split('|');
        if (parts.Length != 4 || !uint.TryParse(parts[1],
                System.Globalization.NumberStyles.HexNumber, null, out uint id))
            throw new FormatException("Invalid SEND command.");

        bool extended = parts[2] == "1";
        byte[] data = Convert.FromHexString(parts[3]);
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN frames cannot exceed 8 bytes.");

        var frame = new WaveshareTransmitData { TransmitType = 0 };
        frame.Frame.CanId = id | (extended ? WaveshareNative.CanEffFlag : 0);
        frame.Frame.Dlc = (byte)data.Length;
        for (int i = 0; i < data.Length; i++)
            frame.Frame.Data[i] = data[i];

        if (WaveshareNative.ZCAN_Transmit(channel, ref frame, 1) != 1)
            throw new IOException("The Waveshare analyser did not accept the CAN frame for transmission.");
    }

    private static unsafe void WriteFrame(WaveshareReceiveData received)
    {
        uint rawId = received.Frame.CanId;
        uint id = rawId & WaveshareNative.CanIdMask;
        bool extended = (rawId & WaveshareNative.CanEffFlag) != 0;
        bool error = (rawId & WaveshareNative.CanErrFlag) != 0;
        int length = Math.Min(received.Frame.Dlc, (byte)8);
        Span<byte> data = stackalloc byte[length];
        for (int i = 0; i < length; i++)
            data[i] = received.Frame.Data[i];

        Console.WriteLine(
            $"FRAME|{DateTime.UtcNow.Ticks}|{id:X}|{(extended ? 1 : 0)}|{(error ? 1 : 0)}|{Convert.ToHexString(data)}");
    }

    private static unsafe void WriteFrame(WaveshareReceiveFdData received)
    {
        uint rawId = received.Frame.CanId;
        uint id = rawId & WaveshareNative.CanIdMask;
        bool extended = (rawId & WaveshareNative.CanEffFlag) != 0;
        bool error = (rawId & WaveshareNative.CanErrFlag) != 0;
        int length = Math.Min(received.Frame.Length, (byte)64);
        Span<byte> data = stackalloc byte[length];
        for (int i = 0; i < length; i++)
            data[i] = received.Frame.Data[i];

        Console.WriteLine(
            $"FRAME|{DateTime.UtcNow.Ticks}|{id:X}|{(extended ? 1 : 0)}|{(error ? 1 : 0)}|{Convert.ToHexString(data)}");
    }

    private static string ReadSerial(IntPtr device)
    {
        if (WaveshareNative.ZCAN_GetDeviceInf(device, out WaveshareDeviceInfo info) != StatusOk)
            return "unknown";
        int end = Array.IndexOf(info.SerialNumber, (byte)0);
        if (end < 0) end = info.SerialNumber.Length;
        return System.Text.Encoding.ASCII.GetString(info.SerialNumber, 0, end).Trim();
    }

    private static int ReadIntArgument(string[] args, string name, int defaultValue)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0)
            return defaultValue;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value))
            throw new ArgumentException($"{name} requires an integer value.");
        return value;
    }

    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct WaveshareChannelInitConfig
{
    [FieldOffset(0)] public uint CanType;
    [FieldOffset(4)] public uint AcceptanceCode;
    [FieldOffset(8)] public uint AcceptanceMask;
    [FieldOffset(12)] public uint ArbitrationTiming;
    [FieldOffset(16)] public uint DataTiming;
    [FieldOffset(20)] public uint Brp;
    [FieldOffset(24)] public byte CanFdFilter;
    [FieldOffset(25)] public byte CanFdMode;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaveshareCanFrame
{
    public uint CanId;
    public byte Dlc;
    public byte Pad;
    public byte Reserved0;
    public byte Reserved1;
    public fixed byte Data[8];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaveshareReceiveData
{
    public WaveshareCanFrame Frame;
    public ulong Timestamp;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaveshareTransmitData
{
    public WaveshareCanFrame Frame;
    public uint TransmitType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaveshareCanFdFrame
{
    public uint CanId;
    public byte Length;
    public byte Flags;
    public byte Reserved0;
    public byte Reserved1;
    public fixed byte Data[64];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaveshareReceiveFdData
{
    public WaveshareCanFdFrame Frame;
    public ulong Timestamp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveshareDeviceInfo
{
    public ushort HardwareVersion;
    public ushort FirmwareVersion;
    public ushort DriverVersion;
    public ushort InterfaceVersion;
    public ushort IrqNumber;
    public byte CanNum;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
    public byte[] SerialNumber;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
    public byte[] HardwareType;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public ushort[] Reserved;
}

internal static class WaveshareNative
{
    public const uint CanEffFlag = 0x80000000U;
    public const uint CanErrFlag = 0x20000000U;
    public const uint CanIdMask = 0x1FFFFFFFU;

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr ZCAN_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_CloseDevice(IntPtr deviceHandle);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_GetDeviceInf(IntPtr deviceHandle, out WaveshareDeviceInfo info);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_SetAbitBaud(IntPtr deviceHandle, uint channelIndex, uint bitrate);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_SetDbitBaud(IntPtr deviceHandle, uint channelIndex, uint bitrate);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr ZCAN_InitCAN(IntPtr deviceHandle, uint channelIndex,
        ref WaveshareChannelInitConfig config);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_StartCAN(IntPtr channelHandle);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_ResetCAN(IntPtr channelHandle);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_ClearBuffer(IntPtr channelHandle);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_GetReceiveNum(IntPtr channelHandle, byte type);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_Receive(IntPtr channelHandle,
        [Out] WaveshareReceiveData[] frames, uint length, int waitTime);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_ReceiveFD(IntPtr channelHandle,
        [Out] WaveshareReceiveFdData[] frames, uint length, int waitTime);

    [DllImport("ControlCANFD.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint ZCAN_Transmit(IntPtr channelHandle,
        ref WaveshareTransmitData frame, uint length);
}
