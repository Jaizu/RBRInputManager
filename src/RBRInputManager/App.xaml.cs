using System.Windows;
using RBRInputManager.Core;

namespace RBRInputManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        UpdateService.CleanupAfterUpdate();
        TooltipPlacement.Register();
        base.OnStartup(e);
    }
}
