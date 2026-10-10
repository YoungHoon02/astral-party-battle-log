using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using BepInEx.Logging;

namespace AstralPartyBattleLog.Net;

// 새 릴리스가 있다고 알리기만 한다. 받거나 교체하지 않는다. 구조는 docs/ARCHITECTURE.md "릴리스 알림".
internal static class ReleaseCheck
{
    public const string Repository = "YoungHoon02/astral-party-battle-log";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";
    // api.github.com은 미인증 IP당 시간당 60회라 IP를 나눠 쓰는 환경에서 막힌다. raw는 CDN이다.
    private const string IndexUrl = "https://raw.githubusercontent.com/" + Repository + "/distribution/release-index.json";
    private const int SupportedSchema = 1;
    private const int MaxIndexBytes = 64 * 1024;

    private static string? _latest;

    public static string? Latest => Volatile.Read(ref _latest);

    // .NET 런타임 스레드와 HttpClient만 쓴다. IL2CPP 쪽에 아무것도 등록하지 않는다.
    public static void Start(ManualLogSource log)
    {
        Version? current = typeof(ReleaseCheck).Assembly.GetName().Version;
        if (current is null) return;
        new Thread(() => Run(log, current)) { IsBackground = true, Name = "BattleLogReleaseCheck" }.Start();
    }

    private static void Run(ManualLogSource log, Version current)
    {
        try
        {
            string? latest = Newer(Fetch(current), current);
            if (latest is null)
            {
                log.LogInfo("[update] no newer release.");
                return;
            }
            Volatile.Write(ref _latest, latest);
            log.LogMessage($"[update] v{latest} is available (current v{Short(current)}): {ReleasesPage}");
        }
        catch (Exception e)
        {
            log.LogInfo($"[update] release check skipped: {e.GetType().Name}: {e.Message}");
        }
    }

    private static byte[] Fetch(Version current)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AstralPartyBattleLog/{Short(current)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, IndexUrl);
        using HttpResponseMessage response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxIndexBytes) throw new InvalidDataException("index too large");

        using Stream stream = response.Content.ReadAsStream();
        var buffer = new byte[MaxIndexBytes + 1];
        int total = 0, read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        if (total > MaxIndexBytes) throw new InvalidDataException("index too large");
        return buffer.AsSpan(0, total).ToArray();
    }

    // 더 새 버전이면 그 버전 문자열, 아니면 null. 인덱스가 어긋나면 예외 — 알림보다 침묵이 낫다.
    internal static string? Newer(byte[] json, Version current)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != SupportedSchema)
            throw new InvalidDataException("unknown index schema");

        string version = root.GetProperty("version").GetString() ?? "";
        if (!Version.TryParse(version, out Version? latest)) throw new InvalidDataException("bad version");

        // 인덱스가 오염돼도 엉뚱한 주소를 안내하지 못하게, 버전으로 주소를 다시 만들어 대조한다.
        string expected = $"https://github.com/{Repository}/releases/download/v{version}/AstralPartyBattleLog-v{version}.zip";
        if (root.GetProperty("downloadUrl").GetString() != expected) throw new InvalidDataException("unexpected download url");
        if (!IsSha256(root.GetProperty("sha256").GetString()) || root.GetProperty("size").GetInt64() <= 0)
            throw new InvalidDataException("bad package digest");

        return Normalize(latest) > Normalize(current) ? version : null;
    }

    // 어셈블리 버전은 0.3.2.0이고 인덱스는 0.3.2라, 네 번째 자리를 버리고 비교한다.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string Short(Version v) => Normalize(v).ToString();

    private static bool IsSha256(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
