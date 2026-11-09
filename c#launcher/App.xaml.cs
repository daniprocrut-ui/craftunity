using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace CraftunityLauncher;

public partial class App : Application
{
    private const string SingleInstanceName = @"Global\CraftunityLauncher-7C5A2D9F";
    private static Mutex? _sharedMutex;
    private Mutex? _singleInstance;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static void ReleaseInstanceLock()
    {
        try
        {
            _sharedMutex?.ReleaseMutex();
        }
        catch
        {
            // мьютекс мог быть уже освобождён
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirst);
        _sharedMutex = _singleInstance;
        if (!isFirst)
        {
            if (!BringExistingToFront())
            {
                MessageBox.Show(
                    "Craftunity Launcher уже запущен.\n\nЗакройте его и откройте снова.",
                    "Craftunity",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
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
        ReleaseInstanceLock();
        _singleInstance?.Dispose();
        _sharedMutex = null;
        base.OnExit(e);
    }

    private static bool BringExistingToFront()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("CraftunityLauncher"))
            {
                if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                SetForegroundWindow(process.MainWindowHandle);
                return true;
            }
        }
        catch
        {
            // окно уже закрывается
        }
        return false;
    }
}
