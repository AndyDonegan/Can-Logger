using System.Runtime.CompilerServices;
using Gtk;

namespace CanLogger;

/// <summary>Interactive LIN details, independent of GTK's transient tooltip lifecycle.</summary>
internal sealed class LinHoverPopup
{
    private readonly TreeView _tree;
    private readonly Popover _popup;
    private readonly TextView _text = new() { Editable = false, CursorVisible = false, WrapMode = WrapMode.WordChar, LeftMargin = 8, RightMargin = 8 };
    private readonly ScrolledWindow _scroll = new();
    private bool _inside, _disposed;
    private uint _closeGeneration, _openGeneration;
    private uint _scheduledCloseGeneration = uint.MaxValue;
    private bool _pending, _queryQueued;
    private int _pointerX = int.MinValue, _pointerY = int.MinValue;
    private static LinHoverPopup? _active;
    private string _path = "";
    private int _x, _y;

    internal LinHoverPopup(TreeView tree)
    {
        _tree = tree;
        _popup = new Popover(tree) { Modal = false, Position = PositionType.Right };
        var content = new Box(Orientation.Vertical, 4) { Margin = 8 };
        var heading = new Box(Orientation.Horizontal, 8);
        heading.PackStart(new Label("LIN details • scroll to read more") { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End }, true, true, 0);
        content.PackStart(heading, false, false, 0);
        _scroll.SetPolicy(PolicyType.Never, PolicyType.Automatic);
        _scroll.Add(_text);
        content.PackStart(_scroll, true, true, 0);
        _popup.Add(content);
        _popup.AddEvents((int)(Gdk.EventMask.EnterNotifyMask | Gdk.EventMask.LeaveNotifyMask));
        _popup.EnterNotifyEvent += (_, _) => { _inside = true; ++_closeGeneration; };
        _popup.LeaveNotifyEvent += (_, args) => {
            if (args.Event.Detail == Gdk.NotifyType.Inferior) return;
            _inside = false; Close();
        };
        tree.AddEvents((int)(Gdk.EventMask.EnterNotifyMask | Gdk.EventMask.LeaveNotifyMask | Gdk.EventMask.PointerMotionMask));
        tree.EnterNotifyEvent += (_, _) => EnterTree();
        tree.MotionNotifyEvent += (_, args) => {
            PointerMoved((int)args.Event.X, (int)args.Event.Y);
            // Resolve the current row after GTK has processed the motion event.
            if (!_queryQueued) {
                _queryQueued = true;
                GLib.Idle.Add(() => {
                    _queryQueued = false;
                    if (!_disposed) tree.TriggerTooltipQuery();
                    return false;
                });
            }
        };
        tree.LeaveNotifyEvent += (_, args) => {
            if (args.Event.Detail != Gdk.NotifyType.Inferior) { CancelPending(); ScheduleClose(); }
        };
        tree.ScrollEvent += (_, _) => { if (!_inside) Close(); };
        tree.ButtonPressEvent += (_, _) => Close();
        tree.KeyPressEvent += (_, args) => { if (args.Event.Key == Gdk.Key.Escape) Close(); };
        _popup.KeyPressEvent += (_, args) => { if (args.Event.Key == Gdk.Key.Escape) Close(); };
        tree.Unmapped += (_, _) => Close();
        tree.Destroyed += (_, _) => { Close(); _disposed = true; _popup.Destroy(); };
    }

    private void ScheduleClose()
    {
        // Continuous movement must not keep extending the transfer grace period.
        if (_scheduledCloseGeneration == _closeGeneration) return;
        uint generation = ++_closeGeneration;
        _scheduledCloseGeneration = generation;
        GLib.Timeout.Add(250, () => {
            if (!_disposed && generation == _closeGeneration) {
                _scheduledCloseGeneration = uint.MaxValue;
                if (!_inside) {
                    Close();
                    // If the pointer settled on another row during the grace
                    // period, start that row's normal dwell without requiring
                    // another mouse movement or an incoming frame.
                    _tree.TriggerTooltipQuery();
                }
            }
            return false;
        });
    }

    internal void Close()
    {
        CancelPending();
        ++_closeGeneration;
        _inside = false;
        if (_active == this) _active = null;
        if (!_disposed) _popup.Hide();
    }

    private void CancelPending() { ++_openGeneration; _pending = false; }

    internal void EnterTree()
    {
        // Crossing into another table dismisses the old panel immediately,
        // even if the new table contains CAN rows or no rows at all.
        if (_active != this) _active?.Close();
        ++_closeGeneration;
    }

    internal void PointerMoved(int x, int y)
    {
        if (_inside || (x == _pointerX && y == _pointerY)) return;
        _pointerX = x; _pointerY = y;
        CancelPending();
        if (_popup.Visible) ScheduleClose(); // small bridge into the scrollable panel
    }

    internal void RequestShow(string text, string path, int x, int y)
    {
        if (_disposed || _inside || _popup.Visible || _pending) return;
        // QueryTooltip is a query, not GTK's hover-delay notification. It may
        // run on every motion, so an independent dwell timer is essential.
        if (_active != this) _active?.Close();
        _active = this;
        _pending = true;
        uint generation = ++_openGeneration;
        GLib.Timeout.Add(650, () => {
            if (_disposed || generation != _openGeneration) return false;
            _pending = false;
            Show(text, path, x, y);
            return false;
        });
    }

    internal void Show(string text, string path, int x, int y)
    {
        if (_disposed) return;
        // GTK can query repeatedly as rows arrive. Keep a stable snapshot and
        // scroll position until the user deliberately hovers another row.
        if (_popup.Visible && (_inside || (x == _x && y == _y) || path == _path)) return;
        var parent = _tree.Toplevel as Window;
        if (parent?.Window == null) return;
        var area = _tree.Display.GetMonitorAtWindow(parent.Window)?.Workarea ?? new Gdk.Rectangle(0, 0, parent.AllocatedWidth, parent.AllocatedHeight);
        int width = Math.Max(120, Math.Min(760, Math.Min(area.Width, parent.AllocatedWidth) - 80));
        int height = Math.Max(80, Math.Min(520, Math.Min(area.Height, parent.AllocatedHeight) - 140));
        _scroll.SetSizeRequest(width, height);
        _text.Buffer.Text = text; // Complete definition; no truncation or ellipsis.
        _scroll.Vadjustment.Value = 0;
        _path = path; _x = x; _y = y;
        ++_closeGeneration;
        _popup.PointingTo = new Gdk.Rectangle(x, y, 1, 1);
        _active?.Close();
        _active = this;
        _tree.TooltipWindow?.Hide();
        _popup.ShowAll();
    }
}

internal static class LinTooltip
{
    private static readonly ConditionalWeakTable<TreeView, LinHoverPopup> Popups = new();
    internal static LinHoverPopup ForTree(TreeView tree) => Popups.GetValue(tree, t => new LinHoverPopup(t));

    internal static void Hide(TreeView tree) { if (Popups.TryGetValue(tree, out var popup)) popup.Close(); }

    internal static void Show(TreeView tree, QueryTooltipArgs args, Func<ITreeModel, TreeIter, string> describe)
    {
        // Do not register this interactive popover as a native tooltip: native
        // tooltips close on pointer entry and cannot support reliable scrolling.
        args.RetVal = false;
        int x = args.X, y = args.Y;
        if (!tree.GetTooltipContext(ref x, ref y, args.KeyboardTooltip, out var model, out var path, out var iter)) return;
        int anchorX = args.KeyboardTooltip ? tree.AllocatedWidth / 2 : args.X;
        int anchorY = args.KeyboardTooltip ? tree.AllocatedHeight / 2 : args.Y;
        ForTree(tree).RequestShow(describe(model, iter), path.ToString(), anchorX, anchorY);
    }
}
