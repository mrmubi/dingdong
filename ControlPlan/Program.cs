using System.Threading;

namespace ControlPlan;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-instance guard. Use a global (Local\) named mutex so a
        // second launch from the same user session is rejected cleanly.
        using var mutex = new Mutex(initiallyOwned: true, name: "Local\\DingDong.ControlPlan.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "ControlPlan is already running. Check the system tray or Task Manager.",
                "ControlPlan",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
    }
}
