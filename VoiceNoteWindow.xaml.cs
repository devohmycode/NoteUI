using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NoteUI;

public sealed partial class VoiceNoteWindow : Window
{
    private readonly NotesManager _notesManager;

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private bool _isPinnedOnTop;

    // Model / download
    private SttModelInfo? _selectedModel;
    private string _selectedLanguage = "";
    private CancellationTokenSource? _downloadCts;

    // Settings persistence
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteUI");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "voice_settings.json");

    // Recording
    private ISpeechRecognizer? _recognizer;
    private bool _isRecording;
    private DateTime _recordingStartTime;
    private DispatcherTimer? _durationTimer;
    private string _fullTranscription = "";

    // Waveform
    private readonly float[] _waveformBars = new float[36];
    private readonly float[] _waveformTargets = new float[36];
    private float _currentAudioLevel;
    private DispatcherTimer? _waveformTimer;

    // Drag
    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    public event Action? NoteCreated;

    public VoiceNoteWindow(NotesManager notesManager)
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;

        WindowHelper.RemoveWindowBorder(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 580));
        WindowHelper.CenterOnScreen(this);
        WindowShadow.Apply(this);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme);

        _notesManager = notesManager;

        // Try to restore saved model
        var savedId = LoadSavedModelId();
        var savedModel = savedId != null
            ? SttModels.Available.FirstOrDefault(m => m.Id == savedId && m.IsDownloaded)
            : null;

        if (savedModel != null)
            ShowRecordingPage(savedModel);

        this.Closed += (_, _) =>
        {
            if (_isRecording) StopRecording();
            _recognizer?.Dispose();
            _acrylicController?.Dispose();
        };
    }

    // ── Settings persistence ─────────────────────────────────────

    private static string? LoadSavedModelId()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var json = File.ReadAllText(SettingsPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("selectedModelId").GetString();
        }
        catch { return null; }
    }

    private static void SaveSelectedModelId(string modelId)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(new { selectedModelId = modelId });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    // ── Language selection ────────────────────────────────────────

    private void LanguageFr_Click(object sender, RoutedEventArgs e)
    {
        _selectedLanguage = "fr";
        ShowModelSelectionForLanguage("Fran\u00e7ais");
    }

    private void LanguageEn_Click(object sender, RoutedEventArgs e)
    {
        _selectedLanguage = "en";
        ShowModelSelectionForLanguage("English");
    }

    private void ShowModelSelectionForLanguage(string langLabel)
    {
        ModelSelectionTitle.Text = $"Mod\u00e8les disponibles \u2014 {langLabel}";

        var langFilter = _selectedLanguage == "fr" ? "Fran\u00e7ais" : "Anglais";
        var models = SttModels.Available
            .Where(m => m.Languages == langFilter)
            .Select(m => new ModelListItem(m))
            .ToList();

        FilteredModelList.ItemsSource = models;

        LanguageSelectionPage.Visibility = Visibility.Collapsed;
        ModelSelectionPage.Visibility = Visibility.Visible;
        StatusText.Text = "S\u00e9lectionner un mod\u00e8le";
    }

    // ── Model selection ──────────────────────────────────────────

    private async void ModelList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ModelListItem item) return;
        var model = item.Model;

        if (model.IsDownloaded)
        {
            SaveSelectedModelId(model.Id);
            ShowRecordingPage(model);
            return;
        }

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadStatusText.Text = $"T\u00e9l\u00e9chargement de {model.Name}...";
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;

        var progress = new Progress<double>(p =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (p < 0)
                {
                    DownloadProgress.IsIndeterminate = true;
                    DownloadStatusText.Text = "Extraction...";
                }
                else
                {
                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = p * 100;
                    DownloadStatusText.Text = $"{model.Name} \u2014 {(int)(p * 100)}%";
                }
            });
        });

        try
        {
            await ModelDownloader.DownloadAsync(model, progress, _downloadCts.Token);
            DownloadPanel.Visibility = Visibility.Collapsed;
            SaveSelectedModelId(model.Id);
            ShowRecordingPage(model);
        }
        catch (OperationCanceledException)
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "T\u00e9l\u00e9chargement annul\u00e9";
        }
        catch (Exception ex)
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Erreur : {ex.Message}";
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void ShowRecordingPage(SttModelInfo model)
    {
        _selectedModel = model;

        LanguageSelectionPage.Visibility = Visibility.Collapsed;
        ModelSelectionPage.Visibility = Visibility.Collapsed;
        RecordingPage.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;

        ActiveModelName.Text = model.Name;
        StatusText.Text = "Pr\u00eat \u2014 appuyez pour enregistrer";
    }

    // ── Settings flyout (change model) ───────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
        };

        var panel = new StackPanel { Width = 320, Spacing = 4 };

        panel.Children.Add(new TextBlock
        {
            Text = "Changer de mod\u00e8le",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(4, 4, 0, 8)
        });

        panel.Children.Add(CreateSectionHeader("Fran\u00e7ais"));
        foreach (var model in SttModels.Available.Where(m => m.Languages == "Fran\u00e7ais"))
            panel.Children.Add(CreateModelButton(model, flyout));

        panel.Children.Add(CreateSectionHeader("English"));
        foreach (var model in SttModels.Available.Where(m => m.Languages == "Anglais"))
            panel.Children.Add(CreateModelButton(model, flyout));

        flyout.Content = panel;
        flyout.ShowAt(SettingsButton);
    }

    private static TextBlock CreateSectionHeader(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        Margin = new Thickness(4, 8, 0, 4)
    };

    private UIElement CreateModelButton(SttModelInfo model, Flyout flyout)
    {
        var isCurrent = _selectedModel?.Id == model.Id;
        var isDownloaded = model.IsDownloaded;

        var grid = new Grid { Padding = new Thickness(4, 6, 4, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel { Spacing = 2 };
        textPanel.Children.Add(new TextBlock
        {
            Text = model.Name,
            FontWeight = isCurrent
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal
        });

        var engine = model.Engine == SttEngine.Vosk ? "Vosk" : "Whisper";
        var status = isCurrent ? "\u25cf Actif" : isDownloaded ? "T\u00e9l\u00e9charg\u00e9" : $"{model.SizeMB} MB";
        textPanel.Children.Add(new TextBlock
        {
            Text = $"{engine} \u2014 {status}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = isCurrent
                ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        Grid.SetColumn(textPanel, 0);
        grid.Children.Add(textPanel);

        if (isDownloaded)
        {
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }

        var listItem = new ListViewItem { Content = grid, Padding = new Thickness(0) };
        listItem.Tapped += async (_, _) =>
        {
            flyout.Hide();

            if (isDownloaded)
            {
                if (_isRecording) StopRecording();
                _recognizer?.Dispose();
                _recognizer = null;

                SaveSelectedModelId(model.Id);
                ShowRecordingPage(model);
            }
            else
            {
                await DownloadAndSwitchModel(model);
            }
        };

        return listItem;
    }

    private async Task DownloadAndSwitchModel(SttModelInfo model)
    {
        if (_isRecording) StopRecording();
        _recognizer?.Dispose();
        _recognizer = null;

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        StatusText.Text = $"T\u00e9l\u00e9chargement de {model.Name}...";

        var progress = new Progress<double>(p =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusText.Text = p >= 0
                    ? $"{model.Name} \u2014 {(int)(p * 100)}%"
                    : "Extraction...";
            });
        });

        try
        {
            await ModelDownloader.DownloadAsync(model, progress, _downloadCts.Token);
            SaveSelectedModelId(model.Id);
            ShowRecordingPage(model);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "T\u00e9l\u00e9chargement annul\u00e9";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erreur : {ex.Message}";
        }
    }

    // ── Recording ────────────────────────────────────────────────

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
            StopRecording();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        if (_selectedModel == null) return;

        try
        {
            _recognizer = SpeechRecognizerFactory.Create(_selectedModel);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erreur : {ex.Message}";
            return;
        }

        _fullTranscription = "";
        TranscriptionText.Text = "";
        PartialText.Text = "";
        SaveNoteButton.Visibility = Visibility.Collapsed;

        _recognizer.OnPartialResult += text =>
        {
            DispatcherQueue.TryEnqueue(() => PartialText.Text = text);
        };

        _recognizer.OnFinalResult += text =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_fullTranscription.Length > 0) _fullTranscription += " ";
                _fullTranscription += text;
                TranscriptionText.Text = _fullTranscription;
                PartialText.Text = "";
            });
        };

        _recognizer.OnAudioLevel += level =>
        {
            _currentAudioLevel = level;
        };

        _recognizer.Start();
        _isRecording = true;
        _recordingStartTime = DateTime.Now;

        RecordIcon.Glyph = "\uE71A";
        RecordButton.Background = new SolidColorBrush(Colors.Red);
        StatusText.Text = "Enregistrement en cours...";

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _recordingStartTime;
            RecordingDuration.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        };
        _durationTimer.Start();

        StartWaveformAnimation();
    }

    private void StopRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;

        _recognizer?.Stop();
        _durationTimer?.Stop();
        _durationTimer = null;
        StopWaveformAnimation();

        RecordIcon.Glyph = "\uE720";
        RecordButton.Background = null;

        var duration = DateTime.Now - _recordingStartTime;
        StatusText.Text = $"Termin\u00e9 \u2014 {(int)duration.TotalMinutes}:{duration.Seconds:D2}";

        if (!string.IsNullOrWhiteSpace(_fullTranscription))
            SaveNoteButton.Visibility = Visibility.Visible;

        _recognizer?.Dispose();
        _recognizer = null;
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_fullTranscription)) return;

        var title = _fullTranscription.Length > 50
            ? _fullTranscription[..50] + "..."
            : _fullTranscription;

        var note = _notesManager.CreateNote();
        _notesManager.UpdateNote(note.Id, _fullTranscription, title);
        NoteCreated?.Invoke();

        _fullTranscription = "";
        TranscriptionText.Text = "";
        PartialText.Text = "";
        SaveNoteButton.Visibility = Visibility.Collapsed;
        RecordingDuration.Text = "0:00";
        StatusText.Text = "Note enregistr\u00e9e !";
    }

    // ── Waveform animation ───────────────────────────────────────

    private void StartWaveformAnimation()
    {
        Array.Clear(_waveformBars);
        Array.Clear(_waveformTargets);

        _waveformTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _waveformTimer.Tick += WaveformTick;
        _waveformTimer.Start();
    }

    private void StopWaveformAnimation()
    {
        _waveformTimer?.Stop();
        _waveformTimer = null;
        DrawWaveformBars(clear: true);
    }

    private void WaveformTick(object? sender, object e)
    {
        for (int i = 0; i < _waveformTargets.Length - 1; i++)
            _waveformTargets[i] = _waveformTargets[i + 1];

        var level = Math.Clamp(_currentAudioLevel * 8f, 0f, 1f);
        var rand = Random.Shared.NextSingle() * 0.3f;
        _waveformTargets[^1] = Math.Clamp(level + rand * level, 0f, 1f);

        for (int i = 0; i < _waveformBars.Length; i++)
        {
            _waveformBars[i] += (_waveformTargets[i] - _waveformBars[i]) * 0.3f;
        }

        DrawWaveformBars();
    }

    private void DrawWaveformBars(bool clear = false)
    {
        WaveformCanvas.Children.Clear();

        var width = WaveformCanvas.ActualWidth;
        var height = WaveformCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        if (clear) return;

        int barCount = _waveformBars.Length;
        double barWidth = Math.Max(2, (width - (barCount - 1) * 2) / barCount);
        double gap = 2;
        double totalWidth = barCount * barWidth + (barCount - 1) * gap;
        double startX = (width - totalWidth) / 2;

        for (int i = 0; i < barCount; i++)
        {
            var barHeight = Math.Max(4, _waveformBars[i] * height * 0.8);
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                RadiusX = barWidth / 2,
                RadiusY = barWidth / 2,
                Fill = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            };

            Canvas.SetLeft(rect, startX + i * (barWidth + gap));
            Canvas.SetTop(rect, (height - barHeight) / 2);
            WaveformCanvas.Children.Add(rect);
        }
    }

    // ── Window chrome ────────────────────────────────────────────

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _isPinnedOnTop = !_isPinnedOnTop;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _isPinnedOnTop;
        PinIcon.Glyph = _isPinnedOnTop ? "\uE77A" : "\uE718";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording) StopRecording();
        _recognizer?.Dispose();
        this.Close();
    }

    // ── Drag ───────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private void DragBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        GetCursorPos(out _dragStartCursor);
        _dragStartPos = AppWindow.Position;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void DragBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        GetCursorPos(out var current);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            _dragStartPos.X + (current.X - _dragStartCursor.X),
            _dragStartPos.Y + (current.Y - _dragStartCursor.Y)));
    }

    private void DragBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }
}

// ── View model for model list ────────────────────────────────────

public class ModelListItem
{
    public SttModelInfo Model { get; }
    public string Name => Model.Name;
    public long SizeMB => Model.SizeMB;
    public string Languages => Model.Languages;
    public string EngineName => Model.Engine == SttEngine.Vosk ? "Vosk" : "Whisper";
    public Visibility IsDownloaded => Model.IsDownloaded ? Visibility.Visible : Visibility.Collapsed;

    public ModelListItem(SttModelInfo model) => Model = model;
}
