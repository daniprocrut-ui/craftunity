using System;
using System.Windows;

namespace CraftunityLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var area = SystemParameters.WorkArea;
        var window = new MainWindow
        {
            Width = Math.Min(1180, area.Width - 40),
            Height = Math.Min(760, area.Height - 40),
        };
        MainWindow = window;
        window.Show();
    }
}
