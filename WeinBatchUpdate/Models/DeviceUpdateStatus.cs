using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WeinBatchUpdate.Services;
using WeinBatchUpdate.ViewModels;

namespace WeinBatchUpdate.Models
{
    /// <summary>
    /// Represents the live status of a single target device during a batch update.
    /// Each instance is displayed as a row in the DataGrid.
    /// </summary>
    public partial class DeviceUpdateStatus : ViewModelBase
    {
        /// <summary>Internal localization key (e.g. "Waiting", "GetPublicKey", "Success").</summary>
        private string? _statusKey;

        /// <summary>
        /// Localization key for the current status.
        /// Setting this automatically translates <see cref="Status"/> via
        /// <see cref="LocalizationService"/>.
        /// </summary>
        public string? StatusKey
        {
            get => _statusKey;
            set
            {
                _statusKey = value;
                Status = value != null ? LocalizationService.Instance[value] : null;
            }
        }

        /// <summary>Target device IP address (and optional port).</summary>
        public string? Ip { get; set; }

        /// <summary>Human-readable status text bound to the DataGrid Status column.</summary>
        [ObservableProperty]
        public partial string? Status { get; set; }

        /// <summary>Whether this device can be retried (set to <c>true</c> on failure).</summary>
        [ObservableProperty]
        public partial bool CanRetry { get; set; }

        /// <summary>Progress from 0.0 to 1.0, bound to the ProgressBar.</summary>
        [ObservableProperty]
        public partial double Progress { get; set; }

        /// <summary>Optional detail message shown in a tooltip (e.g. error description).</summary>
        [ObservableProperty]
        public partial string? Message { get; set; }

        /// <summary>Command bound to the Retry button in the DataGrid Action column.</summary>
        public ICommand? RetryCommand { get; set; }

        /// <summary>
        /// Re-translates <see cref="Status"/> from <see cref="StatusKey"/>
        /// when the UI language changes. Called by <see cref="MainWindowViewModel.RefreshDeviceStatuses"/>.
        /// </summary>
        public void RefreshStatusText()
        {
            if (StatusKey != null)
                Status = LocalizationService.Instance[StatusKey];
        }
    }
}
