using System.Reflection;
using CanLogger;
using Gtk;

// Desktop integration test; synthetic frames only, no hardware connection.
Application.Init();
CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
object Field(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target)!;
object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
void Check(bool pass, string detail) { if (!pass) throw new Exception(detail); }
void Pump(int ms = 150)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    do { while (Application.EventsPending()) Application.RunIteration(false); Thread.Sleep(5); } while (DateTime.UtcNow < until);
}
var app = new CanAnalyzerApp(true);
var main = (Window)Call(app, "BuildWindow")!;
app.GetType().GetField("_window", flags)!.SetValue(app, main);
main.ShowAll(); Pump();
Window? watch = null;
try
{
    var button = (Button)Field(app, "_liveWatchButton");
    Check(!button.Sensitive, "Watch must be disabled with no IDs");
    void Add(bool lin, string id)
    {
        ((Entry)Field(app, lin ? "_linWatchIdEntry" : "_watchIdEntry")).Text = id;
        Call(app, "AddWatchIdForBus", lin); Pump();
    }
    Add(false, "55");
    watch = (Window)Field(app, "_liveWatchWindow");
    var store = (ListStore)Field(watch, "_store");
    Check(button.Sensitive && watch.Visible && button.Label == "Hide Watch", "Selection opens watch");
    Check(store.IterNChildren() == 1, "One row per selected ID");
    int smallHeight = watch.AllocatedHeight;
    Add(true, "55");
    Check(store.IterNChildren() == 2, "CAN/LIN with same ID must be separate");
    TreeIter Find(string bus, uint id)
    {
        TreeIter result = default; bool found = false;
        store.Foreach((m, p, i) => {
            if ((string)m.GetValue(i, 0) == bus && ((string)m.GetValue(i, 1)).StartsWith(id + " ")) { result = i; found = true; }
            return false;
        });
        Check(found, "Missing watch row"); return result;
    }
    var can = new CanMessage(DateTime.Now, 55, false, false, 2, new byte[] { 10, 255 });
    Call(app, "AddMessageToStore", can);
    Call(app, "AddLinMessageToStore", new LinMessage(DateTime.Now, 55, 19600, 0, 0, new byte[] { 42, 0x99 }), "test", "", "test");
    Check((string)store.GetValue(Find("CAN", 55), 2) == "10", "CAN decimal byte");
    Check((string)store.GetValue(Find("CAN", 55), 3) == "255", "CAN second byte");
    Check((string)store.GetValue(Find("LIN", 55), 2) == "42", "LIN data separate");
    Check((string)store.GetValue(Find("LIN", 55), 3) == "—", "LIN excludes checksum");
    Check((string)store.GetValue(Find("CAN", 55), 1) == "55 37", "ID keeps decimal and hex together");
    Check((string)store.GetValue(Find("CAN", 55), 10) == "0A", "Hex byte 0 is in the right-hand group");
    Check((string)store.GetValue(Find("CAN", 55), 11) == "FF", "Hex byte 1 matches decimal byte 1");
    Check((string)store.GetValue(Find("LIN", 55), 10) == "2A", "LIN hex group receives payload");
    Check((string)store.GetValue(Find("LIN", 55), 11) == "—", "Hex group excludes LIN checksum");
    Call(app, "AddMessageToStore", can with { Dlc = 8, Data = new byte[] { 10, 255, 2, 3, 4, 5, 6, 127 } });
    Check((string)store.GetValue(Find("CAN", 55), 9) == "127" &&
          (string)store.GetValue(Find("CAN", 55), 17) == "7F", "Byte 7 reaches both groups");
    Call(app, "AddMessageToStore", can);
    Check((string)store.GetValue(Find("CAN", 55), 9) == "—" &&
          (string)store.GetValue(Find("CAN", 55), 17) == "—", "Short payload clears both groups");
    var rows = (System.Collections.IDictionary)Field(watch, "_rows");
    var row = rows[("CAN", 55u)]!;
    var expiry = (long[])Field(row, "HighlightUntil");
    long deadline = expiry[0];
    Check(deadline > Environment.TickCount64, "Changed bytes highlight");
    Call(app, "AddMessageToStore", can);
    Check(expiry[0] == deadline, "Unchanged frames do not restart highlight");
    Pump(750); Check(expiry.All(x => x == 0), "Highlight expires");
    Call(app, "AddMessageToStore", can with { Dlc = 1, Data = new byte[] { 11 } });
    Check((string)store.GetValue(Find("CAN", 55), 3) == "—", "Short payload clears old bytes");
    Call(watch, "HideWatch");
    Check(!watch.Visible && button.Label == "Show Watch", "Hide updates toggle");
    Call(app, "AddMessageToStore", can);
    Call(watch, "ShowWatch"); Pump();
    Check((string)store.GetValue(Find("CAN", 55), 2) == "10", "Hidden window continues receiving");
    for (int i = 100; i < 112; i++) Add(false, i.ToString());
    Check(watch.AllocatedHeight > smallHeight, "Window grows with rows");
    int largeHeight = watch.AllocatedHeight;
    Call(app, "SetWatchAll", false); Pump();
    Check(!watch.Visible && !button.Sensitive && store.IterNChildren() == 0, "None clears and hides watch");
    Add(false, "55");
    Check(watch.AllocatedHeight < largeHeight, "Window shrinks with rows");
    Check((string)store.GetValue(Find("CAN", 55), 2) == "—", "Reselection starts awaiting fresh data");
    // Exercise the actual checkbox handler, including removal while hidden.
    var selectionStore = (ListStore)Field(app, "_watchStore");
    string? path55 = null;
    selectionStore.Foreach((m, p, i) => {
        if ((bool)m.GetValue(i, 5) && (string)m.GetValue(i, 4) == "CAN" && (uint)m.GetValue(i, 1) == 55)
            path55 = p.ToString();
        return false;
    });
    Call(app, "OnWatchToggled", new object(), new ToggledArgs { Args = new object[] { path55! } });
    Check(!watch.Visible && !button.Sensitive, "Unticking last ID hides window");
    Call(app, "OnWatchToggled", new object(), new ToggledArgs { Args = new object[] { path55! } });
    Check(watch.Visible && button.Sensitive, "Ticking checkbox opens window");
    Call(app, "SetWatchAll", true); Pump(300);
    Check(store.IterNChildren() > 12, "All includes known IDs");
    var area = watch.Display.GetMonitorAtWindow(watch.Window).Workarea;
    Check(watch.AllocatedHeight <= area.Height - 80, "Large watch list stays within screen height");
    var position = store.GetPath(Find("CAN", 55)).ToString();
    Call(app, "AddMessageToStore", can);
    Check(store.GetPath(Find("CAN", 55)).ToString() == position, "Incoming data never reorders rows");
    Call(app, "AddLinMessageToStore", new LinMessage(DateTime.Now, 55, 19600, 0, 1, new byte[] { 42 }), "test", "", "test");
    Check((string)store.GetValue(Find("LIN", 55), 2) == "—", "Incomplete LIN response shows no inferred payload");
    Console.WriteLine("PASS: selection, bus isolation, payloads, highlights, hide/reopen, and automatic sizing.");
}
finally { watch?.Destroy(); main.Destroy(); }
