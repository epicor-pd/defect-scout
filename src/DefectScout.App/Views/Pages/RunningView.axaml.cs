using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DefectScout.App.Views.Pages;

public partial class RunningView : UserControl
{
    public RunningView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
