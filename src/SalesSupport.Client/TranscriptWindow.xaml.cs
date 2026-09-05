using System.Collections.Specialized;
using System.Windows;

namespace SalesSupport.Client;

/// <summary>
/// Optional live transcript log — every merged utterance the backend confirmed, plus
/// typed asks, with the in-flight partial at the bottom. Follows the tail unless the
/// user has scrolled up to read back.
/// </summary>
public partial class TranscriptWindow : Window
{
    public TranscriptWindow()
    {
        InitializeComponent();
        NativeChrome.Track(this);
        Loaded += (_, _) =>
        {
            Scroller.ScrollToEnd();
            if (DataContext is MainViewModel vm)
            {
                vm.TranscriptLog.CollectionChanged += OnLogChanged;
                Closed += (_, _) => vm.TranscriptLog.CollectionChanged -= OnLogChanged;
            }
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Scroller.VerticalOffset >= Scroller.ScrollableHeight - 48)
            Scroller.ScrollToEnd();
    }
}
