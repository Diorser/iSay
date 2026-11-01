using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using iSay.Voice;
using Microsoft.Win32;

namespace iSay.Desktop;

public sealed class SettingsStore
{
    public string DirectoryPath { get; }
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    private readonly object writeLock = new();
    public SettingsStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iSay", "VoiceDesk");
        Directory.CreateDirectory(DirectoryPath);
    }
    public VoiceSettings Load()
    {
        var path = Path.Combine(DirectoryPath, "设置.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(path)) ?? new();
            NormalizeTextFields(settings);
            settings.Validate();
            settings.ApiKey = Unprotect(settings.ApiKeyProtected);
            settings.AsrApiKey = Unprotect(settings.AsrApiKeyProtected);
            return settings;
        }
        catch
        {
            BackupDamaged(path);
            return new();
        }
    }
    public void Save(VoiceSettings settings)
    {
        NormalizeTextFields(settings);
        settings.Validate();
        if (settings.ApiKey.Length > 0 || settings.ApiKeyProtected.Length == 0) settings.ApiKeyProtected = Protect(settings.ApiKey);
        if (settings.AsrApiKey.Length > 0 || settings.AsrApiKeyProtected.Length == 0) settings.AsrApiKeyProtected = Protect(settings.AsrApiKey);
        AtomicWrite("设置.json", JsonSerializer.Serialize(settings, options));
    }
    public List<HistoryEntry> LoadHistory(VoiceSettings settings)
    {
        try
        {
            if (!settings.SaveHistory) return [];
            return (JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(Path.Combine(DirectoryPath, "历史.json"))) ?? [])
                .Where(x => x.CreatedAt >= DateTimeOffset.Now.AddDays(-settings.HistoryRetentionDays)).OrderByDescending(x => x.CreatedAt).Take(500).ToList();
        }
        catch { BackupDamaged(Path.Combine(DirectoryPath, "历史.json")); return []; }
    }
    public void SaveHistory(IEnumerable<HistoryEntry> history, VoiceSettings settings) => AtomicWrite("历史.json", JsonSerializer.Serialize(settings.SaveHistory ? history.Where(x => x.CreatedAt >= DateTimeOffset.Now.AddDays(-settings.HistoryRetentionDays)).Take(500) : [], options));
    private void AtomicWrite(string name, string text)
    {
        lock (writeLock)
        {
            var path = Path.Combine(DirectoryPath, name);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static void BackupDamaged(string path)
    {
        try { if (File.Exists(path)) File.Copy(path, path + ".损坏备份-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"), false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void NormalizeTextFields(VoiceSettings settings)
    {
        // JSON 中的 null 不应让词典或密钥处理在后续录音时才崩溃。
        var defaults = new VoiceSettings();
        foreach (var property in typeof(VoiceSettings).GetProperties().Where(x => x.PropertyType == typeof(string)))
            if (property.GetValue(settings) is null) property.SetValue(settings, property.GetValue(defaults));
    }
    public static void SetStartup(bool enabled, bool startMinimized = true)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("iSayVoiceDesk", $"\"{Environment.ProcessPath}\" {(startMinimized ? "--background" : "--show")}");
        else key.DeleteValue("iSayVoiceDesk", false);
    }
    // 使用当前 Windows 用户的 DPAPI 保护密钥，配置文件不保存明文。
    private static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(value), false));
    private static string Unprotect(string value)
    {
        try { return string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(value), true)); }
        catch { return ""; }
    }
    private static byte[] Crypt(byte[] value, bool decrypt)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        Marshal.Copy(value, 0, input.Data, value.Length);
        Blob output = default;
        try
        {
            var ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception();
            var bytes = new byte[output.Length];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
