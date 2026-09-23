namespace CanLogger;

/// <summary>Reported PSU configuration, not proof of a responding appliance.</summary>
public sealed record LinHeaterConfiguration(byte Setting, DateTime ObservedAt)
{
    public static LinHeaterConfiguration? FromCan(CanMessage message) =>
        message.ArbitrationId == 133 && !message.IsExtended && !message.IsError &&
        message.Dlc == 8 && message.Data.Length == 8
            ? new(message.Data[2], message.Timestamp) : null;

    public string Description => Setting switch {
        0 => "No CI-BUS heater configured",
        1 => "Alde configured; select the Alde generation manually",
        2 => "Truma CP+ configured",
        3 => "Whale configured",
        4 => "Eberspacher configured",
        5 => "Setting 5 ambiguous: firmware says Webasto; spreadsheet says Whale Ci-Bus",
        _ => $"Unknown heater setting {Setting}"
    };
    public string Evidence => $"CAN 133 / 0x85 B2={Setting} at {ObservedAt:HH:mm:ss}: {Description}. Configuration only; not appliance presence.";
    public string? LayoutFor(int linId, DateTime receivedAt)
    {
        if (receivedAt < ObservedAt || linId is < 55 or > 58) return null;
        string? name = Setting switch { 2 => "Truma CP+", 3 => "Whale", 4 => "Eberspacher", _ => null };
        return LinScheme.Find(linId)?.Layouts.Any(l => l.Name == name) == true ? name : null;
    }
}
