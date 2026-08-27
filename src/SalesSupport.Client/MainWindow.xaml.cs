using System.Windows;

namespace SalesSupport.Client;

public partial class MainWindow : Window
{
    private TranscriptWindow? _transcript;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
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
