using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Optimum.Installer.Views;

public partial class MainWindow : Window
{
    private const double CompactLayoutBreakpoint = 760;

    public MainWindow() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        var compact = e.NewSize.Width < CompactLayoutBreakpoint;
        RootLayout.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(200);
        CurrentStepTitle.FontSize = compact ? 22 : 28;
        ContentLayout.Margin = compact
            ? new Thickness(12)
            : new Thickness(28, 20, 28, 20);
    }
}
