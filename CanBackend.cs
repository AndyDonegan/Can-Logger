namespace CanLogger;

/// <summary>
/// Common interface for CAN backends (socketcan or stdin pipe).
/// </summary>
internal interface ICanBackend
{
    event Action<CanMessage>? OnMessageReceived;
    event Action<string>? OnError;
    bool IsRunning { get; }
    string? InterfaceName { get; }
    void Start(string interfaceName);
    void Stop();
    void Send(uint arbitrationId, byte[] data, bool isExtended = false);
}

/// <summary>
/// High-level CAN interface that wraps CanSocket with a background read loop.
/// Thread-safe; raises OnMessageReceived on a dedicated thread.
/// </summary>
public class CanBackend : ICanBackend, IDisposable
{
    private int _socket = -1;
    private volatile bool _running;
    private Thread? _readThread;
    private readonly object _sendLock = new();

    /// <summary>Fired on the reader thread for each received CAN frame.</summary>
    public event Action<CanMessage>? OnMessageReceived;

    /// <summary>Fired when an I/O error occurs on the read loop.</summary>
    public event Action<string>? OnError;

    public bool IsRunning => _running;
    public string? InterfaceName { get; private set; }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void Start(string interfaceName)
    {
        if (_running)
            return;

        _socket = CanSocket.Open(interfaceName);
        CanSocket.SetNonBlocking(_socket);
        InterfaceName = interfaceName;
        _running = true;

        _readThread = new Thread(ReadLoop)
        {
            Name = "CAN-Reader",
            IsBackground = true,
        };
        _readThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _readThread?.Join(TimeSpan.FromSeconds(2));

        if (_socket >= 0)
        {
            CanSocket.CloseSocket(_socket);
            _socket = -1;
        }
        InterfaceName = null;
    }

    public void Dispose()
    {
        Stop();
    }

    // ------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------

    public void Send(uint arbitrationId, byte[] data, bool isExtended = false)
    {
        if (_socket < 0)
            throw new InvalidOperationException("CAN interface not open.");
        lock (_sendLock)
        {
            CanSocket.Send(_socket, arbitrationId, data, isExtended);
        }
    }

    // ------------------------------------------------------------------
    // Read loop (runs on background thread)
    // ------------------------------------------------------------------

    private void ReadLoop()
    {
        while (_running)
        {
            try
            {
                var result = CanSocket.Receive(_socket);
                if (result.HasValue)
                {
                    var (id, isExt, isErr, dlc, data) = result.Value;
                    var msg = new CanMessage(
                        Timestamp: DateTime.Now,
                        ArbitrationId: id,
                        IsExtended: isExt,
                        IsError: isErr,
                        Dlc: dlc,
                        Data: data,
                        ErrorDescription: isErr ? "Error frame received" : null
                    );
                    OnMessageReceived?.Invoke(msg);
                }
                else
                {
                    // No data — short sleep to avoid busy-waiting
                    Thread.Sleep(1);
                }
            }
            catch (IOException ex)
            {
                OnError?.Invoke($"Read error: {ex.Message}");
                _running = false;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Unexpected error: {ex.Message}");
            }
        }
    }
}
