using System;
using System.IO;
using System.Linq;
using Gtk;

namespace CanLogger;

/// <summary>
/// Main GTK# GUI for the CAN Bus Analyzer.
/// </summary>
public class CanAnalyzerApp
{
    private ICanBackend _backend;
    private readonly bool _stdinMode;
    private ListStore _messageStore = null!;
    private TreeView _treeView = null!;
    private Window _window = null!;
    private LinReceivePanel? _linPanel;
    private int _canConfigurationGeneration;

    // Controls
    private ComboBoxText _interfaceCombo = null!;
    private ComboBoxText _bitrateCombo = null!;
    private Button _refreshInterfacesBtn = null!;
    private Button _startStopBtn = null!;
    private Button _clearBtn = null!;
    private Button _logBtn = null!;
    private Label _logStatusLabel = null!;
    private CheckButton _lockScrollCheck = null!;
    private readonly List<SendFrameControls> _sendFrames = new();
    private TreeView _watchTreeView = null!;
    private ListStore _watchStore = null!;
    // Published snapshots are never mutated: the receive thread reads this set.
    private volatile HashSet<uint> _filterIds = new();
    private Entry _watchIdEntry = null!;
    private Label _watchIdError = null!;
    private Entry _linWatchIdEntry = null!;
    private Label _linWatchIdError = null!;
    private HashSet<uint> _linFilterIds = new();

    private Label _statusLabel = null!;
    private Label _msgCountLabel = null!;

    // State
    private int _messageCount;
    private bool _logEnabled;
    private StreamWriter? _logWriter;
    private string? _logFilePath;
    private DateTime _logStartedAtUtc;
    private long _loggedFrameCount;
    private uint _logStatusTimerId;
    private readonly object _logLock = new();

    private const int MaxLogRows = 2000;
    private static readonly string[] SendFrameBackgroundColors =
        { "#D9ECFF", "#FFF0C2", "#DDF5E3" };
    private static readonly string[] SendFrameMarkerColors =
        { "#2F80ED", "#E09A00", "#219653" };

    // Column indices in ListStore
    private enum Col
    {
        Num, Timestamp, IdDec, DataDec, IdHex, Dlc, DataHex, Desc, Type,
        SendSlot, RowBackground, Bus, LinDetails, LinLayout, LinRaw, LinStatus, Count
    }

    private sealed class SendFrameControls
    {
        public int Index { get; init; }
        public Label IdLabel { get; init; } = null!;
        public Label DataLabel { get; init; } = null!;
        public Entry IdEntry { get; init; } = null!;
        public Entry DataEntry { get; init; } = null!;
        public Button HexDecToggle { get; init; } = null!;
        public CheckButton ExtendedCheck { get; init; } = null!;
        public Button SendButton { get; init; } = null!;
        public Entry PeriodEntry { get; init; } = null!;
        public Button PeriodicButton { get; init; } = null!;
        public bool InputIsHex { get; set; } = true;
        public bool PeriodicRunning { get; set; }
    }

    // ------------------------------------------------------------------
    // Application entry point
    // ------------------------------------------------------------------
    public static void Main(string[] args)
    {
        if (args.Contains("--waveshare-bridge"))
        {
            Environment.ExitCode = WaveshareBridgeProgram.Run(args);
            return;
        }
        if (args.Contains("--itek-bridge"))
        {
            Environment.ExitCode = ItekBridgeProgram.Run(args);
            return;
        }

        bool stdinMode = args.Contains("--stdin") || args.Contains("-s");

        // Load CAN bus scheme (shipped alongside the binary)
        string schemePath = Path.Combine(AppContext.BaseDirectory, "can-scheme.csv");
        if (File.Exists(schemePath))
            CanScheme.Load(schemePath);

        Application.Init();
        var app = new CanAnalyzerApp(stdinMode);
        app.Run();
    }

    // ------------------------------------------------------------------
    // Constructor
    // ------------------------------------------------------------------
    public CanAnalyzerApp(bool stdinMode = false)
    {
        _stdinMode = stdinMode;
        _backend = stdinMode ? new CandumpStdinBackend() : new CanBackend();
        _backend.OnMessageReceived += OnCanMessage;
        _backend.OnError += OnCanError;
    }

    public void Run()
    {
        _window = BuildWindow();
        _window.ShowAll();
        Application.Run();
    }

    // ------------------------------------------------------------------
    // Build the GUI
    // ------------------------------------------------------------------
    private Window BuildWindow()
    {
        var win = new Window("CAN / LIN Bus Analyzer")
        {
            Resizable = true,
            TypeHint = Gdk.WindowTypeHint.Normal,
        };
        win.SetDefaultSize(1300, 700);
        win.DeleteEvent += (_, _) =>
        {
            _linPanel?.Close();
            StopAll(stopLogging: true);
            Application.Quit();
        };

        var mainBox = new Box(Orientation.Vertical, 0);

        // -- Top control bar ------------------------------------------------
        var controlBar = new Box(Orientation.Horizontal, 4);
        controlBar.Margin = 5;

        controlBar.PackStart(new Label("Interface:"), false, false, 0);
        _interfaceCombo = ComboBoxText.NewWithEntry();
        _interfaceCombo.SetSizeRequest(150, -1);
        if (_stdinMode)
        {
            _interfaceCombo.AppendText("stdin (pipe)");
            _interfaceCombo.Active = 0;
            _interfaceCombo.Sensitive = false;
        }
        else
        {
            RefreshCanInterfaces();
            _interfaceCombo.TooltipText =
                "Detected SocketCAN, Waveshare USB-CAN-FD, and iTEK USBCAN interfaces.";
        }
        controlBar.PackStart(_interfaceCombo, false, false, 2);

        _refreshInterfacesBtn = new Button("Refresh") { FocusOnClick = false };
        _refreshInterfacesBtn.Clicked += (_, _) => RefreshCanInterfaces();
        _refreshInterfacesBtn.TooltipText = "Scan again for CAN interfaces";
        controlBar.PackStart(_refreshInterfacesBtn, false, false, 0);

        controlBar.PackStart(new Label("Bitrate:"), false, false, 0);
        _bitrateCombo = new ComboBoxText();
        foreach (var br in new[] { "10000", "20000", "50000", "100000", "125000",
                                   "250000", "500000", "800000", "1000000" })
            _bitrateCombo.AppendText(br);
        _bitrateCombo.Active = 4; // 125000 — the target bus used by this project
        _bitrateCombo.SetSizeRequest(100, -1);
        _bitrateCombo.TooltipText =
            "CAN bus speed. The app applies this to a local CAN interface when Start is clicked.";
        controlBar.PackStart(_bitrateCombo, false, false, 2);

        _startStopBtn = new Button("▶ Start");
        _startStopBtn.Clicked += OnStartStop;
        controlBar.PackStart(_startStopBtn, false, false, 2);

        _clearBtn = new Button("Clear");
        _clearBtn.Clicked += (_, _) => ClearMessages();
        controlBar.PackStart(_clearBtn, false, false, 2);

        _lockScrollCheck = new CheckButton("Lock scroll") { Active = false };
        controlBar.PackStart(_lockScrollCheck, false, false, 4);

        var fullscreenBtn = new Button("Maximize") { FocusOnClick = false };
        var isFs = false;
        fullscreenBtn.Clicked += (_, _) =>
        {
            if (isFs)
            {
                _window.Unfullscreen();
                fullscreenBtn.Label = "Maximize";
            }
            else
            {
                _window.Fullscreen();
                fullscreenBtn.Label = "Restore";
            }
            isFs = !isFs;
        };
        controlBar.PackStart(fullscreenBtn, false, false, 2);

        controlBar.PackStart(new Separator(Orientation.Vertical), false, false, 4);

        _logBtn = new Button("\U0001f4c4 Log output to file");
        _logBtn.Clicked += OnToggleLogging;
        controlBar.PackStart(_logBtn, false, false, 2);

        _logStatusLabel = new Label
        {
            UseMarkup = true,
            Xalign = 0,
            Ellipsize = Pango.EllipsizeMode.Middle,
            Markup = "<span foreground=\"#777777\">● Not logging</span>",
        };
        _logStatusLabel.SetSizeRequest(320, -1);
        controlBar.PackStart(_logStatusLabel, true, true, 4);

        mainBox.PackStart(controlBar, false, false, 0);
        _linPanel = new LinReceivePanel();
        mainBox.PackStart(_linPanel.ConnectionControls, false, false, 0);

        // -- Main area: watch list (left) | message table (right) -----------
        var paned = new Paned(Orientation.Horizontal) { Position = 300 };

        // ===== Watch list panel (left) =====================================
        var watchPanel = new Box(Orientation.Vertical, 2) { WidthRequest = 300 };

        // Watch list header
        var watchHeader = new Label("<b>Watch List</b>") { UseMarkup = true, Margin = 4 };
        watchPanel.PackStart(watchHeader, false, false, 0);

        var addColumns = new Box(Orientation.Horizontal, 6) { Homogeneous = true, Margin = 4 };
        Box AddIdControls(string title, string placeholder, string tip, out Entry entry, out Label error, System.Action add)
        {
            var column = new Box(Orientation.Vertical, 2);
            column.PackStart(new Label(title) { Xalign = 0 }, false, false, 0);
            var row = new Box(Orientation.Horizontal, 3);
            var input = new Entry { PlaceholderText = placeholder, WidthChars = 7, TooltipText = tip };
            var button = new Button("Add");
            button.Clicked += (_, _) => add();
            input.Activated += (_, _) => add();
            row.PackStart(input, true, true, 0); row.PackStart(button, false, false, 0);
            column.PackStart(row, false, false, 0);
            var validation = new Label { Xalign = 0, Wrap = true, MaxWidthChars = 20, NoShowAll = true };
            input.Changed += (_, _) => validation.Hide();
            column.PackStart(validation, false, false, 0);
            entry = input; error = validation;
            return column;
        }
        addColumns.PackStart(AddIdControls("Add CAN ID", "123 / 0x7AB", "CAN ID: decimal or 0x hex, 0–0x1FFFFFFF. Session only.",
            out _watchIdEntry, out _watchIdError, AddWatchId), true, true, 0);
        addColumns.PackStart(AddIdControls("Add LIN ID", "55 / 0x37", "LIN ID: decimal or 0x hex, 0–63 (0x3F). Enter the ID, not the protected PID. Session only.",
            out _linWatchIdEntry, out _linWatchIdError, () => AddWatchIdForBus(true)), true, true, 0);
        watchPanel.PackStart(addColumns, false, false, 0);

        // Watch list TreeView
        _watchStore = new ListStore(
            typeof(bool),    // toggle
            typeof(uint),    // CAN ID (hidden)
            typeof(string),  // ID (decimal and hex)
            typeof(string),  // Description
            typeof(string),  // Bus: CAN or LIN
            typeof(bool)     // Selectable ID row (false for headings)
        );

        PopulateWatchStore();

        _watchTreeView = new TreeView(_watchStore)
        {
            HeadersVisible = true,
            EnableSearch = true,
            SearchColumn = 3, // Description
            HasTooltip = true,
        };
        LinTooltip.ForTree(_watchTreeView);
        _watchTreeView.QueryTooltip += (_, args) => {
            args.RetVal = false;
            int x = args.X, y = args.Y;
            if (!_watchTreeView.GetTooltipContext(ref x, ref y, args.KeyboardTooltip, out var model, out var tooltipPath, out var iter) ||
                !(bool)model.GetValue(iter, 5)) { LinTooltip.Hide(_watchTreeView); return; }
            if ((string)model.GetValue(iter, 4) == "CAN") { LinTooltip.Hide(_watchTreeView); ShowCanTooltip(_watchTreeView, args, 1); }
            else LinTooltip.Show(_watchTreeView, args, (m, row) => LinScheme.Describe((int)(uint)m.GetValue(row, 1)));
        };

        var wtToggle = new CellRendererToggle();
        wtToggle.Toggled += OnWatchToggled;
        var wtCol = new TreeViewColumn("", wtToggle, "active", 0, "visible", 5) { MinWidth = 30 };
        _watchTreeView.AppendColumn(wtCol);

        AddWatchColumn("ID (dec - hex)", 2, 100);
        AddWatchColumn("Description", 3, 140);

        var watchScroller = new ScrolledWindow { ShadowType = ShadowType.In };
        watchScroller.Add(_watchTreeView);
        watchPanel.PackStart(watchScroller, true, true, 0);

        // Watch list buttons
        var watchBtnBox = new ButtonBox(Orientation.Horizontal)
        {
            Layout = ButtonBoxStyle.Start,
            Spacing = 2,
            Margin = 2,
        };
        var selectAllBtn = new Button("All");
        selectAllBtn.Clicked += (_, _) => SetWatchAll(true);
        watchBtnBox.PackStart(selectAllBtn, false, false, 0);
        var deselectAllBtn = new Button("None");
        deselectAllBtn.Clicked += (_, _) => SetWatchAll(false);
        watchBtnBox.PackStart(deselectAllBtn, false, false, 0);
        var watchInfoBtn = new Button("Info");
        watchInfoBtn.Clicked += OnWatchInfo;
        watchBtnBox.PackStart(watchInfoBtn, false, false, 0);
        watchPanel.PackStart(watchBtnBox, false, false, 0);

        // Respect the Watch List minimum width when dragging the divider.
        paned.Pack1(watchPanel, false, false);

        // ===== Message tree view (right) ===================================
        _messageStore = new ListStore(
            typeof(int),    // #
            typeof(string), // Timestamp
            typeof(int),    // ID (dec)
            typeof(string), // Data (dec)
            typeof(string), // ID (hex)
            typeof(int),    // DLC
            typeof(string), // Data (hex)
            typeof(string), // Description
            typeof(string), // Type
            typeof(int),    // Assigned send-frame slot (0 means unassigned)
            typeof(string), // Assigned row background, or transparent when unassigned
            typeof(string), // Bus
            typeof(string), // Full LIN snapshot
            typeof(string), // LIN interpretation
            typeof(string), // LIN raw bytes
            typeof(string)  // LIN diagnostics
        );

        _treeView = new TreeView(_messageStore)
        {
            HeadersVisible = true,
            EnableSearch = false,
            HasTooltip = true,
        };
        LinTooltip.ForTree(_treeView);
        _treeView.QueryTooltip += OnTreeViewQueryTooltip;
        _treeView.ButtonPressEvent += OnMessageButtonPress;

        AddColumn("#", Col.Num, 40);
        AddColumn("Received time", Col.Timestamp, 130);
        AddColumn("Bus", Col.Bus, 45);
        AddColumn("ID (decimal)", Col.IdDec, 85);
        AddColumn("ID (hex)", Col.IdHex, 85);
        AddColumn("Bytes", Col.Dlc, 45);
        AddColumn("Data (decimal)", Col.DataDec, 200);
        AddColumn("Data (hex)", Col.DataHex, 250);
        AddColumn("Description", Col.Desc, 180);
        AddColumn("Type / Status", Col.Type, 150);
        _linPanel.FrameDisplayed += AddLinMessageToStore;
        _treeView.RowActivated += (_, args) => {
            if (_messageStore.GetIter(out var row, args.Path) && IsLinRow(_messageStore, row))
                new LinReferenceWindow(_window, (int)_messageStore.GetValue(row, (int)Col.IdDec),
                    (int)_messageStore.GetValue(row, (int)Col.Dlc) < 0 ? "" : (string)_messageStore.GetValue(row, (int)Col.DataHex),
                    (string)_messageStore.GetValue(row, (int)Col.LinRaw),
                    (string)_messageStore.GetValue(row, (int)Col.LinStatus),
                    (string)_messageStore.GetValue(row, (int)Col.LinLayout));
        };

        var scrolledWindow = new ScrolledWindow
        {
            ShadowType = ShadowType.EtchedIn,
        };
        scrolledWindow.Add(_treeView);
        paned.Pack2(scrolledWindow, true, true);

        mainBox.PackStart(paned, true, true, 5);

        // -- Send frame panels ----------------------------------------------
        var sendFramesBox = new Box(Orientation.Vertical, 2);
        for (int index = 0; index < 3; index++)
            sendFramesBox.PackStart(BuildSendFramePanel(index), false, false, 0);
        mainBox.PackStart(sendFramesBox, false, false, 5);

        // -- Status bar -----------------------------------------------------
        var statusBar = new Box(Orientation.Horizontal, 4);
        statusBar.Margin = 3;

        _statusLabel = new Label("Disconnected") { Selectable = true };
        statusBar.PackStart(_statusLabel, false, false, 0);

        _msgCountLabel = new Label("Messages: 0") { Halign = Align.End };
        statusBar.PackStart(_msgCountLabel, true, true, 4);

        mainBox.PackStart(statusBar, false, false, 0);

        // Stdin mode: disable controls that don't apply
        if (_stdinMode)
        {
            _bitrateCombo.Sensitive = false;
            _bitrateCombo.TooltipText =
                "Bitrate is configured on the remote machine in stdin/SSH mode.";
            _refreshInterfacesBtn.Sensitive = false;
        }

        win.Add(mainBox);
        return win;
    }

    private Frame BuildSendFramePanel(int index)
    {
        var sendBox = new Box(Orientation.Horizontal, 4) { Margin = 5 };
        var controls = new SendFrameControls
        {
            Index = index,
            IdLabel = new Label("ID (hex):"),
            DataLabel = new Label("Data (hex bytes):"),
            IdEntry = new Entry("7DF") { WidthChars = 8 },
            DataEntry = new Entry("02 01 00") { WidthChars = 30 },
            HexDecToggle = new Button("Hex"),
            ExtendedCheck = new CheckButton("Extended ID"),
            SendButton = new Button("Send") { Sensitive = false },
            PeriodEntry = new Entry("") { WidthChars = 6 },
            PeriodicButton = new Button("Start Periodic") { Sensitive = false },
        };

        controls.HexDecToggle.Clicked += (_, _) => ToggleHexDec(controls);
        controls.SendButton.Clicked += (_, _) => SendFrame(controls);
        controls.PeriodicButton.Clicked += (_, _) => TogglePeriodic(controls);

        sendBox.PackStart(controls.IdLabel, false, false, 0);
        sendBox.PackStart(controls.IdEntry, false, false, 2);
        sendBox.PackStart(controls.DataLabel, false, false, 0);
        sendBox.PackStart(controls.DataEntry, false, false, 2);
        sendBox.PackStart(controls.HexDecToggle, false, false, 2);
        sendBox.PackStart(controls.ExtendedCheck, false, false, 2);
        sendBox.PackStart(controls.SendButton, false, false, 4);
        sendBox.PackStart(new Separator(Orientation.Vertical), false, false, 4);
        sendBox.PackStart(new Label("Periodic (ms):"), false, false, 0);
        sendBox.PackStart(controls.PeriodEntry, false, false, 2);
        sendBox.PackStart(controls.PeriodicButton, false, false, 2);

        _sendFrames.Add(controls);
        string instruction = index switch
        {
            0 => "click",
            1 => "Shift-click",
            _ => "Ctrl-click",
        };
        var titleLabel = new Label
        {
            UseMarkup = true,
            Markup = $"<span foreground=\"{SendFrameMarkerColors[index]}\">■</span> " +
                $"Send CAN Frame {index + 1} ({instruction} a received frame)",
        };
        var frame = new Frame { LabelWidget = titleLabel };
        frame.Add(sendBox);
        return frame;
    }

    private void AddColumn(string title, Col col, int width)
    {
        var cell = new CellRendererText();
        var column = new TreeViewColumn
        {
            Title = title,
            Resizable = true,
            MinWidth = width,
        };
        if (col is Col.Desc or Col.Type) {
            column.Sizing = TreeViewColumnSizing.Fixed;
            column.FixedWidth = width;
            cell.Ellipsize = Pango.EllipsizeMode.End;
        }
        column.PackStart(cell, true);
        column.SetCellDataFunc(cell, (TreeViewColumn view, CellRenderer renderer, ITreeModel model, TreeIter row) => {
            var text = (CellRendererText)renderer;
            text.Text = col == Col.Dlc && (int)model.GetValue(row, (int)col) < 0
                ? "—" : Convert.ToString(model.GetValue(row, (int)col));
            bool selected = _treeView.Selection.IterIsSelected(row);
            text.CellBackground = selected ? null : (string)model.GetValue(row, (int)Col.RowBackground);
            text.Foreground = !selected && IsLinRow(model, row) ? "#172B3A" : null;
        });
        _treeView.AppendColumn(column);
    }

    // ------------------------------------------------------------------
    // CAN message handler (called on background thread)
    // ------------------------------------------------------------------
    private void OnCanMessage(CanMessage msg)
    {
        // Observe reported PSU settings even when the CAN watch list hides ID 133.
        if (LinHeaterConfiguration.FromCan(msg) is { } configuration)
        {
            int generation = _canConfigurationGeneration;
            GLib.Idle.Add(() => {
                if (generation == _canConfigurationGeneration) _linPanel?.ObserveHeaterConfiguration(configuration);
                return false;
            });
        }
        // Filter: skip if watch list is active and ID doesn't match
        var filterIds = _filterIds;
        if (filterIds.Count > 0 && !filterIds.Contains(msg.ArbitrationId))
            return;

        // Marshal to GTK main thread
        GLib.Idle.Add(() =>
        {
            AddMessageToStore(msg);
            return false; // remove the idle handler
        });
    }

    private static bool IsLinRow(ITreeModel model, TreeIter row) =>
        (string?)model.GetValue(row, (int)Col.Bus) == "LIN";

    private void OnTreeViewQueryTooltip(object o, QueryTooltipArgs args)
    {
        args.RetVal = false;
        int x = args.X, y = args.Y;
        if (!_treeView.GetTooltipContext(ref x, ref y, args.KeyboardTooltip, out var model, out _, out var row)) {
            LinTooltip.Hide(_treeView);
            return;
        }
        if (IsLinRow(model, row))
            LinTooltip.Show(_treeView, args, (m, r) => (string)m.GetValue(r, (int)Col.LinDetails));
        else {
            LinTooltip.Hide(_treeView);
            ShowCanTooltip(_treeView, args, (int)Col.IdDec);
        }
    }

    private void AddLinMessageToStore(LinMessage frame, string description, string? layout, string evidence)
    {
        string raw = string.Join(" ", frame.RawBytes.Select(b => b.ToString("X2")));
        byte[] payload = frame.Complete ? frame.RawBytes[..^1] : Array.Empty<byte>();
        string hex = frame.Complete ? string.Join(" ", payload.Select(b => b.ToString("X2"))) : "—";
        string dec = frame.Complete ? string.Join(" ", payload.Select(b => b.ToString())) : "—";
        string status = frame.Complete ? frame.Status : $"Incomplete — raw bytes available; {frame.Status}";
        string last = frame.RawBytes.Length == 0 ? "Unavailable" : $"{frame.RawBytes[^1]} / 0x{frame.RawBytes[^1]:X2}";
        string diagnostics = $"Received PID (decimal / hex): {frame.Pid} / 0x{frame.Pid:X2}\n" +
            $"Expected PID (decimal / hex): {frame.ExpectedPid} / 0x{frame.ExpectedPid:X2}\n" +
            $"Reported baud: {frame.Baud}\nAdapter time: {frame.DeviceTime:F6}\n" +
            $"Raw bytes (decimal): {string.Join(" ", frame.RawBytes.Select(b => b.ToString()))}\nRaw bytes (hex): {raw}\n" +
            $"Last byte / checksum candidate (decimal / hex): {last}\nChecksum: {frame.ChecksumStatus}\n{frame.Status}\n{evidence}";
        string details = LinScheme.Describe(frame.Id, layout, frame.Payload, raw, diagnostics);
        _messageCount++;
        var row = _messageStore.InsertWithValues(0, _messageCount, frame.Timestamp.ToString("HH:mm:ss.fff"),
            frame.Id, dec, $"0x{frame.Id:X2}", frame.Complete ? payload.Length : -1, hex, description,
            status, 0, "#E8F4FC", "LIN", details, layout ?? "", raw, diagnostics);
        LogDisplayedRow(row);
        if (!_lockScrollCheck.Active) _treeView.ScrollToCell(new TreePath("0"), null, true, 0, 0);
        TrimMessageRows();
    }

    private void TrimMessageRows()
    {
        if (_messageStore.IterNChildren() > MaxLogRows && _messageStore.IterNthChild(out var last, MaxLogRows))
            while (_messageStore.Remove(ref last)) { }
        _msgCountLabel.Text = $"Messages: {_messageCount}";
    }

    private static void ShowCanTooltip(TreeView treeView, QueryTooltipArgs args, int idColumn)
    {
        args.RetVal = false;
        if (!CanScheme.IsLoaded) return;

        // Tooltip coordinates are widget-relative; GTK resolves the row while
        // accounting for the header, scrolling, and keyboard-triggered tooltips.
        int x = args.X;
        int y = args.Y;
        if (!treeView.GetTooltipContext(ref x, ref y, args.KeyboardTooltip,
                out var model, out var path, out var iter))
            return;

        uint id = Convert.ToUInt32(model.GetValue(iter, idColumn));
        string tip = CanScheme.GetTooltipText(id);
        if (string.IsNullOrEmpty(tip)) return;

        int anchorX = args.KeyboardTooltip ? treeView.AllocatedWidth / 2 : args.X;
        int anchorY = args.KeyboardTooltip ? treeView.AllocatedHeight / 2 : args.Y;
        treeView.TranslateCoordinates(treeView.Toplevel, anchorX, anchorY,
            out int parentX, out int parentY);
        var monitor = treeView.Display.GetMonitorAtWindow(treeView.Toplevel.Window);
        var workarea = monitor?.Workarea ?? new Gdk.Rectangle(
            0, 0, treeView.Toplevel.AllocatedWidth, treeView.Toplevel.AllocatedHeight);
        if ((treeView.Toplevel.Window.State & (Gdk.WindowState.Maximized | Gdk.WindowState.Fullscreen)) != 0)
        {
            workarea.Width = Math.Min(workarea.Width, treeView.Toplevel.AllocatedWidth);
            workarea.Height = Math.Min(workarea.Height, treeView.Toplevel.AllocatedHeight);
        }
        if (treeView.TooltipWindow is not CanTooltipWindow popup)
        {
            popup = new CanTooltipWindow { TransientFor = treeView.Toplevel as Window };
            treeView.TooltipWindow = popup;
            treeView.Destroyed += (_, _) => popup.Destroy();
        }
        popup.SetContent(CreateCanTooltipLabel(tip, workarea), new Gdk.Point(parentX, parentY));
        treeView.SetTooltipRow(args.Tooltip, path);
        args.RetVal = true;
    }

    private static Label CreateCanTooltipLabel(string text, Gdk.Rectangle workarea)
    {
        // Leave room for the tooltip border and desktop edges. Wrapping also
        // prevents a long Options/History line from forcing the popup off-screen.
        int maxWidth = Math.Max(1, workarea.Width - 64);
        int width = Math.Min(720, maxWidth);
        var label = new Label
        {
            Text = text,
            Wrap = true,
            LineWrapMode = Pango.WrapMode.WordChar,
            MaxWidthChars = 1,
            WidthRequest = width,
            Xalign = 0,
            Yalign = 0,
        };
        label.Show();
        label.GetPreferredHeightForWidth(width, out _, out int height);
        // Use more horizontal space if a long definition would exceed the height.
        while (height > workarea.Height - 64 && width < maxWidth)
        {
            width = Math.Min(width + 120, maxWidth);
            label.WidthRequest = width;
            label.GetPreferredHeightForWidth(width, out _, out height);
        }
        return label;
    }

    private void OnCanError(string error)
    {
        GLib.Idle.Add(() =>
        {
            _statusLabel.Text = $"Error: {error}";
            return false;
        });
    }

    private void AddMessageToStore(CanMessage msg)
    {
        _messageCount++;
        string ts = msg.Timestamp.ToString("HH:mm:ss.fff");
        string idHex = msg.IsError ? "-" : msg.IdHex;
        string dataDec = msg.IsError
            ? "-"
            : string.Join(" ", msg.Data.Select(b => ((int)b).ToString()));
        string dataHex = msg.IsError
            ? $"ERROR: {msg.ErrorDescription ?? "Unknown"}"
            : msg.DataHex;
        string desc = msg.IsError ? "" : (CanScheme.GetDescription(msg.ArbitrationId) ?? "");
        string frameType = msg.FrameType;

        var row = _messageStore.InsertWithValues(0,
            _messageCount, ts, (int)msg.ArbitrationId, dataDec, idHex, (int)msg.Dlc,
            dataHex, desc, frameType, 0, "rgba(0,0,0,0)", "CAN", "", "", "", "");

        // Auto-scroll to top unless locked
        if (!_lockScrollCheck.Active)
            _treeView.ScrollToCell(new TreePath("0"), null, true, 0, 0);

        LogDisplayedRow(row);

        TrimMessageRows();
    }

    // Both receive paths call this only after a row passes its watch filter and
    // enters the visible model. Existing history is not replayed when recording starts.
    private void LogDisplayedRow(TreeIter row)
    {
        if (_logEnabled && _logWriter != null)
        {
            Exception? writeError = null;
            lock (_logLock)
            {
                try
                {
                    var columns = new[] { Col.Num, Col.Timestamp, Col.Bus, Col.IdDec, Col.IdHex,
                        Col.Dlc, Col.DataDec, Col.DataHex, Col.Desc, Col.Type };
                    string[] values = columns.Select(col => col == Col.Dlc && (int)_messageStore.GetValue(row, (int)col) < 0
                        ? "—" : Convert.ToString(_messageStore.GetValue(row, (int)col), System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray();
                    _logWriter.WriteLine(string.Join(",", values.Select(CsvField)));
                    _loggedFrameCount++;
                }
                catch (Exception ex)
                {
                    writeError = ex;
                }
            }

            if (writeError != null)
            {
                string failedPath = _logFilePath ?? "the selected file";
                StopLogging("Recording stopped because the log file could not be written.");
                ShowError("Log Error",
                    $"Logging to '{failedPath}' has stopped.\n\n{writeError.Message}");
            }
        }

    }

    private static string CsvField(string value) => value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    // ------------------------------------------------------------------
    // Button handlers
    // ------------------------------------------------------------------
    private void OnStartStop(object? sender, EventArgs e)
    {
        if (_backend.IsRunning)
        {
            StopAll();
        }
        else
        {
            try
            {
                string iface = _stdinMode ? "stdin" : _interfaceCombo.Entry.Text.Trim();
                int bitrate = GetSelectedBitrate();
                if (!_stdinMode)
                    SelectBackend(iface, bitrate);
                if (!_stdinMode && !IsWindowsVendorInterface(iface))
                    CanInterfaceManager.EnsureReady(iface, bitrate);
                ResetLinHeaterConfiguration();
                _backend.Start(iface);
                _startStopBtn.Label = "\u23f9 Stop";
                SetSendControlsSensitive(true);
                SetConnectionControlsSensitive(false);
                _statusLabel.Text = _stdinMode
                    ? $"Connected \u2014 {iface}"
                    : $"Connected \u2014 {iface} @ {bitrate} bit/s";
                UpdateLoggingStatus();
            }
            catch (Exception ex)
            {
                ShowError("CAN Error",
                    $"Could not open interface:\n{ex.Message}\n\n" +
                    "Make sure the interface exists and you have permissions.");
            }
        }
    }

    private void ToggleHexDec(SendFrameControls controls)
    {
        controls.InputIsHex = !controls.InputIsHex;
        if (controls.InputIsHex)
        {
            controls.HexDecToggle.Label = "Hex";
            controls.IdLabel.Text = "ID (hex):";
            controls.DataLabel.Text = "Data (hex bytes):";
        }
        else
        {
            controls.HexDecToggle.Label = "Dec";
            controls.IdLabel.Text = "ID (dec):";
            controls.DataLabel.Text = "Data (dec bytes):";
        }

        PopulateSendFieldsFromAssignedRow(controls);
    }

    private void OnMessageButtonPress(object o, ButtonPressEventArgs args)
    {
        if (args.Event.Button != 1 ||
            !_treeView.GetPathAtPos((int)args.Event.X, (int)args.Event.Y,
                out TreePath path, out _) ||
            !_messageStore.GetIter(out TreeIter iter, path))
            return;

        string frameType = (string)_messageStore.GetValue(iter, (int)Col.Type);
        if (IsLinRow(_messageStore, iter) || frameType == "ERR")
            return;

        Gdk.ModifierType modifiers = args.Event.State;
        int sendFrameIndex = (modifiers & Gdk.ModifierType.ControlMask) != 0
            ? 2
            : (modifiers & Gdk.ModifierType.ShiftMask) != 0 ? 1 : 0;
        AssignRowToSendFrame(iter, sendFrameIndex);
        args.RetVal = true;
    }

    private void AssignRowToSendFrame(TreeIter selectedIter, int sendFrameIndex)
    {
        if (sendFrameIndex < 0 || sendFrameIndex >= _sendFrames.Count)
            return;

        if (IsLinRow(_messageStore, selectedIter)) return;
        int assignedSlot = sendFrameIndex + 1;
        _messageStore.Foreach((model, path, iter) =>
        {
            if ((int)_messageStore.GetValue(iter, (int)Col.SendSlot) == assignedSlot)
            {
                _messageStore.SetValue(iter, (int)Col.SendSlot, 0);
                _messageStore.SetValue(iter, (int)Col.RowBackground, "rgba(0,0,0,0)");
            }
            return false;
        });

        _messageStore.SetValue(selectedIter, (int)Col.SendSlot, assignedSlot);
        _messageStore.SetValue(selectedIter, (int)Col.RowBackground,
            SendFrameBackgroundColors[sendFrameIndex]);
        _treeView.QueueDraw();
        PopulateSendFields(selectedIter, _sendFrames[sendFrameIndex]);
    }

    private void PopulateSendFieldsFromAssignedRow(SendFrameControls controls)
    {
        int assignedSlot = controls.Index + 1;
        _messageStore.Foreach((model, path, iter) =>
        {
            if ((int)_messageStore.GetValue(iter, (int)Col.SendSlot) != assignedSlot)
                return false;

            PopulateSendFields(iter, controls);
            return true;
        });
    }

    private void PopulateSendFields(TreeIter iter, SendFrameControls controls)
    {
        string frameType = (string)_messageStore.GetValue(iter, (int)Col.Type);
        if (IsLinRow(_messageStore, iter) || frameType == "ERR")
            return;

        int arbitrationId = (int)_messageStore.GetValue(iter, (int)Col.IdDec);
        if (controls.InputIsHex)
        {
            string idHex = (string)_messageStore.GetValue(iter, (int)Col.IdHex);
            string dataHex = (string)_messageStore.GetValue(iter, (int)Col.DataHex);
            controls.IdEntry.Text = idHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? idHex[2..]
                : idHex;
            controls.DataEntry.Text = dataHex == "-" ? "" : dataHex;
        }
        else
        {
            string dataDec = (string)_messageStore.GetValue(iter, (int)Col.DataDec);
            controls.IdEntry.Text = arbitrationId.ToString();
            controls.DataEntry.Text = dataDec == "-" ? "" : dataDec;
        }

        controls.ExtendedCheck.Active = frameType == "EXT";
    }

    private void SendFrame(SendFrameControls controls)
    {
        try
        {
            uint id;
            byte[] data;
            if (controls.InputIsHex)
            {
                id = Convert.ToUInt32(controls.IdEntry.Text.Trim(), 16);
                data = ParseHexData(controls.DataEntry.Text.Trim());
            }
            else
            {
                id = uint.Parse(controls.IdEntry.Text.Trim());
                data = ParseDecData(controls.DataEntry.Text.Trim());
            }
            bool isExt = controls.ExtendedCheck.Active;
            _backend.Send(id, data, isExt);
        }
        catch (Exception ex)
        {
            ShowError("Send Error", ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Watch list panel methods
    // ------------------------------------------------------------------

    private void AddWatchColumn(string title, int storeIdx, int width)
    {
        var cell = new CellRendererText();
        var column = new TreeViewColumn
        {
            Title = title,
            Resizable = true,
            MinWidth = width,
        };
        column.PackStart(cell, true);
        column.AddAttribute(cell, "text", storeIdx);
        _watchTreeView.AppendColumn(column);
    }

    private void PopulateWatchStore()
    {
        _watchStore.Clear();
        _watchStore.AppendValues(false, 0u, "CAN", "", "CAN", false);
        foreach (var def in CanScheme.AllEntries)
            _watchStore.AppendValues(_filterIds.Contains(def.Id), def.Id, $"{def.IdDec} - {def.IdHex}", def.Description, "CAN", true);
        _watchStore.AppendValues(false, 0u, "LIN", "", "LIN", false);
        foreach (var def in LinScheme.Entries)
            _watchStore.AppendValues(_linFilterIds.Contains((uint)def.Id), (uint)def.Id, $"{def.Id} - 0x{def.Id:X2}", def.Name, "LIN", true);
    }

    private static bool TryParseWatchId(string text, out uint id)
    {
        text = text.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return uint.TryParse(hex ? text[2..] : text,
            hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out id) && id <= 0x1FFFFFFF;
    }

    private void AddWatchId() => AddWatchIdForBus(false);

    private void AddWatchIdForBus(bool lin)
    {
        var entry = lin ? _linWatchIdEntry : _watchIdEntry;
        var error = lin ? _linWatchIdError : _watchIdError;
        if (!TryParseWatchId(entry.Text, out uint id) || (lin && id > 63))
        {
            error.Text = lin ? "Enter LIN ID 0–63 (0x00–0x3F), not PID." : "Enter CAN ID 0–536870911 (0x0–0x1FFFFFFF).";
            error.Show(); return;
        }
        string bus = lin ? "LIN" : "CAN";
        TreeIter match = default, linHeading = default;
        bool found = false;
        _watchStore.Foreach((model, path, iter) => {
            if (!(bool)model.GetValue(iter, 5)) {
                if ((string)model.GetValue(iter, 4) == "LIN") linHeading = iter;
                return false;
            }
            if ((string)model.GetValue(iter, 4) != bus || (uint)model.GetValue(iter, 1) != id) return false;
            match = iter; found = true; return false;
        });
        if (found) _watchStore.SetValue(match, 0, true);
        else {
            match = lin ? _watchStore.Append() : _watchStore.InsertBefore(linHeading);
            _watchStore.SetValues(match, true, id, $"{id} - 0x{id.ToString(lin ? "X2" : "X3")}", "Ad-hoc (session only)", bus, true);
        }
        if (lin) { _linFilterIds.Add(id); UpdateLinWatchFilter(); }
        else _filterIds = new HashSet<uint>(_filterIds) { id };
        var rowPath = _watchStore.GetPath(match);
        _watchTreeView.SetCursor(rowPath, null, false);
        _watchTreeView.ScrollToCell(rowPath, null, true, 0.5f, 0);
        entry.Text = ""; error.Hide(); entry.GrabFocus();
    }

    private void UpdateLinWatchFilter() => _linPanel?.SetWatchIds(_linFilterIds.Select(id => (int)id));

    private void OnWatchToggled(object o, ToggledArgs args)
    {
        if (_watchStore.GetIter(out var iter, new TreePath(args.Path)))
        {
            if (!(bool)_watchStore.GetValue(iter, 5)) return;
            bool lin = (string)_watchStore.GetValue(iter, 4) == "LIN";
            bool current = (bool)_watchStore.GetValue(iter, 0);
            _watchStore.SetValue(iter, 0, !current);
            uint id = (uint)_watchStore.GetValue(iter, 1);

            var selectedIds = new HashSet<uint>(lin ? _linFilterIds : _filterIds);
            if (!current)
                selectedIds.Add(id);
            else
                selectedIds.Remove(id);
            if (lin) { _linFilterIds = selectedIds; UpdateLinWatchFilter(); }
            else _filterIds = selectedIds;
        }
    }

    private void SetWatchAll(bool selected)
    {
        var selectedIds = new HashSet<uint>();
        var linIds = new HashSet<uint>();
        _watchStore.Foreach((model, path, iter) =>
        {
            if (!(bool)model.GetValue(iter, 5)) return false;
            uint id = (uint)_watchStore.GetValue(iter, 1);
            _watchStore.SetValue(iter, 0, selected);
            if (selected) { if ((string)model.GetValue(iter, 4) == "LIN") linIds.Add(id); else selectedIds.Add(id); }
            return false;
        });
        _filterIds = selectedIds;
        _linFilterIds = linIds; UpdateLinWatchFilter();
    }

    private void OnWatchInfo(object? sender, EventArgs e)
    {
        TreePath? path;
        TreeViewColumn? col;
        _watchTreeView.GetCursor(out path, out col);
        if (path == null) return;

        if (_watchStore.GetIter(out var iter, path))
        {
            if (!(bool)_watchStore.GetValue(iter, 5)) return;
            uint id = (uint)_watchStore.GetValue(iter, 1);
            if ((string)_watchStore.GetValue(iter, 4) == "LIN") { new LinReferenceWindow(_window, (int)id); return; }
            string info = CanScheme.GetInfoText(id);
            ShowCanIdInfo(id, info);
        }
    }

    private void ShowCanIdInfo(uint id, string info)
    {
        var dialog = new Dialog(
            $"ID {id} (0x{id:X}) Details", _window,
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

    private void TogglePeriodic(SendFrameControls controls)
    {
        if (controls.PeriodicRunning)
        {
            StopPeriodic(controls);
        }
        else
        {
            if (!int.TryParse(controls.PeriodEntry.Text.Trim(), out int ms) || ms <= 0)
            {
                ShowError("Error", "Enter a positive interval in ms.");
                return;
            }
            controls.PeriodicRunning = true;
            controls.PeriodicButton.Label = "Stop Periodic";
            SchedulePeriodic(controls, ms);
        }
    }

    private void SchedulePeriodic(SendFrameControls controls, int intervalMs)
    {
        if (!controls.PeriodicRunning) return;
        SendFrame(controls);
        GLib.Timeout.Add((uint)intervalMs, () =>
        {
            if (controls.PeriodicRunning && _backend.IsRunning)
                SendFrame(controls);
            return controls.PeriodicRunning;
        });
    }

    private static void StopPeriodic(SendFrameControls controls)
    {
        controls.PeriodicRunning = false;
        controls.PeriodicButton.Label = "Start Periodic";
    }

    private void StopAllPeriodic()
    {
        foreach (SendFrameControls controls in _sendFrames)
            StopPeriodic(controls);
    }

    private void OnToggleLogging(object? sender, EventArgs e)
    {
        if (_logEnabled)
        {
            StopLogging();
            return;
        }

        var dialog = new FileChooserDialog(
            "Save analyzer output", _window,
            FileChooserAction.Save,
            "Cancel", ResponseType.Cancel,
            "Save", ResponseType.Accept);
        dialog.DoOverwriteConfirmation = true;
        dialog.CurrentName = "CAN-LIN Analyzer Output.csv";

        if (dialog.Run() == (int)ResponseType.Accept)
        {
            StartLogging(dialog.Filename);
        }
        dialog.Destroy();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private void StartLogging(string path)
    {
        try
        {
            _logFilePath = Path.GetFullPath(path);
            _logWriter = new StreamWriter(_logFilePath, append: false) { AutoFlush = true };
            _logWriter.WriteLine("#,Received time,Bus,ID (decimal),ID (hex),Bytes,Data (decimal),Data (hex),Description,Type / Status");
            _logStartedAtUtc = DateTime.UtcNow;
            _loggedFrameCount = 0;
            _logEnabled = true;
            _logBtn.Label = "■ Stop logging";
            _logBtn.StyleContext.AddClass("destructive-action");
            StartLogStatusTimer();
            UpdateLoggingStatus();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            lock (_logLock)
            {
                try { _logWriter?.Dispose(); }
                catch (Exception) { }
                _logWriter = null;
            }
            _logEnabled = false;
            SetLoggingFailureStatus("Logging could not be started.");
            ShowError("Log Error", ex.Message);
        }
    }

    private void StopLogging(string? failureMessage = null)
    {
        bool wasLogging = _logEnabled || _logWriter != null;
        if (!wasLogging)
            return;

        _logEnabled = false;
        StopLogStatusTimer();
        Exception? closeError = null;
        lock (_logLock)
        {
            try { _logWriter?.Dispose(); }
            catch (Exception ex) { closeError = ex; }
            _logWriter = null;
        }

        _logBtn.Label = "\U0001f4c4 Log output to file";
        _logBtn.StyleContext.RemoveClass("destructive-action");
        if (failureMessage != null || closeError != null)
        {
            string message = failureMessage ?? "Recording stopped while closing the log file.";
            SetLoggingFailureStatus(message);
        }
        else
        {
            string fileName = Path.GetFileName(_logFilePath) ?? "log file";
            string frameText = FormatFrameCount(_loggedFrameCount);
            _logStatusLabel.Markup =
                $"<span foreground=\"#2e7d32\">● Saved</span> — " +
                $"{EscapeMarkup(fileName)} — {frameText}";
            _logStatusLabel.TooltipText = _logFilePath ?? "";
        }
        UpdateStatus();
    }

    private void StartLogStatusTimer()
    {
        StopLogStatusTimer();
        _logStatusTimerId = GLib.Timeout.Add(1000, () =>
        {
            if (!_logEnabled)
            {
                _logStatusTimerId = 0;
                return false;
            }

            UpdateLoggingStatus();
            return true;
        });
    }

    private void StopLogStatusTimer()
    {
        if (_logStatusTimerId == 0)
            return;

        GLib.Source.Remove(_logStatusTimerId);
        _logStatusTimerId = 0;
    }

    private void UpdateLoggingStatus()
    {
        if (!_logEnabled)
            return;

        string fileName = Path.GetFileName(_logFilePath) ?? "log file";
        TimeSpan elapsed = DateTime.UtcNow - _logStartedAtUtc;
        string elapsedText =
            $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        _logStatusLabel.Markup =
            $"<span foreground=\"#d32f2f\"><b>● RECORDING</b></span> — " +
            $"{EscapeMarkup(fileName)} — {elapsedText} — {FormatFrameCount(_loggedFrameCount)}" +
            (_loggedFrameCount == 0 ? " — waiting for output" : "");
        _logStatusLabel.TooltipText = _logFilePath ?? "";
    }

    private void SetLoggingFailureStatus(string message)
    {
        _logStatusLabel.Markup =
            $"<span foreground=\"#d32f2f\"><b>● Logging stopped</b></span> — " +
            EscapeMarkup(message);
        _logStatusLabel.TooltipText = _logFilePath ?? "";
    }

    private static string FormatFrameCount(long count) =>
        $"{count:N0} {(count == 1 ? "frame" : "frames")}";

    private static string EscapeMarkup(string value) =>
        System.Security.SecurityElement.Escape(value) ?? "";

    private void ClearMessages()
    {
        _messageStore.Clear();
        _messageCount = 0;
        _linPanel?.ResetCounts();
        _msgCountLabel.Text = "Messages: 0";
    }

    private void ResetLinHeaterConfiguration()
    {
        System.Threading.Interlocked.Increment(ref _canConfigurationGeneration);
        _linPanel?.ResetHeaterConfiguration();
    }

    private void StopAll(bool stopLogging = false)
    {
        StopAllPeriodic();
        _backend.Stop();
        ResetLinHeaterConfiguration();
        if (stopLogging)
            StopLogging();
        _startStopBtn.Label = "\u25b6 Start";
        SetSendControlsSensitive(false);
        SetConnectionControlsSensitive(true);
        UpdateStatus();
        UpdateLoggingStatus();
    }

    private void SetSendControlsSensitive(bool sensitive)
    {
        foreach (SendFrameControls controls in _sendFrames)
        {
            controls.SendButton.Sensitive = sensitive;
            controls.PeriodicButton.Sensitive = sensitive;
        }
    }

    private void UpdateStatus()
    {
        if (_backend.IsRunning)
        {
            string bitrate = _stdinMode ? "" : $" @ {GetSelectedBitrate()} bit/s";
            _statusLabel.Text = $"Connected \u2014 {_backend.InterfaceName}{bitrate}";
        }
        else
        {
            _statusLabel.Text = "Disconnected";
        }
    }

    private void RefreshCanInterfaces()
    {
        if (_stdinMode || _interfaceCombo == null)
            return;

        string current = _interfaceCombo.Entry.Text.Trim();
        var interfaces = CanInterfaceManager.GetCanInterfaces()
            .Concat(WaveshareWindowsBackend.GetAvailableInterfaces())
            .Concat(ItekWindowsBackend.GetAvailableInterfaces())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Keep the conventional name available before an adapter is plugged in,
        // and retain a manually-entered interface such as slcan0.
        if (!interfaces.Contains("can0", StringComparer.Ordinal))
            interfaces.Add("can0");
        if (!string.IsNullOrEmpty(current) &&
            !interfaces.Contains(current, StringComparer.Ordinal))
            interfaces.Add(current);

        _interfaceCombo.RemoveAll();
        foreach (string iface in interfaces.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            _interfaceCombo.AppendText(iface);

        string selected = string.IsNullOrEmpty(current) ? interfaces[0] : current;
        _interfaceCombo.Entry.Text = selected;
    }

    private int GetSelectedBitrate()
    {
        string? text = _bitrateCombo.ActiveText;
        if (!int.TryParse(text, out int bitrate) || bitrate <= 0)
            throw new ArgumentException("Select a valid CAN bitrate.");
        return bitrate;
    }

    private void SelectBackend(string interfaceName, int bitrate)
    {
        ICanBackend backend = interfaceName switch
        {
            _ when WaveshareWindowsBackend.IsWaveshareInterface(interfaceName) =>
                new WaveshareWindowsBackend(bitrate),
            _ when ItekWindowsBackend.IsItekInterface(interfaceName) =>
                new ItekWindowsBackend(bitrate),
            _ => new CanBackend(),
        };

        _backend.Stop();
        ResetLinHeaterConfiguration();
        _backend.OnMessageReceived -= OnCanMessage;
        _backend.OnError -= OnCanError;
        if (_backend is IDisposable disposable)
            disposable.Dispose();

        _backend = backend;
        _backend.OnMessageReceived += OnCanMessage;
        _backend.OnError += OnCanError;
    }

    private static bool IsWindowsVendorInterface(string interfaceName) =>
        WaveshareWindowsBackend.IsWaveshareInterface(interfaceName) ||
        ItekWindowsBackend.IsItekInterface(interfaceName);

    private void SetConnectionControlsSensitive(bool sensitive)
    {
        if (_stdinMode)
            return;

        _interfaceCombo.Sensitive = sensitive;
        _refreshInterfacesBtn.Sensitive = sensitive;
        _bitrateCombo.Sensitive = sensitive;
    }

    private static byte[] ParseHexData(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<byte>();
        byte[] data = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            data[i] = Convert.ToByte(parts[i], 16);
        if (data.Length > 8)
            throw new ArgumentException("Data length exceeds 8 bytes (classic CAN).");
        return data;
    }

    private static byte[] ParseDecData(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<byte>();
        byte[] data = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            data[i] = byte.Parse(parts[i]);
        if (data.Length > 8)
            throw new ArgumentException("Data length exceeds 8 bytes (classic CAN).");
        return data;
    }

    private void ShowError(string title, string message)
    {
        var dialog = new Dialog(title, _window, DialogFlags.Modal | DialogFlags.DestroyWithParent);

        // Selectable, scrollable text view
        var textView = new TextView
        {
            Editable = false,
            CursorVisible = false,
            WrapMode = WrapMode.Word,
            Buffer = { Text = message },
        };
        var scroller = new ScrolledWindow { ShadowType = ShadowType.In };
        scroller.SetSizeRequest(480, 120);
        scroller.Add(textView);
        dialog.ContentArea.PackStart(scroller, true, true, 8);

        // Button box
        var btnBox = new ButtonBox(Orientation.Horizontal) { Layout = ButtonBoxStyle.End, MarginTop = 4 };
        dialog.ContentArea.PackStart(btnBox, false, false, 0);

        // Copy button
        var copyBtn = new Button("Copy");
        copyBtn.Clicked += (_, _) =>
        {
            var clipboard = Clipboard.Get(Gdk.Atom.Intern("CLIPBOARD", false));
            clipboard.Text = message;
            Clipboard.Get(Gdk.Atom.Intern("PRIMARY", false)).Text = message;
        };
        btnBox.PackStart(copyBtn, false, false, 0);

        // Close button
        dialog.AddButton("Close", ResponseType.Close);
        dialog.DefaultResponse = ResponseType.Close;

        dialog.ShowAll();
        dialog.Run();
        dialog.Destroy();
    }
}
