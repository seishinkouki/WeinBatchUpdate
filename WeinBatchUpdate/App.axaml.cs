using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using WeinBatchUpdate.Services;
using WeinBatchUpdate.ViewModels;
using WeinBatchUpdate.Views;

namespace WeinBatchUpdate
{
    /// <summary>
    /// Application entry-point class.
    /// Initializes Avalonia framework, loads the main window,
    /// and sets up Semi.Avalonia theme locale synchronization with <see cref="LocalizationService"/>.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Reference to the Semi.Avalonia theme for live locale updates.
        /// </summary>
        private Semi.Avalonia.SemiTheme? _semiTheme;

        /// <summary>
        /// Called once during startup. Loads XAML resources and captures
        /// the SemiTheme style for later locale synchronization.
        /// </summary>
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            _semiTheme = Styles.OfType<Semi.Avalonia.SemiTheme>().FirstOrDefault();
        }

        /// <summary>
        /// Called after the framework is fully initialized.
        /// Creates the main window with its ViewModel and wires up
        /// the language-change handler that keeps the SemiTheme locale in sync.
        /// </summary>
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };
            }

            // When the user switches language, update Semi.Avalonia's built-in control strings
            LocalizationService.Instance.PropertyChanged += (_, _) =>
            {
                if (_semiTheme != null)
                    _semiTheme.Locale = new CultureInfo(LocalizationService.Instance.CurrentLanguage);
            };

            // Apply the initial locale
            if (_semiTheme != null)
                _semiTheme.Locale = new CultureInfo(LocalizationService.Instance.CurrentLanguage);

            base.OnFrameworkInitializationCompleted();
        }
    }
}
