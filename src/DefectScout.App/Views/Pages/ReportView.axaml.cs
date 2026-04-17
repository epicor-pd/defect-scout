using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DefectScout.App.Views.Pages;

public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
