using System.Security.Cryptography;
using System.Text.Json;

namespace VoiceChange;

internal sealed class SpeechSettings(string endpoint, string key, string workspaceId = "", string miniMaxKey = "")
{
    public string Endpoint { get; } = endpoint;
    public string Key { get; } = key;
    public string WorkspaceId { get; } = workspaceId;
    public string MiniMaxKey { get; } = miniMaxKey;

    public static bool IsDomesticBuild { get; } =
#if DOMESTIC_SPEECH
        true;
#else
        false;
#endif

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key) &&
        !string.IsNullOrWhiteSpace(IsDomesticBuild ? MiniMaxKey : Endpoint);

    public ISpeechService CreateService() => IsDomesticBuild
        ? new DomesticSpeech(WorkspaceId, Key, MiniMaxKey)
        : new AzureSpeech(Endpoint, Key);

    private const string CustomerKeyResource = "VoiceChange.CustomerSpeechKey";
    public static bool IsCustomerBuild { get; } =
        typeof(SpeechSettings).Assembly.GetManifestResourceInfo(CustomerKeyResource) is not null;

    private static readonly string Pathname = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceChange",
        IsDomesticBuild ? "speech-cn.dat" : "speech.dat");

    public static SpeechSettings Load()
    {
        if (IsCustomerBuild)
        {
            using var reader = new StreamReader(typeof(SpeechSettings).Assembly.GetManifestResourceStream(CustomerKeyResource)!);
            var key = reader.ReadToEnd().Trim();
            if (key.Length == 0)
                throw new InvalidDataException("客户版语音配置缺失。");
            return new("southeastasia", key);
        }

        byte[] encrypted;
        try
        {
            encrypted = File.ReadAllBytes(Pathname);
        }
        catch (FileNotFoundException)
        {
            return FromEnvironment();
        }
        catch (DirectoryNotFoundException)
        {
            return FromEnvironment();
        }

        var data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            var settings = JsonSerializer.Deserialize<SpeechSettings>(data);
            if (settings is null || !settings.IsConfigured)
                throw new InvalidDataException("保存的语音设置不完整。");
            return settings;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pathname)!);
        var data = JsonSerializer.SerializeToUtf8Bytes(this);
        var temporary = Pathname + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, Pathname, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            File.Delete(temporary);
        }
    }

    private static SpeechSettings FromEnvironment()
    {
        if (IsDomesticBuild)
            return new("", Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY") ?? "",
                Environment.GetEnvironmentVariable("DASHSCOPE_WORKSPACE_ID") ?? "",
                Environment.GetEnvironmentVariable("MINIMAX_API_KEY") ?? "");

        var customResource = Environment.GetEnvironmentVariable("SPEECH_RESOURCE");
        var endpoint = Environment.GetEnvironmentVariable("SPEECH_REGION") ??
            (string.IsNullOrWhiteSpace(customResource) ? "" : $"https://{customResource}.cognitiveservices.azure.com");
        return new(endpoint, Environment.GetEnvironmentVariable("SPEECH_KEY") ?? "");
    }
}
