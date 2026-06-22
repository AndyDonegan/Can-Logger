using System.Runtime.InteropServices;

namespace CanLogger;

/// <summary>
/// Low-level Linux SocketCAN wrapper using P/Invoke.
/// Provides raw send/receive of CAN frames via socketcan.
/// </summary>
public static class CanSocket
{
    // ------------------------------------------------------------------
    // Constants from <linux/can.h> and <linux/can/raw.h>
    // ------------------------------------------------------------------
    private const int AF_CAN = 29;
    private const int PF_CAN = AF_CAN;
    private const int SOL_CAN_RAW = 101;
    private const int CAN_RAW = 1;

    // CAN frame flags (bits within can_id)
    public const uint CAN_EFF_FLAG = 0x80000000U; // Extended Frame Format
    public const uint CAN_RTR_FLAG = 0x40000000U; // Remote Transmission Request
    public const uint CAN_ERR_FLAG = 0x20000000U; // Error frame

    public const uint CAN_SFF_MASK = 0x000007FFU; // Standard frame mask (11-bit)
    public const uint CAN_EFF_MASK = 0x1FFFFFFFU; // Extended frame mask (29-bit)

    public const int CAN_MAX_DLEN = 8; // Classic CAN max data length

    // ------------------------------------------------------------------
    // Native structs
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct can_frame
    {
        public uint can_id;   // 32-bit CAN_ID + EFF/RTR/ERR flags
        public byte can_dlc;  // data length code (0..8)
        public byte __pad;
        public byte __res0;
        public byte __res1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = CAN_MAX_DLEN)]
        public byte[] data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct sockaddr_can
    {
        public ushort can_family; // AF_CAN
        public int can_ifindex;
        // union { struct { canid_t rx_id, tx_id; } tp; } can_addr — not needed for RAW
    }

    // ------------------------------------------------------------------
    // libc P/Invoke
    // ------------------------------------------------------------------

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int bind(int sockfd, ref sockaddr_can addr, int addrlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr write(int fd, IntPtr buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr read(int fd, IntPtr buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int sockfd, int level, int optname,
        IntPtr optval, int optlen);

    [DllImport("libc", SetLastError = true)]
    private static extern uint if_nametoindex(string ifname);

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>Open a raw CAN socket bound to the given interface.</summary>
    public static int Open(string interfaceName)
    {
        uint ifindex = if_nametoindex(interfaceName);
        if (ifindex == 0)
            throw new IOException($"Interface '{interfaceName}' not found: {Marshal.GetLastPInvokeErrorMessage()}");

        int sock = socket(PF_CAN, 1 /* SOCK_RAW */, CAN_RAW);
        if (sock < 0)
            throw new IOException($"socket() failed: {Marshal.GetLastPInvokeErrorMessage()}");

        var addr = new sockaddr_can
        {
            can_family = AF_CAN,
            can_ifindex = (int)ifindex,
        };

        if (bind(sock, ref addr, Marshal.SizeOf<sockaddr_can>()) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            close(sock);
            throw new IOException($"bind() failed: {new System.ComponentModel.Win32Exception(errno).Message}");
        }

        return sock;
    }

    /// <summary>Close a CAN socket.</summary>
    public static void CloseSocket(int sock)
    {
        close(sock);
    }

    /// <summary>Send a CAN frame.</summary>
    public static void Send(int sock, uint canId, ReadOnlySpan<byte> data, bool isExtended)
    {
        if (data.Length > CAN_MAX_DLEN)
            throw new ArgumentException($"Data length {data.Length} exceeds CAN max {CAN_MAX_DLEN}");

        var frame = new can_frame
        {
            can_id = (isExtended ? CAN_EFF_FLAG : 0) | (canId & (isExtended ? CAN_EFF_MASK : CAN_SFF_MASK)),
            can_dlc = (byte)data.Length,
            data = new byte[CAN_MAX_DLEN],
        };
        data.CopyTo(frame.data);

        int size = Marshal.SizeOf<can_frame>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(frame, ptr, false);
            IntPtr written = write(sock, ptr, size);
            if (written == (IntPtr)(-1))
                throw new IOException($"write() failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Try to receive a CAN frame (non-blocking). Returns null if no data available.</summary>
    public static (uint canId, bool isExtended, bool isError, byte dlc, byte[] data)? Receive(int sock)
    {
        int size = Marshal.SizeOf<can_frame>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            IntPtr n = read(sock, ptr, size);
            if (n == (IntPtr)(-1))
            {
                int errno = Marshal.GetLastPInvokeError();
                // EAGAIN/EWOULDBLOCK → no data available
                if (errno == 11 || errno == 11)
                    return null;
                throw new IOException($"read() failed: {new System.ComponentModel.Win32Exception(errno).Message}");
            }
            if ((int)n < Marshal.SizeOf<can_frame>())
                return null;

            var frame = Marshal.PtrToStructure<can_frame>(ptr);
            bool isExtended = (frame.can_id & CAN_EFF_FLAG) != 0;
            bool isError = (frame.can_id & CAN_ERR_FLAG) != 0;
            uint id = frame.can_id & (isExtended ? CAN_EFF_MASK : CAN_SFF_MASK);
            byte dlc = frame.can_dlc;
            byte[] data = new byte[dlc];
            Array.Copy(frame.data, data, dlc);
            return (id, isExtended, isError, dlc, data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Enable non-blocking mode on the socket.</summary>
    public static void SetNonBlocking(int sock)
    {
        // F_GETFL / F_SETFL with O_NONBLOCK
        int flags = fcntl(sock, 3 /* F_GETFL */, 0);
        if (flags < 0)
            throw new IOException($"fcntl(F_GETFL) failed: {Marshal.GetLastPInvokeErrorMessage()}");
        if (fcntl(sock, 4 /* F_SETFL */, flags | 2048 /* O_NONBLOCK */) < 0)
            throw new IOException($"fcntl(F_SETFL) failed: {Marshal.GetLastPInvokeErrorMessage()}");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
