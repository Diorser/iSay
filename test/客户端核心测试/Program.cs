using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using iSay.Desktop;
using iSay.Voice;

var checks = new List<object>();
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(new { 名称 = name, 结果 = "通过" }); }
var directory = Path.Combine(Path.GetTempPath(), "iSay-test-" + Guid.NewGuid().ToString("N"));
var store = new SettingsStore(directory);
var settings = new VoiceSettings { ApiKey = "测试密钥-不要明文保存", AsrApiKey = "测试识别密钥", Hotkey = "F3" };
store.Save(settings);
var saved = File.ReadAllText(Path.Combine(directory, "设置.json"));
Check(!saved.Contains("测试密钥") && !saved.Contains("测试识别密钥"), "配置中没有明文密钥");
var loaded = store.Load();
Check(loaded.ApiKey == settings.ApiKey && loaded.AsrApiKey == settings.AsrApiKey && loaded.Hotkey == "F3", "设置和DPAPI密钥重载");
File.WriteAllText(Path.Combine(directory, "设置.json"), "{broken");
var recovered = store.Load();
Check(recovered.Hotkey == "RightAlt" && recovered.MuteSystemAudioWhileRecording && Directory.GetFiles(directory, "*.损坏备份-*").Length == 1,
    "损坏配置保留备份并恢复右Alt与录音静音默认值");
var invalid = new VoiceSettings { ApiBaseUrl = "socks5://localhost:1234" };
try { invalid.Validate(); throw new Exception("错误API地址未被拒绝"); } catch (ArgumentException) { Check(true, "API地址拒绝代理协议"); }
store.SaveHistory([new("1", DateTimeOffset.Now, "今天今天", "今天今天", "原文"), new("2", DateTimeOffset.Now.AddDays(-90), "过期", "过期", "原文")], settings);
Check(store.LoadHistory(settings).Count == 1, "历史保留期限");
settings.SaveHistory = false;
store.SaveHistory([], settings);
Check(store.LoadHistory(settings).Count == 0, "关闭历史后不保存转写");
var format = new TextFormatting();
settings = new VoiceSettings { CustomVocabulary = "扣德克斯=>Codex" };
Check(TextFormatting.Basic("今天今天，准备去扣德克斯。", settings) == "今天今天，准备去Codex。", "词典纠错且保留真实重复词");
var fallback = await format.FormatAsync(" 保留原话。 ", settings);
Check(fallback.Text == "保留原话。" && fallback.Warning is not null && fallback.Method == "基础整理", "缺少AI配置明确回退");

async Task<FormattingResult> LocalResponse(string response, int code = 200)
{
    var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
    using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
    var server = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        if (!body.Contains("测试模型") && !body.Contains("\\u6D4B")) throw new Exception("请求未包含模型");
        context.Response.StatusCode = code;
        var bytes = Encoding.UTF8.GetBytes(response); await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    });
    var result = await format.FormatAsync("原话原话123。", new() { ApiBaseUrl = $"http://127.0.0.1:{port}/v1", ApiModel = "测试模型", ProxyMode = "direct" });
    await server;
    return result;
}
var successful = await LocalResponse("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"整理后的原话123。\"}}]}");
Check(successful.Text == "整理后的原话123。" && successful.Method == "AI智能整理", "AI协议成功分支（本地响应）");
foreach (var response in new[] { "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"截断\"}}]}", "{\"choices\":[{\"message\":{\"content\":\"\"}}]}", "invalid", "{\"choices\":[]}" })
{
    var result = await LocalResponse(response);
    Check(result.Text == "原话原话123。" && result.Warning is not null, "异常AI响应保留原话 " + checks.Count);
}
var httpError = await LocalResponse("{}", 503);
Check(httpError.Text == "原话原话123。" && httpError.Warning?.Contains("503") == true, "AI HTTP失败保留原文");
var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../test/客户端核心测试结果.json"));
File.WriteAllText(output, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{checks.Count} 项核心测试通过。证据：{output}");
