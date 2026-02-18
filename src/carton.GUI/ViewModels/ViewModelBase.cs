using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.GUI.Models;

namespace carton.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
}

public abstract partial class PageViewModelBase : ViewModelBase
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    public abstract NavigationPage PageType { get; }
}
