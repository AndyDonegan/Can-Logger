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
    foreach (string mode in new[] { "windowed", "maximized", "fullscreen" })
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
            var cell = tree.GetCellArea(new TreePath("0"), tree.Columns[0]);
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
    }
}
finally
{
    window.Destroy();
    Pump(100);
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
