using System.Diagnostics;
using System.Collections.Concurrent;

namespace CanLogger;

/// <summary>Owns a Windows-side, receive-only vendor-library process.</summary>
public sealed class MicrochipLinBackend
{
    public const string AnalyzerId = "microchip-apg-usb";
    private readonly int _initialBaud;
    public MicrochipLinBackend(string analyzerId = AnalyzerId, int initialBaud = 19600)
    {
        if (initialBaud is not (10000 or 19200 or 19600)) throw new ArgumentOutOfRangeException(nameof(initialBaud));
        _initialBaud = initialBaud;
        if (analyzerId != AnalyzerId) throw new ArgumentException("Unsupported LIN analyzer.", nameof(analyzerId));
    }

    private Process? _process;
    private volatile bool _stopping;
    public event Action<LinMessage>? Message;
    public event Action<string>? Status;
    public event Action? UsbResponding;
    public event Action<string>? DiagnosticInfo;

    private static string FindRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "lin", "MicrochipLinReceiver.java"))) return dir.FullName;
        throw new DirectoryNotFoundException("Cannot find LIN receiver. Run from the project folder.");
    }

    private static string WindowsPath(string path)
    {
        if (OperatingSystem.IsWindows()) return path;
        var info = new ProcessStartInfo("wslpath") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-w"); info.ArgumentList.Add(path);
        using var p = Process.Start(info) ?? throw new IOException("Cannot convert Windows path.");
        string result = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException("Cannot convert Windows path.");
        return result;
    }

    public async Task RunAsync()
    {
        var diagnostics = new ConcurrentQueue<string>();
        StreamWriter? trace = null;
        var traceLock = new object();
        long traceBytes = 0;
        void Trace(string kind, string line)
        {
            lock (traceLock)
            {
                if (trace == null) return;
                try {
                    string entry = $"{DateTime.UtcNow:O} {kind} {line}";
                    // Bound disk usage during long diagnostic sessions.
                    if (traceBytes + entry.Length > 2_000_000) {
                        trace.Flush(); trace.BaseStream.SetLength(0); trace.BaseStream.Position = 0;
                        traceBytes = 0;
                        trace.WriteLine("Trace rolled over; older entries discarded.");
                    }
                    trace.WriteLine(entry); traceBytes += entry.Length + 1;
                } catch (IOException) { trace.Dispose(); trace = null; }
            }
        }
        try
        {
            string root = FindRoot(), receiver = Path.Combine(root, ".vendor", "microchip-lin", "receiver");
            string java = Path.Combine(receiver, "runtime", "zulu8.96.0.205-ca-fx-jdk8.0.504-win_x64", "bin", "java.exe");
            if (!File.Exists(java) || !File.Exists(Path.Combine(receiver, "classes", "MicrochipLinReceiver.class")))
                throw new FileNotFoundException("LIN dependencies missing. Run: python3 scripts/setup-lin-receiver.py");
            lock (traceLock) {
                trace = new StreamWriter(new FileStream(Path.Combine(receiver, "last-session.log"),
                    FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            }
            Trace("SESSION", "LIN receive started (UTC timestamps; raw FRAME bytes include checksum if present)");
            var info = new ProcessStartInfo(java) { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
            info.ArgumentList.Add("-cp");
            info.ArgumentList.Add(WindowsPath(Path.Combine(receiver, "classes")) + ";" + WindowsPath(Path.Combine(receiver, "lib")) + "\\*");
            info.ArgumentList.Add("MicrochipLinReceiver");
            info.ArgumentList.Add("--initial-baud");
            info.ArgumentList.Add(_initialBaud.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = new Process { StartInfo = info };
            _process = process;
            if (_stopping) return;
            process.Start();
            var stderr = Task.Run(async () => {
                while (await process.StandardError.ReadLineAsync() is { } line) {
                    Trace("VENDOR", line);
                    diagnostics.Enqueue(line);
                    while (diagnostics.Count > 12) diagnostics.TryDequeue(out _);
                }
            });
            bool ready = false;
            int usbInputs = 0;
            string configuredBaud = "unknown";
            void ReportDiagnostics() => DiagnosticInfo?.Invoke($"Adapter rate: {configuredBaud} (readback; initial {_initialBaud}) • USB input reports: {usbInputs}");
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                string? line = await process.StandardOutput.ReadLineAsync(ready ? healthTimeout.Token : startup.Token);
                if (line == null) break;
                Trace("USB", line);
                if (line.StartsWith("READY\t")) { ready = true; Status?.Invoke("Receiving — waiting for LIN traffic"); UsbResponding?.Invoke(); }
                else if (line == "HEARTBEAT") { UsbResponding?.Invoke(); ReportDiagnostics(); }
                else if (line.StartsWith("CONFIG_BAUD\t")) { configuredBaud = line.Split('\t')[1]; ReportDiagnostics(); }
                else if (line.StartsWith("RAW_USB\t")) {
                    var fields = line.Split('\t');
                    if (fields.Length == 5 && fields[2] == "__rawRead") usbInputs++;
                }
                else if (line.StartsWith("FRAME\t")) Message?.Invoke(LinMessage.Parse(line));
                else if (line.StartsWith("ERROR\t")) throw new IOException(line[6..]);
            }
            await process.WaitForExitAsync(); await stderr;
            if (!_stopping) throw new IOException("LIN receiver exited. " + string.Join(" ", diagnostics));
        }
        catch (Exception ex) { Trace("ERROR", ex.Message); if (!_stopping) Status?.Invoke("LIN error: " + ex.Message); }
        finally
        {
            var process = _process;
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
            _process = null;
            Trace("SESSION", "LIN receive ended");
            lock (traceLock) { trace?.Dispose(); trace = null; }
        }
    }

    public void Stop()
    {
        _stopping = true;
        var process = _process;
        if (process == null) return;
        try { process.StandardInput.WriteLine("STOP"); process.StandardInput.Flush(); } catch { }
        _ = Task.Run(async () => {
            await Task.Delay(2500);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
    }
}
