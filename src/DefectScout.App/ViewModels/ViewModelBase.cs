using CommunityToolkit.Mvvm.ComponentModel;

namespace DefectScout.App.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    public abstract string PageTitle { get; }
}
