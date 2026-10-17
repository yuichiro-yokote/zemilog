using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace ZemiLog.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    public bool IsExplicitShutdown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "ゼミログ",
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void ExitApplication()
    {
        if (MainWindow is MainWindow window && window.IsRecording)
        {
            ShowMainWindow();
            MessageBox.Show(window, "録音を停止して保存してから終了してください。", "ゼミログ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        IsExplicitShutdown = true;
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnExit(e);
    }
}
