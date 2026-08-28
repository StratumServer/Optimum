using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Optimum.Installer.ViewModels;

namespace Optimum.Installer.Views;

public partial class ProgressView : UserControl
{
    private INotifyCollectionChanged? _log;

    public ProgressView() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_log is not null)
            _log.CollectionChanged -= OnLogChanged;

        _log = (DataContext as ProgressViewModel)?.Log;
        if (_log is not null)
            _log.CollectionChanged += OnLogChanged;
    }

    // Keep the newest build output in view without the user chasing it.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        this.FindControl<ScrollViewer>("LogScroller")?.ScrollToEnd();
}
