using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ll_launcher.Models;
using ll_launcher.Data;
using ll_launcher.Native;
using ll_launcher.Services;

namespace ll_launcher;
public sealed class IconKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Application.Current.TryFindResource("Icon." + value) ?? Application.Current.FindResource("Icon.folder");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed record GroupView(long Id, string Name, string IconKey, Visibility LabelVisibility);
public sealed class ItemView : INotifyPropertyChanged
{
    private readonly LauncherRepository _repository;
    private readonly Lazy<string> _description;
    private ImageSource? _websiteIcon;
    private bool _websiteIconRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ItemView(LauncherItem item, LauncherRepository repository)
    {
        Item = item;
        _repository = repository;
        _description = new Lazy<string>(DescribeTarget);
    }

    public LauncherItem Item { get; }
    public string Name => Item.Name;
    public string Target => _description.Value;
    public bool IsFavorite => Item.IsFavorite;
    public string StarKey => IsFavorite ? "starFilled" : "star";
    public string FallbackKey => IsWebsite ? "link" : Item.Target == "ms-settings:" ? "wrench" : Item.Kind == "folder" ? "folder" : "file";
    public Visibility FallbackVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public ImageSource? Icon
    {
        get
        {
            if (!_websiteIconRequested && IsWebsite)
            {
                _websiteIconRequested = true;
                _ = LoadWebsiteIconAsync();
            }
            return _websiteIcon ?? ShellService.GetIcon(_repository.LaunchTarget(Item), 48);
        }
    }

    private bool IsWebsite => Item.Kind == "url" || IsSavedShortcut(".url");
    private bool IsSavedShortcut(string extension) => Item.Kind == ShortcutSnapshot.Kind &&
        Environment.ExpandEnvironmentVariables(Item.Target).EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private async Task LoadWebsiteIconAsync()
    {
        var address = Item.Kind == ShortcutSnapshot.Kind ? await Task.Run(() => Target) : Item.Target;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        if (await WebsiteIconService.GetIconAsync(uri) is not { } icon) return;
        _websiteIcon = icon;
        PropertyChanged?.Invoke(this, new(nameof(Icon)));
        PropertyChanged?.Invoke(this, new(nameof(FallbackVisibility)));
    }

    private string? ReadShortcutTarget() => ShellShortcut.GetFileSystemTarget(_repository.LaunchTarget(Item));

    private string DescribeTarget()
    {
        if (Item.Kind != ShortcutSnapshot.Kind) return Item.Target;
        var shortcutName = "已保存的快捷方式 · " + Path.GetFileName(Item.Target);
        try
        {
            var target = IsSavedShortcut(".url")
                ? ShortcutSnapshot.ReadInternetTarget(_repository.LaunchTarget(Item))
                : ReadShortcutTarget();
            return target ?? shortcutName;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or COMException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            return shortcutName;
        }
    }

    internal string FileActionTarget(string source)
    {
        if (!IsSavedShortcut(".lnk")) return source;
        if (ReadShortcutTarget() is { } target) return target;
        if (File.Exists(source)) return source;
        throw new FileNotFoundException("找不到快捷方式指向的文件或文件夹，原快捷方式也已移除。", source);
    }
}

public sealed record ItemRow(List<ItemView> Items, int Columns, DataTemplate Template, double RowWidth);
internal sealed partial class EditDialog : Window
{
    private readonly Dictionary<string, TextBox> _inputs = new();
    public Func<bool>? Validate { get; set; }

    public EditDialog(Window owner, string title)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        Width = Math.Clamp(owner.ActualWidth - 32, 276, 340);
        MaxHeight = Math.Min(Math.Max(250, owner.ActualHeight - 26), SystemParameters.WorkArea.Height);
        Loaded += (_, _) => _inputs.Values.FirstOrDefault()?.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Validate?.Invoke() != false) DialogResult = true;
    }

    private static void Label(Panel panel, string label)
    {
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 3) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(text);
    }

    public void AddField(string key, string label, string value = "", bool advanced = false)
    {
        var panel = advanced ? AdvancedFields : Fields;
        if (advanced)
        {
            Advanced.Visibility = Visibility.Visible;
            if (value.Length > 0) Advanced.IsExpanded = true;
        }
        Label(panel, label);
        var input = new TextBox { Text = value, Padding = new Thickness(8, 3, 8, 3), MinHeight = 27, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        _inputs.Add(key, input);
    }

    public ComboBox AddChoices(string label, IEnumerable<KeyValuePair<string, string>> choices, string selected)
    {
        Label(Fields, label);
        var combo = new ComboBox
        {
            Style = (Style)FindResource("GroupChoice"), ItemsSource = choices.ToList(),
            DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = selected, Height = 28
        };
        System.Windows.Automation.AutomationProperties.SetName(combo, label);
        Fields.Children.Add(combo);
        return combo;
    }

    public ListBox ChooseIcon(IReadOnlyDictionary<string,string> choices, string selected)
    {
        IconPanel.Visibility = Visibility.Visible;
        IconChoices.ItemsSource = choices;
        IconChoices.SelectedValue = selected;
        return IconChoices;
    }

    public string Value(string key) => _inputs[key].Text.Trim();
    public bool Error(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }
}
