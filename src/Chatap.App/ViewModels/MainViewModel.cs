using CommunityToolkit.Mvvm.ComponentModel;

namespace Chatap.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Welcome to Avalonia!";
}
