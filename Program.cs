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
    private readonly ICanBackend _backend;
    private readonly bool _stdinMode;
    private ListStore _messageStore = null!;
    private TreeView _treeView = null!;
    private Window _window = null!;

    // Controls
    private Entry _interfaceEntry = null!;
    private ComboBoxText _bitrateCombo = null!;
    private Button _startStopBtn = null!;
    private Button _clearBtn = null!;
    private Button _logBtn = null!;
    private CheckButton _lockScrollCheck = null!;
    private Label _idLabel = null!;
    private Label _dataLabel = null!;
    private Entry _sendIdEntry = null!;
    private Entry _sendDataEntry = null!;
    private Button _hexDecToggle = null!;
    private CheckButton _extendedCheck = null!;
    private Button _sendBtn = null!;
    private Entry _periodEntry = null!;
    private Button _periodicBtn = null!;
    private bool _inputIsHex = true;
    private TreeView _watchTreeView = null!;
    private ListStore _watchStore = null!;
    private HashSet<uint> _filterIds = new();

    private Label _statusLabel = null!;
    private Label _msgCountLabel = null!;

    // State
    private int _messageCount;
    private bool _logEnabled;
    private StreamWriter? _logWriter;
    private bool _periodicRunning;
    private readonly object _logLock = new();

    private const int MaxLogRows = 2000;

    // Column indices in ListStore
    private enum Col { Num, Timestamp, IdDec, DataDec, IdHex, Dlc, DataHex, Desc, Type, Count }

    // ------------------------------------------------------------------
    // Application entry point
    // ------------------------------------------------------------------
    public static void Main(string[] args)
    {
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
            StopAll();
            Application.Quit();
        };

        var mainBox = new Box(Orientation.Vertical, 0);

        // -- Top control bar ------------------------------------------------
        var controlBar = new Box(Orientation.Horizontal, 4);
        controlBar.Margin = 5;

        controlBar.PackStart(new Label("Interface:"), false, false, 0);
        _interfaceEntry = _stdinMode
            ? new Entry("stdin (pipe)") { WidthChars = 10, IsEditable = false }
            : new Entry("can0") { WidthChars = 10 };
        controlBar.PackStart(_interfaceEntry, false, false, 2);

        controlBar.PackStart(new Label("Bitrate:"), false, false, 0);
        _bitrateCombo = ComboBoxText.NewWithEntry();
        foreach (var br in new[] { "10000", "20000", "50000", "100000", "125000",
                                   "250000", "500000", "800000", "1000000" })
            _bitrateCombo.AppendText(br);
        _bitrateCombo.Entry.Text = "500000";
        _bitrateCombo.SetSizeRequest(100, -1);
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
            typeof(string),  // ID (dec)
            typeof(string),  // ID (hex)
            typeof(string)   // Description
        );

        PopulateWatchStore();

        _watchTreeView = new TreeView(_watchStore)
        {
            HeadersVisible = true,
            EnableSearch = true,
            SearchColumn = 4, // Description
        };

        var wtToggle = new CellRendererToggle();
        wtToggle.Toggled += OnWatchToggled;
        var wtCol = new TreeViewColumn("", wtToggle, "active", 0) { MinWidth = 30 };
        _watchTreeView.AppendColumn(wtCol);

        AddWatchColumn("ID", 2, 45);
        AddWatchColumn("Description", 4, 140);

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
            typeof(string)  // Type
        );

        _treeView = new TreeView(_messageStore)
        {
            HeadersVisible = true,
            EnableSearch = false,
            HasTooltip = true,
        };
        _treeView.QueryTooltip += OnTreeViewQueryTooltip;

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

        // -- Send frame panel -----------------------------------------------
        var sendFrame = new Frame("Send CAN Frame");
        var sendBox = new Box(Orientation.Horizontal, 4);
        sendBox.Margin = 5;

        _idLabel = new Label("ID (hex):");
        sendBox.PackStart(_idLabel, false, false, 0);
        _sendIdEntry = new Entry("7DF") { WidthChars = 8 };
        sendBox.PackStart(_sendIdEntry, false, false, 2);

        _dataLabel = new Label("Data (hex bytes):");
        sendBox.PackStart(_dataLabel, false, false, 0);
        _sendDataEntry = new Entry("02 01 00") { WidthChars = 30 };
        sendBox.PackStart(_sendDataEntry, false, false, 2);

        _hexDecToggle = new Button("Hex");
        _hexDecToggle.Clicked += OnToggleHexDec;
        sendBox.PackStart(_hexDecToggle, false, false, 2);

        _extendedCheck = new CheckButton("Extended ID");
        _extendedCheck.Active = false;
        sendBox.PackStart(_extendedCheck, false, false, 2);

        _sendBtn = new Button("Send");
        _sendBtn.Sensitive = false;
        _sendBtn.Clicked += OnSendFrame;
        sendBox.PackStart(_sendBtn, false, false, 4);

        sendBox.PackStart(new Separator(Orientation.Vertical), false, false, 4);

        sendBox.PackStart(new Label("Periodic (ms):"), false, false, 0);
        _periodEntry = new Entry("") { WidthChars = 6 };
        sendBox.PackStart(_periodEntry, false, false, 2);

        _periodicBtn = new Button("Start Periodic");
        _periodicBtn.Sensitive = false;
        _periodicBtn.Clicked += OnTogglePeriodic;
        sendBox.PackStart(_periodicBtn, false, false, 2);

        sendFrame.Add(sendBox);
        mainBox.PackStart(sendFrame, false, false, 5);

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
        }

        win.Add(mainBox);
        return win;
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
            _messageCount, ts, (int)msg.ArbitrationId, dataDec, idHex, (int)msg.Dlc, dataHex, desc, frameType);

        // Auto-scroll to top unless locked
        if (!_lockScrollCheck.Active)
            _treeView.ScrollToCell(new TreePath("0"), null, true, 0, 0);

        // Log to file if enabled
        if (_logEnabled && _logWriter != null)
        {
            lock (_logLock)
            {
                try
                {
                    _logWriter.WriteLine(
                        $"{_messageCount},{ts},{idHex},{msg.Dlc},{dataHex},{frameType}");
                    _logWriter.Flush();
                }
                catch
                {
                    // Silently stop logging on write error
                }
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
                string iface = _stdinMode ? "stdin" : _interfaceEntry.Text.Trim();
                _backend.Start(iface);
                _startStopBtn.Label = "\u23f9 Stop";
                _sendBtn.Sensitive = true;
                _periodicBtn.Sensitive = true;
                _statusLabel.Text = $"Connected \u2014 {iface}";
            }
            catch (Exception ex)
            {
                ShowError("CAN Error",
                    $"Could not open interface:\n{ex.Message}\n\n" +
                    "Make sure the interface exists and you have permissions.");
            }
        }
    }

    private void OnToggleHexDec(object? sender, EventArgs e)
    {
        _inputIsHex = !_inputIsHex;
        if (_inputIsHex)
        {
            _hexDecToggle.Label = "Hex";
            _idLabel.Text = "ID (hex):";
            _dataLabel.Text = "Data (hex bytes):";
        }
        else
        {
            _hexDecToggle.Label = "Dec";
            _idLabel.Text = "ID (dec):";
            _dataLabel.Text = "Data (dec bytes):";
        }
    }

    private void OnSendFrame(object? sender, EventArgs e)
    {
        try
        {
            uint id;
            byte[] data;
            if (_inputIsHex)
            {
                id = Convert.ToUInt32(_sendIdEntry.Text.Trim(), 16);
                data = ParseHexData(_sendDataEntry.Text.Trim());
            }
            else
            {
                id = uint.Parse(_sendIdEntry.Text.Trim());
                data = ParseDecData(_sendDataEntry.Text.Trim());
            }
            bool isExt = _extendedCheck.Active;
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
                def.IdDec,
                def.IdHex,
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

    private void OnTogglePeriodic(object? sender, EventArgs e)
    {
        if (_periodicRunning)
        {
            StopPeriodic();
        }
        else
        {
            if (!int.TryParse(_periodEntry.Text.Trim(), out int ms) || ms <= 0)
            {
                ShowError("Error", "Enter a positive interval in ms.");
                return;
            }
            _periodicRunning = true;
            _periodicBtn.Label = "Stop Periodic";
            SchedulePeriodic(ms);
        }
    }

    private void SchedulePeriodic(int intervalMs)
    {
        if (!_periodicRunning) return;
        OnSendFrame(null, EventArgs.Empty);
        GLib.Timeout.Add((uint)intervalMs, () =>
        {
            if (_periodicRunning && _backend.IsRunning)
                OnSendFrame(null, EventArgs.Empty);
            return _periodicRunning;
        });
    }

    private void StopPeriodic()
    {
        _periodicRunning = false;
        _periodicBtn.Label = "Start Periodic";
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
            _logWriter = new StreamWriter(path, append: false);
            _logWriter.WriteLine("#,Timestamp,ID (hex),DLC,Data (hex),Type");
            _logEnabled = true;
            _logBtn.Label = "\u23f9 Stop Logging";
            UpdateStatus();
        }
        catch (Exception ex)
        {
            ShowError("Log Error", ex.Message);
        }
    }

    private void StopLogging()
    {
        _logEnabled = false;
        lock (_logLock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
        _logBtn.Label = "\U0001f4c4 Log to File";
        UpdateStatus();
    }

    private void ClearMessages()
    {
        _messageStore.Clear();
        _messageCount = 0;
        _msgCountLabel.Text = "Messages: 0";
    }

    private void StopAll()
    {
        StopPeriodic();
        StopLogging();
        _backend.Stop();
        _startStopBtn.Label = "\u25b6 Start";
        _sendBtn.Sensitive = false;
        _periodicBtn.Sensitive = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_backend.IsRunning)
        {
            string extra = _logEnabled ? " (logging)" : "";
            _statusLabel.Text = $"Connected \u2014 {_backend.InterfaceName}{extra}";
        }
        else
        {
            _statusLabel.Text = "Disconnected";
        }
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

