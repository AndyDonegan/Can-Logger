using Gtk;

namespace CanLogger;

/// <summary>
/// Dialog showing all CAN IDs from the scheme with checkboxes to build
/// a watch list. Includes an Info button that opens per-ID byte detail.
/// </summary>
public class CanSchemeDialog : Dialog
{
    private readonly ListStore _store;
    private readonly TreeView _treeView;
    private HashSet<uint> _selectedIds;

    /// <summary>The set of IDs the user selected (only valid after Run returns ResponseType.Ok).</summary>
    public HashSet<uint> ResultIds { get; private set; } = new();

    private enum Col { Toggle, CanId, IdDec, IdHex, Description, Count }

    public CanSchemeDialog(Window parent, HashSet<uint> currentSelection)
        : base("CAN ID Watch List", parent,
               DialogFlags.Modal | DialogFlags.DestroyWithParent)
    {
        _selectedIds = new HashSet<uint>(currentSelection);

        SetDefaultSize(600, 500);

        // -- TreeView --------------------------------------------------
        _store = new ListStore(
            typeof(bool),    // toggle
            typeof(uint),    // CAN ID (hidden)
            typeof(string),  // ID (dec)
            typeof(string),  // ID (hex)
            typeof(string)   // Description
        );

        // Populate from scheme
        foreach (var def in CanScheme.AllEntries)
        {
            _store.AppendValues(
                _selectedIds.Contains(def.Id),
                def.Id,
                def.IdDec,
                def.IdHex,
                def.Description
            );
        }

        _treeView = new TreeView(_store)
        {
            HeadersVisible = true,
            EnableSearch = true,
            SearchColumn = (int)Col.Description,
        };

        // Toggle column
        var toggleRenderer = new CellRendererToggle();
        toggleRenderer.Toggled += OnToggled;
        var toggleCol = new TreeViewColumn("Watch", toggleRenderer, "active", (int)Col.Toggle);
        toggleCol.MinWidth = 50;
        _treeView.AppendColumn(toggleCol);

        // ID (dec) column
        AddTextColumn("ID (dec)", Col.IdDec, 70);
        // ID (hex) column
        AddTextColumn("ID (hex)", Col.IdHex, 70);
        // Description column
        AddTextColumn("Description", Col.Description, 320);

        var scroller = new ScrolledWindow { ShadowType = ShadowType.In };
        scroller.Add(_treeView);
        ContentArea.PackStart(scroller, true, true, 8);

        // -- Button bar ------------------------------------------------
        var btnBox = new ButtonBox(Orientation.Horizontal)
        {
            Layout = ButtonBoxStyle.Start,
            MarginTop = 4,
            Spacing = 4,
        };

        var selectAllBtn = new Button("Select All");
        selectAllBtn.Clicked += (_, _) => SetAll(true);
        btnBox.PackStart(selectAllBtn, false, false, 0);

        var deselectAllBtn = new Button("Deselect All");
        deselectAllBtn.Clicked += (_, _) => SetAll(false);
        btnBox.PackStart(deselectAllBtn, false, false, 0);

        var infoBtn = new Button("Info");
        infoBtn.Clicked += OnShowInfo;
        btnBox.PackStart(infoBtn, false, false, 0);

        ContentArea.PackStart(btnBox, false, false, 0);

        // -- Dialog buttons --------------------------------------------
        AddButton("Cancel", ResponseType.Cancel);
        AddButton("Apply", ResponseType.Ok);
        DefaultResponse = ResponseType.Ok;

        ShowAll();
    }

    protected override void OnResponse(ResponseType response)
    {
        if (response == ResponseType.Ok)
        {
            ResultIds = new HashSet<uint>(_selectedIds);
        }
        base.OnResponse(response);
    }

    private void AddTextColumn(string title, Col col, int width)
    {
        var cell = new CellRendererText();
        var column = new TreeViewColumn
        {
            Title = title,
            Resizable = true,
            MinWidth = width,
        };
        column.PackStart(cell, true);
        column.AddAttribute(cell, "text", (int)col);
        _treeView.AppendColumn(column);
    }

    private void OnToggled(object o, ToggledArgs args)
    {
        if (_store.GetIter(out var iter, new TreePath(args.Path)))
        {
            bool current = (bool)_store.GetValue(iter, (int)Col.Toggle);
            _store.SetValue(iter, (int)Col.Toggle, !current);
            uint id = (uint)_store.GetValue(iter, (int)Col.CanId);

            if (!current)
                _selectedIds.Add(id);
            else
                _selectedIds.Remove(id);
        }
    }

    private void SetAll(bool selected)
    {
        _selectedIds.Clear();
        _store.Foreach((model, path, iter) =>
        {
            uint id = (uint)_store.GetValue(iter, (int)Col.CanId);
            _store.SetValue(iter, (int)Col.Toggle, selected);
            if (selected) _selectedIds.Add(id);
            return false;
        });
    }

    private void OnShowInfo(object? sender, EventArgs e)
    {
        TreePath? path;
        TreeViewColumn? col;
        _treeView.GetCursor(out path, out col);
        if (path == null) return;

        if (_store.GetIter(out var iter, path))
        {
            uint id = (uint)_store.GetValue(iter, (int)Col.CanId);
            string info = CanScheme.GetInfoText(id);
            ShowInfoDialog(id, info);
        }
    }

    private void ShowInfoDialog(uint id, string info)
    {
        var dialog = new Dialog(
            $"ID {id} (0x{id:X}) Details", this,
            DialogFlags.Modal | DialogFlags.DestroyWithParent);

        var textView = new TextView
        {
            Editable = false,
            CursorVisible = false,
            WrapMode = WrapMode.Word,
            Buffer = { Text = info },
        };
        var scroller = new ScrolledWindow { ShadowType = ShadowType.In };
        scroller.SetSizeRequest(520, 300);
        scroller.Add(textView);
        dialog.ContentArea.PackStart(scroller, true, true, 8);

        // Copy button
        var copyBtn = new Button("Copy");
        copyBtn.Clicked += (_, _) =>
        {
            var clipboard = Clipboard.Get(Gdk.Atom.Intern("CLIPBOARD", false));
            clipboard.Text = info;
            Clipboard.Get(Gdk.Atom.Intern("PRIMARY", false)).Text = info;
        };
        var btnBox = new ButtonBox(Orientation.Horizontal)
        {
            Layout = ButtonBoxStyle.End,
            MarginTop = 4,
        };
        btnBox.PackStart(copyBtn, false, false, 0);
        dialog.ContentArea.PackStart(btnBox, false, false, 0);

        dialog.AddButton("Close", ResponseType.Close);
        dialog.DefaultResponse = ResponseType.Close;
        dialog.ShowAll();
        dialog.Run();
        dialog.Destroy();
    }
}
