using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Optimum.Installer.ViewModels;

namespace Optimum.Installer.Views;

public partial class EulaView : UserControl
{
    public EulaView() => AvaloniaXamlLoader.Load(this);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller || DataContext is not EulaViewModel vm)
            return;

        // Treat a viewport that shows all the text, or a scroll within a few
        // pixels of the bottom, as "read to the end".
        bool atEnd = scroller.Extent.Height <= scroller.Viewport.Height + 1
            || scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 4;
        if (atEnd)
            vm.ScrolledToEnd = true;
    }
}
