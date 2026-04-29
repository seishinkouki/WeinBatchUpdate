using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeinBatchUpdate.Models;
using WeinBatchUpdate.Services;

namespace WeinBatchUpdate.ViewModels
{
    /// <summary>
    /// Root ViewModel for the Weintek batch firmware update tool.
    ///
    /// Responsibilities:
    /// <list type="bullet">
    ///   <item>Collect target device IPs, credentials, and firmware file path from the user.</item>
    ///   <item>Orchestrate concurrent firmware updates across multiple HMI devices.</item>
    ///   <item>Track per-device progress, status, and retry capability in real time.</item>
    ///   <item>Maintain a cumulative log and a final summary report.</item>
    ///   <item>Bridge user language preference (zh-CN / en-US) to <see cref="LocalizationService"/>.</item>
    /// </list>
    ///
    /// Data flow:
    /// <code>
    ///   User input (TargetsText / UserName / Password / FirmwareFile)
    ///        │
    ///        ▼
    ///   StartCommand ──▶ RunBatchWithLimit ──▶ UpdateSingleDeviceAsync × N
    ///        │                                        │
    ///        │                               HMIUpdater.ExecuteFullUpdateAsync
    ///        │                                        │
    ///        ▼                               IProgress&lt;ProgressReport&gt;
    ///   SummaryResult ◀── AppendLog ◀── UI 线程 (Dispatcher.UIThread.Post)
    /// </code>
    ///
    /// All UI-thread marshalling uses <see cref="Dispatcher.UIThread.Post"/> to avoid
    /// blocking the background thread pool.
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        /// <summary>
        /// Characters that delimit IP addresses in <see cref="TargetsText"/>.
        /// Supports newlines, commas, and semicolons so users can paste from
        /// various sources.
        /// </summary>
        private static readonly char[] IpSeparators = { '\r', '\n', ',', ';' };

        /// <summary>
        /// Single <see cref="HttpClient"/> instance reused for all device connections.
        /// The <see cref="HttpClient"/> is designed to be shared across many requests
        /// (avoids socket exhaustion). The 20-minute timeout accommodates slow
        /// firmware uploads over unreliable network links.
        /// </summary>
        private static readonly HttpClient SharedClient;

        static MainWindowViewModel()
        {
            SharedClient = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            SharedClient.DefaultRequestHeaders.Add("User-Agent", "WeinBatchUpdate/1.0");
        }

        /// <summary>
        /// On construction the ViewModel detects the OS display language
        /// (<see cref="CultureInfo.CurrentUICulture"/>) and selects zh-CN or en-US
        /// accordingly. All other locales fall back to English.
        ///
        /// The language-change subscription ensures that existing device statuses
        /// in the DataGrid are re-translated when the user toggles the language
        /// ToggleButton at runtime.
        /// </summary>
        public MainWindowViewModel()
        {
            // Auto-detect the user's OS display language
            if (CultureInfo.CurrentUICulture.Name is "en-US" or "zh-CN")
                LocalizationService.Instance.CurrentLanguage = CultureInfo.CurrentUICulture.Name;
            else
                LocalizationService.Instance.CurrentLanguage = "en-US";

            // Sync the ToggleButton state with the detected language
            IsEnglish = LocalizationService.Instance.CurrentLanguage == "en-US";

            // Re-translate device status rows whenever the user switches language
            LocalizationService.Instance.PropertyChanged += (_, _) => RefreshDeviceStatuses();
        }

        /// <summary>
        /// Raw text from the multi-line TextBox. Each line (or comma/semicolon-
        /// separated entry) represents one target device IP address.
        /// </summary>
        [ObservableProperty]
        public partial string? TargetsText { get; set; }

        /// <summary>
        /// Admin username for the HMI web interface. Defaults to "admin".
        /// Whenever this value changes the Start button's <c>CanExecute</c>
        /// is re-evaluated so it is disabled if the field is empty.
        /// </summary>
        [ObservableProperty]
        public partial string? UserName { get; set; } = "admin";
        partial void OnUserNameChanged(string? value) =>
            (StartCommand as IAsyncRelayCommand)?.NotifyCanExecuteChanged();

        /// <summary>
        /// Admin password for the HMI web interface.
        /// Stored as a plain <c>string</c> in memory; consider <c>SecureString</c>
        /// for production deployments on untrusted machines.
        /// </summary>
        [ObservableProperty]
        public partial string? Password { get; set; }
        partial void OnPasswordChanged(string? value) =>
            (StartCommand as IAsyncRelayCommand)?.NotifyCanExecuteChanged();

        /// <summary>
        /// Full local path to the selected firmware file (<c>.exob</c>).
        /// Set by the file-picker dialog in <see cref="Views.MainWindow"/>.
        /// </summary>
        [ObservableProperty]
        public partial string? FirmwareFile { get; set; }
        partial void OnFirmwareFileChanged(string? value) =>
            (StartCommand as IAsyncRelayCommand)?.NotifyCanExecuteChanged();

        /// <summary>
        /// Maximum number of devices updated simultaneously.
        /// Controlled by a <c>NumericUpDown</c> (1–16) in the UI.
        /// Default of 5 balances throughput with device load.
        /// </summary>
        [ObservableProperty]
        public partial int MaxConcurrency { get; set; } = 5;

        /// <summary>
        /// When <c>true</c> all HTTP calls are replaced with artificial delays
        /// (800 ms–2 s). Use this for UI testing without real hardware.
        /// </summary>
        [ObservableProperty]
        public partial bool SimulationMode { get; set; }

        /// <summary>
        /// Total number of target devices parsed from <see cref="TargetsText"/>.
        /// Set when the batch starts, then remains constant.
        /// </summary>
        [ObservableProperty]
        public partial int TotalCount { get; set; }

        /// <summary>
        /// Running count of devices that have finished (success or failure).
        /// Incremented via <c>Dispatcher.UIThread.Post</c> so no data race.
        /// </summary>
        [ObservableProperty]
        public partial int CompletedCount { get; set; }

        /// <summary>
        /// Final summary report built after all devices have been processed.
        /// Includes overall success/failure counts and per-device error details.
        /// </summary>
        [ObservableProperty]
        public partial string? SummaryResult { get; set; }

        /// <summary>
        /// The DataGrid's <c>ItemsSource</c>. Each entry tracks one device's
        /// IP, live status text, progress value (0→1), and retry capability.
        /// </summary>
        [ObservableProperty]
        public partial ObservableCollection<DeviceUpdateStatus> Devices { get; set; } = new();

        /// <summary>
        /// <c>true</c> while a batch update is in flight.
        /// Disables the Start button (via <c>CanStart</c>) and prevents
        /// accidental re-entry.
        /// </summary>
        [ObservableProperty]
        public partial bool IsRunning { get; set; }
        partial void OnIsRunningChanged(bool value) =>
            (StartCommand as IAsyncRelayCommand)?.NotifyCanExecuteChanged();

        /// <summary>
        /// The log text displayed in the UI. This property is recomputed from
        /// <see cref="_logBuilder"/> on every call to <see cref="AppendLog"/>.
        /// </summary>
        [ObservableProperty]
        public partial string? Logs { get; set; }

        /// <summary>
        /// Backing buffer for the cumulative log. Using <see cref="StringBuilder"/>
        /// avoids O(n²) overhead from repeated string concatenation.
        /// </summary>
        private readonly StringBuilder _logBuilder = new();

        /// <summary>
        /// <c>true</c> when English is active; <c>false</c> for Chinese.
        /// Two-way bound to the ToggleButton's <c>IsChecked</c> in the title bar.
        /// </summary>
        [ObservableProperty]
        public partial bool IsEnglish { get; set; }

        /// <summary>
        /// Unicode flag emoji for the current language:
        /// <list type="bullet">
        ///   <item><c>IsEnglish = false</c> → 🇨🇳  Chinese</item>
        ///   <item><c>IsEnglish = true</c>  → 🇺🇸  English</item>
        /// </list>
        /// </summary>
        [ObservableProperty]
        public partial string LanguageFlag { get; set; } = "🇨🇳";

        /// <summary>
        /// Called when the user clicks the language ToggleButton.
        /// Updates the flag emoji and pushes the new locale to
        /// <see cref="LocalizationService"/>, which in turn notifies
        /// all <see cref="Extensions.LocExtension"/> bindings to refresh.
        /// </summary>
        partial void OnIsEnglishChanged(bool value)
        {
            LanguageFlag = value ? "🇺🇸" : "🇨🇳";
            LocalizationService.Instance.CurrentLanguage = value ? "en-US" : "zh-CN";
        }

        /// <summary>
        /// Bound to the Start button. Kicks off the batch update pipeline.
        ///
        /// Can only execute when <see cref="IsRunning"/> is <c>false</c> and all
        /// three required fields (<see cref="UserName"/>, <see cref="Password"/>,
        /// <see cref="FirmwareFile"/>) are non-empty and the firmware file exists
        /// on disk (see <see cref="CanStart"/>).
        ///
        /// Internally this method:
        /// <list type="number">
        ///   <item>Parses target IPs from <see cref="TargetsText"/>.</item>
        ///   <item>Sets <see cref="IsRunning"/> = <c>true</c> and resets counters.</item>
        ///   <item>Calls <see cref="RunBatchWithLimit"/> with the concurrent limit.</item>
        ///   <item>Composes <see cref="SummaryResult"/> from success/failure counts.</item>
        ///   <item>Sets <see cref="IsRunning"/> = <c>false</c>.</item>
        /// </list>
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            // ── Parse IP list ────────────────────────────────────────────
            var ips = (TargetsText?.Split(IpSeparators, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList())
                      ?? new List<string>();

            if (ips.Count == 0) return;

            // ── Reset state ──────────────────────────────────────────────
            IsRunning = true;
            TotalCount = ips.Count;
            CompletedCount = 0;
            Logs = string.Empty;
            _logBuilder.Clear();

            // ── Execute concurrent updates ────────────────────────────────
            await RunBatchWithLimit(ips, UserName, Password, FirmwareFile, MaxConcurrency);

            // ── Build summary report ─────────────────────────────────────
            var successCount = Devices.Count(d => d.StatusKey == "Success");
            var loc = LocalizationService.Instance;
            var sb = new StringBuilder();
            sb.AppendFormat(loc["SummaryFormat"], successCount, Devices.Count - successCount);
            if (Devices.Count != successCount)
            {
                sb.Append('\n').Append(loc["FailedIPs"]);
                foreach (var d in Devices.Where(d => d.StatusKey != "Success"))
                {
                    sb.Append('\n').Append(d.Ip).Append(" - ").Append(d.Status);
                    if (!string.IsNullOrEmpty(d.Message))
                        sb.Append(" (").Append(d.Message).Append(')');
                }
            }
            SummaryResult = sb.ToString();

            IsRunning = false;
        }

        /// <summary>
        /// Runs the full batch update with a <see cref="SemaphoreSlim"/>-
        /// based concurrency gate.
        ///
        /// Algorithm:
        /// <list type="number">
        ///   <item>Clear <see cref="Devices"/> and repopulate it with one
        ///       <see cref="DeviceUpdateStatus"/> per IP (initial status = "Waiting").</item>
        ///   <item>Create a <see cref="SemaphoreSlim"/> with <paramref name="maxConcurrency"/>
        ///       permits to throttle simultaneous device connections.</item>
        ///   <item>For each device, acquire a permit, call
        ///       <see cref="UpdateSingleDeviceAsync"/>, then release the permit
        ///       and post a <c>CompletedCount++</c> to the UI thread.</item>
        ///   <item>Await <see cref="Task.WhenAll"/> so the method only returns
        ///       after every device has finished (or failed).</item>
        /// </list>
        ///
        /// The semaphore pattern avoids spawning hundreds of <see cref="HttpClient"/>
        /// connections simultaneously while still saturating the configured concurrency
        /// limit.
        /// </summary>
        /// <param name="ips">List of target device IP addresses.</param>
        /// <param name="user">Admin username.</param>
        /// <param name="pass">Admin password.</param>
        /// <param name="file">Local path to the firmware <c>.exob</c> file.</param>
        /// <param name="maxConcurrency">Maximum concurrent update tasks.</param>
        public async Task RunBatchWithLimit(
            List<string> ips, string? user, string? pass, string? file, int maxConcurrency)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(file))
                return;

            // ── Create device status entries ─────────────────────────────
            Devices.Clear();
            foreach (var ip in ips)
            {
                var device = new DeviceUpdateStatus
                {
                    Ip = ip,
                    StatusKey = "Waiting",
                    CanRetry = false,
                };
                // Each device gets its own retry command that captures the device instance
                device.RetryCommand = new RelayCommand(async () => await RetryDeviceAsync(device));
                Devices.Add(device);
            }

            // ── Execute with concurrency limiter ─────────────────────────
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = Devices.Select(async device =>
            {
                await semaphore.WaitAsync();          // wait for a free slot
                try
                {
                    await UpdateSingleDeviceAsync(device, user, pass, file);
                }
                finally
                {
                    semaphore.Release();              // free the slot
                    Dispatcher.UIThread.Post(() => CompletedCount++);
                }
            });
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Updates a single device through the full firmware pipeline.
        ///
        /// Progress callbacks come from <see cref="Services.HMIUpdater"/>
        /// on an arbitrary thread pool thread. They are marshalled to the UI
        /// thread via <see cref="Dispatcher.UIThread.Post"/> (fire-and-forget)
        /// because progress updates are frequent and non-critical — a dropped
        /// frame is better than a stalled thread-pool thread.
        ///
        /// After the pipeline completes, the final status is also posted to the
        /// UI thread, where <see cref="DeviceUpdateStatus.StatusKey"/> is
        /// translated via <see cref="LocalizationService"/> and the result is
        /// appended to the log buffer.
        /// </summary>
        /// <param name="device">The device status entry to update in-place.</param>
        /// <param name="user">Admin username.</param>
        /// <param name="pass">Admin password.</param>
        /// <param name="file">Local firmware file path.</param>
        private async Task UpdateSingleDeviceAsync(
            DeviceUpdateStatus device, string? user, string? pass, string? file)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(file))
                return;

            // ── Progress callback (runs on thread pool) ─────────────────
            var progress = new Progress<HMIUpdater.ProgressReport>(report =>
            {
                // Post to UI thread — non-blocking
                Dispatcher.UIThread.Post(() =>
                {
                    if (report.Value >= 0) device.Progress = report.Value;
                    device.StatusKey = report.Status;  // key → translated via setter
                    device.Message = report.Message;
                });
            });

            // ── Execute firmware pipeline ────────────────────────────────
            var updater = new HMIUpdater(SharedClient, device.Ip, progress)
            {
                SimulationMode = SimulationMode
            };
            bool success = await updater.ExecuteFullUpdateAsync(user, pass, file);

            // ── Final status update (non-blocking post to UI) ────────────
            Dispatcher.UIThread.Post(() =>
            {
                // Preserve "Error" status so the user knows why it failed
                device.StatusKey = success
                    ? "Success"
                    : (device.StatusKey == "Error" ? device.StatusKey : "Failed");
                device.CanRetry = !success;
                AppendLog(string.Format(
                    LocalizationService.Instance["DeviceUpdateLog"],
                    device.Ip,
                    LocalizationService.Instance[device.StatusKey]));
            });
        }

        /// <summary>
        /// Called when the user clicks the Retry button for a failed device.
        /// Resets the progress bar and disables the retry button, then re-runs
        /// the full update pipeline.
        /// </summary>
        private async Task RetryDeviceAsync(DeviceUpdateStatus device)
        {
            device.CanRetry = false;
            device.Progress = 0;
            await UpdateSingleDeviceAsync(device, UserName, Password, FirmwareFile);
        }

        /// <summary>
        /// Guards the Start button's <c>CanExecute</c>:
        /// <list type="bullet">
        ///   <item>Not already running.</item>
        ///   <item><see cref="UserName"/> is non-empty.</item>
        ///   <item><see cref="Password"/> is non-empty.</item>
        ///   <item><see cref="FirmwareFile"/> is non-empty and the file exists on disk.</item>
        /// </list>
        /// <c>IAsyncRelayCommand.NotifyCanExecuteChanged</c> is called whenever
        /// any of these properties change, so the button enables/disables reactively.
        /// </summary>
        private bool CanStart() =>
            !IsRunning
            && !string.IsNullOrWhiteSpace(UserName)
            && !string.IsNullOrWhiteSpace(Password)
            && !string.IsNullOrWhiteSpace(FirmwareFile)
            && System.IO.File.Exists(FirmwareFile);

        /// <summary>
        /// Appends a line to the running log.
        ///
        /// <para>Uses <see cref="StringBuilder"/> to avoid O(n²) growth from
        /// repeated <c>string + string</c>. The <see cref="Logs"/> property is
        /// recomputed from the builder on every append so the binding updates.</para>
        ///
        /// <para>Caller must ensure this runs on the UI thread —
        /// it is always invoked from <see cref="Dispatcher.UIThread.Post"/> callbacks.</para>
        /// </summary>
        /// <param name="text">The log line to append (no trailing newline needed).</param>
        private void AppendLog(string text)
        {
            _logBuilder.AppendLine(text);
            Logs = _logBuilder.ToString();
        }

        /// <summary>
        /// Called whenever <see cref="LocalizationService.CurrentLanguage"/>
        /// changes. Iterates over all active <see cref="DeviceUpdateStatus"/>
        /// entries and re-translates their <see cref="DeviceUpdateStatus.Status"/>
        /// from the stored <see cref="DeviceUpdateStatus.StatusKey"/>.
        ///
        /// This ensures the DataGrid reflects the new language immediately
        /// without requiring a new batch run.
        /// </summary>
        private void RefreshDeviceStatuses()
        {
            foreach (var device in Devices)
                device.RefreshStatusText();
        }
    }
}
