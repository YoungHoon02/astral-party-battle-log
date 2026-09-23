using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AstralPartyBattleLog.UI;

// 화면 쪽 동기화 신호 후보를 찾는 진단 전용 프로브. 기록만 하고 오버레이 예약에는 관여하지 않는다.
// 사용법과 해석 주의점은 docs/OVERLAY-TIMING.md "측정 4".
internal static class ScreenProbe
{
    private const string OwnRoot = "AstralPartyBattleLogOverlay";
    private const string PlayerCameraSuffix = "_VirtualCamera";

    // 측정 4 첫 판에서 사건과 대응한 연출 오브젝트. Thinking·Gold_*는 한 사건에 여러 번 켜질 수 있어
    // 사건당 1회 신호로 보지 않는다(docs/OVERLAY-TIMING.md).
    public const string DefaultNames = "DiceController;Dice_;BattleShow;Thinking;Gold_UP;Gold_Down";
    private const int MaxDepth = 12;
    private const int MaxLinesPerFrame = 80;
    private const int MaxNewPathsPerFrame = 40;
    private const int MaxNewNamesPerFrame = 1000;
    private const int MaxFailures = 20;
    private const int RetryFrames = 30;
    private const double StatsIntervalSec = 10;

    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"[+\-]?\d+(?:[.,/:]\d+)*", RegexOptions.Compiled);
    private static readonly Regex LongDigits = new(@"\d{6,}", RegexOptions.Compiled);
    private static readonly Regex DiceFace = new(@"^Dice_\d+_(\d+)", RegexOptions.Compiled);

    private static ManualLogSource? _log;
    private static bool _enabled;
    private static int _failures;

    private static string[] _textPrefixes = Array.Empty<string>();
    private static string[] _namePrefixes = Array.Empty<string>();
    private static Watch[] _watches = Array.Empty<Watch>();

    private static readonly Dictionary<IntPtr, string?> Paths = new();
    private static readonly Dictionary<IntPtr, bool> Active = new();
    private static readonly Dictionary<IntPtr, NameTag> NameTags = new();

    // Sig < 0이면 화면 신호가 아니다. Diag는 진단 기록(ScreenProbeNames) 대상 여부.
    private readonly struct NameTag
    {
        public readonly bool Diag;
        public readonly int Sig;
        public readonly int Pip;

        public NameTag(bool diag, int sig, int pip)
        {
            Diag = diag; Sig = sig; Pip = pip;
        }
    }

    private static Action<ScreenSignal, int, bool, IntPtr>? _signal;
    private static bool _discover;
    private static bool _diagnostic;
    private static readonly Dictionary<IntPtr, string> Texts = new();
    private static readonly Dictionary<string, int> RootChanges = new();

    private static int _frame = -1;
    private static int _linesThisFrame;
    private static int _pathsThisFrame;
    private static int _namesThisFrame;

    // 패치 성공과 전투 신호 발견을 따로 판정하려는 누계. 호출 0이면 후킹이 안 걸린 것이고,
    // 호출은 많은데 경로 통과가 0이면 전투 UI가 다른 경로로 바뀐다는 뜻이다.
    private static long _activeCalls, _nameMatched, _activeChanges, _textCalls, _textMatched;
    private static long _ownSkipped, _lines, _budgetName, _budgetPath, _budgetLine;
    private static long _lastReportedCalls = -1;
    private static readonly Stopwatch StatsClock = Stopwatch.StartNew();
    private static double _nextStats = StatsIntervalSec;

    private sealed class Watch
    {
        public string Path = "";
        public GameObject? Go;
        public int NextTry;
        public int State = -1;
    }

    // signal이 있으면 진단과 무관하게 SetActive 후킹을 설치한다. 설치에 실패하면 false.
    public static bool Init(ManualLogSource log, Harmony harmony, bool discover, string names, string watch,
                            string textPaths, Action<ScreenSignal, int, bool, IntPtr>? signal = null)
    {
        _log = log;
        _namePrefixes = Split(names);
        _watches = Split(watch).Select(p => new Watch { Path = p }).ToArray();
        _textPrefixes = Split(textPaths);
        _discover = discover;
        _signal = signal;
        _diagnostic = discover || _watches.Length > 0 || _textPrefixes.Length > 0;
        _enabled = _diagnostic || signal is not null;

        bool hooked = true;
        if (discover || signal is not null)
        {
            hooked = TryPatch(harmony, typeof(ProbeSetActivePatch), "GameObject.SetActive");
            if (!hooked) _signal = null;
        }
        if (discover)
            log.LogInfo(_namePrefixes.Length == 0
                ? "[probe] recording every SetActive change (no name filter)"
                : $"[probe] recording SetActive for {string.Join(',', _namePrefixes)}");
        if (_textPrefixes.Length > 0)
        {
            TryPatch(harmony, typeof(ProbeTmpTextPatch), "TMP_Text text");
            TryPatch(harmony, typeof(ProbeUiTextPatch), "UI.Text text");
        }
        if (_watches.Length > 0) log.LogInfo($"[probe] watching {_watches.Length} path(s)");
        return hooked;
    }

    private static string[] Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryPatch(Harmony harmony, Type patch, string label)
    {
        try
        {
            harmony.PatchAll(patch);
            _log?.LogInfo($"[probe] patched {label}");
            return true;
        }
        catch (Exception e)
        {
            _log?.LogWarning($"[probe] could not patch {label}: {e.Message}");
            return false;
        }
    }

    // 포인터는 오브젝트가 파괴되면 재사용될 수 있어 씬마다 비운다.
    public static void OnScene(string scene)
    {
        if (!_enabled) return;
        if (_diagnostic) ReportStats(force: true);
        RootChanges.Clear();
        Paths.Clear();
        Active.Clear();
        NameTags.Clear();
        Texts.Clear();
        foreach (Watch w in _watches)
        {
            w.Go = null;
            w.NextTry = 0;
            w.State = -1;
        }
        if (_diagnostic) Write(Time.frameCount, $"scene name={Mask(scene)}");
    }

    // SetActive 후킹은 Animator·Timeline이 네이티브로 켜는 경우와 부모가 켜져 보이게 되는 경우를 놓친다.
    // 지정 경로는 최종 가시 상태(activeInHierarchy)를 매 프레임 직접 읽는다.
    public static void Tick()
    {
        if (!_diagnostic) return;
        try
        {
            if (StatsClock.Elapsed.TotalSeconds >= _nextStats)
            {
                _nextStats = StatsClock.Elapsed.TotalSeconds + StatsIntervalSec;
                ReportStats(force: false);
            }

            int frame = Time.frameCount;
            foreach (Watch w in _watches)
            {
                if (w.Go == null)
                {
                    if (frame < w.NextTry) continue;
                    w.NextTry = frame + RetryFrames;
                    w.Go = Resolve(w.Path);
                    if (w.Go == null) continue;
                }

                int state = w.Go.activeInHierarchy ? 1 : 0;
                if (state == w.State) continue;
                w.State = state;
                if (TakeLine()) Write(frame, $"watch={state} path={w.Path}");
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    // GameObject.Find는 꺼진 오브젝트를 못 찾으므로 켜져 있는 루트만 찾고 나머지는 Transform.Find로 내려간다.
    private static GameObject? Resolve(string path)
    {
        int slash = path.IndexOf('/');
        string root = slash < 0 ? path : path[..slash];
        GameObject? go = GameObject.Find("/" + root);
        if (go == null || slash < 0) return go;
        Transform? child = go.transform.Find(path[(slash + 1)..]);
        return child == null ? null : child.gameObject;
    }

    public static void NoteActive(GameObject go, bool value)
    {
        if (!_enabled) return;
        try
        {
            _activeCalls++;
            IntPtr key = go.Pointer;
            if (!Classify(key, go, out NameTag tag) || (!tag.Diag && tag.Sig < 0)) return;
            if (tag.Diag) _nameMatched++;
            if (Active.TryGetValue(key, out bool last) && last == value) return;
            Active[key] = value;
            if (tag.Sig >= 0) _signal?.Invoke((ScreenSignal)tag.Sig, tag.Pip, value, key);
            if (!tag.Diag) return;
            _activeChanges++;

            if (!TryPath(key, go.transform, out string? path) || path is null) return;
            CountRoot(path);
            if (TakeLine()) Write(_frame, $"active={(value ? 1 : 0)} path={path}");
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    // 이름 조회는 포인터별로 한 번만 한다. 경로 예산(40)을 같이 쓰면 FairyGUI 패널이 한 프레임에
    // 수백 개를 켜고 끌 때 대상 오브젝트까지 버려져서(측정 4 둘째 판 budgetName 12,944) 따로 크게 둔다.
    private static bool Classify(IntPtr key, GameObject go, out NameTag tag)
    {
        if (NameTags.TryGetValue(key, out tag)) return true;
        if (_namePrefixes.Length == 0 && _signal is null)
        {
            tag = new NameTag(_discover, -1, 0);
            NameTags[key] = tag;
            return true;
        }
        RollFrame();
        if (_namesThisFrame >= MaxNewNamesPerFrame)
        {
            _budgetName++;
            return false;
        }
        _namesThisFrame++;
        string name = go.name;
        bool diag = _discover && (_namePrefixes.Length == 0
                                  || _namePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)));
        (int sig, int pip) = _signal is null ? (-1, 0) : SignalOf(name);
        tag = new NameTag(diag, sig, pip);
        NameTags[key] = tag;
        return true;
    }

    private static (int Sig, int Pip) SignalOf(string name)
    {
        if (name == "BattleShow(Clone)") return ((int)ScreenSignal.Window, 0);
        if (name.StartsWith("BattleShow_Atk", StringComparison.Ordinal)) return ((int)ScreenSignal.Hit, 0);
        Match dice = DiceFace.Match(name);
        return dice.Success ? ((int)ScreenSignal.DiceFace, int.Parse(dice.Groups[1].Value)) : (-1, 0);
    }

    // 텍스트는 경로 필터를 통과한 뒤에만 읽는다. TMP getter는 버퍼를 문자열로 다시 만들 수 있어서다.
    public static void NoteTmp(TMPro.TMP_Text text)
    {
        if (!_enabled) return;
        try
        {
            if (IsTextTarget(text, out IntPtr key, out string path)) RecordText(key, path, text.text);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    public static void NoteUiText(UnityEngine.UI.Text text)
    {
        if (!_enabled) return;
        try
        {
            if (IsTextTarget(text, out IntPtr key, out string path)) RecordText(key, path, text.text);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private static bool IsTextTarget(Component text, out IntPtr key, out string path)
    {
        _textCalls++;
        key = text.Pointer;
        path = "";
        if (!TryPath(key, text.transform, out string? found) || found is null) return false;
        if (!_textPrefixes.Any(found.StartsWith)) return false;
        CountRoot(found);
        _textMatched++;
        path = found;
        return true;
    }

    private static void RecordText(IntPtr key, string path, string? raw)
    {
        string plain = Tags.Replace(raw ?? "", "").Trim();
        if (Texts.TryGetValue(key, out string? last) && last == plain) return;
        Texts[key] = plain;
        if (!TakeLine()) return;

        // 참가자 표시명이 섞일 수 있어 숫자만 남기고, 계정 uid 같은 긴 숫자는 가린다.
        string nums = string.Join(' ', Numbers.Matches(plain).Select(m => MaskNumber(m.Value)));
        Write(_frame, $"text num=\"{nums}\" len={plain.Length} path={path}");
    }

    // 이전 조사에서 텍스트 후킹은 런처·한글패치 UI만 잡았다. 전투 씬에서 그 밖의 루트가 나오는지가
    // "게임(핫업데이트) 코드의 호출도 디투어에 걸린다"의 판정 기준이다.
    private static void CountRoot(string path)
    {
        int slash = path.IndexOf('/');
        string root = slash < 0 ? path : path[..slash];
        RootChanges[root] = RootChanges.TryGetValue(root, out int n) ? n + 1 : 1;
    }

    private static string MaskNumber(string number) => number.Count(char.IsDigit) >= 6 ? "#" : number;

    // 오브젝트 이름에 닉네임·식별자가 들어갈 수 있다. 프리팹 이름은 대개 ASCII라 그 밖의 글자가 섞인
    // 이름과 긴 숫자만 가린다. 게임은 플레이어별 카메라를 "<닉네임>_VirtualCamera"로 만들어서 ASCII
    // 닉네임도 새므로 따로 가린다(측정 4 첫 판). 가린 경로는 ScreenProbeWatch에 그대로 쓸 수 없다.
    private static string Mask(string name)
    {
        if (name.EndsWith(PlayerCameraSuffix, StringComparison.Ordinal) && name.Length > PlayerCameraSuffix.Length)
            return "~" + PlayerCameraSuffix;
        Match dice = DiceFace.Match(name);
        if (dice.Success) return "Dice_" + dice.Groups[1].Value;
        foreach (char c in name)
            if (c > '~') return $"~{name.Length}";
        return LongDigits.Replace(name, "#");
    }

    // 경로 계산이 가장 비싸므로 새 경로는 프레임당 개수를 제한한다. 캐시된 경로는 제한 없이 통과한다.
    private static bool TryPath(IntPtr key, Transform transform, out string? path)
    {
        RollFrame();
        if (Paths.TryGetValue(key, out path)) return true;
        if (_pathsThisFrame >= MaxNewPathsPerFrame)
        {
            _budgetPath++;
            return false;
        }
        _pathsThisFrame++;

        if (transform.root.name == OwnRoot)
        {
            _ownSkipped++;
            Paths[key] = null;
            return true;
        }

        var parts = new List<string>();
        Transform? t = transform;
        for (int i = 0; t != null && i < MaxDepth; i++, t = t.parent) parts.Add(Mask(t.name));
        parts.Reverse();
        path = (t != null ? ".../" : "") + string.Join('/', parts);
        Paths[key] = path;
        return true;
    }

    // 연출 중 UI가 한 프레임에 몰아 바뀌면 로그가 게임을 끊어 측정을 오염시킨다.
    private static bool TakeLine()
    {
        RollFrame();
        if (_linesThisFrame >= MaxLinesPerFrame)
        {
            _budgetLine++;
            return false;
        }
        _linesThisFrame++;
        _lines++;
        return true;
    }

    private static void RollFrame()
    {
        int frame = Time.frameCount;
        if (frame == _frame) return;
        _frame = frame;
        _linesThisFrame = 0;
        _pathsThisFrame = 0;
        _namesThisFrame = 0;
    }

    private static void ReportStats(bool force)
    {
        long calls = _activeCalls + _textCalls;
        if (!force && calls == _lastReportedCalls) return;
        _lastReportedCalls = calls;
        Write(Time.frameCount,
              $"stats activeCalls={_activeCalls} nameMatched={_nameMatched} activeChanges={_activeChanges} " +
              $"textCalls={_textCalls} textMatched={_textMatched} paths={Paths.Count} " +
              $"ownSkipped={_ownSkipped} lines={_lines} " +
              $"budgetName={_budgetName} budgetPath={_budgetPath} budgetLine={_budgetLine} " +
              $"roots={string.Join(',', RootChanges.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key}:{p.Value}"))}");
    }

    private static void Write(int frame, string text) =>
        _log?.LogInfo($"[probe] {DateTime.Now:HH:mm:ss.fff} f={frame} {text}");

    private static void Fail(Exception e)
    {
        if (++_failures < MaxFailures) return;
        _enabled = false;
        _log?.LogWarning($"[probe] disabled after {_failures} errors: {e.Message}");
    }
}

[HarmonyPatch]
internal static class ProbeSetActivePatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) });

    private static void Postfix(GameObject __instance, bool __0) => ScreenProbe.NoteActive(__instance, __0);
}

// SetText 오버로드 일부는 set_text를 거치지 않고 버퍼를 직접 채운다. 중복 호출은 RecordText가 걸러낸다.
[HarmonyPatch]
internal static class ProbeTmpTextPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertySetter(typeof(TMPro.TMP_Text), nameof(TMPro.TMP_Text.text));
        foreach (MethodInfo m in typeof(TMPro.TMP_Text).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == nameof(TMPro.TMP_Text.SetText) && m.DeclaringType == typeof(TMPro.TMP_Text))
                yield return m;
    }

    private static void Postfix(TMPro.TMP_Text __instance) => ScreenProbe.NoteTmp(__instance);
}

[HarmonyPatch]
internal static class ProbeUiTextPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.PropertySetter(typeof(UnityEngine.UI.Text), nameof(UnityEngine.UI.Text.text));

    private static void Postfix(UnityEngine.UI.Text __instance) => ScreenProbe.NoteUiText(__instance);
}
