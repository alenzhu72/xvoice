using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CognitiveServices.Speech;

namespace VoiceChange;

internal sealed partial class AzureSpeech : ISpeechService
{
    public const int OutputRate = VoicePreset.SampleRate;

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const string RegionSuffix = ".api.cognitive.microsoft.com";
    private const string CustomSuffix = ".cognitiveservices.azure.com";
    private readonly string sttBase;
    private readonly string ttsBase;
    private readonly string key;

    public AzureSpeech(string endpoint, string key)
    {
        var host = endpoint.ToLowerInvariant();
        if (host.StartsWith("https://", StringComparison.Ordinal))
            host = host[8..].TrimEnd('/');

        if (host.EndsWith(CustomSuffix, StringComparison.Ordinal) &&
            Regex.IsMatch(host[..^CustomSuffix.Length], @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z"))
        {
            sttBase = $"https://{host}/stt";
            ttsBase = $"https://{host}/tts";
        }
        else
        {
            var region = host.EndsWith(RegionSuffix, StringComparison.Ordinal) ? host[..^RegionSuffix.Length] : host;
            if (!Regex.IsMatch(region, @"\A[a-z][a-z0-9]{1,31}\z"))
                throw new ArgumentException("请输入 Azure 区域代码（例如 southeastasia），或粘贴门户的 HTTPS 终结点。");
            sttBase = $"https://{region}.stt.speech.microsoft.com";
            ttsBase = $"https://{region}.tts.speech.microsoft.com";
        }

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("请填写 Azure Speech 密钥。");

        this.key = key;
    }

    public async Task CheckConnectionAsync(CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Get, ttsBase + "/cognitiveservices/voices/list");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        CheckResponse(response, "连接测试");
    }

    public SpeechConfig CreateRecognitionConfig()
    {
        var url = "wss://" + sttBase[8..] + "/speech/recognition/conversation/cognitiveservices/v1";
        var config = SpeechConfig.FromEndpoint(new Uri(url), key);
        config.SpeechRecognitionLanguage = "zh-CN";
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "350");
        return config;
    }

    public async Task SpeakAsync(string text, VoicePreset preset, Func<Stream, CancellationToken, Task> play, CancellationToken token)
    {
        XNamespace ssml = "http://www.w3.org/2001/10/synthesis";
        var body = new XElement(ssml + "speak",
            new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xml + "lang", "zh-CN"),
            new XElement(ssml + "voice", new XAttribute("name", preset.Voice), text));

        using var request = CreateRequest(HttpMethod.Post, ttsBase + "/cognitiveservices/v1");
        request.Headers.Add("X-Microsoft-OutputFormat", "raw-24khz-16bit-mono-pcm");
        request.Headers.UserAgent.ParseAdd("VoiceChange/0.1");
        request.Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/ssml+xml");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        CheckResponse(response, "语音合成");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await play(stream, token).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        return request;
    }

    private static void CheckResponse(HttpResponseMessage response, string stage)
    {
        if (response.IsSuccessStatusCode)
            return;

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "请核对区域和密钥是否来自同一个 Speech 资源，并检查网络访问权限。",
            HttpStatusCode.NotFound => "请核对 Azure Speech 区域或门户终结点。",
            HttpStatusCode.TooManyRequests => "服务达到请求或配额上限，请稍后重试。",
            _ => "请稍后重试。"
        };
        throw new InvalidOperationException($"{stage}失败（HTTP {(int)response.StatusCode}）。{hint}");
    }

    internal static string DescribeError(Exception error)
    {
        return error switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                "无法解析 Azure Speech 域名。请核对区域或终结点，并检查 DNS。",
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
                "无法建立 Azure Speech 的 HTTPS 连接。请检查系统时间及代理设置。",
            HttpRequestException => "无法连接 Azure Speech。请检查网络、代理及区域终结点。",
            OperationCanceledException => "请求超时，请检查网络后重试。",
            _ => error.Message
        };
    }
}
