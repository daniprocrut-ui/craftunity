using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace CraftunityLauncher;

public partial class App : Application
{
    private const string SingleInstanceName = @"Global\CraftunityLauncher-7C5A2D9F";
    private Mutex? _singleInstance;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirst);
        if (!isFirst)
        {
            BringExistingToFront();
            Shutdown();
            return;
        }
        var area = SystemParameters.WorkArea;
        var window = new MainWindow
        {
            Width = Math.Min(1180, area.Width - 40),
            Height = Math.Min(760, area.Height - 40),
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch
        {
            // мьютекс мог быть уже освобождён
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void BringExistingToFront()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("CraftunityLauncher"))
            {
                if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                SetForegroundWindow(process.MainWindowHandle);
                return;
            }
        }
        catch
        {
            // окно уже закрывается
        }
    }
}
