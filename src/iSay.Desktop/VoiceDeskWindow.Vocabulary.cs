using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using iSay.Voice;

namespace iSay.Desktop;

public partial class VoiceDeskWindow
{
    private string? pendingClearSource;
    private readonly DispatcherTimer vocabularyHelpTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private VoiceViewModel VocabularyVm => (VoiceViewModel)DataContext;
    private void OnToggleVocabularyLearningSettings(object sender, RoutedEventArgs e)
    {
        OpenVocabularySettingsDialog();
    }
    private void OnVocabularyHelpClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: ToolTip tip })
        {
            tip.PlacementTarget = (UIElement)sender; tip.IsOpen = !tip.IsOpen;
            vocabularyHelpTimer.Stop(); if (tip.IsOpen) vocabularyHelpTimer.Start();
        }
    }
    private void CloseVocabularyHelp()
    {
        if (VocabularyHelpButton.ToolTip is ToolTip tip) tip.IsOpen = false;
        vocabularyHelpTimer.Stop();
    }
    private void EditWord(RecognitionVocabularyEntry? entry, string? initial = null)
    {
        if (!VocabularyVm.CanManageVocabulary) return;
        VocabularyVm.VocabularyStatusText = "";
        VocabularyVm.BeginVocabularyEdit(entry, initial);
        OpenVocabularyEditorDialog();
    }
    private void OnAddWordClick(object sender, RoutedEventArgs e) => EditWord(null);
    private void OnQuickWordSearch(object sender, RoutedEventArgs e)
    {
        VocabularyVm.VocabularySearch = VocabularyVm.VocabularyQuickText.Trim();
        VocabularyPageScroll.ScrollToTop();
    }
    private void OnQuickWordAdd(object sender, RoutedEventArgs e)
    {
        if (VocabularyVm.CanManageVocabulary)
            ActionRequested?.Invoke(this, new("quick-add-recognition-word", VocabularyVm.VocabularyQuickText));
    }
    private void OnQuickWordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.Control) OnQuickWordAdd(sender, e); else OnQuickWordSearch(sender, e);
        }
        else if (e.Key == Key.Escape) { VocabularyVm.VocabularyQuickText = ""; OnQuickWordSearch(sender, e); e.Handled = true; }
    }
    private void OnCancelWordEdit(object sender, RoutedEventArgs e) => VocabularyVm.CancelVocabularyEdit();
    private void OnSaveWordClick(object sender, RoutedEventArgs e)
    {
        if (!VocabularyVm.CanManageVocabulary) return;
        var entry = VocabularyVm.EditingVocabularyEntry;
        ActionRequested?.Invoke(this, entry is null ? new("remember-recognition-word") : new("rename-recognition-word", new VocabularyEditRequest(entry, VocabularyVm.VocabularyDraft)));
    }
    private void OnWordEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnSaveWordClick(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; VocabularyVm.CancelVocabularyEdit(); }
    }
    private void OnWordDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecognitionVocabularyEntry entry })
            ActionRequested?.Invoke(this, new("delete-vocabulary-words", new[] { entry }));
    }
    private void OnWordMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: RecognitionVocabularyEntry entry, Tag: string command }) return;
        if (command == "edit") EditWord(entry);
        else if (command == "speak") ActionRequested?.Invoke(this, new("speak-vocabulary-word", entry.Word));
        else if (command == "prefer") ActionRequested?.Invoke(this, new("toggle-preferred-word", entry));
        else if (command == "delete") ActionRequested?.Invoke(this, new("delete-vocabulary-words", new[] { entry }));
    }
    private void OnWordDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != RecognitionWordList;
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.OriginalSource is FrameworkElement { DataContext: RecognitionVocabularyEntry entry } &&
            e.OriginalSource is not Button) EditWord(entry);
    }
    private void OnWordListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && RecognitionWordList.SelectedItem is RecognitionVocabularyEntry entry) { e.Handled = true; EditWord(entry); }
        else if (e.Key == Key.Delete) { e.Handled = true; OnDeleteSelectedWords(sender, e); }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true; RecognitionWordList.SelectAll();
        }
    }
    private void OnWordSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeleteSelectedWords is not null) DeleteSelectedWords.Content = UiText.Translate(RecognitionWordList.SelectedItems.Count > 0 ? $"删除选中（{RecognitionWordList.SelectedItems.Count}）" : "删除选中");
    }
    private void OnDeleteSelectedWords(object sender, RoutedEventArgs e)
    {
        var entries = RecognitionWordList.SelectedItems.OfType<RecognitionVocabularyEntry>().ToArray();
        if (entries.Length > 0) ActionRequested?.Invoke(this, new("delete-vocabulary-words", entries));
    }
    private void OnVocabularyFilterChanged(object sender, RoutedEventArgs e)
    {
        if (VocabularyPageScroll is not null) VocabularyPageScroll.ScrollToTop();
    }
    private void OnVocabularyTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, VocabularyContent)) VocabularyPageScroll.ScrollToTop();
    }
    private void OnOpenWordTools(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; button.ContextMenu!.PlacementTarget = button; button.ContextMenu.IsOpen = true;
    }
    private void OnWordSortClick(object sender, RoutedEventArgs e)
    {
        VocabularyVm.VocabularySort = ((MenuItem)sender).Tag.ToString()!; VocabularyPageScroll.ScrollToTop();
    }
    private void OnCancelWordImport(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(this, new("cancel-vocabulary-import"));
    private void OnLearningMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LearningToken token, Tag: string command }) return;
        if (command == "speak") ActionRequested?.Invoke(this, new("speak-vocabulary-word", token.Word));
        else if (command == "keep") EditWord(token.ManualEntry, token.Word); else HandleLearningToken(token);
    }
    private void OnLearningTokenClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: LearningToken token }) HandleLearningToken(token); }
    private void HandleLearningToken(LearningToken token)
    {
        if (!VocabularyVm.CanManageVocabulary) return;
        if (token.Origin == "keyboard") EditWord(token.ManualEntry, token.Word);
        else if (token.Origin == "ignored") ActionRequested?.Invoke(this, new("restore-learning-word", token.Word));
        else if (token.Entry is not null) ActionRequested?.Invoke(this, new("forget-learned-word", token.Entry));
    }
    private void OnRequestClearLearning(object sender, RoutedEventArgs e)
    {
        if (!VocabularyVm.CanManageVocabulary || VocabularyVm.LearningSource == "ignored") return;
        pendingClearSource = VocabularyVm.LearningSource;
        ClearLearningMessage.Text = pendingClearSource == "keyboard" ? UiText.Translate("清空键盘学习词库？此操作无法撤销，我的热词和纠错规则会保留。") : UiText.Translate("清空语音学到的词？这些词将进入忽略名单，可以在“已忽略的词”中恢复学习。");
        ClearLearningConfirmation.Visibility = Visibility.Visible;
        ShowManagementDialog(ClearLearningConfirmation, UiText.Translate("清空学习词库"), false, cleanup: () => OnCancelClearLearning(this, new RoutedEventArgs()));
    }
    private void OnCancelClearLearning(object sender, RoutedEventArgs e) { pendingClearSource = null; ClearLearningConfirmation.Visibility = Visibility.Collapsed; if (managementHost == ClearLearningConfirmation) vocabularyDialog?.Close(); }
    private void OnConfirmClearLearning(object sender, RoutedEventArgs e)
    {
        var source = pendingClearSource; OnCancelClearLearning(sender, e);
        if (source is not null) ActionRequested?.Invoke(this, new(source == "keyboard" ? "clear-keyboard-learning" : "clear-learned-words"));
    }
    private void OnAddCorrectionClick(object sender, RoutedEventArgs e) => EditCorrection(null);
    private void EditCorrection(PersonalCorrectionEntry? entry)
    {
        if (!VocabularyVm.CanManageVocabulary) return;
        VocabularyVm.BeginCorrectionEdit(entry);
        ShowManagementDialog(CorrectionEditorHost, VocabularyVm.CorrectionEditorTitle, false, nameof(VoiceViewModel.CorrectionEditorOpen), VocabularyVm.CancelCorrectionEdit, CorrectionMistakenEditor);
    }
    private void OnEditCorrectionClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: PersonalCorrectionEntry entry }) EditCorrection(entry); }
    private void OnDeleteCorrectionClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: PersonalCorrectionEntry entry }) ActionRequested?.Invoke(this, new("forget-correction", entry)); }
    private void OnCancelCorrectionEdit(object sender, RoutedEventArgs e) => VocabularyVm.CancelCorrectionEdit();
    private void OnSaveCorrectionClick(object sender, RoutedEventArgs e)
    {
        if (!VocabularyVm.CanManageVocabulary) return;
        var entry = VocabularyVm.EditingCorrectionEntry;
        ActionRequested?.Invoke(this, entry is null ? new("remember-correction") : new("edit-correction", new CorrectionEditRequest(entry, VocabularyVm.MistakenWord, VocabularyVm.CorrectedWord)));
    }
    private void OnCorrectionEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnSaveCorrectionClick(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; VocabularyVm.CancelCorrectionEdit(); }
    }
}
