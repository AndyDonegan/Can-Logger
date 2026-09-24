using Gtk;

namespace CanLogger;

/// <summary>Scrollable source reference, also used for a frozen received-frame inspection.</summary>
public sealed class LinReferenceWindow : Window
{
    private readonly TextView _details = new() { Editable = false, CursorVisible = false, WrapMode = WrapMode.WordChar, LeftMargin = 10, RightMargin = 10 };
    private readonly ComboBoxText _layout = new();
    private int _selectedId;
    private bool _changing;

    public LinReferenceWindow(Window? parent, int? capturedId = null, string? payload = null, string? raw = null, string? status = null, string? preferredLayout = null)
        : base("EC600 LIN reference")
    {
        TransientFor = parent;
        DestroyWithParent = true;
        SetDefaultSize(1000, 650);
        var box = new Box(Orientation.Vertical, 6) { Margin = 8 };
        Add(box);
        box.PackStart(new Label("EC600 source reference • Select an ID and appliance layout • B0 is the first payload byte") { Xalign = 0, LineWrap = true }, false, false, 0);
        var rows = new ListStore(typeof(int), typeof(string), typeof(string), typeof(string), typeof(string));
        foreach (var def in LinScheme.Entries)
            rows.AppendValues(def.Id, $"{def.Id} / 0x{def.Id:X2}", $"0x{def.Pid:X2}", def.Name, def.Direction);
        if (capturedId.HasValue && LinScheme.Find(capturedId.Value) == null)
            rows.AppendValues(capturedId.Value, $"{capturedId} / 0x{capturedId:X2}", "—", "Unknown in EC600 source", "Unknown");
        var tree = new TreeView(rows) { HasTooltip = false, EnableSearch = true, SearchColumn = 3 };
        foreach (var (title, col) in new[] { ("ID (dec / hex)", 1), ("Expected PID", 2), ("Description", 3), ("Data direction", 4) })
            tree.AppendColumn(title, new CellRendererText(), "text", col);
        var tableScroll = new ScrolledWindow(); tableScroll.Add(tree);
        var lower = new Box(Orientation.Vertical, 5);
        var controls = new Box(Orientation.Horizontal, 5);
        controls.PackStart(new Label("Appliance layout:"), false, false, 0);
        controls.PackStart(_layout, false, false, 0);
        lower.PackStart(controls, false, false, 0);
        var detailsScroll = new ScrolledWindow(); detailsScroll.Add(_details); lower.PackStart(detailsScroll, true, true, 0);
        var panes = new Paned(Orientation.Vertical) { Position = 240 };
        panes.Pack1(tableScroll, true, false); panes.Pack2(lower, true, false);
        box.PackStart(panes, true, true, 0);
        void UpdateDetails()
        {
            bool captured = capturedId == _selectedId;
            _details.Buffer.Text = LinScheme.Describe(_selectedId, _layout.Active == 0 ? null : _layout.ActiveText,
                captured ? payload : null, captured ? raw : null, captured ? status : null);
        }
        tree.Selection.Changed += (_, _) => {
            if (!tree.Selection.GetSelected(out var model, out var iter)) return;
            _selectedId = (int)model.GetValue(iter, 0);
            _changing = true;
            _layout.RemoveAll(); _layout.AppendText("All applicable layouts");
            foreach (var layout in LinScheme.Find(_selectedId)?.Layouts ?? Array.Empty<LinLayout>()) _layout.AppendText(layout.Name);
            _layout.Active = 0;
            if (_selectedId == capturedId && preferredLayout != null && LinScheme.Find(_selectedId) is { } definition) {
                int index = Array.FindIndex(definition.Layouts, l => l.Name == preferredLayout);
                if (index >= 0) _layout.Active = index + 1;
            }
            _changing = false; UpdateDetails();
        };
        _layout.Changed += (_, _) => { if (!_changing) UpdateDetails(); };
        DeleteEvent += (_, args) => { args.RetVal = true; Destroy(); };
        int selected = capturedId.HasValue ? LinScheme.Entries.ToList().FindIndex(e => e.Id == capturedId) : 0;
        if (selected < 0) selected = rows.IterNChildren() - 1;
        tree.Selection.SelectPath(new TreePath(selected.ToString()));
        ShowAll();
    }
}
