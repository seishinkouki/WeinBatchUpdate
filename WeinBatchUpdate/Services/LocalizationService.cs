using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WeinBatchUpdate.Services
{
    /// <summary>
    /// Thread-safe singleton service providing Chinese (zh-CN) and English (en-US) localization.
    /// Implements <see cref="INotifyPropertyChanged"/> so that UI elements bound via <see cref="Extensions.LocExtension"/>
    /// automatically update when the current language changes.
    /// </summary>
    /// <remarks>
    /// Usage:
    /// <code>
    ///   // In C# code:
    ///   var text = LocalizationService.Instance["TargetDevices"];
    ///
    ///   // In XAML via MarkupExtension:
    ///   Text="{ext:Loc Key=TargetDevices}"
    ///
    ///   // Switch language:
    ///   LocalizationService.Instance.CurrentLanguage = "en-US";
    /// </code>
    /// Adding a new language requires only a new dictionary entry in <see cref="_resources"/>.
    /// </remarks>
    public sealed class LocalizationService : INotifyPropertyChanged
    {
        /// <summary>
        /// Single shared instance of the localization service.
        /// </summary>
        public static LocalizationService Instance { get; } = new();

        /// <summary>
        /// Nested dictionary holding all translatable strings.
        /// Outer key: language code (e.g. "zh-CN", "en-US").
        /// Inner key: resource key (e.g. "TargetDevices", "Start").
        /// Value: translated string for the given language.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, string>> _resources = new()
        {
            // ── Chinese (Simplified) translations ──
            ["zh-CN"] = new()
            {
                ["WeinBatchUpdate"] = "威纶通批量升级工具",
                ["TargetDevices"] = "目标设备（每行一个，IP:PORT）",
                ["Username"] = "用户名:",
                ["Password"] = "密码:",
                ["FirmwareFile"] = "固件文件:",
                ["Browse"] = "浏览...",
                ["MaxConcurrency"] = "最大并发:",
                ["Start"] = "开始",
                ["IP"] = "IP",
                ["Status"] = "状态",
                ["Progress"] = "进度",
                ["Action"] = "操作",
                ["Retry"] = "重试",
                ["Total"] = "总数:",
                ["Completed"] = "已完成:",
                ["Language"] = "语言:",
                ["FirmwareFileName"] = "威纶通固件",
                ["OpenFileTitle"] = "选择固件文件",
                ["Waiting"] = "等待中",
                ["Success"] = "成功",
                ["Failed"] = "失败",
                ["Error"] = "异常",
                ["SummaryFormat"] = "完成！成功:{0} 失败:{1}",
                ["FailedIPs"] = "失败ip：",
                ["DeviceUpdateLog"] = "设备 {0} {1}",
                ["NoTargetDevices"] = "未检测到目标设备。",
                ["ParameterError"] = "参数错误",
                ["GetPublicKey"] = "获取公钥",
                ["LoginDevice"] = "登录设备",
                ["PrepareEnv"] = "准备环境",
                ["UploadFirmware"] = "上传固件",
                ["DecompressFirmware"] = "解压固件",
                ["RestartDevice"] = "重启设备",
                ["RestoreService"] = "恢复服务",
                ["CheckStatus"] = "检查状态",
                ["Complete"] = "完成",
                ["ConnectionTimeout"] = "连接超时",
                ["InvalidPublicKeyResponse"] = "无效的公钥响应",
                ["CredentialsEmpty"] = "用户名、密码或公钥不能为空",
                ["FilePathEmpty"] = "文件路径不能为空",
                ["FirmwareNotExist"] = "固件文件不存在",
                ["DeviceCommandError"] = "设备返回指令错误",
                ["TextOrKeyEmpty"] = "文本或公钥不能为空",
            },

            // ── English (United States) translations ──
            ["en-US"] = new()
            {
                ["WeinBatchUpdate"] = "Weintek Batch Update Tool",
                ["TargetDevices"] = "Target Devices (one per line, IP:PORT)",
                ["Username"] = "Username:",
                ["Password"] = "Password:",
                ["FirmwareFile"] = "Firmware File:",
                ["Browse"] = "Browse...",
                ["MaxConcurrency"] = "Max Concurrency:",
                ["Start"] = "Start",
                ["IP"] = "IP",
                ["Status"] = "Status",
                ["Progress"] = "Progress",
                ["Action"] = "Action",
                ["Retry"] = "Retry",
                ["Total"] = "Total:",
                ["Completed"] = "Completed:",
                ["Language"] = "Language:",
                ["FirmwareFileName"] = "Weintek Firmware",
                ["OpenFileTitle"] = "Select Firmware File",
                ["Waiting"] = "Waiting",
                ["Success"] = "Success",
                ["Failed"] = "Failed",
                ["Error"] = "Error",
                ["SummaryFormat"] = "Done! Success:{0} Failed:{1}",
                ["FailedIPs"] = "Failed IPs:",
                ["DeviceUpdateLog"] = "Device {0} {1}",
                ["NoTargetDevices"] = "No target devices detected.",
                ["ParameterError"] = "Parameter error",
                ["GetPublicKey"] = "Get public key",
                ["LoginDevice"] = "Login device",
                ["PrepareEnv"] = "Prepare environment",
                ["UploadFirmware"] = "Upload firmware",
                ["DecompressFirmware"] = "Decompress firmware",
                ["RestartDevice"] = "Restart device",
                ["RestoreService"] = "Restore service",
                ["CheckStatus"] = "Check status",
                ["Complete"] = "Complete",
                ["ConnectionTimeout"] = "Connection timeout",
                ["InvalidPublicKeyResponse"] = "Invalid public key response",
                ["CredentialsEmpty"] = "Username, password or public key cannot be empty",
                ["FilePathEmpty"] = "File path cannot be empty",
                ["FirmwareNotExist"] = "Firmware file does not exist",
                ["DeviceCommandError"] = "Device returned command error",
                ["TextOrKeyEmpty"] = "Text or key cannot be empty",
            },
        };

        /// <summary>
        /// Backing field for <see cref="CurrentLanguage"/>. Defaults to Chinese ("zh-CN").
        /// </summary>
        private string _currentLanguage = "zh-CN";

        /// <summary>
        /// Gets or sets the active language code.
        /// Raises <see cref="PropertyChanged"/> for both <c>CurrentLanguage</c>
        /// and <c>Item[]</c> (the indexer binding path) so that all UI bindings refresh.
        /// </summary>
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage == value) return;
                _currentLanguage = value;
                OnPropertyChanged();
                OnPropertyChanged("Item[]");
            }
        }

        /// <summary>
        /// Indexer that returns the translated string for the given key in the current language.
        /// Falls back to English if the current language lacks the key; returns the key itself
        /// as a last resort (making missing keys visible in the UI).
        /// </summary>
        /// <param name="key">Resource key (e.g. "TargetDevices", "Start").</param>
        public string this[string key]
        {
            get
            {
                if (_resources.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out var value))
                    return value;
                if (_resources["en-US"].TryGetValue(key, out var fallback))
                    return fallback;
                return key;
            }
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Private constructor to enforce the singleton pattern.
        /// </summary>
        private LocalizationService() { }
    }
}
