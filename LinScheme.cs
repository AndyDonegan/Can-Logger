using System.Text;
using System.Text.Json;

namespace CanLogger;

public sealed record LinLayout(string Name, string Source, string Notes, string[] Bytes);
public sealed record LinDefinition(int Id, string Name, string Direction, int Length, string Checksum,
    string Notes, LinLayout[] Layouts)
{
    public byte Pid => new LinMessage(default, (byte)Id, 0, 0, 0, Array.Empty<byte>()).ExpectedPid;
}
public sealed record LinCatalogue(string Source, string Sha256, string ByteNumbering, LinDefinition[] Entries);

/// <summary>Reviewed EC600 source definitions; appliance layouts are selected only by explicit settings or manual choice.</summary>
public static class LinScheme
{
    public static LinCatalogue Catalogue { get; } = Load();
    public static IReadOnlyList<LinDefinition> Entries => Catalogue.Entries;
    public static LinDefinition? Find(int id) => Entries.FirstOrDefault(e => e.Id == id);
    private static LinCatalogue Load()
    {
        using var stream = typeof(LinScheme).Assembly.GetManifestResourceStream("CanLogger.ec600-lin-scheme.json")
            ?? throw new InvalidOperationException("Missing EC600 LIN catalogue.");
        var result = JsonSerializer.Deserialize<LinCatalogue>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (result.Entries.Select(e => e.Id).Distinct().Count() != result.Entries.Length ||
            result.Entries.Any(e => e.Id is < 0 or > 63 || e.Length != 8 || e.Layouts.Length == 0 || e.Layouts.Any(l => l.Bytes.Length != 8)))
            throw new InvalidOperationException("Invalid EC600 LIN catalogue.");
        return result;
    }

    public static string Describe(int id, string? layoutName = null, string? payloadHex = null,
        string? rawHex = null, string? status = null)
    {
        var def = Find(id);
        var text = new StringBuilder($"LIN ID {id} / 0x{id:X2} — {def?.Name ?? "Unknown in reviewed EC600 source"}\n");
        if (def != null)
            text.AppendLine($"Expected PID: 0x{def.Pid:X2} • {def.Direction} • {def.Length} data bytes • {def.Checksum} checksum");
        text.AppendLine("B0 = first data byte; b0 = least significant bit. Binary shown b7 → b0.");
        if (status != null) text.AppendLine($"Capture: {status}");
        if (rawHex != null)
        {
            text.AppendLine($"Raw bytes including checksum/candidate: {(rawHex.Length == 0 ? "none (header only)" : rawHex)}");
            if (string.IsNullOrEmpty(payloadHex))
                text.AppendLine("No complete payload: byte meanings below are reference information, not decoded received values.");
            else
            {
                byte[] bytes = Convert.FromHexString(payloadHex.Replace(" ", ""));
                text.AppendLine("Received payload (checksum excluded):");
                for (int i = 0; i < bytes.Length; i++)
                    text.AppendLine($"B{i}: 0x{bytes[i]:X2} / {bytes[i],3} / {Convert.ToString(bytes[i], 2).PadLeft(8, '0')}");
                if (def != null && bytes.Length != def.Length)
                    text.AppendLine($"Length differs from EC600 source: {bytes.Length} received, {def.Length} expected.");
            }
        }
        if (def == null) return text.ToString();
        if (def.Notes.Length > 0) text.AppendLine(def.Notes);
        var layouts = def.Layouts.Where(l => layoutName == null || l.Name == layoutName).ToArray();
        if (layouts.Length == 0)
        {
            text.AppendLine($"Selected layout '{layoutName}' does not apply to this ID. Showing reference alternatives.");
            layouts = def.Layouts;
        }
        if (layouts.Length > 1) text.AppendLine("Alternative layouts — choose the installed appliance; ID alone does not identify it.");
        foreach (var layout in layouts)
        {
            text.AppendLine($"\n{layout.Name}");
            for (int i = 0; i < layout.Bytes.Length; i++) text.AppendLine($"B{i}: {layout.Bytes[i]}");
            if (layout.Notes.Length > 0) text.AppendLine(layout.Notes);
            text.AppendLine($"Source: {layout.Source}");
        }
        text.AppendLine($"\nReviewed source: {Catalogue.Source}. Unknown bits are not assumed zero or reserved.");
        return text.ToString();
    }
}
