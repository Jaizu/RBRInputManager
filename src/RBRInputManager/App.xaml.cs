using System.Windows;
using RBRInputManager.Core;

namespace RBRInputManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Start();
        Log.Info(LogArea.General, $"RBR Input Manager {UpdateService.CurrentVersion.ToString(3)} started");
        // Crashes still close the app as before, but leave their cause in the log. An error on the UI thread
        // reaches both handlers; it is logged once.
        Exception? crash = null;
        DispatcherUnhandledException += (_, args) =>
        {
            crash = args.Exception;
            Log.Error(LogArea.General, "Unexpected error, the app has to close", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex && ex != crash)
                Log.Error(LogArea.General, "Unexpected error, the app has to close", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error(LogArea.General, "Unexpected error in a background task", args.Exception);

        UpdateService.CleanupAfterUpdate();
        TooltipPlacement.Register();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Stop();
        base.OnExit(e);
    }
}
