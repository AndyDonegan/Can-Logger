using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace CanLogger;

/// <summary>
/// Definition of one byte within a CAN message.
/// </summary>
public class ByteDef
{
    public int ByteIndex { get; init; }
    public string Variable { get; init; } = "";
    public string Function { get; init; } = "";
    public string Options { get; init; } = "";
    public string History { get; init; } = "";
}

/// <summary>
/// Full definition of one CAN ID, including per-byte details.
/// </summary>
public class CanIdDef
{
    public uint Id { get; init; }
    public string Description { get; init; } = "";
    public List<ByteDef> Bytes { get; init; } = new();

    public string IdHex => $"0x{Id:X3}";
    public string IdDec => Id.ToString();
}

/// <summary>
/// Loads and provides access to the CAN bus scheme CSV.
/// </summary>
public static class CanScheme
{
    /// <summary>All entries keyed by CAN ID.</summary>
    public static Dictionary<uint, CanIdDef> Entries { get; private set; } = new();

    /// <summary>All entries in order for display.</summary>
    public static List<CanIdDef> AllEntries { get; private set; } = new();

    public static bool IsLoaded => Entries.Count > 0;

    /// <summary>
    /// Load scheme from a CSV file with columns:
    /// CanID, Description, Bit, Variable, Function, Options, History (optional), ...
    /// </summary>
    public static void Load(string path)
    {
        var temp = new Dictionary<uint, CanIdDef>();

        // Read CSV records, not physical lines: quoted Options/History can span lines.
        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");
        while (!parser.EndOfData)
        {
            string[]? cols = parser.ReadFields();
            if (cols == null || cols.Length < 6) continue;

            if (!uint.TryParse(cols[0].Trim(), out uint id)) continue;
            if (!int.TryParse(cols[2].Trim(), out int byteIdx)) continue;

            if (!temp.TryGetValue(id, out var def))
            {
                def = new CanIdDef
                {
                    Id = id,
                    Description = cols[1].Trim(),
                };
                temp[id] = def;
            }

            def.Bytes.Add(new ByteDef
            {
                ByteIndex = byteIdx,
                Variable = cols[3].Trim(),
                Function = cols[4].Trim(),
                Options = cols.Length > 5 ? cols[5].Trim() : "",
                History = cols.Length > 6 ? cols[6].Trim() : "",
            });
        }

        // Sort bytes by index within each entry
        foreach (var def in temp.Values)
            def.Bytes.Sort((a, b) => a.ByteIndex.CompareTo(b.ByteIndex));

        Entries = temp;
        AllEntries = temp.Values.OrderBy(e => e.Id).ToList();
    }

    /// <summary>Get the description for a CAN ID, or null if unknown.</summary>
    public static string? GetDescription(uint id) =>
        Entries.TryGetValue(id, out var def) ? def.Description : null;

    /// <summary>Build a multi-line tooltip with byte-level details.</summary>
    public static string GetTooltipText(uint id)
    {
        if (!Entries.TryGetValue(id, out var def))
            return $"Unknown ID: {id} (0x{id:X})";

        var sb = new StringBuilder();
        sb.AppendLine($"ID {id} (0x{id:X}) — {def.Description}");
        sb.AppendLine();

        foreach (var b in def.Bytes)
        {
            sb.Append($"  Byte {b.ByteIndex}");
            if (!string.IsNullOrEmpty(b.Variable))
                sb.Append($" — {b.Variable}");
            if (!string.IsNullOrEmpty(b.Function))
                sb.Append($": {b.Function}");
            if (!string.IsNullOrEmpty(b.Options))
            {
                // Replace newlines in options with " / " for compact display
                string opts = b.Options.Replace("\n", " / ").Replace("\r", "");
                sb.Append($"  [{opts}]");
            }
            sb.AppendLine();
        }

        AppendHistory(sb, def);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Build a detailed info string for a single CAN ID (shown in the info dialog).</summary>
    public static string GetInfoText(uint id)
    {
        if (!Entries.TryGetValue(id, out var def))
            return $"No scheme data for CAN ID {id} (0x{id:X}).";

        var sb = new StringBuilder();
        sb.AppendLine($"CAN ID {id} (0x{id:X}) — {def.Description}");
        sb.AppendLine();

        foreach (var b in def.Bytes)
        {
            sb.AppendLine($"Byte {b.ByteIndex}: {b.Variable}");
            sb.AppendLine($"  {b.Function}");
            if (!string.IsNullOrEmpty(b.Options))
                sb.AppendLine($"  Options: {b.Options}");
            sb.AppendLine();
        }

        AppendHistory(sb, def);
        return sb.ToString().TrimEnd();
    }

    private static void AppendHistory(StringBuilder sb, CanIdDef def)
    {
        bool headingAdded = false;
        foreach (var b in def.Bytes)
        {
            if (string.IsNullOrWhiteSpace(b.History)) continue;
            if (!headingAdded)
            {
                sb.AppendLine();
                sb.AppendLine("History:");
                headingAdded = true;
            }
            sb.Append($"  Byte {b.ByteIndex}");
            if (!string.IsNullOrEmpty(b.Variable))
                sb.Append($" — {b.Variable}");
            sb.AppendLine($": {b.History}");
        }
    }

}
