using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using iSay.Voice;

namespace iSay.Desktop;

public sealed class ActionRequestedEventArgs : EventArgs
{
    public ActionRequestedEventArgs(string action, object? argument = null)
    {
        Action = action;
        Argument = argument;
    }

    public string Action { get; }
    public object? Argument { get; }
}

public partial class VoiceDeskWindow : Window
{
    private ICollectionView? historyView;
    private const int HistoryPageSize = 100;
    private int historyPageIndex;
    private bool suppressSettingsTracking = true;
    private int suppressionVersion;
    private HotkeyCaptureSession? hotkeyCapture;
    public int HotkeyCaptureVersion { get; private set; }
    private readonly DispatcherTimer hotkeyCaptureTimeout = new() { Interval = TimeSpan.FromSeconds(15) };
    public bool IsCapturingHotkey => hotkeyCapture is not null;
    private readonly System.Collections.Generic.Dictionary<TextBox, bool> transcriptFollowing = new();
    private readonly System.Collections.Generic.HashSet<TextBox> transcriptScrollPending = new();

    public VoiceDeskWindow()
    {
        InitializeComponent();
        foreach (var box in new[] { ResultTranscriptBox, RawTranscriptBox })
        {
            transcriptFollowing[box] = true;
            box.TextChanged += OnTranscriptChanged;
            box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnTranscriptScrolled));
        }
        DataContextChanged += (_, _) => { ConnectHistory(); ConnectManagement(); ConnectAbout(); };
        Loaded += OnLoaded;
        foreach (var area in new FrameworkElement[] { SettingsContent, VocabularyContent, FormattingContent, VocabularyPage })
        {
            // 热词总开关在标签页外，其余设置各自监听，避免把历史搜索当成设置修改。
            if (area == VocabularyContent) continue;
            area.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnSettingsTextChanged));
            area.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSettingsSelectionChanged));
            area.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnSettingsToggleChanged));
            area.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnSettingsToggleChanged));
        }
        PreviewKeyDown += OnCaptureKeyDown;
        PreviewKeyUp += OnCaptureKeyUp;
        Deactivated += (_, _) => { CloseVocabularyHelp(); EndHotkeyCapture(UiText.Translate("录入已取消，快捷键未更改。")); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CloseVocabularyHelp(); EndHotkeyCapture(UiText.Translate("录入已取消，快捷键未更改。")); } };
        Closed += (_, _) => EndHotkeyCapture();
        // 仅为右侧正文预留保存栏空间，让左侧导航保持贯穿窗口。
        ConfigurationFooter.SizeChanged += (_, _) => UpdateFooterSpacing();
        ConfigurationFooter.IsVisibleChanged += (_, _) => UpdateFooterSpacing();
        NavigationTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, NavigationTabs)) { CloseVocabularyHelp(); EndHotkeyCapture(UiText.Translate("录入已取消，快捷键未更改。")); }
        };
        VocabularyHelpButton.LostKeyboardFocus += (_, _) => CloseVocabularyHelp();
        VocabularyHelpButton.MouseLeave += (_, _) => CloseVocabularyHelp();
        vocabularyHelpTimer.Tick += (_, _) => CloseVocabularyHelp();
        Closed += (_, _) => CloseVocabularyHelp();
        Closed += (_, _) => { if (managementVm is not null) managementVm.PropertyChanged -= OnManagementChanged; };
        HotkeyCaptureButton.LostKeyboardFocus += (_, _) => EndHotkeyCapture(UiText.Translate("录入已取消，快捷键未更改。"));
        hotkeyCaptureTimeout.Tick += (_, _) => EndHotkeyCapture(UiText.Translate("未收到完整按键，请点击后重试。Fn 通常无法单独识别。"));
    }

    public event EventHandler<ActionRequestedEventArgs>? ActionRequested;

    private void UpdateFooterSpacing() => NavigationTabs.Padding = new Thickness(0, 0, 0,
        ConfigurationFooter.Visibility == Visibility.Visible ? ConfigurationFooter.ActualHeight + ConfigurationFooter.Margin.Bottom : 0);

    private void OnForgetLearnedWordClick(object sender, RoutedEventArgs e)
    {
        if (LearnedWordList.SelectedItem is VocabularyLearnedEntry entry)
            ActionRequested?.Invoke(this, new("forget-learned-word", entry));
    }

    public void ShowInputPage() => NavigationTabs.SelectedIndex = 0;

    private void OnTranscriptScrolled(object sender, ScrollChangedEventArgs e)
    {
        // 只把主动滚动视作阅读位置变化，正文增长不会关闭自动跟随。
        if (sender is TextBox box && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
            transcriptFollowing[box] = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2;
    }

    private void OnTranscriptChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || DataContext is not VoiceViewModel { IsRecording: true }) return;
        if (box.Text.Length == 0) transcriptFollowing[box] = true;
        if (!transcriptFollowing[box] || !transcriptScrollPending.Add(box)) return;
        // 合并同一帧的累计快照；只滚视口，不移动用户光标或抢占焦点。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            transcriptScrollPending.Remove(box);
            if (DataContext is VoiceViewModel { IsRecording: true } && transcriptFollowing[box]) box.ScrollToEnd();
        }));
    }

    public void ShowSettingsPage() => SettingsPage.IsSelected = true;

    public void ShowFormattingSettings()
    {
        FormattingPage.IsSelected = true;
    }

    private void OnStartHotkeyCapture(object sender, RoutedEventArgs e) => StartHotkeyCapture();
    private void OnCancelHotkeyCapture(object sender, RoutedEventArgs e) => EndHotkeyCapture(UiText.Translate("已取消，快捷键未更改。"));

    public void StartHotkeyCapture()
    {
        if (DataContext is not VoiceViewModel { CanEditSettings: true }) return;
        EndHotkeyCapture();
        hotkeyCapture = new();
        HotkeyCaptureVersion++;
        HotkeyCaptureButton.Focus();
        HotkeyCaptureButton.SetCurrentValue(Control.BorderBrushProperty, FindResource("Accent"));
        HotkeyCaptureValue.SetCurrentValue(TextBlock.TextProperty, UiText.Translate("请按下快捷键…"));
        HotkeyRecorderLabel.Text = UiText.Translate("正在录入");
        HotkeyCaptureHint.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        HotkeyCaptureHint.Text = UiText.Translate("按下组合后松开确认。Esc 取消，15 秒未操作自动退出。");
        CancelHotkeyCaptureButton.Visibility = Visibility.Visible;
        hotkeyCaptureTimeout.Start();
        ActionRequested?.Invoke(this, new("begin-hotkey-capture"));
    }

    public void ProcessCapturedHotkey(int virtualKey, bool down)
    {
        if (hotkeyCapture is not { } capture) return;
        var result = capture.Process(virtualKey, down);
        if (result.Status == HotkeyCaptureStatus.Accepted && DataContext is VoiceViewModel vm)
        {
            var changed = vm.DraftHotkey != result.Canonical;
            vm.DraftHotkey = result.Canonical!;
            EndHotkeyCapture(changed ? UiText.Translate("快捷键已录入，保存设置后生效。") : UiText.Translate("快捷键未更改。"));
            if (changed) ActionRequested?.Invoke(this, new("settings-changed"));
        }
        else if (result.Status == HotkeyCaptureStatus.Cancelled) EndHotkeyCapture(UiText.Translate("已取消，快捷键未更改。"));
        else if (result.Status == HotkeyCaptureStatus.Invalid)
        {
            HotkeyCaptureValue.SetCurrentValue(TextBlock.TextProperty, UiText.Translate("请重新按键…"));
            HotkeyCaptureHint.Text = result.Display;
            HotkeyCaptureHint.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
        else HotkeyCaptureValue.SetCurrentValue(TextBlock.TextProperty, result.Display);
    }

    public void EndHotkeyCapture(string? message = null)
    {
        if (hotkeyCapture is null) return;
        hotkeyCapture = null;
        HotkeyCaptureVersion++;
        hotkeyCaptureTimeout.Stop();
        HotkeyCaptureValue.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        HotkeyRecorderLabel.Text = UiText.Translate("点击录入");
        HotkeyCaptureButton.ClearValue(Control.BorderBrushProperty);
        HotkeyCaptureHint.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        HotkeyCaptureHint.Text = message ?? UiText.Translate("点击后按快捷键，松开确认；Esc 取消。Fn 通常无法单独识别。");
        CancelHotkeyCaptureButton.Visibility = Visibility.Collapsed;
        ActionRequested?.Invoke(this, new("end-hotkey-capture"));
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e) => CaptureLocalKey(e, true);
    private void OnCaptureKeyUp(object sender, KeyEventArgs e) => CaptureLocalKey(e, false);
    private void CaptureLocalKey(KeyEventArgs e, bool down)
    {
        if (!IsCapturingHotkey) return;
        // 注入事件及无全局钩子的独立实例仍通过本窗口处理；真实按键由现有低层钩子先截获。
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        e.Handled = true;
        ProcessCapturedHotkey(KeyInterop.VirtualKeyFromKey(key), down);
    }

    public void MarkSettingsSaved()
    {
        // 清空密钥占位时不发送修改事件，实际密钥仍由设置服务安全保留。
        suppressSettingsTracking = true;
        SettingsValidationLabel.Text = "";
        SettingsValidationLabel.Visibility = Visibility.Collapsed;
        VocabularySettingsError.Text = "";
        VocabularySettingsError.Visibility = Visibility.Collapsed;
        ApiKeyBox.Clear();
        AsrApiKeyBox.Clear();
        ResumeSettingsTrackingAfterBindings();
    }

    public void ClearKeyInput(bool speechKey)
    {
        // 删除保存值由独立动作处理；清空显示框不能意外恢复上一次的密钥。
        suppressSettingsTracking = true;
        if (speechKey) AsrApiKeyBox.Clear(); else ApiKeyBox.Clear();
        ResumeSettingsTrackingAfterBindings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConnectHistory();
        ResumeSettingsTrackingAfterBindings();
    }

    private void ResumeSettingsTrackingAfterBindings()
    {
        var version = ++suppressionVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (version == suppressionVersion) suppressSettingsTracking = false;
        }));
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action }) return;
        if (action == "retry-composition" && managementHost is not null) vocabularyDialog?.Close();

        if (action == "save-settings")
        {
            // 提交仍在编辑中的数字，错误在保存栏提示，不打断用户操作。
            if (Keyboard.FocusedElement is TextBox focusedTextBox)
                focusedTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            if (HasValidationError(SettingsContent) || HasValidationError(VocabularyContent) || HasValidationError(FormattingContent) || (managementHost is not null && HasValidationError(managementHost)))
            {
                ShowSettingsError(UiText.Translate("请检查红色边框中的数字，填写有效数值后再保存。"));
                return;
            }

            try
            {
                if (DataContext is VoiceViewModel vm) vm.Settings.Validate();
            }
            catch (ArgumentException error)
            {
                ShowSettingsError(error.Message);
                return;
            }
        }

        if (action is "preview-vocabulary-file" or "preview-official-vocabulary") importDialogRequested = true;
        ActionRequested?.Invoke(this, new ActionRequestedEventArgs(action));
    }

    private void ShowSettingsError(string message)
    {
        SettingsValidationLabel.Text = message;
        SettingsValidationLabel.Visibility = Visibility.Visible;
        VocabularySettingsError.Text = message;
        VocabularySettingsError.Visibility = Visibility.Visible;
    }

    private void OnCopyTranscriptClick(object sender, RoutedEventArgs e) =>
        ActionRequested?.Invoke(this, new ActionRequestedEventArgs(TranscriptTabs.SelectedIndex == 1 ? "copy-raw" : "copy-transcript"));

    private void OnFormattingSettingsClick(object sender, RoutedEventArgs e) => ShowFormattingSettings();

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsTracking) return;
        // 密钥只通过事件交给设置服务，不放入可见文字或普通绑定。
        ActionRequested?.Invoke(this, new ActionRequestedEventArgs("api-key-changed", ((PasswordBox)sender).Password));
        MarkSettingsChanged((FrameworkElement)sender);
    }

    private void OnAsrApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsTracking) return;
        ActionRequested?.Invoke(this, new ActionRequestedEventArgs("asr-api-key-changed", ((PasswordBox)sender).Password));
        MarkSettingsChanged((FrameworkElement)sender);
    }

    private void OnSettingsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox textBox) MarkSettingsChanged(textBox);
    }

    private void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 语言选择有独立的即时切换和草稿通知，避免移入设置后重复发送修改事件。
        if (e.OriginalSource is ComboBox comboBox && !ReferenceEquals(comboBox, LanguagePicker)) MarkSettingsChanged(comboBox);
    }

    private void OnSettingsToggleChanged(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is CheckBox checkBox) MarkSettingsChanged(checkBox);
    }

    private void MarkSettingsChanged(FrameworkElement control)
    {
        // 初次绑定、撤销及程序重绑不会被误记为用户编辑。
        if (suppressSettingsTracking || !IsLoaded || !control.IsKeyboardFocusWithin) return;
        if (control.Tag is string tag && tag == "local-vocabulary-editor") return;
        SettingsValidationLabel.Visibility = Visibility.Collapsed;
        ActionRequested?.Invoke(this, new ActionRequestedEventArgs("settings-changed"));
        VocabularySettingsError.Visibility = Visibility.Collapsed;
    }

    private void ConnectHistory()
    {
        if (historyView is not null) historyView.CollectionChanged -= OnHistoryViewChanged;
        // 每个窗口独立过滤视图，不改变历史集合以及其他视图的排序与筛选。
        historyView = DataContext is VoiceViewModel vm ? new ListCollectionView(vm.History) : null;
        if (historyView is not null)
        {
            historyView.Filter = MatchesHistory;
            historyView.SortDescriptions.Add(new SortDescription(nameof(HistoryEntry.CreatedAt), ListSortDirection.Descending));
            historyView.CollectionChanged += OnHistoryViewChanged;
        }
        historyPageIndex = 0;
        RefreshHistoryPage();
    }

    private bool MatchesHistory(object item)
    {
        if (item is not HistoryEntry entry) return false;
        var query = HistorySearchBox.Text.Trim();
        if (query.Length == 0) return true;
        return entry.RawText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || entry.FormattedText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || entry.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture).Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || entry.CreatedAt.ToString("M月d日", CultureInfo.CurrentCulture).Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnHistorySearchChanged(object sender, TextChangedEventArgs e)
    {
        historyPageIndex = 0;
        historyView?.Refresh();
        RefreshHistoryPage();
    }

    private void OnHistoryViewChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshHistoryPage();

    private void RefreshHistoryPage()
    {
        if (HistoryList is null || HistoryResultCount is null) return;
        var matches = historyView?.Cast<HistoryEntry>().ToArray() ?? [];
        var pages = Math.Max(1, (matches.Length + HistoryPageSize - 1) / HistoryPageSize);
        historyPageIndex = Math.Clamp(historyPageIndex, 0, pages - 1);
        var selected = HistoryList.SelectedItem;
        var page = matches.Skip(historyPageIndex * HistoryPageSize).Take(HistoryPageSize).ToArray();
        // 搜索刷新、集合通知和页面刷新可能连续到达，同一页不重复销毁重建一百个正文控件。
        if (HistoryList.ItemsSource is not HistoryEntry[] displayed || !displayed.SequenceEqual(page))
            HistoryList.ItemsSource = page;
        if (selected is not null && HistoryList.Items.Contains(selected)) HistoryList.SelectedItem = selected;
        HistoryResultCount.Text = UiText.Translate(matches.Length == 0 ? "0 条记录" : $"共 {matches.Length} 条 · 当前 {historyPageIndex * HistoryPageSize + 1}–{Math.Min((historyPageIndex + 1) * HistoryPageSize, matches.Length)} 条");
        HistoryPageNumber.Text = $"{historyPageIndex + 1} / {pages}";
        HistoryPreviousPage.IsEnabled = historyPageIndex > 0;
        HistoryNextPage.IsEnabled = historyPageIndex + 1 < pages;
        UpdateHistoryEmptyState();
    }

    private void OnHistoryPageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } || !int.TryParse(direction, out var delta)) return;
        historyPageIndex += delta;
        RefreshHistoryPage();
        HistoryPageScroll.ScrollToTop();
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中记录不会改变草稿，只有明确点击载入才切换到编辑页面。
        UpdateHistoryEmptyState();
    }

    private void UpdateHistoryEmptyState()
    {
        if (HistoryEmptyLabel is null) return;
        HistoryEmptyLabel.Text = string.IsNullOrWhiteSpace(HistorySearchBox.Text) ? UiText.Translate("还没有历史记录") : UiText.Translate("没有匹配的记录");
        HistoryEmptyLabel.Visibility = historyView is null || historyView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHistoryMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { IsEnabled: true, ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnHistoryActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action } element) return;
        // 菜单使用其所属记录，不能回退到列表中另一条已选记录。
        if (element.DataContext is not HistoryEntry entry) return;
        if (action is "history-load" or "archive-audio" && DataContext is VoiceViewModel { CanEditSettings: false }) return;
        HistoryFeedbackText.Visibility = Visibility.Visible;
        ActionRequested?.Invoke(this, new ActionRequestedEventArgs(action, entry));
        if (action == "history-load") ShowInputPage();
    }
    private void OnForgetCorrectionClick(object sender, RoutedEventArgs e)
    {
        if (PersonalCorrectionList.SelectedItem is PersonalCorrectionEntry correction)
            ActionRequested?.Invoke(this, new ActionRequestedEventArgs("forget-correction", correction));
    }

    private void OnForgetRecognitionWordClick(object sender, RoutedEventArgs e)
    {
        if (RecognitionWordList.SelectedItem is RecognitionVocabularyEntry entry)
            ActionRequested?.Invoke(this, new ActionRequestedEventArgs("forget-recognition-word", entry));
    }

    private void OnPreferRecognitionWordClick(object sender, RoutedEventArgs e)
    {
        if (RecognitionWordList.SelectedItem is RecognitionVocabularyEntry entry)
            ActionRequested?.Invoke(this, new ActionRequestedEventArgs("toggle-preferred-word", entry));
    }

    private static bool HasValidationError(DependencyObject target)
    {
        if (Validation.GetHasError(target)) return true;
        foreach (object child in LogicalTreeHelper.GetChildren(target))
            if (child is DependencyObject dependencyChild && HasValidationError(dependencyChild)) return true;
        return false;
    }
}
