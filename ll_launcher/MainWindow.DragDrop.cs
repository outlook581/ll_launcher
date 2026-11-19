using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ll_launcher;

public partial class MainWindow
{
    private sealed record DragEntry(FrameworkElement Element, long Id, bool IsGroup, Point Start);
    private sealed record Insertion(FrameworkElement Cell, long AnchorId, bool After, bool Vertical);

    private DragEntry? _pressedEntry;
    private DragEntry? _activeDrag;
    private InsertionMarker? _marker;
    private long _lastDragScroll;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pressedEntry = null;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private void Group_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit &&
            ItemsControl.ContainerFromElement(GroupList, hit) is ListBoxItem { DataContext: GroupView { Id: > 0 } group } row)
            _pressedEntry = new(row, group.Id, true, e.GetPosition(this));
    }

    private void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemView view } card)
            _pressedEntry = new(card, view.Item.Id, false, e.GetPosition(this));
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) _pressedEntry = null;
        if (_pressedEntry is not { } entry) return;
        var delta = e.GetPosition(this) - entry.Start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pressedEntry = null;
        _activeDrag = entry;
        if (Mouse.Captured != null) Mouse.Capture(null);
        if (_desktop != null) _desktop.IsInteractionActive = true;
        try
        {
            DragDrop.DoDragDrop(entry.Element, new DataObject("LLLauncher.Reorder", entry.Id), DragDropEffects.Move);
        }
        finally
        {
            ClearMarker();
            _activeDrag = null;
            if (_desktop != null) _desktop.IsInteractionActive = false;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        ClearMarker();
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        ClearMarker();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths, DestinationGroup());
        e.Handled = true;
    }

    private void List_DragLeave(object sender, DragEventArgs e)
    {
        var list = (ListBox)sender;
        if (!new Rect(list.RenderSize).Contains(e.GetPosition(list))) ClearMarker();
    }

    private void List_DragOver(object sender, DragEventArgs e) => HandleListDrag((ListBox)sender, e, drop: false);
    private void List_Drop(object sender, DragEventArgs e) => HandleListDrag((ListBox)sender, e, drop: true);

    private void HandleListDrag(ListBox list, DragEventArgs e, bool drop)
    {
        var entry = _activeDrag;
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        if (!files && entry == null) return;
        bool sidebar = list == GroupList;
        if (files && !sidebar) return; // Window_Drop imports explorer files into the current group.

        var viewer = VisualChildren<ScrollViewer>(list).FirstOrDefault();
        if (viewer == null) return;
        var viewport = viewer.Template.FindName("PART_ScrollContentPresenter", viewer) as ScrollContentPresenter;
        if (viewport == null) return;
        Point pointer = e.GetPosition(viewport);
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!new Rect(viewport.RenderSize).Contains(pointer))
        {
            ClearMarker();
            return;
        }

        if (!drop) ScrollDuringDrag(viewer, pointer.Y, viewport.ActualHeight);
        if (sidebar && (files || entry is { IsGroup: false }))
        {
            ClearMarker();
            var hit = viewport.InputHitTest(pointer) as DependencyObject;
            var group = hit == null ? null : (ItemsControl.ContainerFromElement(list, hit) as ListBoxItem)?.DataContext as GroupView;
            if (group is not { Id: > 0 })
            {
                if (files) e.Handled = false;
                return;
            }
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.Move;
            if (drop && files && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                AddPaths(paths, group.Id);
            else if (drop && entry != null)
                Guard(() => { _repo.MoveItem(entry.Id, group.Id); Reload(); });
            return;
        }

        bool sortable = entry != null && entry.IsGroup == sidebar &&
            (sidebar || (_selectedGroup > 0 && string.IsNullOrWhiteSpace(SearchBox.Text)));
        var slot = sortable ? FindInsertion(list, viewport, pointer) : null;
        if (slot == null)
        {
            ClearMarker();
            return;
        }

        e.Effects = DragDropEffects.Move;
        if (!drop) { ShowMarker(slot); return; }
        ClearMarker();
        Guard(() => CommitInsertion(entry!, slot));
    }

    private void ScrollDuringDrag(ScrollViewer viewer, double y, double height)
    {
        if (viewer.ScrollableHeight <= 0 || Environment.TickCount64 - _lastDragScroll < 150) return;
        if (y < 16 && viewer.VerticalOffset > 0) viewer.LineUp();
        else if (y > height - 16 && viewer.VerticalOffset < viewer.ScrollableHeight) viewer.LineDown();
        else return;
        _lastDragScroll = Environment.TickCount64;
        viewer.UpdateLayout();
    }

    private Insertion? FindInsertion(ListBox list, FrameworkElement viewport, Point pointer)
    {
        // Hit-test the layout slots, including margins and unused cells on the final row.
        // Only realized rows participate, so drag feedback also works with a virtualized list.
        var panel = VisualChildren<VirtualizingStackPanel>(list).FirstOrDefault();
        if (panel == null) return null;
        ListBoxItem? nearest = null;
        double nearestTop = 0, nearestDistance = double.PositiveInfinity;
        foreach (var row in panel.Children.OfType<ListBoxItem>())
        {
            double top = row.TranslatePoint(new Point(), viewport).Y;
            double bottom = top + row.ActualHeight;
            if (bottom <= 0 || top >= viewport.ActualHeight) continue;
            double distance = Math.Max(0, Math.Max(top - pointer.Y, pointer.Y - bottom));
            if (distance >= nearestDistance) continue;
            nearest = row;
            nearestTop = top;
            nearestDistance = distance;
            if (distance == 0) break;
        }
        if (nearest == null) return null;
        bool belowMiddle = pointer.Y >= nearestTop + nearest.ActualHeight / 2;
        if (list == GroupList)
            return nearest.DataContext is GroupView { Id: > 0 } group ? new(nearest, group.Id, belowMiddle, false) : null;

        var cells = VisualChildren<UniformGrid>(nearest).FirstOrDefault()?.Children;
        if (cells == null || cells.Count == 0) return null;
        bool vertical = !_listMode;
        if (!vertical) return SlotFor(cells[0], belowMiddle, false);
        if (pointer.Y < nearestTop) return SlotFor(cells[0], false, true);
        if (pointer.Y >= nearestTop + nearest.ActualHeight) return SlotFor(cells[cells.Count - 1], true, true);
        foreach (FrameworkElement cell in cells)
        {
            double left = cell.TranslatePoint(new Point(), viewport).X;
            if (pointer.X < left + cell.ActualWidth)
                return SlotFor(cell, pointer.X >= left + cell.ActualWidth / 2, true);
        }
        return SlotFor(cells[cells.Count - 1], true, true);
    }

    private static Insertion? SlotFor(UIElement cell, bool after, bool vertical) =>
        cell is FrameworkElement { DataContext: ItemView item } element ? new(element, item.Item.Id, after, vertical) : null;

    private void CommitInsertion(DragEntry entry, Insertion slot)
    {
        var ids = entry.IsGroup ? _groups.Select(g => g.Id).ToList() :
            _items.Select(v => v.Item).Where(i => i.GroupId == _selectedGroup).OrderBy(i => i.SortOrder).Select(i => i.Id).ToList();
        int from = ids.IndexOf(entry.Id), anchor = ids.IndexOf(slot.AnchorId);
        if (from < 0 || anchor < 0) return;
        int boundary = anchor + (slot.After ? 1 : 0);
        int to = from < boundary ? boundary - 1 : boundary;
        if (from == to) return;
        ids.RemoveAt(from);
        ids.Insert(to, entry.Id);
        if (entry.IsGroup) _repo.ReorderGroups(ids);
        else _repo.ReorderItems(_selectedGroup, ids);
        Reload();
    }

    private void ShowMarker(Insertion slot)
    {
        if (_marker?.Slot == slot) return;
        ClearMarker();
        var layer = AdornerLayer.GetAdornerLayer(slot.Cell);
        if (layer == null) return;
        _marker = new InsertionMarker(slot, layer);
        layer.Add(_marker);
    }

    private void ClearMarker()
    {
        if (_marker == null) return;
        _marker.Layer.Remove(_marker);
        _marker = null;
    }

    private sealed class InsertionMarker : Adorner
    {
        public Insertion Slot { get; }
        public AdornerLayer Layer { get; }
        private readonly Pen _line;

        public InsertionMarker(Insertion slot, AdornerLayer layer) : base(slot.Cell)
        {
            Slot = slot;
            Layer = layer;
            _line = new Pen((Brush)slot.Cell.FindResource("AccentBrush"), 2.5)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            IsHitTestVisible = false;
            IsClipEnabled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            if (Slot.Vertical)
            {
                double x = Slot.After ? size.Width - 1.5 : 1.5;
                dc.DrawLine(_line, new Point(x, 6), new Point(x, size.Height - 6));
            }
            else
            {
                double y = Slot.After ? size.Height - 1.5 : 1.5;
                dc.DrawLine(_line, new Point(4, y), new Point(size.Width - 4, y));
            }
        }
    }
}
