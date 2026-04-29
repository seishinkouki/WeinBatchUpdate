using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WeinBatchUpdate.Services;
using WeinBatchUpdate.ViewModels;

namespace WeinBatchUpdate.Views
{
    /// <summary>
    /// Code-behind for the main application window.
    /// Handles window dragging in the custom title bar and file-picker dialog.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Clamp the window maximum height to the current screen height,
            // preventing the DataGrid from causing the window to extend beyond
            // the screen when there are many devices.
            var currentScreen = this.Screens.ScreenFromVisual(this);
            if (currentScreen != null)
            {
                this.MaxHeight = currentScreen.Bounds.Height / currentScreen.Scaling - 64;
            }
        }

        /// <summary>
        /// Enables native window dragging from the custom title-bar area.
        /// Triggered by left-click on the transparent <c>Border</c> at the top of the window.
        /// </summary>
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        /// <summary>
        /// Opens the OS file-picker dialog for selecting a Weintek firmware file (<c>.exob</c>).
        /// The selected file path is written to <see cref="MainWindowViewModel.FirmwareFile"/>.
        /// </summary>
        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = LocalizationService.Instance["OpenFileTitle"],
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new(LocalizationService.Instance["FirmwareFileName"])
                        {
                            Patterns = ["*.exob"]
                        },
                    }
                });

                if (files.Count >= 1)
                {
                    if (DataContext is not MainWindowViewModel vm) return;
                    vm.FirmwareFile = files[0].Path.LocalPath;
                }
            }
            catch (Exception ex)
            {
                // File picker errors are non-critical; log and continue
                System.Diagnostics.Debug.WriteLine($"BrowseButton_Click error: {ex.Message}");
            }
        }
    }
}
