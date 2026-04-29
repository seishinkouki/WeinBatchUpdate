using CommunityToolkit.Mvvm.ComponentModel;

namespace WeinBatchUpdate.ViewModels
{
    /// <summary>
    /// Base class for all ViewModels in this application.
    /// Inherits from CommunityToolkit.Mvvm's <see cref="ObservableObject"/>
    /// which provides <see cref="INotifyPropertyChanged"/> support and
    /// source-generator-based <c>[ObservableProperty]</c> and <c>[RelayCommand]</c>.
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
    {
    }
}
