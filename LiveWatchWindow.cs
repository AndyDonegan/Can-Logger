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
        public bool[] Focused = new bool[8];
    }

    private readonly Dictionary<(string Bus, uint Id), WatchRow> _rows = new();
    private readonly ListStore _store;
    private readonly TreeView _tree;
    private readonly Window _parent;
    private bool _closed;
    private bool _fitQueued;
    public event System.Action? VisibilityChanged;

    public LiveWatchWindow(Window parent) : base("Live Watch — CAN / LIN")
    {
        _parent = parent;
        TransientFor = parent;
        DestroyWithParent = true;
        KeepAbove = true;
        BorderWidth = 8;
        _store = new ListStore(Enumerable.Repeat(typeof(string), 18).ToArray());
        _tree = new TreeView(_store) { HeadersVisible = true, EnableSearch = false };
        _tree.Selection.Mode = SelectionMode.None;
        _tree.ButtonPressEvent += OnCellButtonPress;
        for (int i = 0; i < 18; i++)
        {
            int index = i;
            var cell = new WatchCellRenderer(i >= 2, i == 17, i == 10) { Font = "Monospace 13", Xpad = 4, Ypad = 4 };
            var column = new TreeViewColumn { Title = i == 0 ? "Bus" : i == 1 ? "ID" : $"{(i == 2 ? "Dec" : i == 10 ? "Hex" : "")}\n{(i - 2) % 8}", Sizing = TreeViewColumnSizing.Fixed, Expand = false };
            column.PackStart(cell, true);
            column.SetCellDataFunc(cell, (TreeViewColumn c, CellRenderer renderer, ITreeModel model, TreeIter iter) => {
                var text = (CellRendererText)renderer;
                text.Text = (string)model.GetValue(iter, index);
                bool changed = false, focused = false;
                if (index >= 2)
                {
                    string bus = (string)model.GetValue(iter, 0);
                    uint id = uint.Parse(((string)model.GetValue(iter, 1)).Split(' ')[0]);
                    if (_rows.TryGetValue((bus, id), out var row))
                    {
                        int byteIndex = (index - 2) % 8;
                        changed = row.HighlightUntil[byteIndex] > Environment.TickCount64;
                        focused = row.Focused[byteIndex];
                    }
                }
                text.CellBackground = changed ? "#FFE08A" : focused ? "#2463B5" : null;
                text.Foreground = changed ? "#202020" : focused ? "#FFFFFF" : null;
            });
            _tree.AppendColumn(column);
            if (i == 10)
            {
                var css = new CssProvider();
                css.LoadFromData("button { border-left: 4px solid alpha(currentColor, 0.85); }");
                column.Button.StyleContext.AddProvider(css, 800);
                css.Dispose();
            }
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

    // Draw after the cell background, so selection and update flashes retain the grid.
    private sealed class WatchCellRenderer : CellRendererText
    {
        private readonly bool _leftDivider;
        private readonly bool _rightDivider;
        private readonly bool _tableDivider;
        public WatchCellRenderer(bool leftDivider, bool rightDivider, bool tableDivider)
        { _leftDivider = leftDivider; _rightDivider = rightDivider; _tableDivider = tableDivider; }

        protected override void OnRender(Cairo.Context cr, Widget widget,
            Gdk.Rectangle backgroundArea, Gdk.Rectangle cellArea, CellRendererState flags)
        {
            base.OnRender(cr, widget, backgroundArea, cellArea, flags);
            var color = widget.StyleContext.GetColor(StateFlags.Normal);
            cr.Save();
            cr.Rectangle(backgroundArea.X, backgroundArea.Y, backgroundArea.Width, backgroundArea.Height);
            cr.Clip();
            cr.SetSourceRGBA(color.Red, color.Green, color.Blue, 0.22);
            cr.LineWidth = 1;
            cr.MoveTo(backgroundArea.X, backgroundArea.Y + backgroundArea.Height - 0.5);
            cr.LineTo(backgroundArea.X + backgroundArea.Width, backgroundArea.Y + backgroundArea.Height - 0.5);
            cr.Stroke();
            cr.SetSourceRGBA(color.Red, color.Green, color.Blue, 0.75);
            cr.LineWidth = _tableDivider ? 4 : 2;
            if (_leftDivider)
            {
                double x = backgroundArea.X + (_tableDivider ? 2 : 1);
                cr.MoveTo(x, backgroundArea.Y);
                cr.LineTo(x, backgroundArea.Y + backgroundArea.Height);
            }
            if (_rightDivider)
            {
                cr.MoveTo(backgroundArea.X + backgroundArea.Width - 1, backgroundArea.Y);
                cr.LineTo(backgroundArea.X + backgroundArea.Width - 1, backgroundArea.Y + backgroundArea.Height);
            }
            cr.Stroke();
            cr.Restore();
        }
    }

    [GLib.ConnectBefore]
    private void OnCellButtonPress(object sender, ButtonPressEventArgs args)
    {
        if (args.Event.Button != 1 || args.Event.Type != Gdk.EventType.ButtonPress) return;
        if (_tree.GetPathAtPos((int)args.Event.X, (int)args.Event.Y, out TreePath path, out TreeViewColumn column))
        {
            using (path) args.RetVal = ToggleCell(path, column);
        }
    }

    private bool ToggleCell(TreePath path, TreeViewColumn column)
    {
        int index = Array.IndexOf(_tree.Columns, column);
        if (index < 2 || !_store.GetIter(out var iter, path)) return false;
        string bus = (string)_store.GetValue(iter, 0);
        uint id = uint.Parse(((string)_store.GetValue(iter, 1)).Split(' ')[0]);
        if (!_rows.TryGetValue((bus, id), out var row)) return false;
        int byteIndex = (index - 2) % 8;
        row.Focused[byteIndex] = !row.Focused[byteIndex];
        _tree.QueueDraw();
        return true;
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
        QueueFitRows();
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
                : "255";
            using var layout = _tree.CreatePangoLayout(sample);
            layout.FontDescription = font;
            layout.GetPixelSize(out int width, out _);
            int padding = i == 1 ? 24 : 10;
            _tree.Columns[i].FixedWidth = width + padding;
            contentWidth += width + padding;
        }
        // Size from the current row count rather than a potentially stale tree
        // requisition immediately after inserting rows. Include window decorations,
        // the header, and a full blank row beneath the final ID.
        using var firstPath = new TreePath("0");
        int rowHeight = _rows.Count > 0 ? _tree.GetBackgroundArea(firstPath, _tree.Columns[0]).Height : 0;
        if (rowHeight <= 0)
        {
            _tree.Columns[0].Cells[0].GetPreferredHeight(_tree, out _, out int cellHeight);
            rowHeight = cellHeight + 2;
        }
        int headerHeight = Math.Max(40, _tree.Columns[10].Button.AllocatedHeight);
        // GetSize can report a pending resize before allocation catches up.
        // Use the allocated content box so repeated fits see consistent geometry.
        int clientHeight = (Child?.AllocatedHeight ?? 0) + (int)BorderWidth * 2;
        int decorationHeight = IsMapped ? Math.Max(0, AllocatedHeight - clientHeight) : 0;
        int chromeHeight = IsMapped ? Math.Max(0, clientHeight - _tree.AllocatedHeight) : 40;
        int desiredHeight = chromeHeight + headerHeight + (_rows.Count + 1) * rowHeight;
        Resize(Math.Min(contentWidth + 32, Math.Max(1, area.Width - 40)),
            Math.Min(Math.Max(140, desiredHeight), Math.Max(1, area.Height - 100 - decorationHeight)));
    }

    private void QueueFitRows()
    {
        if (_fitQueued) return;
        _fitQueued = true;
        // Recheck once GTK has allocated newly added rows and window decorations.
        GLib.Timeout.Add(40, () => {
            _fitQueued = false;
            if (!_closed && Visible) FitRows();
            return false;
        });
    }

    public void ShowWatch()
    {
        if (_rows.Count == 0) return;
        KeepAbove = true;
        ShowAll();
        FitRows();
        QueueFitRows();
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
