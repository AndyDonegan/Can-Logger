using Gtk;

namespace CanLogger;

/// <summary>Latest payload per selected bus/ID. All access is on the GTK thread.</summary>
internal sealed class LiveWatchWindow : Window
{
    private sealed class WatchRow
    {
        public TreeIter Iter;
        public byte?[] Bytes = new byte?[8];
        public long[] HighlightUntil = new long[8];
    }

    private readonly Dictionary<(string Bus, uint Id), WatchRow> _rows = new();
    private readonly ListStore _store;
    private readonly TreeView _tree;
    private readonly Window _parent;
    private bool _closed;
    public event System.Action? VisibilityChanged;

    public LiveWatchWindow(Window parent) : base("Live Watch — CAN / LIN")
    {
        _parent = parent;
        // An independent, non-modal window can be moved behind the main window.
        BorderWidth = 8;
        _store = new ListStore(Enumerable.Repeat(typeof(string), 18).ToArray());
        _tree = new TreeView(_store) { HeadersVisible = true, EnableSearch = false };
        _tree.Selection.Mode = SelectionMode.None;
        for (int i = 0; i < 18; i++)
        {
            int index = i;
            var cell = new CellRendererText { Font = "Monospace 13", Xpad = 4, Ypad = 4 };
            var column = new TreeViewColumn { Title = i == 0 ? "Bus" : i == 1 ? "ID" : $"{(i == 2 ? "Dec" : i == 10 ? "Hex" : "")}\n{(i - 2) % 8}", Sizing = TreeViewColumnSizing.Fixed, Expand = false };
            column.PackStart(cell, true);
            column.SetCellDataFunc(cell, (TreeViewColumn c, CellRenderer renderer, ITreeModel model, TreeIter iter) => {
                var text = (CellRendererText)renderer;
                text.Text = (string)model.GetValue(iter, index);
                bool changed = false;
                if (index >= 2)
                {
                    string bus = (string)model.GetValue(iter, 0);
                    uint id = uint.Parse(((string)model.GetValue(iter, 1)).Split(' ')[0]);
                    changed = _rows.TryGetValue((bus, id), out var row) && row.HighlightUntil[(index - 2) % 8] > Environment.TickCount64;
                }
                text.CellBackground = changed ? "#FFE08A" : null;
                text.Foreground = changed ? "#202020" : null;
            });
            _tree.AppendColumn(column);
        }
        var box = new Box(Orientation.Vertical, 6);
        box.PackStart(new Label("Decimal bytes 0–7 (left) • Hex bytes 0–7 (right)") { Xalign = 0 }, false, false, 0);
        var scroll = new ScrolledWindow();
        scroll.SetPolicy(PolicyType.Automatic, PolicyType.Automatic);
        scroll.Add(_tree);
        box.PackStart(scroll, true, true, 0);
        Add(box);
        DeleteEvent += (_, args) => { args.RetVal = true; HideWatch(); };
        Destroyed += (_, _) => _closed = true;
        GLib.Timeout.Add(100, () => {
            if (_closed) return false;
            bool expired = false;
            long now = Environment.TickCount64;
            foreach (var row in _rows.Values)
                for (int i = 0; i < 8; i++)
                    if (row.HighlightUntil[i] != 0 && row.HighlightUntil[i] <= now)
                    { row.HighlightUntil[i] = 0; expired = true; }
            if (expired && Visible) _tree.QueueDraw();
            return true;
        });
    }

    public void SetSelection(IEnumerable<(string Bus, uint Id)> selection, bool open)
    {
        var keys = selection.ToHashSet();
        foreach (var key in _rows.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            var iter = _rows[key].Iter;
            _store.Remove(ref iter);
            _rows.Remove(key);
        }
        // Existing rows never move as data arrives or other IDs are added.
        foreach (var key in keys.OrderBy(key => key.Bus).ThenBy(key => key.Id))
        {
            if (_rows.ContainsKey(key)) continue;
            object[] values = new object[18];
            values[0] = key.Bus;
            values[1] = $"{key.Id} {key.Id:X2}";
            for (int i = 2; i < 18; i++) values[i] = "—";
            _rows.Add(key, new WatchRow { Iter = _store.AppendValues(values) });
        }
        FitRows();
        if (_rows.Count == 0) HideWatch();
        else if (open) ShowWatch();
    }

    private void FitRows()
    {
        var area = Display.GetMonitorAtWindow(Visible ? Window : _parent.Window)?.Workarea
            ?? new Gdk.Rectangle(0, 0, 1200, 800);
        // Reserve only the text width plus roughly one character between columns.
        // Byte widths remain stable as values change; ID width follows selected IDs.
        using var font = Pango.FontDescription.FromString("Monospace 13");
        int contentWidth = 0;
        for (int i = 0; i < _tree.Columns.Length; i++)
        {
            string sample = i == 0 ? "Bus" : i == 1
                ? _rows.Keys.Select(key => $"{key.Id} {key.Id:X2}").OrderByDescending(text => text.Length).FirstOrDefault() ?? "ID"
                : i < 10 ? "255" : i == 10 ? "Hex" : "FF";
            using var layout = _tree.CreatePangoLayout(sample);
            layout.FontDescription = font;
            layout.GetPixelSize(out int width, out _);
            _tree.Columns[i].FixedWidth = width + 10;
            contentWidth += width + 10;
        }
        _tree.GetPreferredSize(out _, out var natural);
        Resize(Math.Min(contentWidth + 32, Math.Max(1, area.Width - 40)),
            Math.Min(Math.Max(140, natural.Height + 65), Math.Max(1, area.Height - 100)));
    }

    public void ShowWatch()
    {
        if (_rows.Count == 0) return;
        ShowAll();
        FitRows();
        Present();
        VisibilityChanged?.Invoke();
    }

    public void HideWatch() { Hide(); VisibilityChanged?.Invoke(); }

    public void Update(string bus, uint id, byte[] payload)
    {
        if (!_rows.TryGetValue((bus, id), out var row)) return;
        for (int i = 0; i < 8; i++)
        {
            byte? value = i < payload.Length ? payload[i] : null;
            if (value == row.Bytes[i]) continue;
            row.Bytes[i] = value;
            row.HighlightUntil[i] = Environment.TickCount64 + 650;
            _store.SetValue(row.Iter, i + 2, value.HasValue ? value.Value.ToString() : "—");
            _store.SetValue(row.Iter, i + 10, value.HasValue ? value.Value.ToString("X2") : "—");
        }
    }
}
