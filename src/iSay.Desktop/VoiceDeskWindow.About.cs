using System.Windows;
using System.Windows.Controls;

namespace iSay.Desktop;

public partial class VoiceDeskWindow
{
    private string updateStatus = "尚未检查更新";
    private bool aboutConnected;

    private void ConnectAbout()
    {
        if (DataContext is VoiceViewModel vm) UiText.Current.SetLanguage(vm.Settings.UiLanguage);
        if (aboutConnected) return;
        aboutConnected = true;
        UiText.Current.LanguageChanged += OnWindowLanguageChanged;
        Closed += (_, _) => UiText.Current.LanguageChanged -= OnWindowLanguageChanged;
    }

    private void OnWindowLanguageChanged(object? sender, EventArgs args)
    {
        UpdateStatusLabel.Text = UiText.Translate(updateStatus);
        RefreshHistoryPage();
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.OriginalSource != LanguagePicker || DataContext is not VoiceViewModel vm || LanguagePicker.SelectedValue is not string language) return;
        UiText.Current.SetLanguage(language);
        if (suppressSettingsTracking) return;
        vm.Settings.UiLanguage = language;
        ActionRequested?.Invoke(this, new("settings-changed"));
        ActionRequested?.Invoke(this, new("language-changed", language));
    }

    public void ShowAboutPage()
    {
        SettingsPage.IsSelected = true;
        AboutPage.IsSelected = true;
    }

    internal void RefreshUpdateStatusText(string message)
    {
        updateStatus = message;
        UpdateStatusLabel.Text = UiText.Translate(message);
    }

    /// <summary>更新协调器提供状态；实际网络与安装操作不在界面层执行。</summary>
    public void SetUpdateStatus(string message, bool canDownload = false, bool canInstall = false, bool working = false, double? progress = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetUpdateStatus(message, canDownload, canInstall, working, progress));
            return;
        }
        updateStatus = message;
        UpdateStatusLabel.Text = UiText.Translate(message);
        CheckUpdatesButton.IsEnabled = !working;
        DownloadUpdateButton.Visibility = canDownload ? Visibility.Visible : Visibility.Collapsed;
        SkipUpdateButton.Visibility = canDownload && !working ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = canInstall ? Visibility.Visible : Visibility.Collapsed;
        CancelUpdateButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressBar.IsIndeterminate = working && progress is null;
        UpdateProgressBar.Value = Math.Clamp(progress ?? 0, 0, 100);
    }
}
