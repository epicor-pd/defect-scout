using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DefectScout.App.Views.Pages;

public partial class StepReviewView : UserControl
{
    public StepReviewView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
