using System.Collections.Concurrent;
using Gtk;

namespace CanLogger;

/// <summary>First hardware test surface; CAN and LIN have independent lifecycles.</summary>
public sealed class LinReceivePanel : Box
{
    public Box ConnectionControls { get; } = new(Orientation.Vertical, 3) { Margin = 5 };
    public event System.Action? ShowDataRequested;
    private readonly ComboBoxText _analyzer = new();
    private readonly ComboBoxText _baud = new();
    private readonly ComboBoxText _layout = new();
    private LinHeaterConfiguration? _heater;
    private readonly Label _heaterStatus = new("Auto heater: waiting for CAN 133 settings; manual layout remains available") { Xalign = 0, LineWrap = true };
    private readonly Button _showData = new("Show LIN data");
    private readonly Button _start = new("Start LIN");
    private readonly Label _diagnostics = new("USB trace is saved locally when LIN starts") { Xalign = 0, Selectable = true };
    private readonly Label _status = new("Stopped");
    private readonly ListStore _rows = new(typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string));
    private readonly ConcurrentQueue<LinMessage> _pending = new();
    private MicrochipLinBackend? _backend;
    private int _count, _dropped;
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
        _showData.Clicked += (_, _) => ShowDataRequested?.Invoke();
        bar.PackStart(_showData, false, false, 0);
        ConnectionControls.PackStart(bar, false, false, 0);
        var clear = new Button("Clear LIN");
        clear.Clicked += (_, _) => { _rows.Clear(); _count = 0; _dropped = 0; };
        var tools = new Box(Orientation.Horizontal, 8);
        tools.PackStart(clear, false, false, 0);
        var reference = new Button("EC600 LIN reference");
        reference.Clicked += (_, _) => new LinReferenceWindow(Toplevel as Window);
        tools.PackStart(reference, false, false, 0);
        tools.PackStart(new Label("Hover layout:"), false, false, 0);
        _layout.AppendText("Auto heater from CAN settings");
        _layout.AppendText("All applicable layouts");
        foreach (string name in LinScheme.Entries.SelectMany(e => e.Layouts).Select(l => l.Name).Distinct().OrderBy(n => n)) _layout.AppendText(name);
        _layout.Active = 0;
        _layout.TooltipText = "Reference interpretation only; does not change the analyzer or PSU. Auto uses reported CAN 133 B2; other selections override it for viewing.";
        tools.PackStart(_layout, false, false, 0);
        PackStart(tools, false, false, 0);
        PackStart(_heaterStatus, false, false, 0);
        clear.Halign = Align.Start;
        _status.Xalign = 0; _status.Selectable = true; _status.LineWrap = true;
        ConnectionControls.PackStart(_status, false, false, 0);
        ConnectionControls.PackStart(_diagnostics, false, false, 0);
        PackStart(new Label("LIN data • Hover for byte/bit reference • Double-click for full details • All IDs shown") { Xalign = 0 }, false, false, 0);
        var tree = new TreeView(_rows) { EnableSearch = false, HasTooltip = true };
        string[] titles = { "Received", "ID (dec / hex)", "Received PID (dec / hex)", "Expected PID (dec / hex)", "Reported baud", "Data bytes", "Raw bytes + checksum", "Last byte", "Checksum result", "Status", "Adapter time" };
        for (int i = 0; i < titles.Length; i++) {
            tree.AppendColumn(titles[i], new CellRendererText(), "text", i);
            if (i == 1) tree.AppendColumn("EC600 description", new CellRendererText(), "text", 11);
        }
        static int RowId(ITreeModel model, TreeIter iter) => int.Parse(((string)model.GetValue(iter, 1)).Split(' ')[0]);
        string? RowLayout(ITreeModel model, TreeIter iter) => _layout.Active switch {
            0 => string.IsNullOrEmpty((string)model.GetValue(iter, 12)) ? null : (string)model.GetValue(iter, 12),
            1 => null,
            _ => _layout.ActiveText
        };
        string RowStatus(ITreeModel model, TreeIter iter) => (string)model.GetValue(iter, 9) + "\n" +
            (_layout.Active == 0 ? (string)model.GetValue(iter, 13) : "Manual reference selection; not automatic identification.");
        tree.QueryTooltip += (_, args) => LinTooltip.Show(tree, args, (model, iter) =>
            LinScheme.Describe(RowId(model, iter), RowLayout(model, iter),
                (string)model.GetValue(iter, 5), (string)model.GetValue(iter, 6), RowStatus(model, iter)));
        tree.RowActivated += (_, args) => {
            if (_rows.GetIter(out var iter, args.Path))
                new LinReferenceWindow(Toplevel as Window, RowId(_rows, iter), (string)_rows.GetValue(iter, 5),
                    (string)_rows.GetValue(iter, 6), RowStatus(_rows, iter), RowLayout(_rows, iter));
        };
        var scroll = new ScrolledWindow(); scroll.Add(tree); PackStart(scroll, true, true, 0);
        _start.Clicked += async (_, _) => {
            if (_backend != null) { _start.Sensitive = false; _receiving = false; _status.Text = "Stopping LIN…"; _backend.Stop(); return; }
            if (_analyzer.ActiveId != MicrochipLinBackend.AnalyzerId) {
                _status.Text = "Select a supported LIN analyzer first.";
                return;
            }
            ShowDataRequested?.Invoke();
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
            _diagnostics.Text = "Reading analyzer configuration…";
            backend.DiagnosticInfo += info => GLib.Idle.Add(() => {
                if (!_closed && _backend == backend) _diagnostics.Text = info;
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
                string? autoLayout = _heater?.LayoutFor(frame.Id, frame.Timestamp);
                string evidence = _heater != null && frame.Timestamp >= _heater.ObservedAt
                    ? _heater.Evidence : "No CAN heater setting observed before this frame; showing reference alternatives.";
                string description = LinScheme.Find(frame.Id)?.Name ?? "Unknown in EC600 source";
                if (autoLayout != null) description = $"{autoLayout} — {(frame.Id < 57 ? "Water heater" : "Space heater")} {(frame.Id % 2 == 1 ? "control" : "information")} (configured)";
                _rows.InsertWithValues(0, frame.Timestamp.ToString("HH:mm:ss.fff"), $"{frame.Id} / 0x{frame.Id:X2}",
                    $"{frame.Pid} / 0x{frame.Pid:X2}", $"{frame.ExpectedPid} / 0x{frame.ExpectedPid:X2}", frame.Baud.ToString(), Space(frame.Payload), Space(Convert.ToHexString(frame.RawBytes)),
                    frame.ChecksumByte, frame.ChecksumStatus, frame.Status, frame.DeviceTime.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    description, autoLayout ?? "", evidence);
                if (_rows.IterNChildren() > 2000 && _rows.IterNthChild(out var last, 2000)) _rows.Remove(ref last);
                _lastRecordAt = DateTime.UtcNow;
                _lastRecordSummary = $"Last reported baud {frame.Baud} • {frame.Status}";
            }
            if (_receiving && DateTime.UtcNow >= _nextStatusUpdate) {
                var now = DateTime.UtcNow;
                _nextStatusUpdate = now.AddSeconds(1);
                int quietSeconds = (int)(now - _lastRecordAt).TotalSeconds;
                string usb = (now - _lastUsbResponse).TotalSeconds < 7 ? "USB responding" : "Waiting for USB health check";
                _status.Text = $"{usb} • Received {_count} records • No new LIN records for {quietSeconds}s" +
                    (_lastRecordSummary.Length > 0 ? $" • {_lastRecordSummary}" : "") +
                    (_dropped > 0 ? $" • Display queue dropped {_dropped}" : "");
            }
            return true;
        });
    }
    public void ObserveHeaterConfiguration(LinHeaterConfiguration configuration)
    {
        if (_closed || (_heater != null && configuration.ObservedAt < _heater.ObservedAt)) return;
        _heater = configuration;
        _heaterStatus.Text = "Auto heater: " + configuration.Evidence;
    }
    public void ResetHeaterConfiguration()
    {
        _heater = null;
        if (!_closed) _heaterStatus.Text = "Auto heater: waiting for CAN 133 settings; manual layout remains available";
    }
    private static string Space(string hex) => string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    public void Close() { _closed = true; _backend?.Stop(); }
}
