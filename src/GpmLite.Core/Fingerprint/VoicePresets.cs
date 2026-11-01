using System.Text;
using System.Text.Json.Nodes;

namespace GpmLite.Core.Fingerprint;

/// <summary>
/// Realistic per-OS <c>speechSynthesis.getVoices()</c> voice lists fed to the patched core via
/// <c>--gl-voices</c>. The names are the well-known PUBLIC voice names shipped by each platform
/// (Windows SAPI, macOS system voices) plus the Google voices Chrome bundles — modelled from public
/// knowledge, NOT copied from any vendor's private data.
///
/// Payload per the WAVE3 switch contract:
///   [{"name":"...","lang":"en-US","default":true,"local":true}, ...]
/// Each entry maps to a SpeechSynthesisVoice (name, lang, localService=local, default, voiceURI=name).
/// </summary>
public static class VoicePresets
{
    /// <summary>One voice entry: display name, BCP-47 language, default flag and local (on-device) flag.</summary>
    private sealed record Voice(string Name, string Lang, bool Default, bool Local);

    // Windows: the standard SAPI voices that ship with Windows 10/11, plus the Google voices Chrome bundles.
    private static readonly Voice[] Windows =
    [
        new("Microsoft David - English (United States)", "en-US", true, true),
        new("Microsoft Zira - English (United States)", "en-US", false, true),
        new("Microsoft Mark - English (United States)", "en-US", false, true),
        new("Microsoft Hazel - English (United Kingdom)", "en-GB", false, true),
        new("Microsoft Susan - English (United Kingdom)", "en-GB", false, true),
        new("Microsoft Catherine - English (Australia)", "en-AU", false, true),
        new("Microsoft Linda - English (Canada)", "en-CA", false, true),
        new("Microsoft Heera - English (India)", "en-IN", false, true),
        // Google voices bundled with Chrome (remote / network synthesis).
        new("Google US English", "en-US", false, false),
        new("Google UK English Female", "en-GB", false, false),
        new("Google UK English Male", "en-GB", false, false),
        new("Google Deutsch", "de-DE", false, false),
        new("Google español", "es-ES", false, false),
        new("Google français", "fr-FR", false, false),
        new("Google italiano", "it-IT", false, false),
        new("Google 日本語", "ja-JP", false, false),
        new("Google 한국의", "ko-KR", false, false),
        new("Google Nederlands", "nl-NL", false, false),
        new("Google polski", "pl-PL", false, false),
        new("Google português do Brasil", "pt-BR", false, false),
        new("Google русский", "ru-RU", false, false),
        new("Google 普通话（中国大陆）", "zh-CN", false, false),
    ];

    // macOS: the system voices available out of the box, plus the Google voices Chrome bundles.
    private static readonly Voice[] MacOs =
    [
        new("Alex", "en-US", true, true),
        new("Samantha", "en-US", false, true),
        new("Fred", "en-US", false, true),
        new("Victoria", "en-US", false, true),
        new("Daniel", "en-GB", false, true),
        new("Karen", "en-AU", false, true),
        new("Moira", "en-IE", false, true),
        new("Tessa", "en-ZA", false, true),
        new("Fiona", "en-GB", false, true),
        new("Rishi", "en-IN", false, true),
        // Google voices bundled with Chrome (remote / network synthesis).
        new("Google US English", "en-US", false, false),
        new("Google UK English Female", "en-GB", false, false),
        new("Google UK English Male", "en-GB", false, false),
        new("Google Deutsch", "de-DE", false, false),
        new("Google español", "es-ES", false, false),
        new("Google français", "fr-FR", false, false),
        new("Google italiano", "it-IT", false, false),
        new("Google 日本語", "ja-JP", false, false),
        new("Google 普通话（中国大陆）", "zh-CN", false, false),
    ];

    // Linux: minimal — the Google voices Chrome bundles plus the common espeak entries.
    private static readonly Voice[] Linux =
    [
        new("Google US English", "en-US", true, false),
        new("Google UK English Female", "en-GB", false, false),
        new("Google UK English Male", "en-GB", false, false),
        new("English (America)", "en-US", false, true),       // espeak / espeak-ng
        new("English (Great Britain)", "en-GB", false, true),
        new("Deutsch", "de-DE", false, false),
        new("français", "fr-FR", false, false),
        new("español", "es-ES", false, false),
    ];

    private static Voice[] For(TargetOs os) => os switch
    {
        TargetOs.MacOs => MacOs,
        TargetOs.Linux => Linux,
        _ => Windows,
    };

    /// <summary>Builds the voice-list JSON array for the given OS, per the switch contract.</summary>
    public static string BuildVoicesJson(TargetOs os)
    {
        var arr = new JsonArray();
        foreach (var v in For(os))
        {
            arr.Add(new JsonObject
            {
                ["name"] = v.Name,
                ["lang"] = v.Lang,
                ["default"] = v.Default,
                ["local"] = v.Local,
            });
        }
        return arr.ToJsonString();
    }

    /// <summary>Base64 of a UTF-8 string, for the <c>--gl-voices</c> switch value.</summary>
    public static string ToBase64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    /// <summary>Convenience: the ready-to-use <c>--gl-voices</c> value for an OS.</summary>
    public static string BuildVoicesBase64(TargetOs os) => ToBase64(BuildVoicesJson(os));
}
