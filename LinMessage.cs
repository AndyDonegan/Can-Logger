using System.Globalization;

namespace CanLogger;

public sealed record LinMessage(DateTime Timestamp, byte Pid, int Baud, double DeviceTime,
    int ReceiveError, byte[] RawBytes)
{
    public int Id => Pid & 0x3f;
    public byte ExpectedPid
    {
        get
        {
            int Bit(int n) => (Id >> n) & 1;
            int p0 = Bit(0) ^ Bit(1) ^ Bit(2) ^ Bit(4);
            int p1 = 1 ^ Bit(1) ^ Bit(3) ^ Bit(4) ^ Bit(5);
            return (byte)(Id | (p0 << 6) | (p1 << 7));
        }
    }
    public bool ParityValid => Pid == ExpectedPid;
    public bool Complete => ReceiveError == 0 && RawBytes.Length >= 2;
    public string Payload => Complete ? Convert.ToHexString(RawBytes[..^1]) : "";
    /// <summary>The last captured byte; its presence alone does not prove a checksum was received.</summary>
    public string ChecksumByte => RawBytes.Length == 0 ? "—" : $"0x{RawBytes[^1]:X2}";
    public string ChecksumStatus
    {
        get
        {
            if (RawBytes.Length == 0) return "Unavailable (no bytes)";
            if (RawBytes.Length == 1) return "Not testable (only one byte)";
            bool Matches(int sum)
            {
                foreach (byte value in RawBytes[..^1])
                {
                    sum += value;
                    if (sum > 255) sum -= 255;
                }
                return (byte)~sum == RawBytes[^1];
            }
            bool classic = Matches(0), enhanced = Id < 60 && Matches(Pid);
            bool candidate = ReceiveError != 0 || !ParityValid;
            if (!classic && !enhanced) return candidate ? "No match (candidate)" : "Invalid";
            string mode = classic && enhanced ? "both" : classic ? "classic" : "enhanced";
            if (candidate) return $"Possible {mode} match ({(ReceiveError != 0 ? "receive error" : "PID error")})";
            return mode == "both" ? "Valid (both)" : $"Valid {mode}";
        }
    }
    public string Status => (ParityValid ? "" : "PID parity error; ") + (ReceiveError switch
    {
        0 => RawBytes.Length < 2 ? "Incomplete response" : ChecksumStatus,
        1 => "Bus timeout", 3 => "Timer error", 4 => "Adapter status error",
        5 => "Event marker error", 6 => "Next header before completion",
        _ => $"Receive error {ReceiveError}"
    });

    public static LinMessage Parse(string line)
    {
        var p = line.Split('\t');
        if (p.Length != 7 || p[0] != "FRAME") throw new FormatException("Invalid LIN bridge record.");
        var culture = CultureInfo.InvariantCulture;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(p[1], culture)).LocalDateTime;
        var pid = byte.Parse(p[2], culture);
        int baud = int.Parse(p[3], culture), error = int.Parse(p[5], culture);
        double time = double.Parse(p[4], culture);
        byte[] bytes = Convert.FromHexString(p[6]);
        if (bytes.Length > 9 || baud < 0 || baud > 65535 || error < 0 || !double.IsFinite(time))
            throw new FormatException("LIN bridge value out of range.");
        return new(timestamp, pid, baud, time, error, bytes);
    }
}
