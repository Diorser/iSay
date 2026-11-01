using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace iSay.Desktop;

public partial class VoiceDeskWindow
{
    private Window? vocabularyDialog;
    internal Func<Window, bool?>? VocabularyDialogPresenter { get; set; }

    private void OpenVocabularyEditorDialog()
    {
        if (vocabularyDialog is not null) return;
        ShowVocabularyDialog(VocabularyWordEditorHost, VocabularyVm.VocabularyEditorTitle, false);
    }

    private void OpenVocabularySettingsDialog()
    {
        if (vocabularyDialog is not null) { vocabularyDialog.Close(); return; }
        ShowVocabularyDialog(VocabularyLearningSettings, UiText.Translate("学习设置"), true);
    }

    private void ShowVocabularyDialog(Border host, string title, bool settings)
    {
        CloseVocabularyHelp();
        var vm = VocabularyVm;
        var originalSettings = (vm.Settings.AsrVocabularyEnabled, vm.Settings.AutoVocabularyLearningEnabled, vm.Settings.KeyboardLearningEnabled, vm.SettingsDirty);
        var parent = (Panel)host.Parent;
        var index = parent.Children.IndexOf(host);
        var properties = new[] { DataContextProperty, Border.BackgroundProperty, Border.PaddingProperty, MarginProperty, Border.CornerRadiusProperty };
        var values = properties.Select(host.ReadLocalValue).ToArray();
        parent.Children.Remove(host);
        host.DataContext = vm; host.Background = Brushes.Transparent; host.Padding = new Thickness(); host.Margin = new Thickness(); host.CornerRadius = new CornerRadius();
        if (settings) host.Visibility = Visibility.Visible;
        var body = new StackPanel();
        if (settings) body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 22) });
        body.Children.Add(host);
        var dialog = new Window
        {
            Title = title + " · iSay", Width = settings ? 620 : 560, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 80), ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false, Background = Brushes.White, Foreground = Foreground, FontFamily = FontFamily, FontSize = FontSize,
            Icon = Icon, DataContext = vm, UseLayoutRounding = true,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new Border { Padding = new Thickness(24), Child = body } }
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        if (IsVisible) dialog.Owner = this;
        vocabularyDialog = dialog;
        var saved = false;
        var saving = false;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(179, 75, 65)), Margin = new Thickness(0, 12, 0, 0) };
        if (settings)
        {
            body.Children.Add(error);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var cancel = new Button { Content = UiText.Translate("取消"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var save = new Button { Content = UiText.Translate("保存设置"), Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Tag = "save-settings" };
            save.SetBinding(IsEnabledProperty, new Binding(nameof(vm.CanEditSettings)));
            cancel.SetBinding(IsEnabledProperty, new Binding(nameof(vm.CanEditSettings)));
            cancel.Click += (_, _) => dialog.Close();
            save.Click += (_, args) =>
            {
                saving = true; vm.SettingsDirty = true;
                OnActionClick(save, args);
                if (vm.SettingsDirty) error.Text = SettingsValidationLabel.Visibility == Visibility.Visible ? SettingsValidationLabel.Text : vm.StatusText;
            };
            actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions);
        }
        PropertyChangedEventHandler changes = (_, args) =>
        {
            if (!settings && args.PropertyName == nameof(vm.VocabularyEditorOpen) && !vm.VocabularyEditorOpen) dialog.Close();
            if (settings && saving && args.PropertyName == nameof(vm.SettingsDirty) && !vm.SettingsDirty) { saved = true; dialog.Close(); }
        };
        vm.PropertyChanged += changes;
        dialog.Closing += (_, args) => { if (settings && saving && vm.IsBusy && !saved) args.Cancel = true; };
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && !(settings && saving && vm.IsBusy)) { args.Handled = true; dialog.Close(); } };
        dialog.Loaded += (_, _) => { if (!settings) { WordEditor.Focus(); WordEditor.SelectAll(); } };
        try
        {
            // 默认显示真正的模态窗口；离屏回归可替换呈现方式，不弹窗、不抢用户前台。
            if (VocabularyDialogPresenter is { } presenter) presenter(dialog); else dialog.ShowDialog();
        }
        finally
        {
            vm.PropertyChanged -= changes;
            vocabularyDialog = null;
            body.Children.Remove(host);
            for (var property = 0; property < properties.Length; property++)
                if (values[property] == DependencyProperty.UnsetValue) host.ClearValue(properties[property]); else host.SetValue(properties[property], values[property]);
            parent.Children.Insert(Math.Min(index, parent.Children.Count), host);
            if (settings)
            {
                host.Visibility = Visibility.Collapsed;
                if (!saved)
                {
                    (vm.Settings.AsrVocabularyEnabled, vm.Settings.AutoVocabularyLearningEnabled, vm.Settings.KeyboardLearningEnabled) = (originalSettings.Item1, originalSettings.Item2, originalSettings.Item3);
                    vm.SettingsDirty = originalSettings.Item4;
                    // 设置对象本身没有属性通知，重新通知绑定以显示取消后恢复的开关。
                    vm.Settings = vm.Settings;
                }
            }
            else vm.CancelVocabularyEdit();
        }
    }
}
