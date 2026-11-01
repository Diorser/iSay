using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iSay.Voice;

namespace iSay.Desktop;

public partial class VoiceDeskWindow
{
    private VoiceViewModel? managementVm;
    private bool importDialogRequested;
    private Border? managementHost;

    private void ConnectManagement()
    {
        if (managementVm is not null) managementVm.PropertyChanged -= OnManagementChanged;
        managementVm = DataContext as VoiceViewModel;
        if (managementVm is not null) managementVm.PropertyChanged += OnManagementChanged;
    }

    private void OnManagementChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(VoiceViewModel.HasVocabularyImport) || !importDialogRequested || managementVm?.HasVocabularyImport != true) return;
        importDialogRequested = false;
        // 等导入预览业务完成，才呈现确认窗口；不在属性通知中重入业务流程。
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (VocabularyVm.HasVocabularyImport && vocabularyDialog is null)
                ShowManagementDialog(VocabularyImportHost, UiText.Translate("确认导入热词"), false, nameof(VoiceViewModel.HasVocabularyImport),
                    () => { if (VocabularyVm.HasVocabularyImport) OnCancelWordImport(this, new RoutedEventArgs()); });
        }));
    }

    private void OnOpenManagementClick(object sender, RoutedEventArgs args)
    {
        if (!VocabularyVm.CanEditSettings || vocabularyDialog is not null || sender is not FrameworkElement { Tag: string key }) return;
        var (host, title, editable) = key switch
        {
            "format-api" => (FormattingApiHost, UiText.Translate("自定义 AI 接口"), true),
            "format-rules" => (FormattingRulesHost, UiText.Translate("词典与额外要求"), true),
            "speech-service" => (SpeechServiceHost, UiText.Translate("语音服务配置"), true),
            "proxy" => (ProxySettingsHost, UiText.Translate("自定义代理地址"), true),
            "diagnostics" => (DiagnosticsHost, UiText.Translate("服务诊断"), false),
            "input-tools" => (InputToolsHost, UiText.Translate("输入兼容性"), false),
            "storage" => (StorageSettingsHost, UiText.Translate("记录与存储"), true),
            "data-tools" => (DataToolsHost, UiText.Translate("本机数据工具"), false),
            _ => throw new ArgumentException(UiText.Translate("未知管理入口"))
        };
        ShowManagementDialog(host, title, editable);
    }

    private void ShowManagementDialog(Border host, string title, bool editable, string? closeProperty = null, Action? cleanup = null, TextBox? focus = null)
    {
        if (vocabularyDialog is not null) return;
        CloseVocabularyHelp(); EndHotkeyCapture();
        var vm = VocabularyVm;
        var settingsObject = vm.Settings;
        var snapshot = typeof(VoiceSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite).ToDictionary(property => property, property => property.GetValue(settingsObject));
        var dirty = vm.SettingsDirty;
        var keyInputs = (ApiKeyBox.Password, AsrApiKeyBox.Password);
        var parent = (Panel)host.Parent;
        var index = parent.Children.IndexOf(host);
        var properties = new[] { DataContextProperty, Border.BackgroundProperty, Border.PaddingProperty, MarginProperty, Border.CornerRadiusProperty, WidthProperty };
        var locals = properties.Select(host.ReadLocalValue).ToArray();
        var visibility = host.Visibility;
        parent.Children.Remove(host);
        host.DataContext = vm; host.Background = Brushes.Transparent; host.Padding = new Thickness(); host.Margin = new Thickness(); host.CornerRadius = new CornerRadius();
        // 预留细滚动条的宽度，避免弹窗按内容定高时反复换行并产生多余滚动条。
        host.Width = Math.Min(680, SystemParameters.WorkArea.Width - 40) - 68;
        if (closeProperty is null) host.Visibility = Visibility.Visible;
        managementHost = host;
        var maxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 80);
        var body = new DockPanel { LastChildFill = true, Margin = new Thickness(26), MaxHeight = maxHeight - 50 };
        var heading = new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 22) };
        DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        footer.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = editable ? UiText.Translate("取消") : UiText.Translate("关闭"), IsCancel = true, MinWidth = 76 };
        cancel.SetBinding(IsEnabledProperty, new Binding(nameof(vm.CanEditSettings)));
        if (closeProperty is null && host != ClearLearningConfirmation) buttons.Children.Add(cancel);
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var scroll = new ScrollViewer { Style = (Style)FindResource("FullPageScroll"), MaxHeight = maxHeight - 215, Content = host };
        scroll.SetBinding(IsEnabledProperty, new Binding(nameof(vm.CanEditSettings)));
        body.Children.Add(scroll);
        var dialog = new Window
        {
            Title = title + " · iSay", Width = 680, MaxWidth = SystemParameters.WorkArea.Width - 40, SizeToContent = SizeToContent.Height,
            MaxHeight = maxHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Brushes.White, Foreground = Foreground, FontFamily = FontFamily, FontSize = FontSize, Icon = Icon,
            UseLayoutRounding = true, DataContext = vm, Content = body
        };
        dialog.Resources.MergedDictionaries.Add(Resources);
        if (IsVisible) dialog.Owner = this;
        vocabularyDialog = dialog;
        var saved = false; var saving = false;
        var initialStatus = vm.StatusText;
        status.Text = "";
        cancel.Click += (_, _) => dialog.Close();
        if (editable)
        {
            var save = new Button { Content = UiText.Translate("保存设置"), IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0), Tag = "save-settings" };
            save.SetBinding(IsEnabledProperty, new Binding(nameof(vm.CanEditSettings)));
            save.Click += (_, args) =>
            {
                saving = true; vm.SettingsDirty = true;
                OnActionClick(save, args);
                if (vm.SettingsDirty) status.Text = SettingsValidationLabel.Visibility == Visibility.Visible ? SettingsValidationLabel.Text : vm.StatusText;
            };
            buttons.Children.Add(save);
        }
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (editable && saving && args.PropertyName == nameof(vm.SettingsDirty) && !vm.SettingsDirty) { saved = true; dialog.Close(); }
            if (closeProperty is not null && args.PropertyName == closeProperty && typeof(VoiceViewModel).GetProperty(closeProperty)?.GetValue(vm) is false) { saved = true; dialog.Close(); }
            if (args.PropertyName == nameof(vm.StatusText) && vm.StatusText != initialStatus) status.Text = vm.StatusText;
        };
        vm.PropertyChanged += changed;
        host.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnSettingsTextChanged));
        host.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSettingsSelectionChanged));
        host.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnSettingsToggleChanged));
        host.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(OnSettingsToggleChanged));
        dialog.Closing += (_, args) => { if (!saved && (vm.IsBusy || vm.VocabularyWorking)) args.Cancel = true; };
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && !vm.IsBusy && !vm.VocabularyWorking) { args.Handled = true; dialog.Close(); } };
        dialog.Loaded += (_, _) => { focus?.Focus(); focus?.SelectAll(); };
        try
        {
            if (VocabularyDialogPresenter is { } presenter) presenter(dialog); else dialog.ShowDialog();
        }
        finally
        {
            vm.PropertyChanged -= changed;
            host.RemoveHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnSettingsTextChanged));
            host.RemoveHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSettingsSelectionChanged));
            host.RemoveHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnSettingsToggleChanged));
            host.RemoveHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(OnSettingsToggleChanged));
            scroll.Content = null;
            managementHost = null; vocabularyDialog = null;
            for (var property = 0; property < properties.Length; property++)
                if (locals[property] == DependencyProperty.UnsetValue) host.ClearValue(properties[property]); else host.SetValue(properties[property], locals[property]);
            parent.Children.Insert(Math.Min(index, parent.Children.Count), host);
            if (closeProperty is null) host.Visibility = visibility;
            if (editable && !saved)
            {
                // 取消只还原本次弹窗草稿，保留打开前未保存的设置与密钥，不写磁盘。
                foreach (var pair in snapshot) pair.Key.SetValue(settingsObject, pair.Value);
                suppressSettingsTracking = true;
                vm.Settings = settingsObject; vm.SettingsDirty = dirty;
                ApiKeyBox.Password = keyInputs.Item1; AsrApiKeyBox.Password = keyInputs.Item2;
                SettingsValidationLabel.Visibility = Visibility.Collapsed; VocabularySettingsError.Visibility = Visibility.Collapsed;
                ResumeSettingsTrackingAfterBindings();
            }
            cleanup?.Invoke();
        }
    }
}
