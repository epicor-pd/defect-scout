using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DefectScout.App.Views.Pages;

public partial class TicketInputView : UserControl
{
    public TicketInputView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
