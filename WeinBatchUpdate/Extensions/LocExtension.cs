using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using WeinBatchUpdate.Services;

namespace WeinBatchUpdate.Extensions
{
    /// <summary>
    /// XAML markup extension that provides localized strings via <c>{ext:Loc Key=...}</c>.
    /// Subscribes to <see cref="LocalizationService.PropertyChanged"/> so that text
    /// automically updates when the user switches languages at runtime.
    /// </summary>
    /// <remarks>
    /// Usage in XAML:
    /// <code>
    ///   xmlns:ext="using:WeinBatchUpdate.Extensions"
    ///   Text="{ext:Loc Key=TargetDevices}"
    ///   Content="{ext:Loc Key=Start}"
    /// </code>
    ///
    /// <para>
    /// The extension returns the initial translation from <see cref="LocalizationService"/>,
    /// then registers a <see cref="INotifyPropertyChanged"/> handler that calls
    /// <see cref="AvaloniaObject.SetValue"/> directly on the target property whenever
    /// the current language changes.
    /// </para>
    ///
    /// <para>
    /// To avoid memory leaks, the handler is automatically unsubscribed when the host
    /// control is detached from the visual tree (<see cref="Controls.Control.DetachedFromVisualTree"/>).
    /// </para>
    /// </remarks>
    public class LocExtension : MarkupExtension
    {
        /// <summary>
        /// Resource key identifying the localized string (e.g. "TargetDevices", "Start").
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Parameterless constructor required by the XAML parser.
        /// </summary>
        public LocExtension() { }

        /// <summary>
        /// Convenience constructor for the <c>LocExtension(key)</c> syntax in XAML.
        /// </summary>
        public LocExtension(string key)
        {
            Key = key;
        }

        /// <summary>
        /// Avalonia calls this method when the markup extension is applied to a target property.
        /// Returns the initial localized value and sets up a property-change listener for live updates.
        /// </summary>
        /// <param name="serviceProvider">XAML service provider that provides target object and property metadata.</param>
        /// <returns>The translated string in the current language.</returns>
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            // Resolve the target Avalonia object and property being set
            var target = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
            if (target?.TargetObject is AvaloniaObject avaloniaObject && target?.TargetProperty is AvaloniaProperty avaloniaProperty)
            {
                // Callback that updates the target property when language changes
                PropertyChangedEventHandler handler = (_, e) =>
                {
                    if (e.PropertyName == "Item[]" || e.PropertyName == "CurrentLanguage")
                    {
                        avaloniaObject.SetValue(avaloniaProperty, LocalizationService.Instance[Key]);
                    }
                };

                // Subscribe to language-change notifications
                LocalizationService.Instance.PropertyChanged += handler;

                // Unsubscribe when the control is removed from the visual tree to prevent leaks
                if (avaloniaObject is Avalonia.Controls.Control control)
                {
                    control.DetachedFromVisualTree += (_, _) =>
                    {
                        LocalizationService.Instance.PropertyChanged -= handler;
                    };
                }
            }

            // Return the initial translation
            return LocalizationService.Instance[Key];
        }
    }
}
