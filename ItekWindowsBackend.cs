using System.Diagnostics;
using System.Globalization;

namespace CanLogger;

/// <summary>
/// Linux/WSL backend that launches the Windows iTEK USBCAN bridge.
/// </summary>
public sealed class ItekWindowsBackend : ICanBackend, IDisposable
{
    public const string Interface = "itek-usbcan";

    private const string WindowsDotnet = "/mnt/c/Program Files/dotnet/dotnet.exe";
    private readonly int _bitrate;
    private readonly object _sendLock = new();
    private Process? _process;
    private Thread? _readerThread;
    private Thread? _errorThread;
    private volatile bool _running;

    public ItekWindowsBackend(int bitrate)
    {
        _bitrate = bitrate;
    }

    public event Action<CanMessage>? OnMessageReceived;
    public event Action<string>? OnError;

    public bool IsRunning => _running && _process is { HasExited: false };
    public string? InterfaceName { get; private set; }

    public static bool IsItekInterface(string interfaceName) => interfaceName == Interface;

    public static IReadOnlyList<string> GetAvailableInterfaces()
    {
        bool isWsl = File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop") ||
            Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") != null;
        bool bridgeReady = File.Exists(WindowsDotnet) &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, "kerneldlls", "usbcan.dll"));
        return isWsl && bridgeReady ? new[] { Interface } : Array.Empty<string>();
    }

    public void Start(string interfaceName)
    {
        if (_running)
            return;
        if (!IsItekInterface(interfaceName))
            throw new ArgumentException($"Unknown iTEK interface '{interfaceName}'.");
        if (!File.Exists(WindowsDotnet))
            throw new IOException("Windows .NET 8 is required for the iTEK bridge.");

        string assemblyPath = Path.Combine(AppContext.BaseDirectory, "CanLogger.dll");
        string transportDll = Path.Combine(AppContext.BaseDirectory, "kerneldlls", "usbcan.dll");
        if (!File.Exists(transportDll))
            throw new IOException(
                "The iTEK USBCAN API is missing. Run scripts/install-itek-api.sh and rebuild.");

        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsDotnet,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(ToWindowsPath(assemblyPath));
        startInfo.ArgumentList.Add("--itek-bridge");
        startInfo.ArgumentList.Add("--bitrate");
        startInfo.ArgumentList.Add(_bitrate.ToString(CultureInfo.InvariantCulture));

        _process = Process.Start(startInfo)
            ?? throw new IOException("Could not launch the Windows iTEK bridge.");

        try
        {
            Task<string?> readyTask = _process.StandardOutput.ReadLineAsync();
            if (!readyTask.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out while opening the iTEK USBCAN-I analyser.");

            string? response = readyTask.Result;
            if (response == null)
            {
                string details = _process.StandardError.ReadToEnd().Trim();
                throw new IOException(string.IsNullOrEmpty(details)
                    ? "The Windows iTEK bridge stopped before opening the analyser."
                    : details);
            }
            if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                throw new IOException(response[6..]);
            if (!response.StartsWith("READY|", StringComparison.Ordinal))
                throw new IOException($"Unexpected iTEK bridge response: {response}");

            InterfaceName = interfaceName;
            _running = true;
            _readerThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "iTEK-CAN-Reader",
            };
            _readerThread.Start();

            _errorThread = new Thread(ErrorLoop)
            {
                IsBackground = true,
                Name = "iTEK-Error-Reader",
            };
            _errorThread.Start();
        }
        catch
        {
            StopProcess();
            throw;
        }
    }

    public void Stop()
    {
        _running = false;
        if (_process != null && !_process.HasExited)
        {
            try
            {
                lock (_sendLock)
                {
                    _process.StandardInput.WriteLine("STOP");
                    _process.StandardInput.Flush();
                }
                _process.WaitForExit(3000);
            }
            catch (Exception) { }
        }

        _readerThread?.Join(TimeSpan.FromSeconds(1));
        _errorThread?.Join(TimeSpan.FromSeconds(1));
        StopProcess();
        InterfaceName = null;
    }

    public void Send(uint arbitrationId, byte[] data, bool isExtended = false)
    {
        if (!IsRunning || _process == null)
            throw new InvalidOperationException("iTEK CAN interface not open.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN frames cannot exceed 8 bytes.");

        lock (_sendLock)
        {
            _process.StandardInput.WriteLine(
                $"SEND|{arbitrationId:X}|{(isExtended ? 1 : 0)}|{Convert.ToHexString(data)}");
            _process.StandardInput.Flush();
        }
    }

    public void Dispose() => Stop();

    private void ReadLoop()
    {
        try
        {
            string? line;
            while (_running && _process != null &&
                   (line = _process.StandardOutput.ReadLine()) != null)
            {
                if (line.StartsWith("FRAME|", StringComparison.Ordinal))
                {
                    CanMessage? message = ParseFrame(line);
                    if (message != null)
                        OnMessageReceived?.Invoke(message);
                }
                else if (line.StartsWith("ERROR|", StringComparison.Ordinal))
                {
                    OnError?.Invoke(line[6..]);
                }
            }
        }
        catch (Exception ex)
        {
            if (_running)
                OnError?.Invoke($"iTEK bridge read error: {ex.Message}");
        }
        finally
        {
            if (_running)
            {
                _running = false;
                OnError?.Invoke("iTEK bridge stopped.");
            }
        }
    }

    private void ErrorLoop()
    {
        try
        {
            string? line;
            while (_running && _process != null &&
                   (line = _process.StandardError.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    OnError?.Invoke(line);
            }
        }
        catch (Exception) { }
    }

    private static CanMessage? ParseFrame(string line)
    {
        string[] parts = line.Split('|');
        if (parts.Length != 6 || !long.TryParse(parts[1], out long ticks) ||
            !uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
            return null;

        byte[] data;
        try { data = Convert.FromHexString(parts[5]); }
        catch (FormatException) { return null; }

        bool extended = parts[3] == "1";
        bool error = parts[4] == "1";
        return new CanMessage(
            Timestamp: new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(),
            ArbitrationId: id,
            IsExtended: extended,
            IsError: error,
            Dlc: (byte)data.Length,
            Data: data,
            ErrorDescription: error ? "Error frame received" : null);
    }

    private static string ToWindowsPath(string linuxPath)
    {
        if (linuxPath.StartsWith("/mnt/", StringComparison.Ordinal) && linuxPath.Length > 6)
        {
            char drive = char.ToUpperInvariant(linuxPath[5]);
            return $"{drive}:\\{linuxPath[7..].Replace('/', '\\')}";
        }

        string distro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")
            ?? throw new IOException("WSL_DISTRO_NAME is not set; cannot address the bridge from Windows.");
        return $"\\\\wsl.localhost\\{distro}{linuxPath.Replace('/', '\\')}";
    }

    private void StopProcess()
    {
        if (_process == null)
            return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
        _process.Dispose();
        _process = null;
    }
}
