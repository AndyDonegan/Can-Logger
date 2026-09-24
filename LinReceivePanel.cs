using System.Collections.Concurrent;
using Gtk;

namespace CanLogger;

/// <summary>LIN connection and interpretation controls; CAN and LIN have independent lifecycles.</summary>
public sealed class LinReceivePanel : Box
{
    public Box ConnectionControls => this;
    // Emitted on the GTK thread after applying the LIN watch filter.
    public event Action<LinMessage, string, string?, string>? FrameDisplayed;
    private readonly ComboBoxText _analyzer = new();
    private readonly ComboBoxText _baud = new();
    private LinHeaterConfiguration? _heater;
    private readonly Button _start = new("Start LIN");
    private readonly Label _status = new("Stopped");
    private readonly ConcurrentQueue<LinMessage> _pending = new();
    private MicrochipLinBackend? _backend;
    private int _count, _dropped, _filtered;
    private HashSet<int> _watchIds = new();
    private bool _closed, _receiving;
    private DateTime _lastUsbResponse, _lastRecordAt, _nextStatusUpdate;
    private string _lastRecordSummary = "";

    public LinReceivePanel() : base(Orientation.Vertical, 5)
    {
        Margin = 5;
        var bar = new Box(Orientation.Horizontal, 8);
        bar.PackStart(new Label("LIN analyzer:"), false, false, 0);
        _analyzer.Append(MicrochipLinBackend.AnalyzerId, "Microchip APG LIN Analyzer (USB)");
        _analyzer.Active = 0;
        _analyzer.TooltipText = "Supported LIN analyzer type. Connect one Microchip USB LIN analyzer before starting.";
        bar.PackStart(_analyzer, false, false, 0);
        bar.PackStart(new Label("Initial baud:"), false, false, 0);
        foreach (var rate in new[] { "19600", "19200", "10000" }) _baud.AppendText(rate);
        _baud.Active = 0;
        _baud.TooltipText = "Starting analyzer rate. 19600 matches the EC600 source and passed live receive tests. Auto-baud readback can differ.";
        bar.PackStart(_baud, false, false, 0);
        bar.PackStart(_start, false, false, 0);
        ConnectionControls.PackStart(bar, false, false, 0);
        var reference = new Button("EC600 LIN reference");
        reference.Clicked += (_, _) => new LinReferenceWindow(Toplevel as Window);
        bar.PackStart(reference, false, false, 0);
        _status.Xalign = 0; _status.Selectable = true; _status.LineWrap = true;
        ConnectionControls.PackStart(_status, false, false, 0);
        _start.Clicked += async (_, _) => {
            if (_backend != null) { _start.Sensitive = false; _receiving = false; _status.Text = "Stopping LIN…"; _backend.Stop(); return; }
            if (_analyzer.ActiveId != MicrochipLinBackend.AnalyzerId) {
                _status.Text = "Select a supported LIN analyzer first.";
                return;
            }
            _pending.Clear();
            _receiving = false; _lastRecordAt = DateTime.UtcNow; _lastRecordSummary = "";
            var backend = new MicrochipLinBackend(_analyzer.ActiveId, int.Parse(_baud.ActiveText, System.Globalization.CultureInfo.InvariantCulture)); _backend = backend;
            _analyzer.Sensitive = false; _baud.Sensitive = false;
            backend.Message += message => {
                if (_pending.Count < 4000) _pending.Enqueue(message);
                else Interlocked.Increment(ref _dropped);
            };
            backend.Status += message => GLib.Idle.Add(() => {
                if (!_closed && _backend == backend) {
                    _status.Text = message;
                    _receiving = message.StartsWith("Receiving");
                }
                return false;
            });
            backend.UsbResponding += () => GLib.Idle.Add(() => {
                if (!_closed && _backend == backend) _lastUsbResponse = DateTime.UtcNow;
                return false;
            });
            _start.Label = "Stop LIN"; _status.Text = "Connecting to Microchip LIN…";
            await Task.Run(backend.RunAsync);
            GLib.Idle.Add(() => {
                if (!_closed && _backend == backend) {
                    _receiving = false; _backend = null; _start.Label = "Start LIN"; _start.Sensitive = true; _analyzer.Sensitive = true; _baud.Sensitive = true;
                    if (_status.Text.StartsWith("Stopping")) _status.Text = "Stopped";
                }
                return false;
            });
        };
        GLib.Timeout.Add(100, () => {
            if (_closed) return false;
            for (int i = 0; i < 250 && _pending.TryDequeue(out var frame); i++) {
                _count++;
                _lastRecordAt = DateTime.UtcNow;
                _lastRecordSummary = $"Last reported baud {frame.Baud} • {frame.Status}";
                if (_watchIds.Count > 0 && !_watchIds.Contains(frame.Id)) { _filtered++; continue; }
                string? autoLayout = _heater?.LayoutFor(frame.Id, frame.Timestamp);
                string evidence = _heater != null && frame.Timestamp >= _heater.ObservedAt
                    ? _heater.Evidence : "No CAN heater setting observed before this frame; showing reference alternatives.";
                string description = LinScheme.Find(frame.Id)?.Name ?? "Unknown in EC600 source";
                if (autoLayout != null) description = $"{autoLayout} — {(frame.Id < 57 ? "Water heater" : "Space heater")} {(frame.Id % 2 == 1 ? "control" : "information")} (configured)";
                FrameDisplayed?.Invoke(frame, description, autoLayout, evidence);
                _lastRecordAt = DateTime.UtcNow;
                _lastRecordSummary = $"Last reported baud {frame.Baud} • {frame.Status}";
            }
            if (_receiving && DateTime.UtcNow >= _nextStatusUpdate) {
                var now = DateTime.UtcNow;
                _nextStatusUpdate = now.AddSeconds(1);
                int quietSeconds = (int)(now - _lastRecordAt).TotalSeconds;
                string usb = (now - _lastUsbResponse).TotalSeconds < 7 ? "USB responding" : "Waiting for USB health check";
                _status.Text = $"{usb} • Received {_count} records • Passed watch filter: {_count - _filtered} • Hidden by watch filter: {_filtered} • No new LIN records for {quietSeconds}s" +
                    (_lastRecordSummary.Length > 0 ? $" • {_lastRecordSummary}" : "") +
                    (_dropped > 0 ? $" • Display queue dropped {_dropped}" : "");
            }
            return true;
        });
    }
    public void SetWatchIds(IEnumerable<int> ids)
    {
        _watchIds = ids.ToHashSet();
    }
    public void ObserveHeaterConfiguration(LinHeaterConfiguration configuration)
    {
        if (_closed || (_heater != null && configuration.ObservedAt < _heater.ObservedAt)) return;
        _heater = configuration;
    }
    public void ResetHeaterConfiguration()
    {
        _heater = null;
    }
    public void ResetCounts() { _count = 0; _dropped = 0; _filtered = 0; }
    public void Close() { _closed = true; _backend?.Stop(); }
}
