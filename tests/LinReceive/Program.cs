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
    var rows = (Gtk.ListStore)typeof(LinReceivePanel).GetField("_rows", flags)!.GetValue(panel)!;
    void Pump(int milliseconds) {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) {
            while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration();
            Thread.Sleep(10);
        }
    }
    try {
        window.ShowAll(); Pump(300);
        var tabs = (Gtk.Notebook)panel.Parent;
        tabs.CurrentPage = 0; Pump(100);
        var analyzer = (Gtk.ComboBoxText)typeof(LinReceivePanel).GetField("_analyzer", flags)!.GetValue(panel)!;
        var showData = (Gtk.Button)typeof(LinReceivePanel).GetField("_showData", flags)!.GetValue(panel)!;
        Check(start.IsMapped && analyzer.IsMapped && start.AllocatedHeight > 0,
            "LIN connection controls hidden while CAN tab is selected");
        Check(analyzer.ActiveId == MicrochipLinBackend.AnalyzerId, "Wrong analyzer selected");
        showData.Click(); Pump(100);
        Check(tabs.CurrentPage == 1, "Show LIN data did not switch view");
        tabs.CurrentPage = 0;
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
        Console.WriteLine($"LIN UI: {status.Text}; displayed rows={rows.IterNChildren()}");
        Check(start.Label == "Stop LIN" && !status.Text.StartsWith("LIN error"), "GTK receive start failed");
        var diagnostics = (Gtk.Label)typeof(LinReceivePanel).GetField("_diagnostics", flags)!.GetValue(panel)!;
        Check(diagnostics.Text.Contains("Adapter rate:") && diagnostics.Text.Contains("USB input reports:"), "Missing USB diagnostics");
        Check(status.Text.Contains("USB responding") && status.Text.Contains("No new LIN records for"), "Missing live USB/record activity status");
        Check(tabs.CurrentPage == 1 && !analyzer.Sensitive, "Start did not show LIN data/lock analyzer selection");
        start.Click(); Pump(5000);
        Check(start.Label == "Start LIN" && start.Sensitive && analyzer.Sensitive && status.Text == "Stopped", "GTK receive stop failed");
        Console.WriteLine("GTK LIN start/display/stop checks passed.");
    } finally { panel.Close(); window.Destroy(); Pump(200); }
}

if (args.Contains("--checksum-ui")) {
    Gtk.Application.Init();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    var panel = new LinReceivePanel();
    var window = new Gtk.Window("LIN checksum display test");
    window.Add(panel); window.ShowAll();
    var pending = (System.Collections.Concurrent.ConcurrentQueue<LinMessage>)typeof(LinReceivePanel).GetField("_pending", flags)!.GetValue(panel)!;
    var rows = (Gtk.ListStore)typeof(LinReceivePanel).GetField("_rows", flags)!.GetValue(panel)!;
    try {
        pending.Enqueue(Frame(0x85, "01FE", 1));
        pending.Enqueue(Frame(0, "", 1));
        pending.Enqueue(Frame(0x37, "AA0A00000000000014"));
        var until = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < until) {
            while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration();
            Thread.Sleep(10);
        }
        Check(rows.NColumns == 14, "Missing expected PID column");
        Check(rows.IterNChildren() == 3, "Checksum fixtures not displayed");
        rows.IterNthChild(out var known, 0); rows.IterNthChild(out var empty, 1); rows.IterNthChild(out var candidate, 2);
        Check(((string)rows.GetValue(known, 11)).Contains("Water-heater control"), "Known LIN row missing identification");
        Check(((string)rows.GetValue(empty, 11)).Contains("Unknown"), "Unknown LIN row not labelled");
        Check((string)rows.GetValue(empty, 3) == "128 / 0x80" && (string)rows.GetValue(candidate, 3) == "133 / 0x85", "Expected PID column incorrect");
        Check((string)rows.GetValue(empty, 8) == "Unavailable (no bytes)", "Empty checksum column incorrect");
        Check((string)rows.GetValue(candidate, 7) == "0xFE" && (string)rows.GetValue(candidate, 8) == "Possible classic match (receive error)", "Candidate checksum columns incorrect");
        Check(((string)rows.GetValue(candidate, 9)).Contains("timeout"), "Checksum column hid timeout warning");
        Console.WriteLine("GTK checksum columns passed (including empty and timeout records).");
    } finally { panel.Close(); window.Destroy(); panel.ConnectionControls.Destroy(); }
}

if (args.Contains("--reference-ui")) {
    Gtk.Application.Init();
    var popupType = typeof(LinScheme).Assembly.GetType("CanLogger.LinTooltip")!;
    var method = popupType.GetMethod("CreateLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    int checkedLabels = 0;
    foreach (var area in new[] { new Gdk.Rectangle(0,0,800,600), new Gdk.Rectangle(0,0,1920,1080) })
    foreach (var entry in LinScheme.Entries)
    foreach (string? layout in entry.Layouts.Select(l => (string?)l.Name).Prepend(null)) {
        string text = LinScheme.Describe(entry.Id, layout, "AA0A000000000000", "AA 0A 00 00 00 00 00 00 14", "Valid enhanced");
        var label = (Gtk.Label)method.Invoke(null, new object[] { text, area })!;
        label.GetPreferredHeightForWidth(label.WidthRequest, out _, out int height);
        Check(label.WidthRequest <= area.Width - 64 && height <= area.Height - 64, "LIN tooltip exceeds screen bounds");
        if (label.Text != text) Check(label.Text.Contains("Full details:"), "Long tooltip silently loses information");
        label.Destroy(); checkedLabels++;
    }
    var reference = new LinReferenceWindow(null, 55, "AA0A000000000000", "AA 0A 00 00 00 00 00 00 14", "Valid enhanced");
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    var details = (Gtk.TextView)typeof(LinReferenceWindow).GetField("_details", flags)!.GetValue(reference)!;
    var selector = (Gtk.ComboBoxText)typeof(LinReferenceWindow).GetField("_layout", flags)!.GetValue(reference)!;
    Check(details.Buffer.Text.Contains("LIN ID 55") && details.Buffer.Text.Contains("B0: 0xAA"), "Reference lost selected captured frame");
    selector.Active = 2;
    Check(details.Buffer.Text.Contains("Whale") && !details.Buffer.Text.Contains("Truma CP+"), "Reference layout selector failed");
    reference.Destroy();
    Console.WriteLine($"EC600 reference and {checkedLabels} tooltip sizing checks passed.");
}

if (args.Contains("--configuration-ui")) {
    Gtk.Application.Init();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
    var app = new CanAnalyzerApp(stdinMode: true);
    var window = (Gtk.Window)typeof(CanAnalyzerApp).GetMethod("BuildWindow", flags)!.Invoke(app, null)!;
    var panel = (LinReceivePanel)typeof(CanAnalyzerApp).GetField("_linPanel", flags)!.GetValue(app)!;
    var rows = (Gtk.ListStore)typeof(LinReceivePanel).GetField("_rows", flags)!.GetValue(panel)!;
    var queue = (System.Collections.Concurrent.ConcurrentQueue<LinMessage>)typeof(LinReceivePanel).GetField("_pending", flags)!.GetValue(panel)!;
    void PumpConfiguration() {
        var until = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < until) { while (Gtk.Application.EventsPending()) Gtk.Application.RunIteration(); Thread.Sleep(10); }
    }
    try {
        window.ShowAll();
        typeof(CanAnalyzerApp).GetField("_filterIds", flags)!.SetValue(app, new HashSet<uint> { 9 });
        typeof(CanAnalyzerApp).GetMethod("OnCanMessage", flags)!.Invoke(app, new object[] { SettingFrame(3) });
        PumpConfiguration();
        var frame = Frame(0x39, "D20A00000000A30B3B") with { Timestamp = configTime.AddSeconds(1) };
        queue.Enqueue(frame); PumpConfiguration();
        rows.IterNthChild(out var first, 0);
        Check((string)rows.GetValue(first, 12) == "Whale" && ((string)rows.GetValue(first, 11)).Contains("Whale"), "Filtered CAN setting did not select heater");
        panel.ObserveHeaterConfiguration(new(2, configTime.AddSeconds(2)));
        queue.Enqueue(frame with { Timestamp = configTime.AddSeconds(3) }); PumpConfiguration();
        rows.IterNthChild(out var second, 0); rows.IterNthChild(out first, 1);
        Check((string)rows.GetValue(second, 12) == "Truma CP+" && (string)rows.GetValue(first, 12) == "Whale", "Setting change relabelled historical frames");
        typeof(CanAnalyzerApp).GetMethod("OnCanMessage", flags)!.Invoke(app, new object[] { SettingFrame(3) with { Timestamp = configTime.AddSeconds(4) } });
        typeof(CanAnalyzerApp).GetMethod("ResetLinHeaterConfiguration", flags)!.Invoke(app, null);
        PumpConfiguration();
        queue.Enqueue(frame with { Timestamp = configTime.AddSeconds(5) }); PumpConfiguration();
        rows.IterNthChild(out var reset, 0);
        Check((string)rows.GetValue(reset, 12) == "", "Queued old setting survived CAN reset");
        var detail = new LinReferenceWindow(window, 57, frame.Payload, "D2 0A 00 00 00 00 A3 0B 3B", "Valid enhanced", "Whale");
        var selector = (Gtk.ComboBoxText)typeof(LinReferenceWindow).GetField("_layout", flags)!.GetValue(detail)!;
        Check(selector.ActiveText == "Whale", "Captured automatic layout lost in full details");
        detail.Destroy();
        Console.WriteLine("CAN settings / LIN layout integration checks passed (filtered ID, changed setting, reset, captured details).");
    } finally { panel.Close(); window.Destroy(); }
}
