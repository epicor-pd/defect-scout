using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DefectScout.App.Views.Pages;

public partial class WelcomeView : UserControl
{
    public WelcomeView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
