namespace TetherBridge.Desktop.Services;
public enum CloseAction { HideToTray, BeginCleanup, WaitForCleanup, AllowClose }
public sealed class CloseLifecycle
{
    private bool inProgress;
    private bool approved;
    public CloseAction RequestClose(bool minimizeToTray)
    {
        if (approved) return CloseAction.AllowClose;
        if (inProgress) return CloseAction.WaitForCleanup;
        if (minimizeToTray) return CloseAction.HideToTray;
        inProgress = true;
        return CloseAction.BeginCleanup;
    }
    public void Complete(bool success) { inProgress = false; approved = success; }
}
