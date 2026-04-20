namespace NeoSwitch.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
        catch (Exception ex)
        {
            // Without this, a crash in the ctor silently exits with no UI.
            try
            {
                MessageBox.Show(
                    ex.ToString(),
                    "NeoSwitch — fatal error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { }
            Environment.ExitCode = 1;
        }
    }
}
