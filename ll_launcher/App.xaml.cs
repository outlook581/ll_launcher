using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using ll_launcher.Data;


namespace ll_launcher;
public partial class App : Application
{
    public static string DataDirectory { get; private set; } = "";
    internal static string InstallDirectory { get; } = Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "dotnet" or null
        ? AppContext.BaseDirectory : Path.GetDirectoryName(Environment.ProcessPath!)!;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private LauncherRepository? _repository;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var index = Array.FindIndex(e.Args, arg => arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
            var legacyData = Path.Combine(InstallDirectory, "Data");
            var directory = File.Exists(Path.Combine(legacyData, "launcher.db")) ? legacyData : InstallDirectory;
            if (index >= 0)
            {
                if (index + 1 >= e.Args.Length || string.IsNullOrWhiteSpace(e.Args[index + 1]) ||
                    e.Args[index + 1].Equals("--hidden", StringComparison.OrdinalIgnoreCase) ||
                    e.Args[index + 1].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--data-dir 后需要指定数据目录路径。");
                directory = e.Args[index + 1];
            }
            DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..20];
            _mutex = new Mutex(true, "Local\\LLLauncher." + identity, out bool first);
            if (!first)
            {
                try
                {
                    using var other = EventWaitHandle.OpenExisting("Local\\LLLauncher.Show." + identity);
                    other.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    MessageBox.Show("栖点已在启动，请稍后从托盘打开。", "栖点");
                }

                Shutdown();
                return;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\LLLauncher.Show." + identity);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.Reveal()), null, Timeout.Infinite, false);
            _repository = OpenRepository();
            if (_repository == null)
            {
                Shutdown();
                return;
            }

            var assets = Path.Combine(InstallDirectory, "Assets");
            LauncherAssets.Load(Resources, assets);
            var skins = SkinCatalog.Load(Path.Combine(assets, "Skins.json"));
            skins.Apply(_repository.GetSetting("ui.skin"));
            var window = new MainWindow(_repository, skins);
            MainWindow = window;
            if (e.Args.Contains("--hidden", StringComparer.OrdinalIgnoreCase))
            {
                window.ShowActivated = false;
                window.Loaded += HideOnFirstLoad;
            }
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败，现有数据已保留。\n\n" + ex.Message, "栖点", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void HideOnFirstLoad(object sender, RoutedEventArgs e)
    {
        var window = (MainWindow)sender;
        window.Loaded -= HideOnFirstLoad;
        window.Dispatcher.BeginInvoke(new Action(window.HideToTray));
    }

    private LauncherRepository? OpenRepository()
    {
        try
        {
            return new LauncherRepository(DataDirectory);
        }
        catch (Exception ex) when (File.Exists(Path.Combine(DataDirectory, "launcher.db")))
        {
            var restore = MessageBox.Show("无法打开数据，原文件不会被覆盖。\n\n" + ex.Message + "\n\n是否选择一份备份恢复？", "数据恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (restore != MessageBoxResult.Yes)
                return null;
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择栖点数据库备份",
                Filter = "SQLite 备份|*.db",
                InitialDirectory = Path.Combine(DataDirectory, "Backups")
            };
            if (picker.ShowDialog() != true)
                return null;
            BackupRecovery.Restore(DataDirectory, picker.FileName);
            return new LauncherRepository(DataDirectory);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _repository?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
