using System;
using System.Windows.Forms;

namespace TetherBridge.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new System.Threading.Mutex(true, @"Global\TetherBridge-" + identity, out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("TetherBridge is already running for this Windows user.", "TetherBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
