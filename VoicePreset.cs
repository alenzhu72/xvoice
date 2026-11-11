namespace VoiceChange;

internal sealed record VoicePreset(string Name, string Voice)
{
    public const int SampleRate = 24000;
    public override string ToString() => Name;
}
