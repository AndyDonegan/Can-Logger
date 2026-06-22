namespace CanLogger;

/// <summary>
/// Represents a single CAN bus message for display/logging.
/// </summary>
public record CanMessage(
    DateTime Timestamp,
    uint ArbitrationId,
    bool IsExtended,
    bool IsError,
    byte Dlc,
    byte[] Data,
    string? ErrorDescription = null
)
{
    /// <summary>Format the CAN ID as hex string with appropriate width.</summary>
    public string IdHex => IsExtended
        ? $"0x{ArbitrationId:X8}"
        : $"0x{ArbitrationId:X3}";

    /// <summary>Format data bytes as space-separated hex.</summary>
    public string DataHex => Data.Length == 0
        ? "-"
        : BitConverter.ToString(Data).Replace('-', ' ');

    /// <summary>Frame type label.</summary>
    public string FrameType => IsError ? "ERR" : IsExtended ? "EXT" : "STD";
}
