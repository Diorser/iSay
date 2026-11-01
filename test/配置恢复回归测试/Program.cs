using System.IO;
using System.Text.Json;
using iSay.Desktop;
using iSay.Voice;

var directory = Path.Combine(Path.GetTempPath(), "iSay-配置回归-" + Guid.NewGuid().ToString("N"));
var store = new SettingsStore(directory);
var results = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(name); Console.WriteLine("通过：" + name); }

var fresh = store.Load();
Check(fresh.Hotkey == "RightAlt" && fresh.MuteSystemAudioWhileRecording, "新用户默认右Alt录音并将系统播放静音");
File.WriteAllText(Path.Combine(directory, "设置.json"), """
    {"Hotkey":"Alt","MuteSystemAudioWhileRecording":false,"MicrophoneId":"用户原有麦克风"}
    """);
var existing = store.Load();
Check(existing.Hotkey == "Alt" && !existing.MuteSystemAudioWhileRecording && existing.MicrophoneId == "用户原有麦克风",
    "旧用户显式保存的快捷键与不静音选择不被新默认值覆盖");
store.Save(existing);
Check(store.Load().Hotkey == "Alt" && !store.Load().MuteSystemAudioWhileRecording,
    "旧用户偏好重新保存并加载后保持原选择");

File.WriteAllText(Path.Combine(directory, "设置.json"), "{\"CustomVocabulary\":null,\"ApiKeyProtected\":null,\"AsrApiKeyProtected\":null}");
var settings = store.Load();
Check(settings.CustomVocabulary == "" && settings.ApiKey == "" && TextFormatting.Basic("测试。", settings) == "测试。", "null配置不会延迟到录音时崩溃");
settings.ApiKey = "当前用户的测试密钥";
store.Save(settings);
var protectedValue = settings.ApiKeyProtected;
settings.ApiKey = "";
store.Save(settings);
Check(store.Load().ApiKey == "当前用户的测试密钥" && settings.ApiKeyProtected == protectedValue, "空密钥保留已有受保护密钥");
settings.ApiKeyProtected = "无法解密的旧密钥";
settings.ApiKey = "";
store.Save(settings);
var undecipherable = store.Load();
store.Save(undecipherable);
Check(File.ReadAllText(Path.Combine(directory, "设置.json")).Contains("无法解密的旧密钥") || store.Load().ApiKeyProtected == "无法解密的旧密钥", "不可解密密钥不会被静默覆盖为空值");
File.WriteAllText(Path.Combine(directory, "历史.json"), "{broken");
Check(store.LoadHistory(new VoiceSettings()).Count == 0 && Directory.GetFiles(directory, "历史.json.损坏备份-*").Length == 1, "损坏历史保留证据备份");
store.Save(new VoiceSettings());
Check(store.Load().ApiKey == "" && store.Load().ApiKeyProtected == "", "显式恢复默认设置清除旧密钥");
var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../test/配置恢复回归测试结果.json"));
File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count} 项配置恢复回归测试通过。");
