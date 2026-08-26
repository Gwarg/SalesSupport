using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using SalesSupport.Capture;
using SalesSupport.Core.Model;
using SalesSupport.Transcription.Azure;

namespace SalesSupport.Client;

/// <summary>
/// The panel's state machine (docs/panel.md): PreCall → Live → PostCall. One view model,
/// three views switched on Stage. All hub/audio callbacks are marshalled to the UI thread.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly CallClient _client = new();
    private AudioSession? _audio;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _callStarted;

    public MainViewModel()
    {
        StartCommand = new RelayCommand(async () => await StartCallAsync());
        EndCommand = new RelayCommand(async () => await EndCallAsync());
        AskCommand = new RelayCommand(async () => await AskAsync());
        CopyCommand = new RelayCommand(CopySummary);
        NewCallCommand = new RelayCommand(ResetToPreCall);

        Microphones = new ObservableCollection<AudioDeviceInfo>(AudioDevices.ListMicrophones());
        Speakers = new ObservableCollection<AudioDeviceInfo>(AudioDevices.ListSpeakers());
        SelectedMicrophone = Microphones.FirstOrDefault(d => d.DefaultCommunications) ?? Microphones.FirstOrDefault();
        SelectedSpeaker = Speakers.FirstOrDefault(d => d.DefaultCommunications) ?? Speakers.FirstOrDefault();

        _client.PictureUpdated += p => UI(() => ApplyPicture(p));
        _client.PanelDeltaReceived += d => UI(() => ApplyPanelDelta(d));
        _client.TickCompleted += s => UI(() => ApplyTick(s));
        _client.AnswerReady += a => UI(() => { AnswerText = a.Answer; ApplyPanelDelta(a.PanelDelta); });
        _client.SummaryReady += s => UI(() => ShowSummary(s.Summary));
        _client.TickFailed += m => UI(() => ErrorBanner = $"Tick misslyckades: {m}");
        _client.ConnectionClosed += reason => UI(() =>
        {
            if (Stage == "Live") ErrorBanner = $"Anslutningen tappades{(reason is null ? "" : $": {reason}")}";
        });

        _meterTimer.Tick += (_, _) =>
        {
            MicLevel = (_audio?.MicPeak ?? 0) * 100;
            SpeakerLevel = (_audio?.SpeakerPeak ?? 0) * 100;
        };
        _clockTimer.Tick += (_, _) => Duration = (DateTime.UtcNow - _callStarted).ToString(@"mm\:ss");
    }

    // Stage machine
    private string _stage = "PreCall";
    public string Stage { get => _stage; set => Set(ref _stage, value); }

    // Pre-call
    public ObservableCollection<AudioDeviceInfo> Microphones { get; }
    public ObservableCollection<AudioDeviceInfo> Speakers { get; }
    private AudioDeviceInfo? _selectedMicrophone;
    public AudioDeviceInfo? SelectedMicrophone { get => _selectedMicrophone; set => Set(ref _selectedMicrophone, value); }
    private AudioDeviceInfo? _selectedSpeaker;
    public AudioDeviceInfo? SelectedSpeaker { get => _selectedSpeaker; set => Set(ref _selectedSpeaker, value); }
    public string[] Languages { get; } = ["sv", "en"];
    private string _selectedLanguage = "sv";
    public string SelectedLanguage { get => _selectedLanguage; set => Set(ref _selectedLanguage, value); }
    private string _backendUrl = "http://localhost:5155";
    public string BackendUrl { get => _backendUrl; set => Set(ref _backendUrl, value); }
    private string _customerCompany = "";
    public string CustomerCompany { get => _customerCompany; set => Set(ref _customerCompany, value); }
    private string _goal = "";
    public string Goal { get => _goal; set => Set(ref _goal, value); }
    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    // Live
    public ObservableCollection<QuestionVm> Questions { get; } = [];
    public ObservableCollection<ProductVm> Products { get; } = [];
    public ObservableCollection<ThreadVm> Threads { get; } = [];
    public ObservableCollection<string> Facts { get; } = [];
    public ObservableCollection<string> ActionItems { get; } = [];
    private string _companyLine = "";
    public string CompanyLine { get => _companyLine; set => Set(ref _companyLine, value); }
    private string _liveLine = "";
    public string LiveLine { get => _liveLine; set => Set(ref _liveLine, value); }
    private string _duration = "00:00";
    public string Duration { get => _duration; set => Set(ref _duration, value); }
    private double _micLevel;
    public double MicLevel { get => _micLevel; set => Set(ref _micLevel, value); }
    private double _speakerLevel;
    public double SpeakerLevel { get => _speakerLevel; set => Set(ref _speakerLevel, value); }
    private string _lastTiming = "";
    public string LastTiming { get => _lastTiming; set => Set(ref _lastTiming, value); }
    private bool _isThinking;
    public bool IsThinking { get => _isThinking; set => Set(ref _isThinking, value); }
    private string _askText = "";
    public string AskText { get => _askText; set => Set(ref _askText, value); }
    private string _answerText = "";
    public string AnswerText { get => _answerText; set => Set(ref _answerText, value); }
    private string _errorBanner = "";
    public string ErrorBanner { get => _errorBanner; set => Set(ref _errorBanner, value); }
    private bool _isEnding;
    public bool IsEnding { get => _isEnding; set => Set(ref _isEnding, value); }
    private string _endingNotice = "";
    public string EndingNotice { get => _endingNotice; set => Set(ref _endingNotice, value); }

    // Post-call
    private string _summaryText = "";
    public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }
    public ObservableCollection<string> NextSteps { get; } = [];

    public RelayCommand StartCommand { get; }
    public RelayCommand EndCommand { get; }
    public RelayCommand AskCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand NewCallCommand { get; }

    private async Task StartCallAsync()
    {
        try
        {
            StatusText = "Ansluter…";
            await _client.ConnectAsync(BackendUrl);

            StatusText = "Startar samtal…";
            var started = await _client.StartCallAsync(new StartCallRequest(
                SelectedLanguage,
                string.IsNullOrWhiteSpace(CustomerCompany) ? null : CustomerCompany,
                string.IsNullOrWhiteSpace(Goal) ? null : Goal));

            AzureSpeechEngineOptions sttOptions;
            if (started.Stt is { } stt)
            {
                sttOptions = AzureSpeechEngineOptions.FromToken(stt.Token, stt.Region);
            }
            else
            {
                StatusText = "Backend saknar STT-nyckel — provar lokal AZURE_SPEECH_KEY…";
                sttOptions = AzureSpeechEngineOptions.FromEnvironment();
            }

            ApplyPicture(started.Picture);

            _audio = AudioSession.Start(
                SelectedMicrophone?.Name, SelectedSpeaker?.Name,
                sttOptions, started.Language, started.PhraseHints,
                onPartial: (speaker, text) => UI(() =>
                    LiveLine = $"~ [{speaker.ToString().ToLowerInvariant()}] {text}"),
                onFinal: utterance =>
                {
                    UI(() => { LiveLine = ""; IsThinking = true; });
                    return _client.SendUtteranceAsync(new UtteranceIn(utterance.Speaker, utterance.Text, utterance.TimestampMs));
                },
                onError: message => UI(() => ErrorBanner = message));

            _callStarted = DateTime.UtcNow;
            ErrorBanner = "";
            AnswerText = "";
            Stage = "Live";
            _meterTimer.Start();
            _clockTimer.Start();
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"Kunde inte starta: {ex.Message}";
        }
    }

    private async Task EndCallAsync()
    {
        if (IsEnding) return;
        try
        {
            IsEnding = true;
            EndingNotice = "Avslutar — hoppar över kön och skriver sammanfattning…";
            _audio?.Stop();
            _meterTimer.Stop();
            _clockTimer.Stop();
            LiveLine = "";
            await _client.EndCallAsync();
        }
        catch (Exception ex)
        {
            ErrorBanner = $"Kunde inte avsluta: {ex.Message}";
            IsEnding = false;
            EndingNotice = "";
        }
    }

    private async Task AskAsync()
    {
        var query = AskText.Trim();
        if (query.Length == 0) return;
        AskText = "";
        AnswerText = "…";
        try
        {
            await _client.AskAsync(query);
        }
        catch (Exception ex)
        {
            AnswerText = $"Fel: {ex.Message}";
        }
    }

    private void ApplyPicture(CustomerPicture picture)
    {
        CompanyLine = picture.Company is { } company
            ? string.Join(" · ", new[] { company.Name, company.Industry, company.SizeHint }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : "";

        Threads.Clear();
        foreach (var thread in picture.Threads.OrderBy(t => t.Status).ThenByDescending(t => t.Salience))
            Threads.Add(new ThreadVm(thread.Topic, thread.Kind.ToString(), thread.Status.ToString()));

        Facts.Clear();
        foreach (var fact in picture.Facts.OrderByDescending(f => f.Turn).Take(4))
            Facts.Add(fact.Text);

        ActionItems.Clear();
        foreach (var action in picture.ActionItems)
            ActionItems.Add(action.Text);
    }

    private void ApplyPanelDelta(PanelDelta delta)
    {
        foreach (var id in delta.RemovedQuestionIds)
        {
            var question = Questions.FirstOrDefault(q => q.Id == id);
            if (question is not null) Questions.Remove(question);
        }
        foreach (var added in delta.AddedQuestions)
        {
            var vm = new QuestionVm { Id = added.Id, Text = added.Text, Thread = added.ThreadId, Fresh = true };
            Questions.Add(vm);
            DecayFresh(() => vm.Fresh = false);
        }
        foreach (var id in delta.RemovedProductIds)
        {
            var product = Products.FirstOrDefault(p => p.Id == id);
            if (product is not null) Products.Remove(product);
        }
        foreach (var added in delta.AddedProducts)
        {
            var vm = new ProductVm { Id = added.Id, Name = added.DisplayName, Why = added.Why, Price = added.PriceNote, Thread = added.ThreadId, Fresh = true };
            Products.Add(vm);
            DecayFresh(() => vm.Fresh = false);
        }
    }

    private void ApplyTick(TickStats stats)
    {
        IsThinking = false;
        foreach (var id in stats.QuestionsAddressed)
        {
            var question = Questions.FirstOrDefault(q => q.Id == id);
            if (question is not null) question.Asked = true;
        }
        var queue = stats.QueueMs > 500 ? $"kö {stats.QueueMs / 1000.0:F1}s + " : "";
        LastTiming = stats.AdvisorRan
            ? $"{queue}gate {stats.GateMs / 1000.0:F1}s + advisor {stats.AdvisorMs / 1000.0:F1}s"
            : $"{queue}gate {stats.GateMs / 1000.0:F1}s";
    }

    private void ShowSummary(SummaryResult summary)
    {
        IsEnding = false;
        EndingNotice = "";
        SummaryText = summary.Summary;
        NextSteps.Clear();
        foreach (var step in summary.NextSteps)
            NextSteps.Add($"{step.Text} ({(step.Owner == ActionOwner.Rep ? "du" : "kunden")})");
        Stage = "PostCall";
    }

    private void CopySummary()
    {
        var text = SummaryText + Environment.NewLine +
                   string.Join(Environment.NewLine, NextSteps.Select(s => "- " + s));
        Clipboard.SetText(text);
    }

    private void ResetToPreCall()
    {
        _audio?.Dispose();
        _audio = null;
        Questions.Clear();
        Products.Clear();
        Threads.Clear();
        Facts.Clear();
        ActionItems.Clear();
        CompanyLine = "";
        LiveLine = "";
        AnswerText = "";
        ErrorBanner = "";
        IsEnding = false;
        EndingNotice = "";
        SummaryText = "";
        NextSteps.Clear();
        Stage = "PreCall";
    }

    private static async void DecayFresh(Action clear)
    {
        await Task.Delay(2000);
        Application.Current?.Dispatcher.Invoke(clear);
    }

    private static void UI(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
