using CanLogger;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
LinMessage Frame(byte pid, string bytes, int error = 0) => LinMessage.Parse($"FRAME\t1790181644204\t{pid}\t19200\t0.123456\t{error}\t{bytes}");
Check(Frame(0x37, "AA0A00000000000014").ChecksumStatus == "Valid enhanced", "Live ID 55 checksum failed");
Check(Frame(0x39, "D20A00000000A30B3B").ChecksumStatus == "Valid enhanced", "Live ID 57 checksum failed");
Check(Frame(0xBA, "", 1).ParityValid && Frame(0x78, "", 1).ParityValid, "Polling header parity incorrect");
Check(Frame(0x34, "").ExpectedPid == 0xB4 && !Frame(0x34, "").ParityValid, "ID 52 expected PID incorrect");
Check(Frame(0xB4, "").ParityValid && Frame(0x00, "").ExpectedPid == 0x80, "PID comparison incorrect");
var classic = Frame(0x85, "1020304050607080BD");
Check(classic.Id == 5 && classic.ParityValid && classic.Payload == "1020304050607080", "PID normalization/payload failed");
Check(classic.ChecksumStatus == "Valid classic", "Classic checksum failed");
Check(Frame(0x85, "102030405060708038").ChecksumStatus == "Valid enhanced", "Enhanced checksum failed");
Check(Frame(0x85, "102030405060708000").ChecksumStatus == "Invalid", "Bad checksum accepted");
Check(!Frame(0x05, "01FE").ParityValid, "Bad parity accepted");
var partial = Frame(52, "101010F4", 1);
Check(!partial.Complete && partial.Payload == "" && partial.RawBytes.Length == 4 && partial.Status.Contains("timeout"), "Partial frame lost or treated as complete");
Check(!Frame(0x80, "", 1).Complete, "Header-only record accepted as full response");
Check(Frame(0x3c, "01C2").ChecksumStatus == "Invalid", "Diagnostic ID accepted enhanced checksum");
Check(Frame(0x85, "01FE").ChecksumStatus == "Valid classic", "Short response checksum not checked");
Check(Frame(0x85, "01FE", 1).ChecksumStatus == "Possible classic match (receive error)", "Timeout classic candidate not checked");
Check(Frame(0x85, "0179", 1).ChecksumStatus == "Possible enhanced match (receive error)", "Timeout enhanced candidate not checked");
Check(Frame(0x85, "01FE", 6).Status.Contains("Next header"), "Checksum match hid receive error");
Check(Frame(0x05, "01FE").ChecksumStatus.Contains("PID error"), "Bad parity reported as verified checksum");
Check(partial.ChecksumByte == "0xF4" && partial.ChecksumStatus == "No match (candidate)", "Captured timeout candidate incorrect");
Check(Frame(0x80, "").ChecksumStatus == "Unavailable (no bytes)" && Frame(0x80, "").ChecksumByte == "—", "Empty record checksum incorrect");
Check(Frame(0x80, "FF", 1).ChecksumStatus == "Not testable (only one byte)", "Single byte misidentified as checksum");
Check(Frame(0x3c, "01FE", 1).ChecksumStatus.Contains("classic match"), "Diagnostic classic candidate incorrect");
Check(Frame(0x3c, "01C2", 1).ChecksumStatus == "No match (candidate)", "Diagnostic enhanced candidate accepted");
foreach (string bad in new[] { "FRAME\t1\t256\t1\t0\t0\t", "FRAME\t1\t0\t1\tNaN\t0\t", "FRAME\t1\t0\t1\t0\t0\t00112233445566778899", "FRAME\t1\t0\t1\t0\t0\t0" }) {
    bool rejected = false;
    try { LinMessage.Parse(bad); } catch { rejected = true; }
    Check(rejected, "Malformed record accepted");
}
Check(LinScheme.Entries.Select(e => e.Id).SequenceEqual(new[] {8,11,12,23,26,27,36,55,56,57,58,60,61}), "EC600 ID inventory changed");
Check(LinScheme.Find(58)!.Pid == 0xBA && LinScheme.Find(61)!.Pid == 0x7D, "Reference PID mismatch");
string described = LinScheme.Describe(55, "Whale", "AA0A000000000000", "AA 0A 00 00 00 00 00 00 14", "Valid enhanced");
Check(described.Contains("B0: 0xAA / 170 / 10101010") && !described.Contains("B8:"), "Payload bit view includes checksum or wrong bits");
Check(described.Contains("Whale") && !described.Contains("Truma CP+"), "Selected layout mixed with another make");
Check(LinScheme.Describe(56, null, "", "", "Bus timeout").Contains("No complete payload"), "Header-only record presented as data");
Check(LinScheme.Describe(55, "Webasto").Contains("does not apply"), "Inapplicable layout silently decoded");
Check(LinScheme.Describe(1).Contains("Unknown"), "Unknown ID given invented definition");
Check(LinScheme.Describe(58).Contains("Alternative layouts"), "Shared ID ambiguity hidden");
var configTime = DateTime.Now;
CanMessage SettingFrame(byte value) => new(configTime, 133, false, false, 8, new byte[] {0,0,value,0,0,0,0,0});
foreach (var (setting, name) in new[] {(2, "Truma CP+"), (3, "Whale"), (4, "Eberspacher")})
    Check(LinHeaterConfiguration.FromCan(SettingFrame((byte)setting))!.LayoutFor(58, configTime) == name, "Wrong automatic heater mapping");
foreach (byte value in new byte[] {0,1,5,255})
    Check(LinHeaterConfiguration.FromCan(SettingFrame(value))!.LayoutFor(58, configTime) == null, "Ambiguous/unsupported heater auto-selected");
Check(LinHeaterConfiguration.FromCan(SettingFrame(3) with { ArbitrationId = 173 }) == null, "Setting command mistaken for confirmed PSU setting");
Check(LinHeaterConfiguration.FromCan(SettingFrame(3) with { IsExtended = true }) == null, "Extended frame used for configuration");
Check(LinHeaterConfiguration.FromCan(SettingFrame(3) with { IsError = true }) == null, "Error frame used for configuration");
Check(LinHeaterConfiguration.FromCan(SettingFrame(3) with { Dlc = 2 }) == null, "Short setting used");
var configured = LinHeaterConfiguration.FromCan(SettingFrame(4))!;
Check(configured.LayoutFor(55, configTime) == null && configured.LayoutFor(58, configTime.AddSeconds(-1)) == null, "Unsupported ID or future configuration used");
Console.WriteLine("LIN parser and EC600 definition checks passed.");
if (args.Contains("--hardware")) {
    for (int run = 0; run < 2; run++) {
        var backend = new MicrochipLinBackend(); int count = 0;
        backend.Status += Console.WriteLine;
        backend.Message += frame => { Interlocked.Increment(ref count); Console.WriteLine($"ID={frame.Id} PID={frame.Pid:X2} baud={frame.Baud} raw={Convert.ToHexString(frame.RawBytes)} {frame.Status}"); };
        Task receiver = Task.Run(backend.RunAsync);
        await Task.Delay(12000);
        backend.Stop();
        await receiver.WaitAsync(TimeSpan.FromSeconds(8));
        Check(count > 10, "Hardware restart did not receive sustained records");
        Console.WriteLine($"Hardware run {run+1}: {count} records; stop completed.");
    }
}

if (args.Contains("--ui")) {
    Gtk.Application.Init();
    Console.WriteLine($"GTK display: {Gdk.Display.Default.NativeType}");
    CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
    var app = new CanAnalyzerApp(stdinMode: true);
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    var window = (Gtk.Window)typeof(CanAnalyzerApp).GetMethod("BuildWindow", flags)!.Invoke(app, null)!;
    var panel = (LinReceivePanel)typeof(CanAnalyzerApp).GetField("_linPanel", flags)!.GetValue(app)!;
    var start = (Gtk.Button)typeof(LinReceivePanel).GetField("_start", flags)!.GetValue(panel)!;
    var status = (Gtk.Label)typeof(LinReceivePanel).GetField("_status", flags)!.GetValue(panel)!;
    var rows = (Gtk.ListStore)typeof(CanAnalyzerApp).GetField("_messageStore", flags)!.GetValue(app)!;
    void Pump(int milliseconds) {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) {
            while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration();
            Thread.Sleep(10);
        }
    }
    try {
        window.ShowAll(); Pump(300);
        var analyzer = (Gtk.ComboBoxText)typeof(LinReceivePanel).GetField("_analyzer", flags)!.GetValue(panel)!;
        Check(start.IsMapped && analyzer.IsMapped, "LIN controls not visible");
        Check(analyzer.ActiveId == MicrochipLinBackend.AnalyzerId, "Wrong analyzer selected");
        start.Click(); Pump(12000);
        if (args.Contains("--long")) {
            int initialCount = rows.IterNChildren();
            Pump(33000);
            Check(initialCount > 0 && rows.IterNChildren() > initialCount + 10, "LIN reception did not continue throughout the test");
            int valid = 0;
            for (int i = 0; i < rows.IterNChildren(); i++) {
                rows.IterNthChild(out var row, i);
                if ((string)rows.GetValue(row, 8) == "Valid enhanced") valid++;
            }
            Check(valid > 10, "Too few checksum-verified responses");
            Console.WriteLine($"Sustained reception: {initialCount} rows at 12s, {rows.IterNChildren()} at 45s; {valid} valid enhanced checksums.");
        }
        Check(rows.IterNChildren() > 0, "Live LIN frames missing from combined view");
        Console.WriteLine($"LIN UI: {status.Text}; combined rows={rows.IterNChildren()}");
        Check(start.Label == "Stop LIN" && !status.Text.StartsWith("LIN error"), "GTK receive start failed");
        Check(status.Text.Contains("USB responding") && status.Text.Contains("No new LIN records for"), "Missing live USB/record activity status");
        Check(!analyzer.Sensitive && panel.IsMapped, "Start hid controls or did not lock analyzer selection");
        start.Click(); Pump(5000);
        Check(start.Label == "Start LIN" && start.Sensitive && analyzer.Sensitive && status.Text == "Stopped", "GTK receive stop failed");
        Console.WriteLine("GTK LIN start/display/stop checks passed.");
    } finally { panel.Close(); window.Destroy(); Pump(200); }
}

if (args.Contains("--checksum-ui")) {
    Gtk.Application.Init();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
    var app = new CanAnalyzerApp(stdinMode: true);
    var window = (Gtk.Window)typeof(CanAnalyzerApp).GetMethod("BuildWindow", flags)!.Invoke(app, null)!;
    var panel = (LinReceivePanel)typeof(CanAnalyzerApp).GetField("_linPanel", flags)!.GetValue(app)!;
    window.ShowAll();
    var pending = (System.Collections.Concurrent.ConcurrentQueue<LinMessage>)typeof(LinReceivePanel).GetField("_pending", flags)!.GetValue(panel)!;
    var rows = (Gtk.ListStore)typeof(CanAnalyzerApp).GetField("_messageStore", flags)!.GetValue(app)!;
    try {
        pending.Enqueue(Frame(0x85, "01FE", 1));
        pending.Enqueue(Frame(0, "", 1));
        pending.Enqueue(Frame(0x37, "AA0A00000000000014"));
        var until = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < until) {
            while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration();
            Thread.Sleep(10);
        }
        Check(rows.IterNChildren() == 3, "Checksum fixtures not displayed");
        rows.IterNthChild(out var known, 0); rows.IterNthChild(out var empty, 1); rows.IterNthChild(out var candidate, 2);
        Check(((string)rows.GetValue(known, 7)).Contains("Water-heater control"), "Known LIN row missing identification");
        Check(((string)rows.GetValue(empty, 7)).Contains("Unknown"), "Unknown LIN row not labelled");
        Check(((string)rows.GetValue(empty, 15)).Contains("128 / 0x80"), "Expected PID missing");
        Check(((string)rows.GetValue(empty, 15)).Contains("Unavailable (no bytes)"), "Empty checksum incorrect");
        Check(((string)rows.GetValue(candidate, 15)).Contains("254 / 0xFE") && ((string)rows.GetValue(candidate, 15)).Contains("Possible classic match (receive error)"), "Candidate checksum incorrect");
        Check(((string)rows.GetValue(candidate, 8)).Contains("timeout"), "Checksum hid timeout warning");
        var linTree = (Gtk.TreeView)typeof(CanAnalyzerApp).GetField("_treeView", flags)!.GetValue(app)!;
        var busColumn = linTree.Columns.Single(c => c.Title == "Bus");
        linTree.Selection.UnselectAll();
        busColumn.CellSetCellData(rows, known, false, false);
        var busCell = (Gtk.CellRendererText)busColumn.Cells[0];
        Check(busCell.Text == "LIN", "LIN bus label missing");
        bool Flag(Gtk.CellRendererText cell, string name) {
            var get = typeof(GLib.Object).GetMethod("GetProperty", flags | System.Reflection.BindingFlags.Public)!;
            using var value = (GLib.Value)get.Invoke(cell, new object[] {name})!;
            return (bool)value;
        }
        Check(Flag(busCell, "cell-background-set") && Flag(busCell, "foreground-set"), "LIN tint or readable text missing");
        foreach (var column in linTree.Columns) {
            column.CellSetCellData(rows, known, false, false);
            Check(Flag((Gtk.CellRendererText)column.Cells[0], "cell-background-set"), "LIN tint missing from a column");
        }
        linTree.Selection.SelectIter(known);
        busColumn.CellSetCellData(rows, known, false, false);
        Check(!Flag(busCell, "cell-background-set") && !Flag(busCell, "foreground-set"), "LIN tint overrides selection theme");
        Console.WriteLine("GTK bus labels, LIN tint/selection and checksum columns passed.");
    } finally { panel.Close(); window.Destroy(); }
}

if (args.Contains("--reference-ui")) {
    Gtk.Application.Init();
    var reference = new LinReferenceWindow(null, 55, "AA0A000000000000", "AA 0A 00 00 00 00 00 00 14", "Valid enhanced");
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    var details = (Gtk.TextView)typeof(LinReferenceWindow).GetField("_details", flags)!.GetValue(reference)!;
    var selector = (Gtk.ComboBoxText)typeof(LinReferenceWindow).GetField("_layout", flags)!.GetValue(reference)!;
    Check(details.Buffer.Text.Contains("LIN ID 55") && details.Buffer.Text.Contains("B0: 0xAA"), "Reference lost selected captured frame");
    selector.Active = 2;
    Check(details.Buffer.Text.Contains("Whale") && !details.Buffer.Text.Contains("Truma CP+"), "Reference layout selector failed");
    reference.Destroy();
    Console.WriteLine("EC600 reference checks passed; interactive popup checks are in TooltipPlacement.");
}

if (args.Contains("--configuration-ui")) {
    Gtk.Application.Init();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
    var app = new CanAnalyzerApp(stdinMode: true);
    var window = (Gtk.Window)typeof(CanAnalyzerApp).GetMethod("BuildWindow", flags)!.Invoke(app, null)!;
    var panel = (LinReceivePanel)typeof(CanAnalyzerApp).GetField("_linPanel", flags)!.GetValue(app)!;
    var rows = (Gtk.ListStore)typeof(CanAnalyzerApp).GetField("_messageStore", flags)!.GetValue(app)!;
    var queue = (System.Collections.Concurrent.ConcurrentQueue<LinMessage>)typeof(LinReceivePanel).GetField("_pending", flags)!.GetValue(panel)!;
    void PumpConfiguration() {
        var until = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < until) { while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration(); Thread.Sleep(10); }
    }
    try {
        window.ShowAll();
        var canTree = (Gtk.TreeView)typeof(CanAnalyzerApp).GetField("_treeView", flags)!.GetValue(app)!;
        Check(canTree.Columns.Any(c => c.Title == "Bus"), "Bus column missing");
        typeof(CanAnalyzerApp).GetField("_filterIds", flags)!.SetValue(app, new HashSet<uint> { 9 });
        typeof(CanAnalyzerApp).GetMethod("OnCanMessage", flags)!.Invoke(app, new object[] { SettingFrame(3) });
        PumpConfiguration();
        var frame = Frame(0x39, "D20A00000000A30B3B") with { Timestamp = configTime.AddSeconds(1) };
        queue.Enqueue(frame); PumpConfiguration();
        rows.IterNthChild(out var first, 0);
        Check((string)rows.GetValue(first, 13) == "Whale" && ((string)rows.GetValue(first, 7)).Contains("Whale"), "Filtered CAN setting did not select heater");
        panel.ObserveHeaterConfiguration(new(2, configTime.AddSeconds(2)));
        queue.Enqueue(frame with { Timestamp = configTime.AddSeconds(3) }); PumpConfiguration();
        rows.IterNthChild(out var second, 0); rows.IterNthChild(out first, 1);
        Check((string)rows.GetValue(second, 13) == "Truma CP+" && (string)rows.GetValue(first, 13) == "Whale", "Setting change relabelled historical frames");
        typeof(CanAnalyzerApp).GetMethod("OnCanMessage", flags)!.Invoke(app, new object[] { SettingFrame(3) with { Timestamp = configTime.AddSeconds(4) } });
        typeof(CanAnalyzerApp).GetMethod("ResetLinHeaterConfiguration", flags)!.Invoke(app, null);
        PumpConfiguration();
        queue.Enqueue(frame with { Timestamp = configTime.AddSeconds(5) }); PumpConfiguration();
        rows.IterNthChild(out var reset, 0);
        Check((string)rows.GetValue(reset, 13) == "", "Queued old setting survived CAN reset");
        var detail = new LinReferenceWindow(window, 57, frame.Payload, "D2 0A 00 00 00 00 A3 0B 3B", "Valid enhanced", "Whale");
        var selector = (Gtk.ComboBoxText)typeof(LinReferenceWindow).GetField("_layout", flags)!.GetValue(detail)!;
        Check(selector.ActiveText == "Whale", "Captured automatic layout lost in full details");
        detail.Destroy();
        Console.WriteLine("CAN settings / LIN layout integration checks passed (filtered ID, changed setting, reset, captured details).");
    } finally { panel.Close(); window.Destroy(); }
}

if (args.Contains("--watch-ui")) {
    Gtk.Application.Init();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
    var app = new CanAnalyzerApp(stdinMode: true);
    object Field(string name) => typeof(CanAnalyzerApp).GetField(name, flags)!.GetValue(app)!;
    void Call(string name, params object[] values) => typeof(CanAnalyzerApp).GetMethod(name, flags)!.Invoke(app, values);
    var window = (Gtk.Window)typeof(CanAnalyzerApp).GetMethod("BuildWindow", flags)!.Invoke(app, null)!;
    var panel = (LinReceivePanel)Field("_linPanel");
    var store = (Gtk.ListStore)Field("_watchStore");
    var tree = (Gtk.TreeView)Field("_watchTreeView");
    var canInput = (Gtk.Entry)Field("_watchIdEntry");
    var linInput = (Gtk.Entry)Field("_linWatchIdEntry");
    var queue = (System.Collections.Concurrent.ConcurrentQueue<LinMessage>)typeof(LinReceivePanel).GetField("_pending", flags)!.GetValue(panel)!;
    var received = (Gtk.ListStore)typeof(CanAnalyzerApp).GetField("_messageStore", flags)!.GetValue(app)!;
    void PumpWatch() {
        var until = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < until) { while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration(); Thread.Sleep(10); }
    }
    void Add(bool lin, string id) { (lin ? linInput : canInput).Text = id; Call("AddWatchIdForBus", lin); }
    int Count(string bus, uint id) {
        int count = 0;
        store.Foreach((m,p,i) => { if ((bool)m.GetValue(i,5) && (string)m.GetValue(i,4)==bus && (uint)m.GetValue(i,1)==id) count++; return false; });
        return count;
    }
    try {
        window.ShowAll(); PumpWatch();
        int headers = 0, knownLin = 0; bool reachedLin = false;
        store.Foreach((m,p,i) => {
            string bus = (string)m.GetValue(i,4);
            if (!(bool)m.GetValue(i,5)) { headers++; if (bus=="LIN") reachedLin=true; }
            else { Check(bus=="LIN" ? reachedLin : !reachedLin, "Watch sections out of order"); if(bus=="LIN") knownLin++; }
            return false;
        });
        Check(headers==2 && knownLin==13, "Missing watch sections or known LIN IDs");
        canInput.TranslateCoordinates(window,0,0,out int cx,out int cy);
        linInput.TranslateCoordinates(window,0,0,out int lx,out int ly);
        Check(cy==ly && lx>cx && Math.Abs(canInput.AllocatedWidth-linInput.AllocatedWidth)<=1, "Add inputs are not equal-width side by side");
        Add(false,"55"); Add(true,"0x37"); Add(true,"55");
        Check(Count("CAN",55)==1 && Count("LIN",55)==1, "Duplicate or cross-bus ID collision");
        Check(((HashSet<uint>)Field("_filterIds")).SetEquals(new uint[]{55}) && ((HashSet<uint>)Field("_linFilterIds")).SetEquals(new uint[]{55}), "Filters are not independent");
        tree.GetCursor(out var selectedPath, out var selectedColumn);
        Call("OnWatchToggled", tree, new Gtk.ToggledArgs { Args = new object[] { selectedPath.ToString() } });
        Check(((HashSet<uint>)Field("_linFilterIds")).Count==0 && ((HashSet<uint>)Field("_filterIds")).Contains(55), "LIN checkbox changed CAN selection");
        Call("OnWatchToggled", tree, new Gtk.ToggledArgs { Args = new object[] { "0" } });
        Check(!(bool)store.GetValue(store.GetIterFirst(out var header) ? header : throw new Exception("Missing heading"),0), "Heading could be toggled");
        Add(true,"0xBA"); Check(((Gtk.Label)Field("_linWatchIdError")).Visible, "LIN PID outside ID range accepted");
        Add(true,"63"); Add(true,"0"); Add(false,"0x1FFFFFFF");
        Check(Count("LIN",63)==1 && Count("LIN",0)==1 && Count("CAN",0x1FFFFFFF)==1, "Ad-hoc boundary IDs missing");
        Call("SetWatchAll", true);
        store.Foreach((m,p,i) => { if (!(bool)m.GetValue(i,5)) Check(!(bool)m.GetValue(i,0), "All selected a heading"); return false; });
        Call("SetWatchAll", false);
        Check(((HashSet<uint>)Field("_filterIds")).Count==0 && ((HashSet<uint>)Field("_linFilterIds")).Count==0, "None did not clear both filters");
        Add(true,"55");
        queue.Enqueue(Frame(0x37,"AA0A00000000000014")); queue.Enqueue(Frame(0x39,"D20A00000000A30B3B")); PumpWatch();
        Check(received.IterNChildren()==1, "LIN watch did not filter incoming IDs");
        Check(((HashSet<uint>)Field("_filterIds")).Count==0, "LIN selection changed CAN filter");
        Call("SetWatchAll", false); queue.Enqueue(Frame(0x39,"D20A00000000A30B3B")); PumpWatch();
        Check(received.IterNChildren()==2, "Empty LIN selection did not restore all IDs");
        Console.WriteLine("Watch UI passed: CAN/LIN grouping, compact add inputs, duplicate/range checks, independent filters, All/None and live-row filtering.");
    } finally { panel.Close(); window.Destroy(); }
}

if (args.Contains("--combined-ui")) {
    Gtk.Application.Init();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
    var app = new CanAnalyzerApp(stdinMode: true);
    object Field(string name) => typeof(CanAnalyzerApp).GetField(name, flags)!.GetValue(app)!;
    void Call(string name, params object[] values) => typeof(CanAnalyzerApp).GetMethod(name, flags)!.Invoke(app, values);
    var window = (Gtk.Window)typeof(CanAnalyzerApp).GetMethod("BuildWindow", flags)!.Invoke(app, null)!;
    var panel = (LinReceivePanel)Field("_linPanel");
    var rows = (Gtk.ListStore)Field("_messageStore");
    var tree = (Gtk.TreeView)Field("_treeView");
    var queue = (System.Collections.Concurrent.ConcurrentQueue<LinMessage>)typeof(LinReceivePanel).GetField("_pending", flags)!.GetValue(panel)!;
    void Pump() {
        var until = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < until) { while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration(); Thread.Sleep(5); }
    }
    try {
        window.ShowAll();
        IEnumerable<Gtk.Widget> Descendants(Gtk.Widget w) {
            yield return w;
            if (w is Gtk.Container c) foreach (var child in c.Children) foreach (var d in Descendants(child)) yield return d;
        }
        Check(!Descendants(window).OfType<Gtk.Notebook>().Any(), "Obsolete view tabs remain");
        Check(!Descendants(window).OfType<Gtk.Button>().Any(b => b.Label == "Show LIN data"), "Obsolete LIN view button remains");
        Check(panel.IsMapped, "LIN connection controls hidden");
        Call("OnCanMessage", SettingFrame(3));
        queue.Enqueue(Frame(0x39, "D20A00000000A30B3B"));
        queue.Enqueue(partial);
        Pump();
        Check(rows.IterNChildren() == 3, "Both receive paths did not reach combined view");
        rows.IterNthChild(out var incomplete, 0);
        rows.IterNthChild(out var lin, 1);
        rows.IterNthChild(out var can, 2);
        Check((string)rows.GetValue(lin, 11) == "LIN" && (string)rows.GetValue(can, 11) == "CAN", "Mixed bus labels incorrect");
        Check((int)rows.GetValue(lin, 2) == 57 && (string)rows.GetValue(lin, 4) == "0x39", "LIN ID decimal/hex incorrect");
        Check((string)rows.GetValue(lin, 3) == "210 10 0 0 0 0 163 11" && (string)rows.GetValue(lin, 6) == "D2 0A 00 00 00 00 A3 0B" && (int)rows.GetValue(lin, 5) == 8, "Payload included checksum or conversion failed");
        Check((string)rows.GetValue(incomplete, 3) == "—" && (int)rows.GetValue(incomplete, 5) == -1, "Incomplete capture presented as payload");
        Check(((string)rows.GetValue(incomplete, 12)).Contains("244 / 0xF4") && ((string)rows.GetValue(incomplete, 12)).Contains("Raw bytes (decimal): 16 16 16 244"), "Raw bytes/checksum diagnostics lost");
        Call("AssignRowToSendFrame", lin, 0);
        Check((int)rows.GetValue(lin, 9) == 0, "LIN row assigned to CAN send slot");
        Call("AssignRowToSendFrame", can, 0);
        Check((int)rows.GetValue(can, 9) == 1, "CAN send assignment regressed");
        Check(tree.Columns.Select(c => c.Title).SequenceEqual(new[] { "#", "Received time", "Bus", "ID (decimal)", "ID (hex)", "Bytes", "Data (decimal)", "Data (hex)", "Description", "Type / Status" }), "Combined headers incorrect");
        panel.SetWatchIds(new[] {57});
        queue.Enqueue(partial); queue.Enqueue(Frame(0x39, "D20A00000000A30B3B")); Pump();
        Check(rows.IterNChildren() == 4, "Watch filter failed");
        Call("ClearMessages");
        Check(rows.IterNChildren() == 0, "Combined clear failed");
        queue.Enqueue(Frame(0x39, "D20A00000000A30B3B")); Pump();
        rows.IterNthChild(out lin, 0);
        Check((int)rows.GetValue(lin, 0) == 1, "Combined numbering did not reset");
        Console.WriteLine("Combined CAN/LIN integration passed: both streams, formats, incomplete diagnostics, send isolation, filters and clear.");
    } finally { panel.Close(); window.Destroy(); }
}

if (args.Contains("--logging-ui")) {
    Gtk.Application.Init();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
    var app = new CanAnalyzerApp(stdinMode: true);
    object Field(string name) => typeof(CanAnalyzerApp).GetField(name, flags)!.GetValue(app)!;
    void Call(string name, params object?[] values) => typeof(CanAnalyzerApp).GetMethod(name, flags)!.Invoke(app, values);
    var window = (Gtk.Window)typeof(CanAnalyzerApp).GetMethod("BuildWindow", flags)!.Invoke(app, null)!;
    var panel = (LinReceivePanel)Field("_linPanel");
    var queue = (System.Collections.Concurrent.ConcurrentQueue<LinMessage>)typeof(LinReceivePanel).GetField("_pending", flags)!.GetValue(panel)!;
    void Pump() {
        var until = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < until) { while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration(); Thread.Sleep(5); }
    }
    string path = Path.Combine(Path.GetTempPath(), $"can-lin-output-{Guid.NewGuid():N}.csv");
    try {
        window.ShowAll();
        Call("OnCanMessage", SettingFrame(3)); Pump(); // Existing history must not be duplicated.
        Call("StartLogging", path);
        Call("OnCanMessage", SettingFrame(3)); Pump();
        queue.Enqueue(Frame(0x39, "D20A00000000A30B3B"));
        queue.Enqueue(partial); Pump();
        // Change both watch filters while recording; excluded records must not leak into CSV.
        typeof(CanAnalyzerApp).GetField("_filterIds", flags)!.SetValue(app, new HashSet<uint> { 55 });
        panel.SetWatchIds(new[] { 57 });
        Call("OnCanMessage", SettingFrame(3)); queue.Enqueue(partial); Pump();
        Call("ClearMessages");
        queue.Enqueue(Frame(0x39, "D20A00000000A30B3B")); Pump();
        string description = "Heater, \"test\"\nsecond line";
        Call("AddLinMessageToStore", Frame(0x39, "D20A00000000A30B3B"), description, null, "test");
        Check((long)Field("_loggedFrameCount") == 5, "Wrong combined log count or excluded frame logged");
        Call("StopLogging", (object?)null);
        var records = new List<string[]>();
        using (var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(path)) {
            csv.SetDelimiters(","); csv.HasFieldsEnclosedInQuotes = true;
            while (!csv.EndOfData) records.Add(csv.ReadFields()!);
        }
        Check(records.Count == 6 && records.All(r => r.Length == 10), "CSV row/column count wrong");
        Check(records[0][2] == "Bus" && records[0][9] == "Type / Status", "CSV headers do not match output");
        Check(records[1][2] == "CAN" && records[2][2] == "LIN", "CAN/LIN bus distinction missing");
        Check(records[2][3] == "57" && records[2][4] == "0x39" && records[2][5] == "8", "LIN ID/length mismatch");
        Check(records[2][6] == "210 10 0 0 0 0 163 11" && records[2][7] == "D2 0A 00 00 00 00 A3 0B", "Decimal/hex payload mismatch");
        Check(records[3][5] == "—" && records[3][6] == "—" && records[3][9].Contains("Incomplete"), "Incomplete capture misrepresented");
        Check(records[4][0] == "1", "CSV does not preserve displayed row numbering after Clear");
        Check(records[5][8] == description, "CSV quoting corrupted description");
        queue.Enqueue(Frame(0x39, "D20A00000000A30B3B")); Pump();
        Check((long)Field("_loggedFrameCount") == 5, "Stopped recording still writes");
        Console.WriteLine("Output CSV passed: mixed buses, watch filters, decimal/hex, incomplete frames, clear, escaping and stop.");
    } finally { Call("StopLogging", (object?)null); panel.Close(); window.Destroy(); File.Delete(path); }
}
