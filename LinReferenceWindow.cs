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
        var tree = new TreeView(rows) { HasTooltip = true, EnableSearch = true, SearchColumn = 3 };
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
        tree.QueryTooltip += (_, args) => LinTooltip.Show(tree, args, (model, iter) =>
            LinScheme.Describe((int)model.GetValue(iter, 0), _layout.Active > 0 ? _layout.ActiveText : null));
        DeleteEvent += (_, args) => { args.RetVal = true; Destroy(); };
        int selected = capturedId.HasValue ? LinScheme.Entries.ToList().FindIndex(e => e.Id == capturedId) : 0;
        if (selected < 0) selected = rows.IterNChildren() - 1;
        tree.Selection.SelectPath(new TreePath(selected.ToString()));
        ShowAll();
    }
}

internal static class LinTooltip
{
    internal static Label CreateLabel(string fullText, Gdk.Rectangle area)
    {
        int width = Math.Min(900, Math.Max(1, area.Width - 64));
        int maxHeight = Math.Max(1, area.Height - 64);
        var label = new Label { Xalign = 0, Yalign = 0, Wrap = true, LineWrapMode = Pango.WrapMode.WordChar,
            MaxWidthChars = 1, WidthRequest = width, Text = fullText };
        label.Show();
        label.GetPreferredHeightForWidth(width, out _, out int height);
        if (height <= maxHeight) return label;
        const string suffix = "\n… Full details: double-click the received row, or select this ID in EC600 LIN reference.";
        int low = 0, high = fullText.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            label.Text = fullText[..mid] + suffix;
            label.GetPreferredHeightForWidth(width, out _, out height);
            if (height <= maxHeight) low = mid; else high = mid - 1;
        }
        // Trim at a line boundary so a field is not presented as a complete definition when cut short.
        int boundary = low > 0 ? fullText.LastIndexOf('\n', low - 1) : -1;
        label.Text = fullText[..Math.Max(0, boundary)] + suffix;
        return label;
    }

    internal static void Show(TreeView tree, QueryTooltipArgs args, Func<ITreeModel, TreeIter, string> describe)
    {
        args.RetVal = false;
        int x = args.X, y = args.Y;
        if (!tree.GetTooltipContext(ref x, ref y, args.KeyboardTooltip, out var model, out var path, out var iter)) return;
        string text = describe(model, iter);
        var parent = tree.Toplevel as Window;
        if (parent?.Window == null) return;
        tree.TranslateCoordinates(parent, args.KeyboardTooltip ? tree.AllocatedWidth / 2 : args.X,
            args.KeyboardTooltip ? tree.AllocatedHeight / 2 : args.Y, out int px, out int py);
        var area = tree.Display.GetMonitorAtWindow(parent.Window)?.Workarea ?? new Gdk.Rectangle(0, 0, parent.AllocatedWidth, parent.AllocatedHeight);
        if ((parent.Window.State & (Gdk.WindowState.Maximized | Gdk.WindowState.Fullscreen)) != 0)
        {
            area.Width = Math.Min(area.Width, parent.AllocatedWidth);
            area.Height = Math.Min(area.Height, parent.AllocatedHeight);
        }
        if (tree.TooltipWindow is not CanTooltipWindow popup)
        {
            popup = new CanTooltipWindow { TransientFor = parent };
            tree.TooltipWindow = popup;
            tree.Destroyed += (_, _) => popup.Destroy();
        }
        popup.SetContent(CreateLabel(text, area), new Gdk.Point(px, py));
        tree.SetTooltipRow(args.Tooltip, path);
        args.RetVal = true;
    }
}
