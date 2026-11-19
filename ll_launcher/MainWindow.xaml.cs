using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ll_launcher.Data;
using ll_launcher.Models;
using ll_launcher.Services;

namespace ll_launcher;
public partial class MainWindow : Window
{
    public static string DisplayVersion => $"v{typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}";

    private readonly LauncherRepository _repo;
    private readonly SkinCatalog _skins;
    private DesktopIntegration? _desktop;
    private List<LauncherGroup> _groups = [];
    private List<LauncherItem> _items = [];
    private long _selectedGroup;
    private bool _collapsed, _listMode, _ready, _allowClose;
    private readonly DispatcherTimer _saveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(700)
    };
    private readonly DispatcherTimer _searchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
    private readonly DispatcherTimer _backupTimer = new()
    {
        Interval = TimeSpan.FromMinutes(15)
    };
    internal MainWindow(LauncherRepository repo, SkinCatalog skins)
    {
        _repo = repo;
        _skins = skins;
        InitializeComponent();
        _collapsed = repo.GetSetting("sidebar.collapsed") == "1";
        _listMode = repo.GetSetting("view.list") == "1";
        long.TryParse(repo.GetSetting("group.selected"), out _selectedGroup);
        RestoreSavedBounds();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Guard(SaveState);
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshItems();
        };
        _backupTimer.Tick += (_, _) => AutoBackup();
        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_desktop != null)
            return;
        _desktop = new DesktopIntegration(this, TitleBar);
        _desktop.ShowRequested += () => SearchBox.Focus();
        _desktop.ExitRequested += Exit;
        _desktop.BackupRequested += Backup;
        _desktop.StateChanged += QueueSave;
        _desktop.Initialize();

        _desktop.IsPinned = _repo.GetSetting("window.pinned") == "1";
        _desktop.RestoreDock(_repo.GetSetting("window.dock"));
        _ready = true;
        Reload();
        ApplySidebar();
        UpdatePin();
        AutoBackup();
        _backupTimer.Start();
        _ = CaptureLegacyShortcutsAsync();
    }

    private const string LayoutVersion = "standard-v2";
    private const double CardColumnWidth = 80;

    private void RestoreSavedBounds()
    {
        var layout = _repo.GetSetting("window.layout");
        if (layout is LayoutVersion or "compact-v1")
        {
            bool hasWidth = double.TryParse(_repo.GetSetting("window.width"), CultureInfo.InvariantCulture, out var w) && double.IsFinite(w);
            bool hasHeight = double.TryParse(_repo.GetSetting("window.height"), CultureInfo.InvariantCulture, out var h) && double.IsFinite(h);
            // A previous default can differ by a fraction of a DIP after pixel rounding.
            bool oldDefault = layout == "compact-v1" && Math.Abs(w - 373) < 1 && Math.Abs(h - 520) < 1;
            if (!oldDefault)
            {
                if (hasWidth) Width = Math.Clamp(w, MinWidth, 1600);
                if (hasHeight) Height = Math.Clamp(h, MinHeight, 1600);
            }
        }
        if (double.TryParse(_repo.GetSetting("window.left"), CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) &&
            double.TryParse(_repo.GetSetting("window.top"), CultureInfo.InvariantCulture, out var y) && double.IsFinite(y))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x;
            Top = y;
        }
    }

    private void QueueSave()
    {
        if (!_ready)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveState()
    {
        if (!_ready || WindowState != WindowState.Normal)
            return;
        _repo.SetSettings(new Dictionary<string, string> { ["window.layout"] = LayoutVersion, ["window.left"] = Left.ToString(CultureInfo.InvariantCulture), ["window.top"] = Top.ToString(CultureInfo.InvariantCulture), ["window.width"] = Width.ToString(CultureInfo.InvariantCulture), ["window.height"] = Height.ToString(CultureInfo.InvariantCulture), ["window.pinned"] = _desktop?.IsPinned == true ? "1" : "0", ["window.dock"] = _desktop?.DockEdge ?? "", ["sidebar.collapsed"] = _collapsed ? "1" : "0", ["view.list"] = _listMode ? "1" : "0", ["group.selected"] = _selectedGroup.ToString(CultureInfo.InvariantCulture) });
    }

    private void Reload()
    {
        _groups = _repo.GetGroups();
        _items = _repo.GetItems();
        if (_selectedGroup < -1 || (_selectedGroup > 0 && !_groups.Any(g => g.Id == _selectedGroup)))
            _selectedGroup = 0;
        var visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        var views = new List<GroupView>
        {
            new(0, "全部", "grid", visibility),
            new(-1, "常用", "star", visibility)
        };
        views.AddRange(_groups.Select(g => new GroupView(g.Id, g.Name, g.IconKey, visibility)));
        GroupList.ItemsSource = views;
        GroupList.SelectedItem = views.First(g => g.Id == _selectedGroup);
        RefreshItems();
    }

    private void RefreshItems()
    {
        if (!_ready)
            return;
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IEnumerable<LauncherItem> source = _items;
        if (query.Length > 0)
            source = source.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        else if (_selectedGroup == -1)
            source = source.Where(i => i.IsFavorite);
        else if (_selectedGroup > 0)
            source = source.Where(i => i.GroupId == _selectedGroup);
        var ordered = source.ToList(); // GetItems returns items in group and item display order.
        int gridColumns = Math.Max(2, (int)((ContentArea.ActualWidth > 0 ? ContentArea.ActualWidth : 250) / CardColumnWidth));
        var columns = _listMode ? 1 : gridColumns;
        MainItems.ItemsSource = MakeRows(ordered, columns, _listMode);
        FavoritesList.ItemsSource = MakeRows(_items.Where(i => i.IsFavorite).ToList(), gridColumns, false);
        FavoritesPanel.Visibility = _items.Count > 0 && _selectedGroup == 0 && query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoritesEmpty.Visibility = _items.Any(i => i.IsFavorite) ? Visibility.Collapsed : Visibility.Visible;
        SectionTitle.Text = query.Length > 0 ? "搜索结果" : _selectedGroup == 0 ? "全部应用" : _selectedGroup == -1 ? "常用" : _groups.FirstOrDefault(g => g.Id == _selectedGroup)?.Name ?? "应用";
        ItemCount.Text = $"({ordered.Count})";
        EmptyPanel.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = query.Length > 0 ? "没有找到匹配的条目" : "把常用的东西，放在这里";
        GridModeButton.SetResourceReference(BackgroundProperty, _listMode ? "ClearBrush" : "SelectedBrush");
        ListModeButton.SetResourceReference(BackgroundProperty, _listMode ? "SelectedBrush" : "ClearBrush");
    }

    private List<ItemRow> MakeRows(List<LauncherItem> entries, int columns, bool list)
    {
        var template = (DataTemplate)FindResource(list ? "ListCard" : "GridCard");
        var rows = new List<ItemRow>();
        for (int n = 0; n < entries.Count; n += columns)
            rows.Add(new(entries.Skip(n).Take(columns).Select(i => new ItemView(i, Math.Max(100, ContentArea.ActualWidth - 40), _repo)).ToList(), columns, template));
        return rows;
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupView g)
        {
            _selectedGroup = g.Id;
            RefreshItems();
            QueueSave();
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Content_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1)
            RefreshItems();
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ApplySidebar();
        Reload();
        QueueSave();
    }

    private void ApplySidebar()
    {
        SidebarColumn.Width = new GridLength(_collapsed ? 53 : 108);
        SidebarActions.Orientation = _collapsed ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void GridMode_Click(object sender, RoutedEventArgs e)
    {
        _listMode = false;
        RefreshItems();
        QueueSave();
    }

    private void ListMode_Click(object sender, RoutedEventArgs e)
    {
        _listMode = true;
        RefreshItems();
        QueueSave();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_desktop == null)
            return;
        _desktop.IsPinned = !_desktop.IsPinned;
        UpdatePin();
        QueueSave();
    }

    private void UpdatePin()
    {
        PinGlyph.Data = (Geometry)FindResource(_desktop?.IsPinned == true ? "Icon.pinFilled" : "Icon.pin");
        PinButton.SetResourceReference(ForegroundProperty, _desktop?.IsPinned == true ? "AccentBrush" : "TextBrush");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Guard(SaveState);
        _desktop?.HideWindow();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _desktop?.HideWindow();
            return;
        }

        _saveTimer.Stop();
        _searchTimer.Stop();
        _backupTimer.Stop();
        _desktop?.Dispose();
    }

    public void Reveal()
    {
        _desktop?.ShowWindow();
        SearchBox.Focus();
    }

    public void HideToTray() => _desktop?.HideWindow();
    private void Exit()
    {
        Guard(SaveState);
        AutoBackup();
        _allowClose = true;
        Close();
        Application.Current.Shutdown();
    }

    private void AutoBackup()
    {
        try
        {
            _repo.CreateBackupIfPending();
        }
        catch (Exception ex)
        {
            StatusText.Text = "自动备份未完成：" + ex.Message;
        }
    }

    private void Backup() => Guard(() =>
    {
        var path = _repo.CreateBackup();
        StatusText.Text = "已备份：" + Path.GetFileName(path);
    });
    private void Launch(LauncherItem item) => ShellService.Launch(_repo.LaunchTarget(item), item.Arguments, item.WorkingDirectory);

    private async Task CaptureLegacyShortcutsAsync()
    {
        int unavailable = 0;
        int captured = 0;
        string? firstError = null;
        foreach (var item in _items.Where(i => i.Kind is not (ShortcutSnapshot.Kind or "folder") && ShortcutSnapshot.IsLinkPath(i.Target)).ToArray())
        {
            try
            {
                var snapshot = await Task.Run(() => ShortcutSnapshot.Read(item.Target));
                if (_allowClose) return;
                if (_repo.CaptureExistingShortcut(item, snapshot))
                    captured++;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                unavailable++;
                firstError ??= item.Name + "：" + error.Message;
            }
            if (_allowClose) return;
        }
        if (captured > 0)
        {
            Guard(Reload);
            AutoBackup();
        }
        if (unavailable > 0) StatusText.Text = $"有 {unavailable} 个旧快捷方式未保存（{firstError}）";
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDrag != null) return;

        if ((sender as FrameworkElement)?.DataContext is not ItemView view)
            return;
        Guard(() =>
        {
            Launch(view.Item);
            StatusText.Text = "已打开 " + view.Name;
        });
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is ItemView view)
            Guard(() =>
            {
                _repo.SetFavorite(view.Item.Id, !view.IsFavorite);
                Reload();
            });
    }

    private long DestinationGroup() => _selectedGroup > 0 ? _selectedGroup : _repo.DefaultGroupId;
    private void AddItem_Click(object sender, RoutedEventArgs e) => EditItem(null);
    private void AddGroup_Click(object sender, RoutedEventArgs e) => EditGroup(null);
    private bool? Modal(EditDialog dialog)
    {
        if (_desktop != null)
            _desktop.IsInteractionActive = true;
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            if (_desktop != null)
                _desktop.IsInteractionActive = false;
        }
    }

    private void EditItem(LauncherItem? item)
    {
        var dialog = new EditDialog(this, item == null ? "添加启动项" : "编辑启动项");
        dialog.AddField("name", "显示名称", item?.Name ?? "");
        dialog.AddField("target", item?.Kind == ShortcutSnapshot.Kind ? "快捷方式来源（已保存，可更换）" : "程序、文件或网址", item?.Target ?? "");
        var group = dialog.AddChoices("分组", _groups.Select(g => new KeyValuePair<string, string>(g.Id.ToString(), g.Name)), (item?.GroupId ?? DestinationGroup()).ToString());
        dialog.AddField("args", "启动参数", item?.Arguments ?? "", advanced: true);
        dialog.AddField("cwd", "工作目录", item?.WorkingDirectory ?? "", advanced: true);
        string Target() => dialog.Value("target").Trim('"').Trim();
        dialog.Validate = () => Target().Length > 0 && group.SelectedValue != null || dialog.Error("请填写目标并选择分组。");
        if (Modal(dialog) != true)
            return;
        Guard(() =>
        {
            var target = Target();
            var name = dialog.Value("name");
            if (name.Length == 0)
                name = GuessName(target);
            var keepSnapshot = item?.Kind == ShortcutSnapshot.Kind && string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase);
            SaveEntry(new(item?.Id ?? 0, long.Parse((string)group.SelectedValue), name, target, dialog.Value("args"), dialog.Value("cwd"),
                keepSnapshot ? ShortcutSnapshot.Kind : Kind(target), item?.IsFavorite ?? false, item?.SortOrder ?? _items.Count));
            Reload();
        });
    }

    private void SaveEntry(LauncherItem item)
    {
        if (item.Kind != ShortcutSnapshot.Kind && ShortcutSnapshot.IsLinkPath(item.Target) && item.Kind != "folder")
        {
            _repo.SaveItem(item with { Kind = ShortcutSnapshot.Kind }, ShortcutSnapshot.Read(item.Target));
            if (item.Id != 0) ShellService.ClearIconCache();
        }
        else
            _repo.SaveItem(item);
    }

    private static string GuessName(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            return uri.Host;
        var name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
            return target;
        var extension = Path.GetExtension(name);
        if (extension.Length < name.Length && extension.ToLowerInvariant() is ".exe" or ".lnk" or ".url")
            return Path.GetFileNameWithoutExtension(name);
        return name;
    }

    private static string Kind(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? "url" : Directory.Exists(Environment.ExpandEnvironmentVariables(target)) ? "folder" : "file";
    private void EditGroup(LauncherGroup? group)
    {
        var dialog = new EditDialog(this, group == null ? "新建分组" : "编辑分组");
        dialog.AddField("name", "分组名称", group?.Name ?? "");
        var icons = new Dictionary<string, string>
        {
            ["folder"] = "文件夹", ["grid"] = "应用", ["star"] = "收藏", ["flash"] = "快捷", ["wrench"] = "工具",
            ["code"] = "开发", ["briefcase"] = "办公", ["file"] = "文档", ["book"] = "阅读", ["calculator"] = "计算",
            ["paint"] = "设计", ["image"] = "图片", ["camera"] = "摄影", ["music"] = "音乐", ["video"] = "视频",
            ["games"] = "游戏", ["link"] = "网络", ["chat"] = "聊天", ["mail"] = "邮件", ["cloud"] = "云盘"
        };
        var icon = dialog.ChooseIcon(icons, group?.IconKey ?? "folder");
        dialog.Validate = () => dialog.Value("name").Length is> 0 and <= 30 || dialog.Error("分组名称需要 1–30 个字符。");
        if (Modal(dialog) != true)
            return;
        Guard(() =>
        {
            var id = _repo.SaveGroup(new(group?.Id ?? 0, dialog.Value("name"), (string? )icon.SelectedValue ?? "folder", group?.SortOrder ?? _groups.Count));
            _selectedGroup = id;
            Reload();
        });
    }

    private void Item_Context(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ItemView v)
            return;
        e.Handled = true;
        var menu = new ContextMenu();
        AddMenu(menu, "打开", () => Guard(() => Launch(v.Item)));
        AddMenu(menu, v.IsFavorite ? "取消收藏" : "加入常用", () => Guard(() =>
        {
            _repo.SetFavorite(v.Item.Id, !v.IsFavorite);
            Reload();
        }));
        AddMenu(menu, "编辑", () => EditItem(v.Item));
        var move = new MenuItem
        {
            Header = "移动到分组"
        };
        foreach (var g in _groups)
        {
            var id = g.Id;
            var child = new MenuItem
            {
                Header = g.Name,
                IsEnabled = id != v.Item.GroupId
            };
            child.Click += (_, _) => Guard(() =>
            {
                _repo.MoveItem(v.Item.Id, id);
                Reload();
            });
            move.Items.Add(child);
        }

        menu.Items.Add(move);
        if (ShellService.GetFileSystemTarget(v.Item.Target) is { } source)
        {
            AddMenu(menu, "打开文件所在位置", () => Guard(() => ShellService.OpenFileLocation(source, v.Item.WorkingDirectory)));
            AddMenu(menu, "属性", () => Guard(() =>
                ShellService.ShowProperties(new WindowInteropHelper(this).Handle, source, v.Item.WorkingDirectory)));
        }
        menu.Items.Add(new Separator());
        AddMenu(menu, "移除启动项", () => Guard(() =>
        {
            _repo.DeleteItem(v.Item.Id);
            Reload();
        }));
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void Group_Context(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupView v || v.Id <= 0)
            return;
        e.Handled = true;
        var g = _groups.First(x => x.Id == v.Id);
        var menu = new ContextMenu();
        AddMenu(menu, "编辑分组", () => EditGroup(g));
        AddMenu(menu, "向上移动", () => MoveGroup(g, -1), g.Id != _groups[0].Id);
        AddMenu(menu, "向下移动", () => MoveGroup(g, 1), g.Id != _groups[^1].Id);
        if (g.Name != "其他")
            AddMenu(menu, "删除分组（条目移到其他）", () => Guard(() =>
            {
                _repo.DeleteGroup(g.Id);
                _selectedGroup = 0;
                Reload();
            }));
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void MoveGroup(LauncherGroup g, int offset) => Guard(() =>
    {
        var ids = _groups.Select(x => x.Id).ToList();
        var from = ids.IndexOf(g.Id);
        var to = Math.Clamp(from + offset, 0, ids.Count - 1);
        ids.RemoveAt(from);
        ids.Insert(to, g.Id);
        _repo.ReorderGroups(ids);
        Reload();
    });
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, button, _) =>
            [
                new(new Point(button.Width - popup.Width, button.Height + 4), PopupPrimaryAxis.Horizontal),
                new(new Point(button.Width - popup.Width, -popup.Height - 4), PopupPrimaryAxis.Horizontal)
            ]
        };
        AddMenu(menu, "添加启动项 / 网址", () => EditItem(null));
        AddMenu(menu, "从文件选择添加…", ChooseFiles);
        AddMenu(menu, "新建分组", () => EditGroup(null));
        menu.Items.Add(new Separator());
        var appearance = new MenuItem { Header = "皮肤设置" };
        foreach (var group in _skins.Skins.GroupBy(skin => skin.Style))
        {
            var styleMenu = new MenuItem { Header = group.Key };
            foreach (var skin in group)
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new System.Windows.Shapes.Ellipse { Width = 11, Height = 11, Fill = skin.Swatch, Margin = new Thickness(0, 0, 8, 0) });
                header.Children.Add(new TextBlock { Text = skin.Name });
                var choice = new MenuItem { Header = header, IsCheckable = true, IsChecked = skin == _skins.Current };
                System.Windows.Automation.AutomationProperties.SetName(choice, skin.Name);
                choice.Click += (_, _) => Guard(() =>
                {
                    _repo.SetSetting("ui.skin", skin.Id);
                    _skins.Apply(skin.Id);
                });
                styleMenu.Items.Add(choice);
            }
            appearance.Items.Add(styleMenu);
        }
        menu.Items.Add(appearance);
        AddMenu(menu, "立即备份", Backup);
        AddMenu(menu, "打开数据目录", () => Guard(() => ShellService.Launch(App.DataDirectory)));
        AddMenu(menu, "解除边缘吸附", () =>
        {
            _desktop?.Detach();
            QueueSave();
        });
        menu.Items.Add(new Separator());
        AddMenu(menu, "退出栖点", Exit);
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private static void AddMenu(ContextMenu menu, string title, Action action, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = title,
            IsEnabled = enabled
        };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OpenMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Style = (Style)FindResource("LauncherMenu");
        if (_desktop != null)
            _desktop.IsInteractionActive = true;
        menu.Closed += (_, _) =>
        {
            if (_desktop != null)
                _desktop.IsInteractionActive = false;
        };
        menu.IsOpen = true;
    }

    private void ChooseFiles()
    {
        if (_desktop != null)
            _desktop.IsInteractionActive = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true, DereferenceLinks = false,
                Title = "添加程序、快捷方式或文件",
                Filter = "所有文件|*.*"
            };
            if (dialog.ShowDialog(this) == true)
                AddPaths(dialog.FileNames, DestinationGroup());
        }
        finally
        {
            if (_desktop != null)
                _desktop.IsInteractionActive = false;
        }
    }

    private void AddPaths(IEnumerable<string> paths, long groupId) => Guard(() =>
    {
        var existing = _repo.GetItems().Where(i => i.GroupId == groupId).Select(i => i.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        var failures = new List<string>();
        string? stopped = null;
        foreach (var path in paths)
        {
            if (existing.Contains(path)) continue;
            try
            {
                SaveEntry(new(0, groupId, GuessName(path), path, "", "", Kind(path), false, int.MaxValue));
                existing.Add(path);
                added++;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                failures.Add(Path.GetFileName(path) + "：" + error.Message);
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                stopped = $"写入 {Path.GetFileName(path)} 时停止导入：{error.Message}";
                break;
            }
        }
        if (added > 0)
            Reload();
        if (added == 0 && failures.Count == 0 && stopped == null)
        {
            StatusText.Text = "该分组已包含这些条目";
            return;
        }
        var summary = $"已添加 {added} 项到{_groups.First(g => g.Id == groupId).Name}";
        if (failures.Count > 0) summary += $"，无法读取 {failures.Count} 项（{failures[0]}）";
        if (stopped != null) summary += "，" + stopped;
        StatusText.Text = summary;
    });
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0)
                SearchBox.Clear();
            else
                _desktop?.HideWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _searchTimer.Stop();
            RefreshItems();
            var rows = MainItems.ItemsSource as List<ItemRow>;
            var first = rows?.SelectMany(r => r.Items).FirstOrDefault();
            if (first != null)
                Guard(() => Launch(first.Item));
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin)
        {
            _searchTimer.Stop();
            RefreshItems();
            FocusResult(0);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right && MainItems.IsKeyboardFocusWithin && Keyboard.FocusedElement is Button { DataContext: ItemView item })
        {
            var rows = MainItems.Items.Cast<ItemRow>().ToList();
            int columns = rows.FirstOrDefault()?.Columns ?? 1;
            var ids = rows.SelectMany(r => r.Items).Select(v => v.Item.Id).ToList();
            var delta = e.Key switch
            {
                Key.Up => -columns,
                Key.Down => columns,
                Key.Left => -1,
                _ => 1
            };
            var next = ids.IndexOf(item.Item.Id) + delta;
            if (next < 0)
                SearchBox.Focus();
            else
                FocusResult(Math.Min(next, ids.Count - 1));
            e.Handled = true;
        }
    }

    private void FocusResult(int index)
    {
        foreach (var row in MainItems.Items.Cast<ItemRow>())
        {
            if (index >= row.Items.Count)
            {
                index -= row.Items.Count;
                continue;
            }

            MainItems.ScrollIntoView(row);
            MainItems.UpdateLayout();
            if (MainItems.ItemContainerGenerator.ContainerFromItem(row)is ListBoxItem container)
            {
                var button = VisualChildren<Button>(container).FirstOrDefault(b => b.DataContext == row.Items[index] && b.ToolTip == null);
                button?.Focus();
            }

            return;
        }
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in VisualChildren<T>(child))
                yield return descendant;
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (_desktop != null)
                _desktop.IsInteractionActive = true;
            try
            {
                MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (_desktop != null)
                    _desktop.IsInteractionActive = false;
            }
        }
    }

}
