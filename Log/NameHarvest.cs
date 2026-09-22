using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 게임 설정 에셋을 Addressables로 직접 불러와 <c>names.tsv</c>를 만든다.
/// 이 방식을 쓰는 이유와 지킬 것은 <c>docs/ARCHITECTURE.md</c> "카드/스킬/버프 이름표".
/// </summary>
internal static class NameHarvest
{
    private const string Label = "GameData_INT";

    private static ManualLogSource? _log;
    private static NameTable? _live;
    private static string? _dest;
    private static bool _done;

    // 기본값 상태는 IsValid()로 거르므로 안전하다.
#pragma warning disable CS8618
    private static AsyncOperationHandle<Il2CppSystem.Collections.Generic.IList<TextAsset>> _handle;
#pragma warning restore CS8618
    private static bool _requested;
    private static int _startFrame;

    // Addressables 초기화 전에 부르면 실패한다.
    private const int WarmupFrames = 600;
    private const int TimeoutFrames = 1200;

    public static void Arm(ManualLogSource log, NameTable live, string destPath, bool force)
    {
        _log = log;
        _live = live;
        _dest = destPath;
        _done = !force && File.Exists(destPath);
        if (!_done) log.LogInfo("No name table yet. Will load the game's config assets directly.");
    }

    public static void Tick()
    {
        if (_done || _live is null || _dest is null) return;

        int frame = Time.frameCount;
        if (frame < WarmupFrames) return;

        try
        {
            if (!_requested)
            {
                _requested = true;
                _startFrame = frame;
                // 콜백은 null이어야 한다. IL2CPP 델리게이트 등록은 이 게임에서 확정 크래시다.
                Il2CppSystem.Object key = (Il2CppSystem.String)Label;
                _handle = Addressables.LoadAssetsAsync<TextAsset>(key, null);
                _log?.LogInfo($"Requested '{Label}' from Addressables.");
                return;
            }

            if (!_handle.IsDone)
            {
                HandlePending(frame);
                return;
            }

            Consume();
        }
        catch (Exception e)
        {
            _done = true;
            _log?.LogWarning($"Could not load config assets; ids will be shown instead: {e.Message}");
        }
    }

    private static void HandlePending(int frame)
    {
        if (frame - _startFrame < TimeoutFrames) return;

        _done = true;
        ReleaseHandle();
        _log?.LogWarning($"Addressables did not finish loading '{Label}' in time; "
                         + "cards and skills will be shown as ids. "
                         + "Set Names.Rebuild in the config and restart to try again.");
    }

    private static void Consume()
    {
        _done = true;

        var assets = new Dictionary<string, byte[]>();
        try
        {
            if (_handle.Status != AsyncOperationStatus.Succeeded)
            {
                _log?.LogWarning($"Addressables could not provide '{Label}' "
                                 + $"(status {_handle.Status}); ids will be shown instead.");
                return;
            }

            HashSet<string> wanted = NameConfig.Required();
            var list = _handle.Result;
            if (list is null) { _log?.LogWarning("Addressables returned no assets."); return; }

            // interop 인터페이스는 상속 멤버를 물려받지 않아 Count가 ICollection<T>에만 있다.
            int total = list.Cast<Il2CppSystem.Collections.Generic.ICollection<TextAsset>>().Count;
            for (int i = 0; i < total; i++)
            {
                TextAsset asset = list[i];
                if (asset is null) continue;
                string name = asset.name;
                if (!wanted.Contains(name) || assets.ContainsKey(name)) continue;

                Il2CppStructArray<byte>? raw = asset.bytes;
                if (raw is null || raw.Length == 0) continue;

                var copy = new byte[raw.Length];
                for (int b = 0; b < copy.Length; b++) copy[b] = raw[b];
                assets[name] = copy;
            }
            _log?.LogInfo($"Read {assets.Count}/{wanted.Count} config assets from {total} loaded.");
        }
        finally
        {
            // 안 놓으면 설정 번들이 통째로 메모리에 남는다.
            ReleaseHandle();
        }

        Write(assets);
    }

    private static void ReleaseHandle()
    {
        try
        {
            if (_requested && _handle.IsValid()) Addressables.Release(_handle);
        }
        catch (Exception e)
        {
            _log?.LogWarning($"Releasing the Addressables handle failed: {e.Message}");
        }
    }

    private static void Write(Dictionary<string, byte[]> assets)
    {
        var rows = NameConfig.Build(assets);
        if (rows.Count == 0)
        {
            _log?.LogWarning("No names could be built from the config assets; "
                             + "cards and skills will be shown as ids.");
            return;
        }

        var fresh = new Dictionary<string, string>(rows.Count);
        var sb = new StringBuilder(
            "# kind\tid\tname — generated by the plugin from game data. Do not edit.\n");
        foreach ((string kind, long id, string text) in rows)
        {
            sb.Append(kind).Append('\t').Append(id).Append('\t').Append(text).Append('\n');
            fresh[NameTable.Key(kind, id)] = text;
        }

        File.WriteAllText(_dest!, sb.ToString(), new UTF8Encoding(false));
        _live!.Adopt(fresh);

        HashSet<string> missing = NameConfig.Required();
        missing.ExceptWith(assets.Keys);
        if (missing.Count > 0)
            _log?.LogWarning($"Built {rows.Count} names, but {missing.Count} config asset(s) "
                             + "were missing. Set Names.Rebuild and restart to try again.");
        else
            _log?.LogInfo($"Built {rows.Count} names -> {_dest}");
    }
}
