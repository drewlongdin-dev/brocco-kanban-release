namespace BroccoKanban;

/// <summary>Entry point and application host for BroccoKanban.</summary>
internal static class Program
{
    [STAThread]
    /// <summary>Initialises the WinForms runtime and launches <see cref="MainForm"/>.</summary>
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Icons.Load();
        string? filePath = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
        Application.Run(new MainForm(filePath));
    }
}
