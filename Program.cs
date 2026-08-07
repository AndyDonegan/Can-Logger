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
    private HashSet<uint> _filterIds = new();

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
        SendSlot, RowBackground, Count
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
        var win = new Window("CAN Bus Analyzer")
        {
            Resizable = true,
            TypeHint = Gdk.WindowTypeHint.Normal,
        };
        win.SetDefaultSize(1300, 700);
        win.DeleteEvent += (_, _) =>
        {
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
                "Detected SocketCAN interfaces and Waveshare USB-CAN-FD channels.";
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

        _logBtn = new Button("\U0001f4c4 Log to File");
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

        // -- Main area: watch list (left) | message table (right) -----------
        var paned = new Paned(Orientation.Horizontal) { Position = 260 };

        // ===== Watch list panel (left) =====================================
        var watchPanel = new Box(Orientation.Vertical, 2);

        // Watch list header
        var watchHeader = new Label("<b>Watch List</b>") { UseMarkup = true, Margin = 4 };
        watchPanel.PackStart(watchHeader, false, false, 0);

        // Watch list TreeView
        _watchStore = new ListStore(
            typeof(bool),    // toggle
            typeof(uint),    // CAN ID (hidden)
            typeof(string),  // ID (decimal and hex)
            typeof(string)   // Description
        );

        PopulateWatchStore();

        _watchTreeView = new TreeView(_watchStore)
        {
            HeadersVisible = true,
            EnableSearch = true,
            SearchColumn = 3, // Description
        };

        var wtToggle = new CellRendererToggle();
        wtToggle.Toggled += OnWatchToggled;
        var wtCol = new TreeViewColumn("", wtToggle, "active", 0) { MinWidth = 30 };
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

        paned.Pack1(watchPanel, false, true);

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
            typeof(string)  // Assigned row background, or transparent when unassigned
        );

        _treeView = new TreeView(_messageStore)
        {
            HeadersVisible = true,
            EnableSearch = false,
            HasTooltip = true,
        };
        _treeView.QueryTooltip += OnTreeViewQueryTooltip;
        _treeView.ButtonPressEvent += OnMessageButtonPress;

        AddColumn("#", Col.Num, 40);
        AddColumn("Timestamp", Col.Timestamp, 130);
        AddColumn("ID (dec)", Col.IdDec, 60);
        AddColumn("Data (dec)", Col.DataDec, 200);
        AddColumn("ID (hex)", Col.IdHex, 90);
        AddColumn("DLC", Col.Dlc, 40);
        AddColumn("Data (hex)", Col.DataHex, 300);
        AddColumn("Description", Col.Desc, 200);
        AddColumn("Type", Col.Type, 60);

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
        column.PackStart(cell, true);
        column.AddAttribute(cell, "text", (int)col);
        column.AddAttribute(cell, "cell-background", (int)Col.RowBackground);
        _treeView.AppendColumn(column);
    }

    // ------------------------------------------------------------------
    // CAN message handler (called on background thread)
    // ------------------------------------------------------------------
    private void OnCanMessage(CanMessage msg)
    {
        // Filter: skip if watch list is active and ID doesn't match
        if (_filterIds.Count > 0 && !_filterIds.Contains(msg.ArbitrationId))
            return;

        // Marshal to GTK main thread
        GLib.Idle.Add(() =>
        {
            AddMessageToStore(msg);
            return false; // remove the idle handler
        });
    }

    private void OnTreeViewQueryTooltip(object o, QueryTooltipArgs args)
    {
        if (!CanScheme.IsLoaded) return;

        if (_treeView.GetPathAtPos(args.X, args.Y, out var path, out _))
        {
            if (_messageStore.GetIter(out var iter, path))
            {
                // IdDec column stores the arbitration ID as int
                uint id = (uint)(int)_messageStore.GetValue(iter, (int)Col.IdDec);
                string tip = CanScheme.GetTooltipText(id);
                if (!string.IsNullOrEmpty(tip))
                {
                    args.Tooltip.Text = tip;
                    args.RetVal = true;
                }
            }
        }
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

        _messageStore.InsertWithValues(0,
            _messageCount, ts, (int)msg.ArbitrationId, dataDec, idHex, (int)msg.Dlc,
            dataHex, desc, frameType, 0, "rgba(0,0,0,0)");

        // Auto-scroll to top unless locked
        if (!_lockScrollCheck.Active)
            _treeView.ScrollToCell(new TreePath("0"), null, true, 0, 0);

        // Log to file if enabled
        if (_logEnabled && _logWriter != null)
        {
            Exception? writeError = null;
            lock (_logLock)
            {
                try
                {
                    long logRowNumber = _loggedFrameCount + 1;
                    _logWriter.WriteLine(
                        $"{logRowNumber},{ts},{idHex},{msg.Dlc},{dataHex},{frameType}");
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

        // Trim old rows
        if (_messageStore.IterNChildren() > MaxLogRows)
        {
            if (_messageStore.IterNthChild(out var last, MaxLogRows))
                while (_messageStore.Remove(ref last)) { }
        }

        _msgCountLabel.Text = $"Messages: {_messageCount}";
    }

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
                if (!_stdinMode && !WaveshareWindowsBackend.IsWaveshareInterface(iface))
                    CanInterfaceManager.EnsureReady(iface, bitrate);
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
        if (frameType == "ERR")
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
        if (frameType == "ERR")
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
        if (!CanScheme.IsLoaded) return;

        foreach (var def in CanScheme.AllEntries)
        {
            _watchStore.AppendValues(
                _filterIds.Contains(def.Id),
                def.Id,
                $"{def.IdDec} - {def.IdHex}",
                def.Description
            );
        }
    }

    private void OnWatchToggled(object o, ToggledArgs args)
    {
        if (_watchStore.GetIter(out var iter, new TreePath(args.Path)))
        {
            bool current = (bool)_watchStore.GetValue(iter, 0);
            _watchStore.SetValue(iter, 0, !current);
            uint id = (uint)_watchStore.GetValue(iter, 1);

            if (!current)
                _filterIds.Add(id);
            else
                _filterIds.Remove(id);
        }
    }

    private void SetWatchAll(bool selected)
    {
        _filterIds.Clear();
        _watchStore.Foreach((model, path, iter) =>
        {
            uint id = (uint)_watchStore.GetValue(iter, 1);
            _watchStore.SetValue(iter, 0, selected);
            if (selected) _filterIds.Add(id);
            return false;
        });
    }

    private void OnWatchInfo(object? sender, EventArgs e)
    {
        TreePath? path;
        TreeViewColumn? col;
        _watchTreeView.GetCursor(out path, out col);
        if (path == null) return;

        if (_watchStore.GetIter(out var iter, path))
        {
            uint id = (uint)_watchStore.GetValue(iter, 1);
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
            "Save CAN Log", _window,
            FileChooserAction.Save,
            "Cancel", ResponseType.Cancel,
            "Save", ResponseType.Accept);
        dialog.DoOverwriteConfirmation = true;
        dialog.CurrentName = "can_log.csv";

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
            _logWriter.WriteLine("#,Timestamp,ID (hex),DLC,Data (hex),Type");
            _logStartedAtUtc = DateTime.UtcNow;
            _loggedFrameCount = 0;
            _logEnabled = true;
            _logBtn.Label = "■ Stop Logging";
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

        _logBtn.Label = "\U0001f4c4 Log to File";
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
            (_backend.IsRunning ? "" : " — waiting for CAN");
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
        _msgCountLabel.Text = "Messages: 0";
    }

    private void StopAll(bool stopLogging = false)
    {
        StopAllPeriodic();
        _backend.Stop();
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
        ICanBackend backend = WaveshareWindowsBackend.IsWaveshareInterface(interfaceName)
            ? new WaveshareWindowsBackend(bitrate)
            : new CanBackend();

        _backend.Stop();
        _backend.OnMessageReceived -= OnCanMessage;
        _backend.OnError -= OnCanError;
        if (_backend is IDisposable disposable)
            disposable.Dispose();

        _backend = backend;
        _backend.OnMessageReceived += OnCanMessage;
        _backend.OnError += OnCanError;
    }

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
