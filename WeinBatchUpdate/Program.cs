using System;
using Avalonia;

namespace WeinBatchUpdate
{
    /// <summary>
    /// Entry point for the desktop application.
    /// Initializes the Avalonia platform, configures the application,
    /// and launches the classic desktop lifetime.
    /// </summary>
    /// <remarks>
    /// Do not use Avalonia APIs or any SynchronizationContext-reliant code
    /// before <c>BuildAvaloniaApp()</c> returns — the framework is not yet initialized.
    /// </remarks>
    internal sealed class Program
    {
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        /// <summary>
        /// Configures the Avalonia application builder with platform detection,
        /// developer tools (Debug only), the Inter font, and trace logging.
        /// </summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }
}
