using System.Windows;

namespace SalesSupport.Client;

public partial class MainWindow : Window
{
    private TranscriptWindow? _transcript;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        // The ask-lane chat follows its newest message, like any messaging thread.
        vm.ChatChanged += () => Dispatcher.BeginInvoke(ChatScroller.ScrollToEnd);
        // Title bar follows the theme (D35).
        NativeChrome.Track(this);
    }

    private void ToggleTranscript(object sender, RoutedEventArgs e)
    {
        if (_transcript is not null)
        {
            _transcript.Close();
            return;
        }
        _transcript = new TranscriptWindow { Owner = this, DataContext = DataContext };
        _transcript.Closed += (_, _) => _transcript = null;
        _transcript.Left = Left >= _transcript.Width + 16 ? Left - _transcript.Width - 8 : Left + ActualWidth + 8;
        _transcript.Top = Top;
        _transcript.Show();
    }
}
