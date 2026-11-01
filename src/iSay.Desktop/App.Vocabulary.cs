using System.IO;
using System.Text.Json;
using iSay.Voice;
using Microsoft.Win32;

namespace iSay.Desktop;

public partial class App
{
    private RecognitionVocabularyStore? recognitionVocabulary;
    private VocabularyImportResult? pendingVocabularyImport;
    private PersonalCorrectionEntry? deletedCorrection;

    private void InitializeRecognitionVocabulary()
    {
        var path = Path.Combine(store.DirectoryPath, "识别热词.json");
        try
        {
            try { recognitionVocabulary = new RecognitionVocabularyStore(path); }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                File.Move(path, path + ".损坏备份-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"), false);
                recognitionVocabulary = new RecognitionVocabularyStore(path);
                startupVocabularyWarning += " 识别词表格式异常，原文件已备份。";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            recognitionVocabulary = null;
            startupVocabularyWarning += " 识别词表暂不可用，原文件已保留。";
        }
    }

    private void RefreshRecognitionVocabulary()
    {
        vm.RecognitionWords.Clear();
        foreach (var entry in recognitionVocabulary?.Snapshot() ?? []) vm.RecognitionWords.Add(entry);
        vm.NotifyRecognitionWordsChanged();
    }

    private async Task<bool> HandleVocabularyActionAsync(ActionRequestedEventArgs action)
    {
        if (action.Action is not ("remember-recognition-word" or "quick-add-recognition-word" or "forget-recognition-word" or "toggle-preferred-word"
            or "preview-official-vocabulary" or "preview-vocabulary-file" or "apply-vocabulary-import"
            or "export-vocabulary" or "inspect-native-runtime" or "rename-recognition-word" or "delete-vocabulary-words"
            or "cancel-vocabulary-import" or "edit-correction" or "undo-correction-delete")) return false;
        if (!vm.CanManageVocabulary) return true;
        vm.VocabularyWorking = true;
        try
        {
            switch (action.Action)
            {
                case "remember-recognition-word":
                case "quick-add-recognition-word":
                    if (recognitionVocabulary is null) { SetVocabularyStatus("识别词表暂不可用，原文件已保留。"); break; }
                    var draft = action.Action == "quick-add-recognition-word" ? action.Argument as string ?? "" : vm.VocabularyDraft;
                    var added = await Task.Run(() => recognitionVocabulary.Add(draft));
                    RefreshRecognitionVocabulary();
                    SetVocabularyStatus(added ? "热词已保存在本机，下次录音生效。" : "词条已存在、词表已满，或内容不是有效的 1 至 64 字术语。");
                    if (added)
                    {
                        vm.CancelVocabularyEdit(); RefreshVocabularyEngine();
                        if (action.Action == "quick-add-recognition-word") { vm.VocabularyQuickText = ""; vm.VocabularySearch = ""; vm.PreferredOnly = false; }
                    }
                    break;
                case "rename-recognition-word":
                    if (recognitionVocabulary is null || action.Argument is not VocabularyEditRequest edit) break;
                    var renamed = await Task.Run(() => recognitionVocabulary.Rename(edit.Entry, edit.Word));
                    SetVocabularyStatus(renamed ? "热词已更新，下次录音生效。" : "未保存：词条已变化、名称重复，或内容不符合 1–64 字要求。");
                    if (renamed) { RefreshRecognitionVocabulary(); vm.CancelVocabularyEdit(); RefreshVocabularyEngine(); }
                    break;
                case "delete-vocabulary-words":
                    if (recognitionVocabulary is null || action.Argument is not RecognitionVocabularyEntry[] selectedWords) break;
                    var removed = await Task.Run(() => recognitionVocabulary.RemoveMany(selectedWords));
                    RefreshRecognitionVocabulary();
                    SetVocabularyStatus($"已删除 {removed.Count} 个热词。");
                    if (removed.Count > 0) RefreshVocabularyEngine();
                    break;
                case "cancel-vocabulary-import":
                    pendingVocabularyImport = null; vm.HasVocabularyImport = false; vm.VocabularyImportPreview = "";
                    SetVocabularyStatus("已取消导入，词库保持不变。");
                    break;
                case "edit-correction":
                    if (vocabulary is null || action.Argument is not CorrectionEditRequest correctionEdit) break;
                    var updated = await Task.Run(() => vocabulary.Update(correctionEdit.Entry, correctionEdit.Mistaken, correctionEdit.Corrected));
                    SetVocabularyStatus(updated ? "纠错规则已更新，下次录音生效。" : "未保存：错词已存在、原规则已变化，或两个词相同/格式无效。");
                    if (updated)
                    {
                        RefreshVocabulary(); vm.CancelCorrectionEdit(); QueueNativeVocabularyProjection();
                        QueueVocabularyCorrection(correctionEdit.Mistaken.Trim(), correctionEdit.Corrected.Trim());
                        if (savedSettings.LearnVocabularyEnabled) await PrepareEngineAsync(true);
                    }
                    break;
                case "undo-correction-delete":
                    if (vocabulary is null || deletedCorrection is null) break;
                    var correctionRestored = await Task.Run(() => vocabulary.Restore(deletedCorrection));
                    if (correctionRestored) { RefreshVocabulary(); QueueNativeVocabularyProjection(); if (savedSettings.LearnVocabularyEnabled) await PrepareEngineAsync(true); }
                    deletedCorrection = null; vm.CanUndoCorrection = false;
                    SetVocabularyStatus(correctionRestored ? "已恢复删除的纠错规则。" : "未恢复：同名规则已被重新创建或词库已满，现有内容保留。");
                    break;
                case "forget-recognition-word":
                    if (recognitionVocabulary is null || action.Argument is not RecognitionVocabularyEntry selected) break;
                    await Task.Run(() => recognitionVocabulary.Remove(selected.Word));
                    RefreshRecognitionVocabulary();
                    SetVocabularyStatus("已删除选中的本地热词，原有词库不变。");
                    RefreshVocabularyEngine();
                    break;
                case "toggle-preferred-word":
                    if (recognitionVocabulary is null || action.Argument is not RecognitionVocabularyEntry preferred) break;
                    var preferenceChanged = await Task.Run(() => recognitionVocabulary.SetPreferred(preferred.Word, !preferred.Preferred));
                    RefreshRecognitionVocabulary();
                    SetVocabularyStatus(preferenceChanged
                        ? preferred.Preferred ? "已取消此词的优先设置。" : "已将此词设为优先：排在普通词前，手工词典仍优先。"
                        : "未能设置优先词；最多允许 256 个优先词，词条不存在时也不会写入。");
                    if (preferenceChanged) RefreshVocabularyEngine();
                    break;
                case "preview-official-vocabulary":
                    pendingVocabularyImport = null;
                    vm.HasVocabularyImport = false;
                    vm.VocabularyImportPreview = "";
                    SetVocabularyStatus("正在只读检查本机已有语音词库……");
                    var official = await Task.Run(() => action.Argument is string officialDirectory
                        ? InstalledVocabularyReader.ReadDirectory(officialDirectory) : InstalledVocabularyReader.ReadDefault());
                    ShowVocabularyImportPreview(official);
                    break;
                case "preview-vocabulary-file":
                    var importPath = action.Argument as string;
                    if (importPath is null)
                    {
                        var open = new OpenFileDialog { Title = "选择要导入的词表", Filter = "词表文件 (*.json;*.txt)|*.json;*.txt", CheckFileExists = true };
                        if (open.ShowDialog(window) != true) break;
                        importPath = open.FileName;
                    }
                    pendingVocabularyImport = null;
                    vm.HasVocabularyImport = false;
                    vm.VocabularyImportPreview = "";
                    var importResult = await Task.Run(() => VocabularyInterchange.Read(importPath));
                    ShowVocabularyImportPreview(importResult);
                    break;
                case "apply-vocabulary-import":
                    if (recognitionVocabulary is null || pendingVocabularyImport is null) break;
                    // 只合并正确词，不把外来纠错对覆盖到用户已确认的纠错规则。
                    var words = pendingVocabularyImport.Words
                        .Concat(pendingVocabularyImport.Corrections.Select(entry => entry.Corrected)).ToArray();
                    var summary = await Task.Run(() => recognitionVocabulary.Import(words));
                    pendingVocabularyImport = null;
                    vm.HasVocabularyImport = false;
                    vm.VocabularyImportPreview = "";
                    RefreshRecognitionVocabulary();
                    SetVocabularyStatus($"已导入 {summary.AddedCount} 个新词，跳过 {summary.ExistingCount} 个已有词及 {summary.RejectedCount} 个无效词；识别热词开关保持不变。");
                    RefreshVocabularyEngine();
                    break;
                case "export-vocabulary":
                    var exportPath = action.Argument as string;
                    if (exportPath is null)
                    {
                        var save = new SaveFileDialog { Title = "导出本地识别词表", Filter = "JSON 词表 (*.json)|*.json", FileName = "iSay个人词表.json", DefaultExt = ".json", AddExtension = true };
                        if (save.ShowDialog(window) != true) break;
                        exportPath = save.FileName;
                    }
                    var exportWords = recognitionVocabulary?.Snapshot().Select(entry => entry.Word).ToArray() ?? [];
                    var exportCorrections = vocabulary?.Snapshot() ?? [];
                    await Task.Run(() => VocabularyInterchange.Write(exportPath, exportWords, exportCorrections));
                    SetVocabularyStatus("词表已导出到选定文件，不含优先设置；未上传到任何服务。");
                    break;
                case "inspect-native-runtime":
                    var report = await NativeRuntimeDiagnostics.InspectAsync(new NativeRuntimeDiagnosticOptions(
                        AppContext.BaseDirectory, store.DirectoryPath,
                        string.IsNullOrWhiteSpace(savedSettings.NativeRuntimePath) ? null : savedSettings.NativeRuntimePath)
                        { RecognitionExecutableName = Options(savedSettings).NativeExecutableName }, lifetime.Token);
                    vm.EngineStatusText = report.ToDisplayText();
                    SetVocabularyStatus("已检查本机运行文件和部署边界；这项检查不代表云端服务已经连通。");
                    break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            LogEvent("个人词表操作", error.GetType().Name);
            SetVocabularyStatus("词表操作未完成：" + error.Message);
        }
        finally { vm.VocabularyWorking = false; }
        return true;
    }

    private void ShowVocabularyImportPreview(VocabularyImportResult result)
    {
        pendingVocabularyImport = result;
        vm.HasVocabularyImport = result.Words.Count > 0 || result.Corrections.Count > 0;
        vm.VocabularyImportPreview = $"读取到 {result.Words.Count} 个热词、{result.Corrections.Count} 对纠错，跳过 {result.RejectedCount} 个无效项。"
            + "导入只合并正确词，不覆盖纠错规则，也不会自动开启热词上传。"
            + (string.IsNullOrWhiteSpace(result.Warning) ? "" : " " + result.Warning);
        SetVocabularyStatus(vm.HasVocabularyImport ? "预览已就绪，点击“导入预览词条”后才会写入 iSay。" : result.Warning ?? "没有读取到支持导入的语音词条。");
    }

    private void SetVocabularyStatus(string message)
    {
        vm.VocabularyStatusText = message;
        if (!vm.IsRecording && !vm.IsBusy) vm.StatusText = message;
    }

    private void RefreshVocabularyEngine()
    {
        QueueNativeVocabularyProjection();
        // 修改词表不会重启正在采集的会话；已保存的开关决定下一段是否使用热词。
        if (savedSettings.AsrVocabularyEnabled && !vm.IsRecording) _ = PrepareEngineAsync(true);
    }
}
