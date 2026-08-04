using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CanLogger;

/// <summary>
/// Discovers Linux SocketCAN network interfaces and prepares them for use.
/// </summary>
public static class CanInterfaceManager
{
    private const string SysClassNetPath = "/sys/class/net";
    private const string IpCommand = "ip";
    private const string CanArpHardwareType = "280";

    private static readonly Regex ValidInterfaceName = new(
        @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Return all interfaces exposed by Linux as CAN network devices.
    /// USB SocketCAN adapters appear here after their kernel driver loads.
    /// </summary>
    public static IReadOnlyList<string> GetCanInterfaces()
    {
        try
        {
            return Directory.EnumerateDirectories(SysClassNetPath)
                .Where(path => ReadText(Path.Combine(path, "type")) == CanArpHardwareType)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Ensure a local CAN interface is up at the selected bitrate. No privileged
    /// command is run when the interface is already configured correctly.
    /// Virtual CAN interfaces have no bitrate and are only brought up if needed.
    /// </summary>
    public static void EnsureReady(string interfaceName, int bitrate)
    {
        Validate(interfaceName, bitrate);

        var state = ReadState(interfaceName);
        if (state.Kind == "vcan")
        {
            if (!state.IsUp)
                RunIp("link", "set", "dev", interfaceName, "up");
            return;
        }

        if (state.Kind != "can")
            throw new IOException($"Interface '{interfaceName}' is not a SocketCAN interface.");

        if (state.IsUp && state.Bitrate == bitrate)
            return;

        bool wasUp = state.IsUp;
        try
        {
            if (wasUp)
                RunIp("link", "set", "dev", interfaceName, "down");
            RunIp("link", "set", "dev", interfaceName, "type", "can",
                "bitrate", bitrate.ToString());
            RunIp("link", "set", "dev", interfaceName, "up");
        }
        catch (IOException ex)
        {
            // Do our best not to leave an interface down if reconfiguration fails.
            if (wasUp)
            {
                try { RunIp("link", "set", "dev", interfaceName, "up"); }
                catch (IOException) { }
            }

            throw new IOException(
                $"Could not configure '{interfaceName}' at {bitrate} bit/s. " +
                "Changing a CAN interface requires root or CAP_NET_ADMIN.\n\n" +
                $"You can configure it before starting the app with:\n" +
                $"sudo ip link set dev {interfaceName} down\n" +
                $"sudo ip link set dev {interfaceName} type can bitrate {bitrate}\n" +
                $"sudo ip link set dev {interfaceName} up\n\n" +
                $"Details: {ex.Message}", ex);
        }
    }

    private static void Validate(string interfaceName, int bitrate)
    {
        if (string.IsNullOrWhiteSpace(interfaceName) ||
            !ValidInterfaceName.IsMatch(interfaceName))
            throw new ArgumentException("Select or enter a valid CAN interface name (for example, can0).");

        if (bitrate <= 0 || bitrate > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(bitrate), "Select a valid positive CAN bitrate.");
    }

    private static CanInterfaceState ReadState(string interfaceName)
    {
        var result = RunIp("-details", "-json", "link", "show", "dev", interfaceName);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var item = document.RootElement.EnumerateArray().First();
            bool isUp = item.TryGetProperty("flags", out var flags) &&
                flags.EnumerateArray().Any(flag => flag.GetString() == "UP");

            string? kind = null;
            int? bitrate = null;
            if (item.TryGetProperty("linkinfo", out var linkInfo))
            {
                if (linkInfo.TryGetProperty("info_kind", out var infoKind))
                    kind = infoKind.GetString();

                if (linkInfo.TryGetProperty("info_data", out var infoData))
                    bitrate = ReadBitrate(infoData);
            }

            return new CanInterfaceState(kind, isUp, bitrate);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            throw new IOException($"Could not read details for interface '{interfaceName}'.", ex);
        }
    }

    private static int? ReadBitrate(JsonElement infoData)
    {
        // Current iproute2 nests CAN timing under "bittiming". Keep the
        // alternate fields for fixed-bitrate controllers and older versions.
        if (infoData.TryGetProperty("bittiming", out var bitTiming) &&
            bitTiming.TryGetProperty("bitrate", out var nestedBitrate) &&
            nestedBitrate.TryGetInt32(out int parsedNestedBitrate))
            return parsedNestedBitrate;

        foreach (string propertyName in new[] { "bittiming_bitrate", "bitrate" })
        {
            if (infoData.TryGetProperty(propertyName, out var bitrateElement) &&
                bitrateElement.TryGetInt32(out int parsedBitrate))
                return parsedBitrate;
        }

        return null;
    }

    private static CommandResult RunIp(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = IpCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new IOException("Failed to start the 'ip' command.");
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string message = string.IsNullOrWhiteSpace(standardError)
                    ? $"ip exited with code {process.ExitCode}."
                    : standardError.Trim();
                throw new IOException(message);
            }

            return new CommandResult(standardOutput, standardError);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException(
                "The Linux 'ip' command is required. Install the iproute2 package.", ex);
        }
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private sealed record CanInterfaceState(string? Kind, bool IsUp, int? Bitrate);
    private sealed record CommandResult(string StandardOutput, string StandardError);
}
