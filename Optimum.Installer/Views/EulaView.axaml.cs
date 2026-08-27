using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Optimum.Installer.ViewModels;

namespace Optimum.Installer.Views;

public partial class EulaView : UserControl
{
    public EulaView() => AvaloniaXamlLoader.Load(this);

    // ScrollChanged fires for extent, viewport, and offset changes, so it also
    // fires once when layout gives the ScrollViewer its real extent. That covers
    // both "the notice fits" and "scrolled to the bottom" without hooking
    // LayoutUpdated, which loops.
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller || DataContext is not EulaViewModel vm)
            return;

        if (ScrollReadGate.ReadToEnd(scroller.Extent.Height, scroller.Viewport.Height, scroller.Offset.Y))
            vm.ScrolledToEnd = true;
    }
}
