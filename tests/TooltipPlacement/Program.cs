using System.Reflection;
using System.Runtime.InteropServices;
using CanLogger;
using Gtk;

// Run on a Linux GTK desktop. This opens temporary app windows but does not
// connect to CAN hardware, send frames, write logs, or move the mouse pointer.
Application.Init();
Console.WriteLine($"Display backend: {Gdk.Display.Default.NativeType}");
CanScheme.Load(Path.Combine(AppContext.BaseDirectory, "can-scheme.csv"));
var app = new CanAnalyzerApp(stdinMode: true);
var appType = typeof(CanAnalyzerApp);
const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
var handler = appType.GetMethod("ShowCanTooltip", BindingFlags.NonPublic | BindingFlags.Static)!;
var window = (Window)appType.GetMethod("BuildWindow", fields)!.Invoke(app, null)!;
var store = (ListStore)appType.GetField("_messageStore", fields)!.GetValue(app)!;
store.AppendValues(1, "12:00:00", 91, "00", "05B", 1, "00", "History test", "STD", 0, "#FFFFFF");
using var tooltip = new Tooltip(Native.g_object_new(Native.gtk_tooltip_get_type(), IntPtr.Zero));

try
{
    window.ShowAll();
    Pump(300);
    foreach (string mode in args.Contains("--hover-only") ? new[] { "windowed" } : new[] { "windowed", "maximized", "fullscreen" })
    {
        if (mode == "maximized") window.Maximize();
        if (mode == "fullscreen") window.Fullscreen();
        var state = mode == "maximized" ? Gdk.WindowState.Maximized : Gdk.WindowState.Fullscreen;
        if (mode != "windowed")
            WaitFor(() => (window.Window.State & state) != 0, $"desktop did not enter {mode}");
        Pump(300);
        Console.WriteLine($"Testing {mode}: {window.AllocatedWidth}x{window.AllocatedHeight}");

        foreach (string field in new[] { "_watchTreeView", "_treeView" })
        {
            var tree = (TreeView)appType.GetField(field, fields)!.GetValue(app)!;
            var cell = tree.GetCellArea(new TreePath(field == "_watchTreeView" ? "1" : "0"), tree.Columns[0]);
            tree.ConvertBinWindowToWidgetCoords(cell.X + 5, cell.Y + cell.Height / 2,
                out int left, out int y);
            foreach (int x in new[] { left, tree.AllocatedWidth / 2, tree.AllocatedWidth - 6 })
            {
                Query(tree, field, x, y);
                var popup = tree.TooltipWindow;
                popup.Realize();
                Gdk.Rectangle? final = null;
                Gdk.MovedToRectHandler onPosition = (_, e) =>
                {
                    if (e.P1 != IntPtr.Zero)
                        final = Marshal.PtrToStructure<Gdk.Rectangle>(e.P1);
                };
                popup.Window.MovedToRect += onPosition;
                try
                {
                    popup.Show();
                    WaitFor(() => final.HasValue, "compositor did not report popup placement");
                    Pump(100);
                    Verify(final!.Value, popup, mode, field);

                    // GTK may query again without hiding the current tooltip.
                    final = null;
                    Query(tree, field, x == left ? tree.AllocatedWidth - 6 : left, y);
                    WaitFor(() => final.HasValue, "visible tooltip was not repositioned");
                    Pump(100);
                    Verify(final!.Value, popup, mode, field);
                    Console.WriteLine($"PASS: {mode}/{field}, hover x={x}, including visible update");
                }
                finally
                {
                    popup.Hide();
                    popup.Window.MovedToRect -= onPosition;
                    Pump(50);
                }
            }
        }
        VerifyLongLinPopup(mode);
    }
}
finally
{
    window.Destroy();
    Pump(100);
}

void VerifyLongLinPopup(string mode)
{
    var tree = (TreeView)appType.GetField("_treeView", fields)!.GetValue(app)!;
    var factory = typeof(LinScheme).Assembly.GetType("CanLogger.LinTooltip")!
        .GetMethod("ForTree", BindingFlags.NonPublic | BindingFlags.Static)!;
    var controller = factory.Invoke(null, new object[] { tree })!;
    var type = controller.GetType();
    var show = type.GetMethod("Show", fields)!;
    var close = type.GetMethod("Close", fields)!;
    var popup = (Popover)type.GetField("_popup", fields)!.GetValue(controller)!;
    var scroll = (ScrolledWindow)type.GetField("_scroll", fields)!.GetValue(controller)!;
    var text = (TextView)type.GetField("_text", fields)!.GetValue(controller)!;
    foreach (int id in args.Contains("--hover-only") ? Array.Empty<int>() : new[] { 57, 58 })
    foreach (int x in new[] { 8, tree.AllocatedWidth - 8 })
    foreach (int y in new[] { tree.AllocatedHeight * 3 / 4, tree.AllocatedHeight - 12 }) {
        close.Invoke(controller, null);
        string full = LinScheme.Describe(id);
        show.Invoke(controller, new object[] { full, id.ToString(), x, y });
        Pump(150);
        if (!popup.TranslateCoordinates(window, 0, 0, out int px, out int py))
            throw new Exception("Cannot measure LIN popover position");
        if (px < 0 || py < 0 || px + popup.AllocatedWidth > window.AllocatedWidth || py + popup.AllocatedHeight > window.AllocatedHeight)
            throw new Exception($"LIN {id} clipped in {mode}: {px},{py} {popup.AllocatedWidth}x{popup.AllocatedHeight}");
        if (text.Buffer.Text != full) throw new Exception("LIN definition truncated");
        var adjustment = scroll.Vadjustment;
        if (adjustment.Upper <= adjustment.PageSize) throw new Exception("Long LIN definition has no scroll range");
        adjustment.Value = adjustment.Upper - adjustment.PageSize;
        Pump(50);
        double bottom = adjustment.Value;
        int hides = 0;
        EventHandler hidden = (_, _) => hides++;
        popup.Hidden += hidden;
        for (int sample = 0; sample < 10; sample++) {
            show.Invoke(controller, new object[] { "Changed live frame", "new-row", x, y });
            Pump(20);
        }
        popup.Hidden -= hidden;
        if (hides != 0 || !popup.Visible || text.Buffer.Text != full || Math.Abs(adjustment.Value - bottom) > 1)
            throw new Exception("Live updates disturbed open LIN details or scroll position");
        // Pointer entry must protect the interactive panel from the delayed close.
        type.GetField("_inside", fields)!.SetValue(controller, true);
        type.GetMethod("ScheduleClose", fields)!.Invoke(controller, null);
        Pump(650);
        if (!popup.Visible) throw new Exception("LIN popup closed while reading inside it");
        type.GetField("_inside", fields)!.SetValue(controller, false);
        type.GetMethod("ScheduleClose", fields)!.Invoke(controller, null);
        Pump(650);
        if (popup.Visible) throw new Exception("LIN popup did not close after pointer departure");
        close.Invoke(controller, null);
    }
    if (mode == "windowed") {
        var request = type.GetMethod("RequestShow", fields)!;
        var motion = type.GetMethod("PointerMoved", fields)!;
        var enter = type.GetMethod("EnterTree", fields)!;
        var watchTree = (TreeView)appType.GetField("_watchTreeView", fields)!.GetValue(app)!;
        var watch = factory.Invoke(null, new object[] { watchTree })!;
        var watchPopup = (Popover)type.GetField("_popup", fields)!.GetValue(watch)!;
        // Rapid traversal must never show a panel, even though GTK asks for content.
        for (int i = 0; i < 8; i++) {
            motion.Invoke(controller, new object[] { 10 + i, 100 });
            request.Invoke(controller, new object[] { "Hover test", "0", 10 + i, 100 });
            Pump(80);
            if (popup.Visible) throw new Exception("Popup appeared during pointer movement");
        }
        // Repeated queries with a stationary pointer must not restart the dwell timer.
        for (int i = 0; i < 4; i++) {
            request.Invoke(controller, new object[] { "Hover test", "0", 17, 100 });
            Pump(160);
        }
        if (!popup.Visible) throw new Exception("Stationary hover did not open details");
        for (int i = 0; i < 5; i++) {
            motion.Invoke(controller, new object[] { 50 + i, 100 });
            Pump(80);
        }
        if (popup.Visible) throw new Exception("Continuous movement prolonged the old popup");
        show.Invoke(controller, new object[] { "Main", "0", 10, 100 });
        enter.Invoke(watch, null);
        if (popup.Visible) throw new Exception("Crossing into watch list left the old popup open");
        request.Invoke(watch, new object[] { "Watch details", "1", 10, 100 });
        Pump(150);
        if (watchPopup.Visible) throw new Exception("Watch popup ignored hover delay");
        enter.Invoke(controller, null);
        Pump(700);
        if (watchPopup.Visible) throw new Exception("Abandoned watch hover opened later");
        show.Invoke(controller, new object[] { "Main", "0", 10, 100 });
        show.Invoke(watch, new object[] { "Watch", "1", 10, 100 });
        if (popup.Visible || !watchPopup.Visible) throw new Exception("Multiple panels can remain open");
        close.Invoke(watch, null);
        Console.WriteLine("PASS: hover dwell, movement cancellation, repeated queries, cross-table dismissal, abandoned timer and single-popup ownership");
    }
    if (!args.Contains("--hover-only")) Console.WriteLine($"PASS: {mode}/LIN 57 and 58: 8 bounded placements, full text, scrolling, stable snapshot across 80 updates");
}

void Query(TreeView tree, string field, int x, int y)
{
    var query = new QueryTooltipArgs { Args = new object[] { x, y, false, tooltip } };
    handler.Invoke(null, new object[] { tree, query, field == "_watchTreeView" ? 1 : 2 });
    if (query.RetVal is not true) throw new Exception($"No tooltip for {field} at {x},{y}");
}

void Verify(Gdk.Rectangle rect, Window popup, string mode, string field)
{
    // Wayland's compositor returns parent-relative coordinates. Gtk.GetPosition()
    // is deliberately not used: it can report a requested rather than real position.
    if (mode != "windowed" && (rect.X < 0 || rect.Y < 0 ||
        rect.X + rect.Width > window.AllocatedWidth || rect.Y + rect.Height > window.AllocatedHeight))
        throw new Exception($"Clipped {mode}/{field}: {rect}");
    uint id = field == "_watchTreeView" ? CanScheme.AllEntries[0].Id : 91;
    if (popup.Child is not Label label || label.Text != CanScheme.GetTooltipText(id))
        throw new Exception("Tooltip text differs from CAN definition");
}

static void WaitFor(Func<bool> condition, string failure)
{
    var deadline = DateTime.UtcNow.AddSeconds(4);
    while (!condition() && DateTime.UtcNow < deadline) Pump(20);
    if (!condition()) throw new Exception(failure);
}

static void Pump(int milliseconds)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
    do
    {
        while (Application.EventsPending()) Application.RunIteration();
        Thread.Sleep(5);
    } while (DateTime.UtcNow < deadline);
}

static class Native
{
    [DllImport("libgtk-3.so.0")]
    public static extern nuint gtk_tooltip_get_type();
    [DllImport("libgobject-2.0.so.0")]
    public static extern IntPtr g_object_new(nuint type, IntPtr firstProperty);
}
