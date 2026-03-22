using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace NoteUI;

public sealed partial class NotepadWindow : Window
{
    private readonly NotesManager _notesManager;
    private SnippetManager? _snippetManager;

    private IDisposable? _acrylicController;
    private SystemBackdropConfiguration? _configSource;

    private bool _isPinned;
    private float _zoomFactor = 1.0f;
    private const float BaseFontSize = 14f;

    private bool _isDragging;
    private POINT _dragStartCursor;
    private Windows.Graphics.PointInt32 _dragStartPos;

    private Flyout? _slashFlyout;
    private bool _slashUndoing;
    private bool _isMarkdownMode;
    private bool _autoResize;
    private int _autoResizeMinHeight = 200;
    private int _autoResizeMaxHeight = 900;
    private bool _isSplitView;
    private AiManager? _aiManager;

    // ── Tabs ────────────────────────────────────────────────────
    private sealed class TabData
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public string RtfContent { get; set; } = "";
        public string? FilePath { get; set; }
        public string? NoteId { get; set; }
    }

    private readonly List<TabData> _tabs = [];
    private string _activeTabId = "";
    private bool _switchingTab;

    public event Action? NoteCreated;

    public void SetSnippetManager(SnippetManager snippetManager) => _snippetManager = snippetManager;

    public NotepadWindow(NotesManager notesManager)
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        var transparent = new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 };
        AppWindow.TitleBar.BackgroundColor = transparent;
        AppWindow.TitleBar.InactiveBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonBackgroundColor = transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = transparent;
        var presenter = WindowHelper.GetOverlappedPresenter(this);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = true;

        WindowHelper.RemoveWindowBorderKeepResize(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 640));
        WindowHelper.CenterOnScreen(this);
        WindowShadow.Apply(this);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        var backdrop = AppSettings.LoadSettings();
        var theme = AppSettings.LoadThemeSetting();
        AppSettings.ApplyToWindow(this, backdrop, ref _acrylicController, ref _configSource);
        AppSettings.ApplyThemeToWindow(this, theme, _configSource);

        _notesManager = notesManager;
        ApplyNotepadLocalization();
        RefreshAiUi();
        Editor.ContextRequested += Editor_ContextRequested;

        this.Closed += (_, _) =>
        {
            _acrylicController?.Dispose();
        };

        AddNewTab();
        // Focus editor on open
        this.Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        this.Activated -= OnFirstActivated;
        FocusEditorAtEnd();
    }

    private void FocusEditorAtEnd()
    {
        Editor.Document.GetText(TextGetOptions.None, out var text);
        var len = text.TrimEnd('\r', '\n').Length;
        Editor.Document.Selection.SetRange(len, len);
        Editor.Focus(FocusState.Programmatic);
    }

    public void LoadNoteContent(string title, string rtfContent, string? noteId = null)
    {
        SaveCurrentTabContent();
        var tab = new TabData { Title = title, NoteId = noteId };
        _tabs.Add(tab);
        _activeTabId = tab.Id;
        _switchingTab = true;
        if (!string.IsNullOrEmpty(rtfContent) && rtfContent.StartsWith("{\\rtf", StringComparison.Ordinal))
            Editor.Document.SetText(TextSetOptions.FormatRtf, rtfContent);
        else
            Editor.Document.SetText(TextSetOptions.None, rtfContent ?? "");
        _switchingTab = false;
        tab.RtfContent = rtfContent ?? "";
        TitleText.Text = $"{title} \u2014 {Lang.T("notepad")}";
        RefreshTabStrip();
        FocusEditorAtEnd();
    }

    private void ApplyNotepadLocalization()
    {
        FileMenu.Title = Lang.T("file_menu");
        MenuNew.Text = Lang.T("new_item");
        MenuNewTab.Text = Lang.T("new_tab");
        MenuCloseTab.Text = Lang.T("close_tab");
        MenuOpen.Text = Lang.T("open");
        MenuSave.Text = Lang.T("save");
        MenuSaveAs.Text = Lang.T("save_as");
        MenuSaveToNotes.Text = Lang.T("save_to_notes");
        MenuClose.Text = Lang.T("close");

        EditMenu.Title = Lang.T("edit_menu");
        MenuUndo.Text = Lang.T("undo");
        MenuRedo.Text = Lang.T("redo");
        MenuCut.Text = Lang.T("cut");
        MenuCopy.Text = Lang.T("copy");
        MenuPaste.Text = Lang.T("paste");
        MenuSelectAll.Text = Lang.T("select_all");
        MenuDateTime.Text = Lang.T("datetime_menu");
        MenuSnippet.Text = Lang.T("snippet");

        ViewMenu.Title = Lang.T("view_menu");
        WordWrapItem.Text = Lang.T("word_wrap");
        MenuZoomIn.Text = Lang.T("zoom_in");
        MenuZoomOut.Text = Lang.T("zoom_out");
        MenuZoomDefault.Text = Lang.T("zoom_default");
        AutoResizeItem.Text = Lang.T("auto_resize");
        SplitViewItem.Text = Lang.T("split_view");

        MenuHeadingNormal.Text = Lang.T("normal_text");
        MenuHeadingH1.Text = Lang.T("heading1");
        MenuHeadingH2.Text = Lang.T("heading2");
        MenuHeadingH3.Text = Lang.T("heading3");

        ToolTipService.SetToolTip(HeadingButton, Lang.T("paragraph_style"));
        ToolTipService.SetToolTip(BoldButton, Lang.T("tip_bold"));
        ToolTipService.SetToolTip(ItalicButton, Lang.T("tip_italic"));
        ToolTipService.SetToolTip(StrikethroughButton, Lang.T("tip_strikethrough"));
        ToolTipService.SetToolTip(UnderlineButton, Lang.T("tip_underline"));
        ToolTipService.SetToolTip(LinkButton, Lang.T("link"));
        ToolTipService.SetToolTip(PinButton, Lang.T("tip_pin"));
        ToolTipService.SetToolTip(NpCloseButton, Lang.T("tip_close"));
        ToolTipService.SetToolTip(AiButton, Lang.T("tip_ai"));

        RichTextLabel.Text = Lang.T("rich_text");
        CharCountText.Text = Lang.T("char_count_many", 0);
    }

    // ── Tabs ──────────────────────────────────────────────────────

    private void AddNewTab()
    {
        SaveCurrentTabContent();
        var tab = new TabData { Title = Lang.T("untitled") };
        _tabs.Add(tab);
        _activeTabId = tab.Id;
        _switchingTab = true;
        Editor.Document.SetText(TextSetOptions.None, "");
        _switchingTab = false;
        TitleText.Text = $"{Lang.T("untitled")} \u2014 {Lang.T("notepad")}";
        RefreshTabStrip();
        Editor.Focus(FocusState.Programmatic);
    }

    private void SwitchToTab(string tabId)
    {
        if (tabId == _activeTabId) return;
        SaveCurrentTabContent();

        _activeTabId = tabId;
        var tab = _tabs.Find(t => t.Id == tabId)!;

        _switchingTab = true;
        if (string.IsNullOrEmpty(tab.RtfContent))
            Editor.Document.SetText(TextSetOptions.None, "");
        else
            Editor.Document.SetText(TextSetOptions.FormatRtf, tab.RtfContent);
        _switchingTab = false;

        TitleText.Text = $"{tab.Title} \u2014 {Lang.T("notepad")}";
        RefreshTabStrip();
        Editor.Focus(FocusState.Programmatic);
    }

    private void CloseActiveTab()
    {
        if (_tabs.Count <= 1) return;
        var idx = _tabs.FindIndex(t => t.Id == _activeTabId);
        _tabs.RemoveAt(idx);
        var newIdx = Math.Min(idx, _tabs.Count - 1);
        _activeTabId = ""; // prevent saving removed tab
        SwitchToTab(_tabs[newIdx].Id);
    }

    private void SaveCurrentTabContent()
    {
        if (string.IsNullOrEmpty(_activeTabId)) return;
        var current = _tabs.Find(t => t.Id == _activeTabId);
        if (current == null) return;
        Editor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        current.RtfContent = rtf;
    }

    private void RefreshTabStrip()
    {
        TabStrip.Children.Clear();

        foreach (var tab in _tabs)
        {
            var isActive = tab.Id == _activeTabId;
            var tabId = tab.Id;
            var hasSavedName = !string.IsNullOrEmpty(tab.FilePath);

            var tabBtn = new Button
            {
                Background = isActive
                    ? (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Height = 34,
                Width = 120,
                Opacity = isActive ? 1.0 : 0.7,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (hasSavedName)
            {
                var titleBlock = new TextBlock
                {
                    Text = Path.GetFileNameWithoutExtension(tab.FilePath),
                    FontSize = 12,
                    FontWeight = isActive
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                Grid.SetColumn(titleBlock, 0);
                grid.Children.Add(titleBlock);
            }

            if (_tabs.Count > 1)
            {
                var closeBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 8 },
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4),
                    CornerRadius = new CornerRadius(4),
                    Width = 22,
                    Height = 22,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                closeBtn.Click += (_, _) =>
                {
                    var idxToClose = _tabs.FindIndex(t => t.Id == tabId);
                    if (idxToClose < 0) return;
                    _tabs.RemoveAt(idxToClose);
                    if (_activeTabId == tabId)
                    {
                        var newIdx = Math.Min(idxToClose, _tabs.Count - 1);
                        _activeTabId = "";
                        SwitchToTab(_tabs[newIdx].Id);
                    }
                    else
                    {
                        RefreshTabStrip();
                    }
                };
                Grid.SetColumn(closeBtn, 1);
                grid.Children.Add(closeBtn);
            }

            tabBtn.Content = grid;
            tabBtn.Click += (_, _) => SwitchToTab(tabId);
            TabStrip.Children.Add(tabBtn);
        }

        // "+" button
        var addBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 10 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Height = 34,
        };
        addBtn.Click += (_, _) => AddNewTab();
        TabStrip.Children.Add(addBtn);
    }

    private void UpdateActiveTabTitle()
    {
        var tab = _tabs.Find(t => t.Id == _activeTabId);
        if (tab == null) return;
        // Title bar text updates from content, but tab strip only shows filename after save
        Editor.Document.GetText(TextGetOptions.None, out var text);
        text = text.TrimEnd('\r', '\n');
        if (!string.IsNullOrEmpty(tab.FilePath))
        {
            tab.Title = Path.GetFileNameWithoutExtension(tab.FilePath);
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            tab.Title = Lang.T("untitled");
        }
        else
        {
            var firstLine = text.Split('\r', '\n')[0].Trim();
            tab.Title = firstLine.Length > 30 ? firstLine[..30] + "\u2026" : firstLine;
        }
        TitleText.Text = $"{tab.Title} \u2014 {Lang.T("notepad")}";
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddNewTab();

    private void CloseTab_Click(object sender, RoutedEventArgs e) => CloseActiveTab();

    // ── Status bar ───────────────────────────────────────────────

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_switchingTab || _slashUndoing) return;

        Editor.Document.GetText(TextGetOptions.None, out var text);
        var count = text.TrimEnd('\r', '\n').Length;
        CharCountText.Text = count == 1 ? Lang.T("char_count_one") : Lang.T("char_count_many", count);

        UpdateActiveTabTitle();
        AutoResizeWindow();

        // Update split preview in real-time
        if (_isSplitView)
            SplitPreviewMarkdown.Text = ConvertToMarkdown();

        if (_slashFlyout == null && AppSettings.LoadSlashEnabled())
        {
            var slashPos = SlashCommands.DetectSlash(Editor);
            if (slashPos >= 0)
            {
                // Check if "/" replaced a selection — undo to restore it
                _slashUndoing = true;
                Editor.Document.Undo();
                _slashUndoing = false;

                var sel = Editor.Document.Selection;
                bool hadSelection = sel.StartPosition != sel.EndPosition;

                if (hadSelection)
                {
                    // Selection restored by undo — no slash to delete
                    slashPos = -1;
                }
                else
                {
                    // Normal "/" — redo to put it back
                    _slashUndoing = true;
                    Editor.Document.Redo();
                    _slashUndoing = false;
                }

                var formatActions = SlashCommands.RichEditActions(Editor, slashPos, UpdateToolbarState);
                var aiActions = IsAiEnabled() ? CreateSlashAiActions(slashPos) : null;

                var topActions = new List<ActionPanel.ActionItem>();

                void ReopenMain() =>
                    _slashFlyout = SlashCommands.Show(Editor, topActions, () => _slashFlyout = null);

                // Format → opens a new flyout with formatting options
                topActions.Add(new("\uE8D2", Lang.T("format"), [], () =>
                {
                    _slashFlyout = SlashCommands.ShowSubFlyout(Editor,
                        Lang.T("format"), formatActions,
                        onClosed: () => _slashFlyout = null,
                        onEscBack: ReopenMain);
                }));

                // IA → opens a new flyout with AI options
                if (aiActions != null)
                {
                    topActions.Add(new("\uE99A", Lang.T("ai_section"), [], () =>
                    {
                        _slashFlyout = SlashCommands.ShowSubFlyout(Editor,
                            Lang.T("ai_section"), aiActions,
                            onClosed: () => _slashFlyout = null,
                            onEscBack: ReopenMain);
                    }));
                }

                // DateTime
                topActions.Add(new("\uE787", Lang.T("datetime"), ["F5"], () =>
                {
                    SlashCommands.DeleteSlash(Editor, slashPos);
                    Editor.Document.Selection.TypeText(DateTime.Now.ToString("HH:mm dd/MM/yyyy"));
                    Editor.Focus(FocusState.Programmatic);
                    UpdateToolbarState();
                }));

                // Save
                topActions.Add(new("\uE74E", Lang.T("save"), ["Ctrl", "S"], () =>
                {
                    SlashCommands.DeleteSlash(Editor, slashPos);
                    Save_Click(null!, null!);
                    Editor.Focus(FocusState.Programmatic);
                }));

                // Snippet
                if (_snippetManager != null)
                {
                    var sp = slashPos;
                    topActions.Add(new("\uE943", Lang.T("snippet"), [], () =>
                    {
                        SlashCommands.DeleteSlash(Editor, sp);
                        Snippet_Click(null!, null!);
                    }));
                }

                _slashFlyout = SlashCommands.Show(Editor, topActions, () => _slashFlyout = null);
            }
        }
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateLineCol();
        UpdateToolbarState();
    }

    private void Editor_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (!IsAiEnabled())
            return;

        var selection = Editor.Document.Selection;
        selection.GetText(TextGetOptions.None, out var selectedText);
        if (string.IsNullOrWhiteSpace(selectedText))
            return;

        e.Handled = true;
        ShowAiActionsFlyout(Editor);
    }

    private void AiMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAiEnabled())
            return;

        ShowAiActionsFlyout(AiButton);
    }

    private void ShowAiActionsFlyout(FrameworkElement target)
    {
        if (!IsAiEnabled())
            return;

        var flyout = ActionPanel.Create(Lang.T("ai_section"), CreateAiActions());
        flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
        flyout.Opened += (_, _) => AnimateAiFlyoutItems(flyout);
        flyout.ShowAt(target);
    }

    private string AiPrompt(string key) => _aiManager?.GetPrompt(key) ?? AiManager.GetDefaultPrompt(key);

    private List<ActionPanel.ActionItem> CreateAiActions()
    {
        var list = new List<ActionPanel.ActionItem>
        {
            new("\uE8D2", Lang.T("ai_improve_writing"), [],
                () => _ = ApplyAiToSelectionAsync(AiPrompt("ai_improve_writing"))),
            new("\uE8FD", Lang.T("ai_fix_grammar_spelling"), [],
                () => _ = ApplyAiToSelectionAsync(AiPrompt("ai_fix_grammar_spelling"))),
            new("\uE8D3", Lang.T("ai_tone_professional"), [],
                () => _ = ApplyAiToSelectionAsync(AiPrompt("ai_tone_professional"))),
            new("\uE8D4", Lang.T("ai_tone_friendly"), [],
                () => _ = ApplyAiToSelectionAsync(AiPrompt("ai_tone_friendly"))),
            new("\uE8D5", Lang.T("ai_tone_concise"), [],
                () => _ = ApplyAiToSelectionAsync(AiPrompt("ai_tone_concise"))),
        };
        if (_aiManager != null)
            foreach (var cp in _aiManager.Settings.CustomPrompts)
            {
                var instruction = cp.Instruction;
                list.Add(new("\uE945", cp.Title, [],
                    () => _ = ApplyAiToSelectionAsync(instruction)));
            }
        return list;
    }

    private List<ActionPanel.ActionItem> CreateSlashAiActions(int slashPos)
    {
        var list = new List<ActionPanel.ActionItem>
        {
            new("\uE8D2", Lang.T("ai_improve_writing"), [],
                () => { SlashCommands.DeleteSlash(Editor, slashPos); _ = ApplyAiToSelectionAsync(AiPrompt("ai_improve_writing")); }),
            new("\uE8FD", Lang.T("ai_fix_grammar_spelling"), [],
                () => { SlashCommands.DeleteSlash(Editor, slashPos); _ = ApplyAiToSelectionAsync(AiPrompt("ai_fix_grammar_spelling")); }),
            new("\uE8D3", Lang.T("ai_tone_professional"), [],
                () => { SlashCommands.DeleteSlash(Editor, slashPos); _ = ApplyAiToSelectionAsync(AiPrompt("ai_tone_professional")); }),
            new("\uE8D4", Lang.T("ai_tone_friendly"), [],
                () => { SlashCommands.DeleteSlash(Editor, slashPos); _ = ApplyAiToSelectionAsync(AiPrompt("ai_tone_friendly")); }),
            new("\uE8D5", Lang.T("ai_tone_concise"), [],
                () => { SlashCommands.DeleteSlash(Editor, slashPos); _ = ApplyAiToSelectionAsync(AiPrompt("ai_tone_concise")); }),
        };
        if (_aiManager != null)
            foreach (var cp in _aiManager.Settings.CustomPrompts)
            {
                var instruction = cp.Instruction;
                list.Add(new("\uE945", cp.Title, [],
                    () => { SlashCommands.DeleteSlash(Editor, slashPos); _ = ApplyAiToSelectionAsync(instruction); }));
            }
        return list;
    }

    private static void AnimateAiFlyoutItems(Flyout flyout)
    {
        if (flyout.Content is not Panel panel) return;
        var delay = 0;
        foreach (var child in panel.Children)
        {
            if (child is Button btn)
            {
                AnimationHelper.FadeSlideIn(btn, delay, 180);
                delay += 24;
            }
        }
    }

    private async Task ApplyAiToSelectionAsync(string instruction)
    {
        var selection = Editor.Document.Selection;
        var start = selection.StartPosition;
        var end = selection.EndPosition;
        if (start >= end)
        {
            await ShowAiMessageAsync(Lang.T("ai_select_text_first"));
            return;
        }

        selection.GetText(TextGetOptions.None, out var selectedText);
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            await ShowAiMessageAsync(Lang.T("ai_select_text_first"));
            return;
        }

        var rewritten = await TransformSelectionWithAiAsync(selectedText, instruction);
        if (string.IsNullOrWhiteSpace(rewritten))
            return;

        var range = Editor.Document.GetRange(start, end);
        range.SetText(TextSetOptions.None, rewritten);
        Editor.Document.Selection.SetRange(start, start + rewritten.Length);
        Editor.Focus(FocusState.Programmatic);
    }

    private async Task<string?> TransformSelectionWithAiAsync(string selectedText, string instruction)
    {
        try { _aiManager ??= new AiManager(); _aiManager.Load(); }
        catch { await ShowAiMessageAsync("AI unavailable"); return null; }
        if (!_aiManager.IsEnabled)
        {
            await ShowAiMessageAsync(Lang.T("ai_no_model_selected"));
            return null;
        }

        var prompt = BuildAiPrompt(selectedText, instruction);
        var history = new List<AiManager.ChatMessage>();
        var output = new StringBuilder();

        try
        {
            if (TryGetActiveCloud(out var provider, out var apiKey, out var modelId))
            {
                await foreach (var token in provider.StreamChatAsync(
                    apiKey,
                    modelId,
                    _aiManager.Settings.SystemPrompt,
                    history,
                    prompt,
                    _aiManager.Settings.Temperature,
                    _aiManager.Settings.MaxTokens))
                {
                    output.Append(token);
                }
            }
            else if (TryGetActiveLocal(out var localModelFile))
            {
                await _aiManager.LoadModelAsync(localModelFile);
                await foreach (var token in _aiManager.ChatLocalAsync(prompt, history))
                    output.Append(token);
            }
            else
            {
                await ShowAiMessageAsync(Lang.T("ai_no_model_selected"));
                return null;
            }
        }
        catch (Exception ex)
        {
            await ShowAiMessageAsync(Lang.T("ai_transform_failed", ex.Message));
            return null;
        }

        var text = NormalizeAiOutput(output.ToString());
        if (string.IsNullOrWhiteSpace(text))
        {
            await ShowAiMessageAsync(Lang.T("ai_transform_empty"));
            return null;
        }
        return text;
    }

    private bool TryGetActiveCloud(out ICloudAiProvider provider, out string apiKey, out string modelId)
    {
        provider = default!;
        apiKey = "";
        modelId = "";

        var providerId = _aiManager?.Settings.LastProviderId ?? "";
        modelId = _aiManager?.Settings.LastModelId ?? "";
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return false;

        var p = _aiManager!.GetProvider(providerId);
        if (p == null)
            return false;

        apiKey = _aiManager.GetApiKey(providerId);
        if (string.IsNullOrWhiteSpace(apiKey))
            return false;

        provider = p;
        return true;
    }

    private bool TryGetActiveLocal(out string localModelFile)
    {
        localModelFile = _aiManager?.Settings.LastLocalModelFileName ?? "";
        if (string.IsNullOrWhiteSpace(localModelFile))
            return false;

        var modelPath = Path.Combine(AiManager.ModelsDir, localModelFile);
        return File.Exists(modelPath);
    }

    private static string BuildAiPrompt(string selectedText, string instruction) =>
        $"""
        You are rewriting user text.
        Task: {instruction}

        Rules:
        - IMPORTANT: Reply in the SAME language as the original text. If the text is in French, reply in French. If in English, reply in English.
        - Preserve the original meaning and facts.
        - Return ONLY the rewritten text. No title, no explanation, no bullet list, no prefix like "assistant" or "Here is".

        Text:
        <<<
        {selectedText}
        >>>
        """;

    private static string NormalizeAiOutput(string raw)
    {
        var text = raw.Trim();

        // Strip "assistant" role prefix emitted by some local models (Llama, etc.)
        if (text.StartsWith("assistant", StringComparison.OrdinalIgnoreCase))
        {
            text = text["assistant".Length..].TrimStart(':', ' ', '\n', '\r');
        }

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = text.IndexOf('\n');
            if (firstLineBreak >= 0)
            {
                text = text[(firstLineBreak + 1)..];
                var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (closingFence >= 0)
                    text = text[..closingFence];
            }
        }

        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];
        return text.Trim();
    }

    public void RefreshAiUi()
    {
        try
        {
            _aiManager ??= new AiManager();
            _aiManager.Load();
            if (!_aiManager.IsEnabled)
                _aiManager.UnloadModel();
            AiButton.Visibility = _aiManager.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            AiButton.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsAiEnabled()
    {
        try
        {
            _aiManager ??= new AiManager();
            _aiManager.Load();
            return _aiManager.IsEnabled;
        }
        catch { return false; }
    }

    private async Task ShowAiMessageAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = Lang.T("ai_section"),
            Content = message,
            CloseButtonText = Lang.T("ok"),
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void UpdateLineCol()
    {
        var sel = Editor.Document.Selection;
        var start = sel.StartPosition;
        if (start < 0) start = 0;

        Editor.Document.GetText(TextGetOptions.None, out var fullText);
        if (start > fullText.Length) start = fullText.Length;

        var before = fullText[..start];
        var lines = before.Split('\r');
        LineColText.Text = $"Ln {lines.Length}, Col {lines[^1].Length + 1}";
    }

    private void UpdateToolbarState()
    {
        var charFmt = Editor.Document.Selection.CharacterFormat;
        BoldButton.IsChecked = charFmt.Bold == FormatEffect.On;
        ItalicButton.IsChecked = charFmt.Italic == FormatEffect.On;
        StrikethroughButton.IsChecked = charFmt.Strikethrough == FormatEffect.On;
        UnderlineButton.IsChecked = charFmt.Underline != UnderlineType.None;

        var fontSize = charFmt.Size;
        HeadingButton.Content = fontSize switch
        {
            24f => "H1",
            20f => "H2",
            16f => "H3",
            _ => "Normal"
        };
    }

    // ── Toolbar ──────────────────────────────────────────────────

    private void Bold_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Document.Selection;
        sel.CharacterFormat.Bold = sel.CharacterFormat.Bold == FormatEffect.On
            ? FormatEffect.Off : FormatEffect.On;
        Editor.Focus(FocusState.Programmatic);
    }

    private void Italic_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Document.Selection;
        sel.CharacterFormat.Italic = sel.CharacterFormat.Italic == FormatEffect.On
            ? FormatEffect.Off : FormatEffect.On;
        Editor.Focus(FocusState.Programmatic);
    }

    private void Strikethrough_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Document.Selection;
        sel.CharacterFormat.Strikethrough = sel.CharacterFormat.Strikethrough == FormatEffect.On
            ? FormatEffect.Off : FormatEffect.On;
        Editor.Focus(FocusState.Programmatic);
    }

    private void Underline_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Document.Selection;
        sel.CharacterFormat.Underline = sel.CharacterFormat.Underline != UnderlineType.None
            ? UnderlineType.None : UnderlineType.Single;
        Editor.Focus(FocusState.Programmatic);
    }

    private void ApplyHeadingFormat(float fontSize, bool bold)
    {
        var sel = Editor.Document.Selection;
        var charFmt = sel.CharacterFormat;
        charFmt.Size = fontSize;
        charFmt.Bold = bold ? FormatEffect.On : FormatEffect.Off;
        sel.CharacterFormat = charFmt;
    }

    private void Heading_Normal_Click(object sender, RoutedEventArgs e)
    {
        ApplyHeadingFormat(BaseFontSize, false);
        HeadingButton.Content = "Normal";
        Editor.Focus(FocusState.Programmatic);
    }

    private void Heading_H1_Click(object sender, RoutedEventArgs e)
    {
        ApplyHeadingFormat(24f, true);
        HeadingButton.Content = "H1";
        Editor.Focus(FocusState.Programmatic);
    }

    private void Heading_H2_Click(object sender, RoutedEventArgs e)
    {
        ApplyHeadingFormat(20f, true);
        HeadingButton.Content = "H2";
        Editor.Focus(FocusState.Programmatic);
    }

    private void Heading_H3_Click(object sender, RoutedEventArgs e)
    {
        ApplyHeadingFormat(16f, true);
        HeadingButton.Content = "H3";
        Editor.Focus(FocusState.Programmatic);
    }

    // ── Link ─────────────────────────────────────────────────────

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var sel = Editor.Document.Selection;
        sel.GetText(TextGetOptions.None, out var selectedText);
        selectedText = selectedText.TrimEnd('\r', '\n');

        var flyout = new Flyout();
        flyout.FlyoutPresenterStyle = ActionPanel.CreateFlyoutPresenterStyle(300, 360);

        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(8) };

        panel.Children.Add(ActionPanel.CreateHeader("Lien"));

        var displayBox = new TextBox
        {
            PlaceholderText = "Texte du lien (facultatif)",
            Header = "Texte d'affichage",
            FontSize = 13,
            Text = selectedText ?? ""
        };
        panel.Children.Add(displayBox);

        var urlBox = new TextBox
        {
            PlaceholderText = "Lien vers une page web existante",
            Header = "Adresse",
            FontSize = 13
        };
        panel.Children.Add(urlBox);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var insertBtn = new Button
        {
            Content = "Ins\u00e9rer",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        insertBtn.Click += (_, _) =>
        {
            var url = urlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            var display = displayBox.Text.Trim();
            var docSel = Editor.Document.Selection;

            if (!string.IsNullOrEmpty(display) && string.IsNullOrEmpty(selectedText))
            {
                docSel.TypeText(display);
                // Select the just-typed text so the link applies to it
                docSel.SetRange(docSel.EndPosition - display.Length, docSel.EndPosition);
            }

            docSel.Link = "\"" + url + "\"";
            docSel.CharacterFormat.ForegroundColor = Windows.UI.Color.FromArgb(255, 96, 180, 255);

            flyout.Hide();
            Editor.Focus(FocusState.Programmatic);
        };

        var cancelBtn = new Button { Content = "Annuler" };
        cancelBtn.Click += (_, _) =>
        {
            flyout.Hide();
            Editor.Focus(FocusState.Programmatic);
        };

        buttonsPanel.Children.Add(insertBtn);
        buttonsPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonsPanel);

        flyout.Content = panel;
        flyout.ShowAt(LinkButton);
    }

    // ── Menu: Fichier ────────────────────────────────────────────

    private void New_Click(object sender, RoutedEventArgs e) => AddNewTab();

    private void SaveToNotes_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.GetText(TextGetOptions.None, out var text);
        text = text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        var firstLine = text.Split('\r', '\n')[0].Trim();
        var title = firstLine.Length > 60 ? firstLine[..60] : firstLine;

        var note = _notesManager.CreateNote();
        _notesManager.UpdateNote(note.Id, text, title);
        NoteCreated?.Invoke();

        TitleText.Text = $"{title} \u2014 Bloc-notes";

        var tab = _tabs.Find(t => t.Id == _activeTabId);
        if (tab != null) { tab.Title = title; RefreshTabStrip(); }
    }

    private string GetSaveContent(out string extension)
    {
        if (HasFormatting())
        {
            extension = ".md";
            return ConvertToMarkdown();
        }
        else
        {
            extension = ".txt";
            Editor.Document.GetText(TextGetOptions.None, out var text);
            return text.TrimEnd('\r', '\n');
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var tab = _tabs.Find(t => t.Id == _activeTabId);
        if (tab == null) return;

        if (!string.IsNullOrEmpty(tab.FilePath))
        {
            try
            {
                var content = GetSaveContent(out _);
                await File.WriteAllTextAsync(tab.FilePath, content);
            }
            catch { }
            return;
        }

        await SaveAsInternal(tab);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var tab = _tabs.Find(t => t.Id == _activeTabId);
        if (tab == null) return;
        await SaveAsInternal(tab);
    }

    private async Task SaveAsInternal(TabData tab)
    {
        var content = GetSaveContent(out var ext);

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        if (ext == ".md")
        {
            picker.FileTypeChoices.Add("Fichier Markdown", [".md"]);
            picker.FileTypeChoices.Add("Fichier texte", [".txt"]);
        }
        else
        {
            picker.FileTypeChoices.Add("Fichier texte", [".txt"]);
            picker.FileTypeChoices.Add("Fichier Markdown", [".md"]);
        }
        picker.SuggestedFileName = tab.Title;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        await File.WriteAllTextAsync(file.Path, content);

        tab.FilePath = file.Path;
        tab.Title = Path.GetFileNameWithoutExtension(file.Name);
        TitleText.Text = $"{tab.Title} \u2014 Bloc-notes";
        RefreshTabStrip();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".rtf");
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var content = await FileIO.ReadTextAsync(file);

        // Open in a new tab
        SaveCurrentTabContent();
        var tab = new TabData
        {
            Title = Path.GetFileNameWithoutExtension(file.Name),
            FilePath = file.Path
        };
        _tabs.Add(tab);
        _activeTabId = tab.Id;

        _switchingTab = true;
        if (file.FileType.Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            Editor.Document.SetText(TextSetOptions.FormatRtf, content);
        else
            Editor.Document.SetText(TextSetOptions.None, content);
        _switchingTab = false;

        TitleText.Text = $"{tab.Title} \u2014 Bloc-notes";
        RefreshTabStrip();
        Editor.Focus(FocusState.Programmatic);
    }

    private void CloseMenu_Click(object sender, RoutedEventArgs e) => this.Close();

    // ── Menu: Modifier ───────────────────────────────────────────

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.Undo();
        Editor.Focus(FocusState.Programmatic);
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.Redo();
        Editor.Focus(FocusState.Programmatic);
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.Selection.Cut();
        Editor.Focus(FocusState.Programmatic);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.Selection.Copy();
        Editor.Focus(FocusState.Programmatic);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.Selection.Paste(0);
        Editor.Focus(FocusState.Programmatic);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.GetText(TextGetOptions.None, out var text);
        Editor.Document.Selection.SetRange(0, text.Length);
        Editor.Focus(FocusState.Programmatic);
    }

    private void InsertDateTime_Click(object sender, RoutedEventArgs e)
    {
        Editor.Document.Selection.TypeText(DateTime.Now.ToString("HH:mm dd/MM/yyyy"));
        Editor.Focus(FocusState.Programmatic);
    }

    private void Snippet_Click(object sender, RoutedEventArgs e)
    {
        if (_snippetManager == null) return;

        var tab = _tabs.Find(t => t.Id == _activeTabId);
        if (tab == null) return;

        // If this tab isn't linked to a note, save to notes first
        if (string.IsNullOrEmpty(tab.NoteId))
        {
            Editor.Document.GetText(TextGetOptions.None, out var text);
            text = text.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(text)) return;

            var firstLine = text.Split('\r', '\n')[0].Trim();
            var title = firstLine.Length > 60 ? firstLine[..60] : firstLine;

            var note = _notesManager.CreateNote();
            _notesManager.UpdateNote(note.Id, text, title);
            tab.NoteId = note.Id;
            NoteCreated?.Invoke();
        }

        Editor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        ActionPanel.ShowSnippetFlyout(MenuSnippet, tab.NoteId!, _snippetManager, rtf);
    }

    // ── Menu: Affichage ──────────────────────────────────────────

    private void WordWrap_Click(object sender, RoutedEventArgs e)
    {
        Editor.TextWrapping = WordWrapItem.IsChecked ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _zoomFactor = Math.Min(3.0f, _zoomFactor + 0.1f);
        ApplyZoom();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _zoomFactor = Math.Max(0.5f, _zoomFactor - 0.1f);
        ApplyZoom();
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _zoomFactor = 1.0f;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        Editor.FontSize = BaseFontSize * _zoomFactor;
        ZoomText.Text = $"{(int)(_zoomFactor * 100)}%";
        AutoResizeWindow();
    }

    private void AutoResize_Click(object sender, RoutedEventArgs e)
    {
        _autoResize = AutoResizeItem.IsChecked;
        if (_autoResize) AutoResizeWindow();
    }

    private void SplitView_Click(object sender, RoutedEventArgs e)
    {
        _isSplitView = SplitViewItem.IsChecked;

        if (_isSplitView)
        {
            if (_isMarkdownMode)
            {
                _isMarkdownMode = false;
                PreviewScroll.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
                MarkdownToggleText.Text = Lang.T("markdown");
            }

            SplitPreviewMarkdown.Text = ConvertToMarkdown();
            SplitColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitPreviewPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SplitPreviewPanel.Visibility = Visibility.Collapsed;
            SplitColumn.Width = new GridLength(0);
        }

        Editor.Focus(FocusState.Programmatic);
    }

    private void AutoResizeWindow()
    {
        if (!_autoResize) return;

        Editor.Document.GetText(TextGetOptions.None, out var text);
        text = text.TrimEnd('\r', '\n');

        // Count logical lines
        var lines = string.IsNullOrEmpty(text) ? 1 : text.Split('\r').Length;

        // Estimate wrapped lines: editor width ~= window width - padding
        var editorWidth = Math.Max(400, AppWindow.Size.Width - 48);
        var avgCharWidth = (double)(BaseFontSize * _zoomFactor * 0.52); // approximate
        var textLines = string.IsNullOrEmpty(text) ? new[] { "" } : text.Split('\r');
        int totalVisualLines = 0;
        foreach (var line in textLines)
        {
            var lineWidth = line.Length * avgCharWidth;
            totalVisualLines += Math.Max(1, (int)Math.Ceiling(lineWidth / editorWidth));
        }

        var lineHeight = (double)(BaseFontSize * _zoomFactor * 1.5);
        var contentHeight = totalVisualLines * lineHeight + 24; // padding

        // Chrome: TitleBar(40) + MenuBar(~32) + Toolbar(~40) + StatusBar(~32) = ~144
        const int chromeHeight = 144;
        var totalHeight = (int)(chromeHeight + contentHeight);
        totalHeight = Math.Clamp(totalHeight, _autoResizeMinHeight, _autoResizeMaxHeight);

        var currentSize = AppWindow.Size;
        if (Math.Abs(currentSize.Height - totalHeight) > 10)
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(currentSize.Width, totalHeight));
        }
    }

    // ── Markdown toggle ─────────────────────────────────────────────

    private void MarkdownToggle_Click(object sender, RoutedEventArgs e)
    {
        _isMarkdownMode = !_isMarkdownMode;

        if (_isMarkdownMode)
        {
            PreviewMarkdown.Text = ConvertToMarkdown();
            Editor.Visibility = Visibility.Collapsed;
            PreviewScroll.Visibility = Visibility.Visible;
            AnimationHelper.FadeIn(PreviewScroll, 200);
            MarkdownToggleText.Text = Lang.T("text_mode");
            ToolTipService.SetToolTip(MarkdownToggle, Lang.T("back_to_editor"));
        }
        else
        {
            PreviewScroll.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            AnimationHelper.FadeIn(Editor, 200);
            MarkdownToggleText.Text = Lang.T("markdown");
            ToolTipService.SetToolTip(MarkdownToggle, Lang.T("markdown_preview"));
            Editor.Focus(FocusState.Programmatic);
        }
    }

    // ── Markdown conversion ──────────────────────────────────────

    private bool HasFormatting()
    {
        var doc = Editor.Document;
        doc.GetText(TextGetOptions.None, out var text);
        text = text.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(text)) return false;

        // Check RTF-level formatting (bold, italic, headings, links, bullets)
        int len = text.Length;
        int pos = 0;
        while (pos < len)
        {
            var range = doc.GetRange(pos, pos);
            int moved = range.MoveEnd(TextRangeUnit.CharacterFormat, 1);
            if (moved == 0 || range.EndPosition <= pos) { pos++; continue; }

            var fmt = range.CharacterFormat;
            if (fmt.Bold == FormatEffect.On) return true;
            if (fmt.Italic == FormatEffect.On) return true;
            if (fmt.Strikethrough == FormatEffect.On) return true;
            if (fmt.Underline != UnderlineType.None) return true;
            if (fmt.Size > BaseFontSize + 0.5f) return true;
            if (!string.IsNullOrEmpty(range.Link)) return true;
            if (range.ParagraphFormat.ListType == MarkerType.Bullet) return true;

            pos = range.EndPosition;
        }

        // Check for Markdown syntax in plain text
        foreach (var line in text.Split('\r'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ") || trimmed.StartsWith("## ") || trimmed.StartsWith("### ")) return true;
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")) return true;
            if (trimmed.StartsWith("> ")) return true;
            if (trimmed.StartsWith("```")) return true;
            if (trimmed.StartsWith("---") || trimmed.StartsWith("***") || trimmed.StartsWith("___")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\.\s")) return true;
            if (trimmed.Contains("**") || trimmed.Contains("~~") || trimmed.Contains("](")) return true;
        }

        return false;
    }

    private string ConvertToMarkdown()
    {
        var doc = Editor.Document;
        doc.GetText(TextGetOptions.None, out var fullText);
        fullText = fullText.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(fullText)) return "";

        var sb = new StringBuilder();
        int totalLen = fullText.Length;
        int pos = 0;

        while (pos < totalLen)
        {
            int paraEnd = fullText.IndexOf('\r', pos);
            if (paraEnd < 0) paraEnd = totalLen;

            if (paraEnd == pos)
            {
                sb.AppendLine();
                pos = paraEnd + 1;
                continue;
            }

            var paraRange = doc.GetRange(pos, paraEnd);
            bool isBullet = paraRange.ParagraphFormat.ListType == MarkerType.Bullet;

            // Detect heading from first character
            var firstChar = doc.GetRange(pos, pos + 1);
            float headingSize = firstChar.CharacterFormat.Size;
            string prefix = "";
            if (headingSize >= 24f) prefix = "# ";
            else if (headingSize >= 20f) prefix = "## ";
            else if (headingSize >= 16f) prefix = "### ";

            if (isBullet) prefix = "- ";

            sb.Append(prefix);

            bool isHeading = prefix.StartsWith('#');

            // Walk formatting runs within paragraph
            int runPos = pos;
            while (runPos < paraEnd)
            {
                var range = doc.GetRange(runPos, runPos);
                int moved = range.MoveEnd(TextRangeUnit.CharacterFormat, 1);
                if (moved == 0 || range.EndPosition <= runPos) { runPos++; continue; }

                if (range.EndPosition > paraEnd)
                    range.SetRange(runPos, paraEnd);

                var fmt = range.CharacterFormat;
                range.GetText(TextGetOptions.None, out var runText);
                runText = runText.TrimEnd('\r');

                bool bold = fmt.Bold == FormatEffect.On && !isHeading;
                bool italic = fmt.Italic == FormatEffect.On;
                bool strike = fmt.Strikethrough == FormatEffect.On;
                string link = range.Link?.Trim('"') ?? "";

                if (!string.IsNullOrEmpty(link))
                {
                    sb.Append($"[{runText}]({link})");
                }
                else
                {
                    if (strike) sb.Append("~~");
                    if (bold && italic) sb.Append("***");
                    else if (bold) sb.Append("**");
                    else if (italic) sb.Append('*');

                    sb.Append(runText);

                    if (bold && italic) sb.Append("***");
                    else if (bold) sb.Append("**");
                    else if (italic) sb.Append('*');
                    if (strike) sb.Append("~~");
                }

                runPos = range.EndPosition;
            }

            sb.AppendLine();
            pos = paraEnd + 1;
        }

        return sb.ToString().TrimEnd();
    }

    // ── Window chrome ────────────────────────────────────────────

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _isPinned;
        PinIcon.Glyph = _isPinned ? "\uE77A" : "\uE718";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => this.Close();

    // ── Drag ─────────────────────────────────────────────────────

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
