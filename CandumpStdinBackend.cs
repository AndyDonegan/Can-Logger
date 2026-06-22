using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CanLogger;

/// <summary>
/// CAN backend that reads candump-formatted lines from stdin.
/// Used with: ssh piZero candump can0 | dotnet run -- --stdin
/// 
/// Parses the default candump format:
///   can0  123  [8]  DE AD BE EF 00 00 00 00
/// 
/// Extended IDs (8 hex chars) are detected automatically.
/// Lines that don't match the pattern are silently skipped.
/// </summary>
public class CandumpStdinBackend : ICanBackend, IDisposable
{
    private Thread? _readThread;
    private volatile bool _running;

    /// <summary>Fired on the reader thread for each parsed CAN frame.</summary>
    public event Action<CanMessage>? OnMessageReceived;

    /// <summary>Fired when an I/O error occurs on the read loop.</summary>
    public event Action<string>? OnError;

    public bool IsRunning => _running;
    public string? InterfaceName { get; private set; }

    // Regex to parse default candump format:
    // "  can0  032  [8]  00 00 00 00 00 00 01 00"
    // Group 1: CAN ID (hex)
    // Group 2: DLC
    // Group 3: Data bytes (space-separated hex, may be empty)
    private static readonly Regex LinePattern = new(
        @"^\s*\S+\s+([0-9A-Fa-f]+)\s+\[(\d+)\]\s*(.*)$",
        RegexOptions.Compiled);

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Start reading from stdin. The interfaceName parameter is accepted for
    /// API compatibility with CanBackend but is ignored — we always read stdin.
    /// </summary>
    public void Start(string interfaceName)
    {
        if (_running)
            return;

        InterfaceName = interfaceName; // stored for display purposes only
        _running = true;

        _readThread = new Thread(ReadLoop)
        {
            Name = "Candump-Stdin",
            IsBackground = true,
        };
        _readThread.Start();
    }

    public void Stop()
    {
        _running = false;
        // Note: Console.ReadLine is blocking and not directly interruptible.
        // The thread will exit once the next line arrives or the pipe closes.
        // It's a background thread, so it won't prevent process exit.
    }

    public void Dispose()
    {
        Stop();
    }

    // ------------------------------------------------------------------
    // Send (not supported over stdin)
    // ------------------------------------------------------------------

    public void Send(uint arbitrationId, byte[] data, bool isExtended = false)
    {
        // Build the cansend frame string: <ID>#<DATA>
        string idStr = arbitrationId.ToString(isExtended ? "X8" : "X3");
        string dataStr = string.Concat(data.Select(b => b.ToString("X2")));
        string frame = $"{idStr}#{dataStr}";

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = $"piZero cansend can0 {frame}",
            RedirectStandardInput = true,  // don't inherit the candump pipe
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new IOException("Failed to start ssh process.");
        proc.WaitForExit(5000);

        if (proc.ExitCode != 0)
        {
            string err = proc.StandardError.ReadToEnd().Trim();
            throw new IOException(
                string.IsNullOrEmpty(err) ? $"cansend failed (exit {proc.ExitCode})" : err);
        }
    }

    // ------------------------------------------------------------------
    // Read loop (runs on background thread)
    // ------------------------------------------------------------------

    private void ReadLoop()
    {
        try
        {
            string? line;
            while (_running && (line = Console.ReadLine()) != null)
            {
                var msg = ParseLine(line);
                if (msg != null)
                    OnMessageReceived?.Invoke(msg);
            }
        }
        catch (IOException ex)
        {
            if (_running)
                OnError?.Invoke($"Stdin read error: {ex.Message}");
        }
        catch (Exception ex)
        {
            if (_running)
                OnError?.Invoke($"Unexpected error: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    // ------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------

    /// <summary>
    /// Parse a single candump output line into a CanMessage, or null if
    /// the line doesn't match.
    /// </summary>
    private static CanMessage? ParseLine(string line)
    {
        var match = LinePattern.Match(line);
        if (!match.Success)
            return null;

        // Parse CAN ID
        string idHex = match.Groups[1].Value;
        uint id = Convert.ToUInt32(idHex, 16);

        // Parse DLC
        byte dlc = byte.Parse(match.Groups[2].Value);

        // Parse data bytes
        string dataPart = match.Groups[3].Value.Trim();
        byte[] data;
        if (string.IsNullOrEmpty(dataPart))
        {
            data = Array.Empty<byte>();
        }
        else
        {
            string[] hexBytes = dataPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            data = new byte[hexBytes.Length];
            for (int i = 0; i < hexBytes.Length; i++)
                data[i] = Convert.ToByte(hexBytes[i], 16);
        }

        // Detect extended frames: candump displays standard IDs as 3 hex chars,
        // extended IDs as 8 hex chars. Any ID requiring more than 3 hex digits
        // is an extended frame.
        bool isExtended = idHex.Length > 3;

        // Detect error frames (error flag bit 29 is set)
        bool isError = (id & 0x20000000) != 0;

        // Strip flags to get the real arbitration ID
        uint arbitrationId;
        if (isError)
        {
            arbitrationId = id & 0x1FFFFFFF;
        }
        else if (isExtended)
        {
            arbitrationId = id & 0x1FFFFFFF;
        }
        else
        {
            arbitrationId = id & 0x7FF;
        }

        return new CanMessage(
            Timestamp: DateTime.Now,
            ArbitrationId: arbitrationId,
            IsExtended: isExtended,
            IsError: isError,
            Dlc: dlc,
            Data: data,
            ErrorDescription: isError ? "Error frame received" : null
        );
    }
}
