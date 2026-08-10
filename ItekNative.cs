using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CanLogger;

/// <summary>
/// Windows-side bridge for the iTEK/E&amp;J classic USBCAN-I vendor API.
/// </summary>
internal static class ItekBridgeProgram
{
    private const uint DeviceType = 3; // VCI_USBCAN1
    private const uint DeviceIndex = 0;
    private const uint ChannelIndex = 0;
    private const uint StatusOk = 1;

    public static int Run(string[] args)
    {
        bool deviceOpen = false;
        bool channelStarted = false;
        try
        {
            int bitrate = ReadIntArgument(args, "--bitrate", 125_000);
            (byte timing0, byte timing1) = GetBitTiming(bitrate);

            if (ItekNative.VCI_OpenDevice(DeviceType, DeviceIndex, 0) != StatusOk)
                throw new IOException(
                    "iTEK USBCAN-I could not be opened. Close other CAN software and check the iTEK WinUSB driver.");
            deviceOpen = true;

            var config = new ItekInitConfig
            {
                AcceptanceCode = 0,
                AcceptanceMask = uint.MaxValue,
                Reserved = 0,
                Filter = 1,
                Timing0 = timing0,
                Timing1 = timing1,
                Mode = 0,
            };

            if (ItekNative.VCI_InitCAN(DeviceType, DeviceIndex, ChannelIndex, ref config) != StatusOk)
                throw new IOException($"The iTEK analyser rejected bitrate {bitrate}.");
            if (ItekNative.VCI_StartCAN(DeviceType, DeviceIndex, ChannelIndex) != StatusOk)
                throw new IOException("Could not start the iTEK USBCAN-I channel.");
            channelStarted = true;

            ItekNative.VCI_ClearBuffer(DeviceType, DeviceIndex, ChannelIndex);
            Console.WriteLine(
                $"READY|{ReadDeviceIdentity()}|1|{bitrate}|{ReadControllerStatus()}");
            Console.Out.Flush();

            return RunBridgeLoop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR|{OneLine(DescribeLoadFailure(ex))}");
            Console.Out.Flush();
            return 1;
        }
        finally
        {
            if (channelStarted)
                ItekNative.VCI_ResetCAN(DeviceType, DeviceIndex, ChannelIndex);
            if (deviceOpen)
                ItekNative.VCI_CloseDevice(DeviceType, DeviceIndex);
        }
    }

    private static int RunBridgeLoop()
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
            Name = "iTEK-Command-Reader",
        };
        inputThread.Start();

        var frames = new ItekCanObject[100];
        while (Volatile.Read(ref inputClosed) == 0 || !commands.IsEmpty)
        {
            while (commands.TryDequeue(out string? command))
            {
                if (command == "STOP")
                    return 0;
                if (command == "STATUS")
                {
                    Console.WriteLine($"STATUS|{ReadControllerStatus()}");
                    Console.Out.Flush();
                    continue;
                }
                if (command.StartsWith("SEND|", StringComparison.Ordinal))
                    SendFrame(command);
            }

            if (!ReceiveFrames(frames))
                Thread.Sleep(2);
        }

        return 0;
    }

    private static bool ReceiveFrames(ItekCanObject[] frames)
    {
        uint pending = ItekNative.VCI_GetReceiveNum(DeviceType, DeviceIndex, ChannelIndex);
        if (pending == 0)
            return false;
        if (pending == uint.MaxValue)
            throw new IOException($"Could not query iTEK receive queue ({ReadError(ChannelIndex)}).");

        uint wanted = Math.Min(pending, (uint)frames.Length);
        uint received = ItekNative.VCI_Receive(
            DeviceType, DeviceIndex, ChannelIndex, frames, wanted, 0);
        if (received == uint.MaxValue)
            throw new IOException($"iTEK receive failed ({ReadError(ChannelIndex)}).");
        if (received > frames.Length)
            throw new IOException("The iTEK API returned an invalid receive count.");

        for (int index = 0; index < received; index++)
            WriteFrame(frames[index]);
        if (received > 0)
            Console.Out.Flush();
        return received > 0;
    }

    private static unsafe void SendFrame(string command)
    {
        string[] parts = command.Split('|');
        if (parts.Length != 4 || !uint.TryParse(parts[1],
                System.Globalization.NumberStyles.HexNumber, null, out uint id))
            throw new FormatException("Invalid SEND command.");

        bool extended = parts[2] == "1";
        byte[] data = Convert.FromHexString(parts[3]);
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN frames cannot exceed 8 bytes.");

        var frame = new ItekCanObject
        {
            Id = id,
            SendType = 0,
            RemoteFlag = 0,
            ExtendedFlag = extended ? (byte)1 : (byte)0,
            DataLength = (byte)data.Length,
        };
        for (int index = 0; index < data.Length; index++)
            frame.Data[index] = data[index];

        uint sent = ItekNative.VCI_Transmit(
            DeviceType, DeviceIndex, ChannelIndex, ref frame, 1);
        if (sent != 1)
            throw new IOException(
                $"The iTEK analyser did not accept the CAN frame ({ReadError(ChannelIndex)}).");
    }

    private static unsafe void WriteFrame(ItekCanObject frame)
    {
        int length = frame.RemoteFlag == 0 ? Math.Min(frame.DataLength, (byte)8) : 0;
        Span<byte> data = stackalloc byte[length];
        for (int index = 0; index < length; index++)
            data[index] = frame.Data[index];

        Console.WriteLine(
            $"FRAME|{DateTime.UtcNow.Ticks}|{frame.Id:X}|{(frame.ExtendedFlag != 0 ? 1 : 0)}|0|{Convert.ToHexString(data)}");
    }

    private static unsafe string ReadDeviceIdentity()
    {
        if (ItekNative.VCI_ReadBoardInfo(
                DeviceType, DeviceIndex, out ItekBoardInfo info) != StatusOk)
            return "iTEK USBCAN-I";

        byte* serial = info.SerialNumber;
        int length = 0;
        while (length < 20 && serial[length] != 0)
            length++;
        string value = System.Text.Encoding.ASCII
            .GetString(new ReadOnlySpan<byte>(serial, length)).Trim();
        return string.IsNullOrEmpty(value) ? "iTEK USBCAN-I" : value;
    }

    private static string ReadError(uint channelIndex)
    {
        if (ItekNative.VCI_ReadErrInfo(
                DeviceType, DeviceIndex, channelIndex, out ItekErrorInfo info) != StatusOk)
            return "no device error details";
        return $"error 0x{info.ErrorCode:X8}";
    }

    private static string ReadControllerStatus()
    {
        if (ItekNative.VCI_ReadCANStatus(
                DeviceType, DeviceIndex, ChannelIndex, out ItekCanStatus status) != StatusOk)
            return "status-unavailable";

        return $"mode=0x{status.Mode:X2},status=0x{status.Status:X2}," +
            $"rx-errors={status.ReceiveErrorCount},tx-errors={status.TransmitErrorCount}";
    }

    private static (byte Timing0, byte Timing1) GetBitTiming(int bitrate) => bitrate switch
    {
        5_000 => (0xBF, 0xFF),
        10_000 => (0x31, 0x1C),
        20_000 => (0x18, 0x1C),
        40_000 => (0x87, 0xFF),
        50_000 => (0x09, 0x1C),
        80_000 => (0x83, 0xFF),
        100_000 => (0x04, 0x1C),
        125_000 => (0x03, 0x1C),
        200_000 => (0x81, 0xFA),
        250_000 => (0x01, 0x1C),
        400_000 => (0x80, 0xFA),
        500_000 => (0x00, 0x1C),
        666_000 => (0x80, 0xB6),
        800_000 => (0x00, 0x16),
        1_000_000 => (0x00, 0x14),
        _ => throw new ArgumentOutOfRangeException(
            nameof(bitrate), bitrate, "The iTEK USBCAN-I does not support this bitrate setting."),
    };

    private static int ReadIntArgument(string[] args, string name, int defaultValue)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0)
            return defaultValue;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value))
            throw new ArgumentException($"{name} requires an integer value.");
        return value;
    }

    private static string DescribeLoadFailure(Exception ex) => ex switch
    {
        DllNotFoundException =>
            "The iTEK USBCAN API or its Visual C++ runtime dependency could not be loaded. " +
            "Run scripts/install-itek-api.sh, rebuild, and install the Microsoft Visual C++ 2015-2022 x64 runtime.",
        BadImageFormatException =>
            "The iTEK USBCAN API has the wrong architecture; the 64-bit DLLs are required.",
        _ => ex.Message,
    };

    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

[StructLayout(LayoutKind.Sequential)]
internal struct ItekInitConfig
{
    public uint AcceptanceCode;
    public uint AcceptanceMask;
    public uint Reserved;
    public byte Filter;
    public byte Timing0;
    public byte Timing1;
    public byte Mode;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ItekCanObject
{
    public uint Id;
    public uint Timestamp;
    public byte TimeFlag;
    public byte SendType;
    public byte RemoteFlag;
    public byte ExtendedFlag;
    public byte DataLength;
    public fixed byte Data[8];
    public fixed byte Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ItekBoardInfo
{
    public ushort HardwareVersion;
    public ushort FirmwareVersion;
    public ushort DriverVersion;
    public ushort InterfaceVersion;
    public ushort IrqNumber;
    public byte CanCount;
    public fixed byte SerialNumber[20];
    public fixed byte HardwareType[40];
    public fixed ushort Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ItekErrorInfo
{
    public uint ErrorCode;
    public fixed byte PassiveErrorData[3];
    public byte ArbitrationLostData;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ItekCanStatus
{
    public byte ErrorInterrupt;
    public byte Mode;
    public byte Status;
    public byte ArbitrationLostCapture;
    public byte ErrorCodeCapture;
    public byte ErrorWarningLimit;
    public byte ReceiveErrorCount;
    public byte TransmitErrorCount;
    public uint Reserved;
}

internal static class ItekNative
{
    private const string LibraryName = "iTEK-usbcan-native";

    static ItekNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(ItekNative).Assembly,
            (libraryName, _, _) => libraryName == LibraryName
                ? NativeLibrary.Load(Path.Combine(
                    AppContext.BaseDirectory, "kerneldlls", "usbcan.dll"))
                : IntPtr.Zero);
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_CloseDevice(uint deviceType, uint deviceIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_InitCAN(
        uint deviceType, uint deviceIndex, uint channelIndex, ref ItekInitConfig config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_ReadBoardInfo(
        uint deviceType, uint deviceIndex, out ItekBoardInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_ReadErrInfo(
        uint deviceType, uint deviceIndex, uint channelIndex, out ItekErrorInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_ReadCANStatus(
        uint deviceType, uint deviceIndex, uint channelIndex, out ItekCanStatus status);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_GetReceiveNum(
        uint deviceType, uint deviceIndex, uint channelIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_ClearBuffer(
        uint deviceType, uint deviceIndex, uint channelIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_StartCAN(
        uint deviceType, uint deviceIndex, uint channelIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_ResetCAN(
        uint deviceType, uint deviceIndex, uint channelIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_Transmit(
        uint deviceType, uint deviceIndex, uint channelIndex,
        ref ItekCanObject frame, uint length);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VCI_Receive(
        uint deviceType, uint deviceIndex, uint channelIndex,
        [Out] ItekCanObject[] frames, uint length, int waitTime);
}
