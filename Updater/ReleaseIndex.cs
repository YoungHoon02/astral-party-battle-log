using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace AstralPartyBattleLog.Updater;

// distribution 브랜치의 release-index.json. 형식과 게시 순서는 docs/ARCHITECTURE.md "릴리스 알림".
internal static class ReleaseIndex
{
    public const string Repository = "YoungHoon02/astral-party-battle-log";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";
    // api.github.com은 미인증 IP당 시간당 60회라 IP를 나눠 쓰는 환경에서 막힌다. raw는 CDN이다.
    public const string Url = "https://raw.githubusercontent.com/" + Repository + "/distribution/release-index.json";
    public const int MaxBytes = 64 * 1024;
    private const int SupportedSchema = 1;

    [DataContract]
    private sealed class Index
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "version")] public string? Version { get; set; }
        [DataMember(Name = "downloadUrl")] public string? DownloadUrl { get; set; }
        [DataMember(Name = "sha256")] public string? Sha256 { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }

    // 더 새 버전이면 그 버전 문자열, 아니면 null. 인덱스가 어긋나면 예외 — 엉뚱한 알림보다 침묵이 낫다.
    public static string? Newer(byte[] json, Version current)
    {
        Index index;
        try
        {
            using var stream = new MemoryStream(json);
            index = (Index)new DataContractJsonSerializer(typeof(Index)).ReadObject(stream)!;
        }
        catch (SerializationException e)
        {
            throw new InvalidDataException("bad index json", e);
        }

        if (index.SchemaVersion != SupportedSchema) throw new InvalidDataException("unknown index schema");
        string version = index.Version ?? "";
        if (!System.Version.TryParse(version, out Version? latest)) throw new InvalidDataException("bad version");

        // 인덱스가 오염돼도 엉뚱한 주소를 안내하지 못하게, 버전으로 주소를 다시 만들어 대조한다.
        string expected = $"https://github.com/{Repository}/releases/download/v{version}/AstralPartyBattleLog-v{version}.zip";
        if (index.DownloadUrl != expected) throw new InvalidDataException("unexpected download url");
        if (!IsSha256(index.Sha256) || index.Size <= 0) throw new InvalidDataException("bad package digest");

        return Normalize(latest!) > Normalize(current) ? version : null;
    }

    // 어셈블리 버전은 0.3.2.0이고 인덱스는 0.3.2라, 네 번째 자리를 버리고 비교한다.
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool IsSha256(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
