using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AstralPartyBattleLog.UI;

/// <summary>
/// 전투 로그 uGUI 오버레이. 설계 참조: astral-party-korean-patch의 <c>OverlayUi</c>
/// (작성자 허락 받음, https://github.com/maynut02/astral-party-korean-patch).
/// </summary>
internal static class LogOverlay
{
    private const string PreferredFontName = "Afacad-Regular";
    private const int MaxPages = 60;

    private const float PadX = 12f;
    private const float PadY = 10f;

    private const float ReferenceHeight = 1080f;
    private const float GripSize = 24f;
    private const float GripInset = 3f;
    private const float HeaderLeft = GripInset + GripSize + 3f;

    private const float DefaultMargin = 24f;

    public const float AutoPosition = -1f;

    private static readonly Color GripIdle = new(1f, 1f, 1f, 0f);
    private static readonly Color GripHot = new(1f, 1f, 1f, 0.12f);

    private enum SignalKind { Line, Page, GameStart }

    private readonly struct Signal
    {
        public readonly SignalKind Kind;
        public readonly string? Text;
        public readonly int Round;

        private Signal(SignalKind kind, string? text, int round)
        {
            Kind = kind;
            Text = text;
            Round = round;
        }

        public static Signal Line(string text) => new(SignalKind.Line, text, 0);
        public static Signal Page(int round) => new(SignalKind.Page, null, round);
        public static Signal GameStart() => new(SignalKind.GameStart, null, 0);
    }

    private sealed class Page
    {
        public int Round;
        public readonly List<string> Lines = new();
    }

    private static readonly ConcurrentQueue<Signal> Pending = new();

    private static readonly List<Page> Pages = new();

    private static ManualLogSource? _log;
    private static GameObject? _root;
    private static Text? _text;
    private static Text? _header;
    private static RectTransform? _panel;
    private static RectTransform? _grip;
    private static Image? _gripBack;
    private static bool _gripHot;
    private static Font? _font;
    private static bool _failed;
    private static bool _dirty;
    private static bool _visible;
    private static bool _userHidden;
    private static bool _inGame;

    private static bool _awaitingBattle;
    private static string? _pickScene;
    private static int _roundsBeforeBattle;
    private static bool _roundSeen;
    private static string? _battleScene;

    public static Action? OnLeftGame;
    private static int _lastFontScan;

    private static int _view;
    private static int _offset;
    private static bool _following = true;

    // 폭 = 폰트 비례항 + 고정 여백. 여백까지 비례시키면 작은 FontSize에서 줄이 잘린다.
    private const float WidthMargin = HeaderLeft + PadX;
    private const float TextWidthPerFontSize = 32f;

    public static int MaxLines = 14;
    public static int FontSize = 15;
    public static KeyCode ToggleKey = KeyCode.F9;
    public static int ScrollLines = 3;

    // 화면 밖이면 표시할 때만 보정하고 이 값은 두어, 해상도를 되돌리면 원래 자리로 돌아온다.
    private static Vector2 _desired;
    private static bool _autoPlace = true;
    private static Vector2 _saved = new(AutoPosition, AutoPosition);
    private static Action<Vector2>? _savePosition;

    private static bool _dragging;
    private static Vector2 _dragMouse;
    private static Vector2 _dragPanel;
    private static int _screenW;
    private static int _screenH;

    public static void Init(ManualLogSource log, int maxLines, int fontSize,
                            KeyCode toggleKey, int scrollLines,
                            Vector2 position, Action<Vector2> savePosition)
    {
        _log = log;
        MaxLines = Math.Max(1, maxLines);
        FontSize = Math.Max(8, fontSize);
        ToggleKey = toggleKey;
        ScrollLines = Math.Max(1, scrollLines);
        _autoPlace = !(float.IsFinite(position.x) && float.IsFinite(position.y)
                       && position.x >= 0f && position.y >= 0f);
        _desired = _autoPlace ? Vector2.zero : position;
        _saved = position;
        _savePosition = savePosition;
    }

    // 소켓 IO 스레드에서도 불린다. Unity 객체를 건드리지 말 것.
    public static void Enqueue(string line)
    {
        if (!_failed) Pending.Enqueue(Signal.Line(line));
    }

    public static void NewPage(int round)
    {
        if (!_failed) Pending.Enqueue(Signal.Page(round));
    }

    public static void GameStarted()
    {
        if (!_failed) Pending.Enqueue(Signal.GameStart());
    }

    public static void OnSceneChanged(string scene)
    {
        if (_awaitingBattle)
        {
            if (scene == _pickScene) return;
            _awaitingBattle = false;
            _battleScene = scene;
            _roundSeen = false;
            if (!_userHidden) SetVisible(true);
            return;
        }

        if (_battleScene is null || scene == _battleScene) return;

        // 라운드 전의 전환은 로딩이다. 단, 픽창으로 돌아왔으면 판을 나간 것이다.
        if (!_roundSeen && scene != _pickScene)
        {
            _battleScene = scene;
            return;
        }

        _battleScene = null;
        _inGame = false;
        SetVisible(false);
        OverlaySchedule.Discard();
        try { OnLeftGame?.Invoke(); } catch { }
    }

    private static void EnterGame()
    {
        if (_inGame) return;
        _inGame = true;
        _userHidden = false;
    }

    private static void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (!visible) EndDrag();
        if (_root is not null) _root.SetActive(visible);
    }

    public static void Pump()
    {
        if (_failed) return;

        try
        {
            HandleKeys();
            HandleDrag();
            TrackScreenSize();

            while (Pending.TryDequeue(out Signal signal))
            {
                switch (signal.Kind)
                {
                    case SignalKind.Line:
                        // 라운드 신호보다 줄이 먼저 올 수 있다(방 입장 시 참가자 목록).
                        if (Pages.Count == 0) OpenPage(0);
                        Pages[^1].Lines.Add(signal.Text!);
                        _dirty = true;
                        break;
                    case SignalKind.Page:
                        OpenPage(signal.Round);
                        break;
                    case SignalKind.GameStart:
                        Pages.Clear();
                        _view = 0;
                        _offset = 0;
                        _following = true;
                        _dirty = true;
                        _battleScene = null;
                        _roundSeen = false;
                        _inGame = false;
                        EnterGame();
                        _awaitingBattle = true;
                        _pickScene = FramePump.CurrentScene;
                        _roundsBeforeBattle = 0;
                        SetVisible(false);
                        break;
                }
            }

            if (!_dirty) return;
            _dirty = false;

            if (_root is null) Create();
            RefreshFont();
            Render();
        }
        catch (Exception e)
        {
            _failed = true;
            _log?.LogWarning($"Overlay failed; battle-log.txt keeps working: {e}");
        }
    }

    private static void HandleKeys()
    {
        if (Input.GetKeyDown(ToggleKey))
        {
            SetVisible(!_visible);
            _userHidden = !_visible;
        }

        if (Pages.Count == 0) return;

        if (_visible && IsPointerOverPanel())
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0f) Step(-ScrollLines);
            else if (wheel < 0f) Step(+ScrollLines);
        }
    }

    private static bool IsPointerOverPanel() => IsPointerOver(_panel);

    private static bool IsPointerOver(RectTransform? rect)
    {
        if (rect is null) return false;
        // ScreenSpaceOverlay 캔버스라 카메라는 null을 넘긴다.
        return RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null);
    }

    private static void HandleDrag()
    {
        if (_panel is null || !_visible)
        {
            EndDrag();
            return;
        }

        Vector2 mouse = Input.mousePosition;

        if (!_dragging)
        {
            bool over = IsPointerOver(_grip);
            SetGripHot(over);
            if (!over || !Input.GetMouseButtonDown(0)) return;

            _dragging = true;
            _dragMouse = mouse;
            _dragPanel = ClampToScreen(CurrentDesired());
            return;
        }

        // 화면 좌표는 y가 위로, 저장 좌표는 y가 아래로 자란다.
        Vector2 delta = (mouse - _dragMouse) / CanvasScale();
        if (!_autoPlace || delta != Vector2.zero)
        {
            _desired = ClampToScreen(new Vector2(_dragPanel.x + delta.x, _dragPanel.y - delta.y));
            _autoPlace = false;
            ApplyPosition();
        }

        if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0)) EndDrag();
    }

    private static void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        SetGripHot(false);
        if (_autoPlace) return;

        var rounded = new Vector2(MathF.Round(_desired.x), MathF.Round(_desired.y));
        _desired = rounded;
        if (_panel is not null) ApplyPosition();
        if (rounded == _saved) return;

        try
        {
            _savePosition?.Invoke(rounded);
            _saved = rounded;
        }
        catch (Exception e)
        {
            _log?.LogWarning($"Could not save overlay position: {e.Message}");
        }
    }

    private static void SetGripHot(bool hot)
    {
        if (_gripHot == hot || _gripBack is null) return;
        _gripHot = hot;
        _gripBack.color = hot ? GripHot : GripIdle;
    }

    private static void TrackScreenSize()
    {
        if (Screen.width == _screenW && Screen.height == _screenH) return;
        _screenW = Screen.width;
        _screenH = Screen.height;
        if (_panel is not null && !_dragging) ApplyPosition();
    }

    private static float _firstLine;
    private static float _linePitch;

    private static float TextHeight(int lines) =>
        _linePitch > 0f ? _firstLine + _linePitch * (lines - 1) : FontSize * 1.45f * lines;

    private static void MeasureLines()
    {
        if (_text is null || _panel is null) return;
        string saved = _text.text;
        _text.text = "가";
        float one = _text.preferredHeight;
        _text.text = "가\n가";
        float two = _text.preferredHeight;
        _text.text = saved;
        if (one <= 0f || two <= one) return;

        _firstLine = one;
        _linePitch = two - one;
        _log?.LogInfo($"Overlay line height measured: first {one:0.#}, pitch {_linePitch:0.#} at font size {FontSize}.");
        _panel.sizeDelta = PanelSize();
        ApplyPosition();
    }

    private static float HeaderGap() => FontSize * 0.5f;

    private static float HeightFor(int lines) => TextHeight(lines) + FontSize + HeaderGap() + PadY * 2f;

    private static Vector2 CanvasSize()
    {
        float scale = CanvasScale();
        return new Vector2(Screen.width / scale, Screen.height / scale);
    }

    // canvas.scaleFactor는 스케일러 Update가 돌기 전엔 1이라 직접 계산한다 (matchWidthOrHeight = 1).
    private static float CanvasScale() => Math.Max(Screen.height, 1) / ReferenceHeight;

    private static void ApplyPosition()
    {
        Vector2 pos = ClampToScreen(CurrentDesired());
        _panel!.anchoredPosition = new Vector2(pos.x, -pos.y);
    }

    private static Vector2 CurrentDesired()
    {
        if (!_autoPlace) return _desired;
        float canvasH = Screen.height / CanvasScale();
        return new Vector2(DefaultMargin, canvasH - DefaultMargin - HeightFor(MaxLines));
    }

    private static Vector2 PanelSize() =>
        new(FontSize * TextWidthPerFontSize + WidthMargin, HeightFor(MaxLines));

    // 창이 화면보다 크면 그립이 있는 위쪽·왼쪽을 살려야 다시 끌 수 있다.
    private static Vector2 ClampToScreen(Vector2 pos)
    {
        Vector2 size = PanelSize();
        Vector2 canvas = CanvasSize();

        float x = Math.Max(0f, Math.Min(pos.x, canvas.x - size.x));
        float y = Math.Max(0f, Math.Min(pos.y, canvas.y - size.y));
        return new Vector2(x, y);
    }

    private static void Step(int lines)
    {
        if (_following)
        {
            _view = Pages.Count - 1;
            _offset = MaxOffset(_view);
        }

        int remaining = Math.Abs(lines);
        int direction = Math.Sign(lines);

        while (remaining > 0)
        {
            int target = _offset + direction * remaining;

            if (target < 0)
            {
                if (_view == 0) { _offset = 0; break; }
                remaining = -target;   // 남은 만큼을 이전 라운드 끝에서 마저 소화한다
                _view--;
                _offset = MaxOffset(_view);
                continue;
            }

            int max = MaxOffset(_view);
            if (target > max)
            {
                if (_view == Pages.Count - 1) { _offset = max; break; }
                remaining = target - max;
                _view++;
                _offset = 0;
                continue;
            }

            _offset = target;
            break;
        }

        _following = _view == Pages.Count - 1 && _offset >= MaxOffset(_view);
        _dirty = true;
    }

    private static int MaxOffset(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return 0;
        return Math.Max(0, Pages[pageIndex].Lines.Count - MaxLines);
    }

    private static void OpenPage(int round)
    {
        // 1라운드 신호는 전투 씬 로드 전(픽창)에 온다(실측). 여기서 씬을 기록하면 곧 이어질
        // 전투 씬 전환을 판 이탈로 읽으므로, 대기 중에는 두 번째 라운드까지 씬 전환을 기다린다.
        if (round > 0)
        {
            EnterGame();
            if (!_awaitingBattle || ++_roundsBeforeBattle >= 2)
            {
                _awaitingBattle = false;
                _battleScene = FramePump.CurrentScene;
                _roundSeen = true;
                if (!_userHidden) SetVisible(true);
            }
        }

        Pages.Add(new Page { Round = round });
        if (Pages.Count > MaxPages)
        {
            Pages.RemoveRange(0, Pages.Count - MaxPages);
            if (_view > 0) _view--;
            _offset = Math.Min(_offset, MaxOffset(_view));
        }
        if (_following) { _view = Pages.Count - 1; _offset = 0; }
        _dirty = true;
    }

    private static void Render()
    {
        if (_text is null || _header is null) return;

        if (Pages.Count == 0)
        {
            _header.text = "";
            _text.text = "";
            return;
        }

        if (_following)
        {
            _view = Pages.Count - 1;
            _offset = MaxOffset(_view);
        }
        _view = Math.Clamp(_view, 0, Pages.Count - 1);
        _offset = Math.Clamp(_offset, 0, MaxOffset(_view));

        Page page = Pages[_view];
        int total = page.Lines.Count;
        int shown = Math.Min(MaxLines, total - _offset);

        string position = total > MaxLines ? $"   {_offset + 1}-{_offset + shown}/{total}" : "";
        string hint = _following ? "" : "   (최신 아님)";
        _header.text = $"Round {page.Round}{position}{hint}";

        _text.text = string.Join("\n", page.Lines.Skip(_offset).Take(MaxLines));
    }

    private static void Create()
    {
        _root = new GameObject("AstralPartyBattleLogOverlay");
        Object.DontDestroyOnLoad(_root);

        Canvas canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32750;   // 한글패치 오버레이(32760)보다 한 칸 아래

        CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        // 폭을 섞으면 울트라와이드에서 빈 배경만 넓어진다.
        scaler.matchWidthOrHeight = 1f;

        var panel = new GameObject("Panel");
        panel.transform.SetParent(_root.transform, false);
        Image background = panel.AddComponent<Image>();
        background.color = new Color(0.02f, 0.03f, 0.06f, 0.62f);
        background.raycastTarget = false;
        // 줄바꿈 대신 자른다. 줄바꿈은 스크롤이 세는 논리 줄 수와 그려지는 줄 수를 어긋나게 한다.
        panel.AddComponent<RectMask2D>();

        // Unity 객체는 가짜 null이라 ?? 대신 interop 캐스팅을 쓴다.
        RectTransform rect = panel.transform.TryCast<RectTransform>()!;
        _panel = rect;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = PanelSize();
        ApplyPosition();

        CreateGrip(panel, FontSize);

        _header = AddText(panel, "Header", TextAnchor.UpperLeft,
                          new Color(0.62f, 0.72f, 0.86f, 0.85f));
        RectTransform headerRect = _header.transform.TryCast<RectTransform>()!;
        headerRect.anchorMin = new Vector2(0f, 1f);
        headerRect.anchorMax = new Vector2(1f, 1f);
        headerRect.pivot = new Vector2(0f, 1f);
        headerRect.offsetMin = new Vector2(HeaderLeft, -FontSize - PadY);
        headerRect.offsetMax = new Vector2(-PadX, -PadY);

        _text = AddText(panel, "Lines", TextAnchor.UpperLeft,
                        new Color(0.93f, 0.96f, 1f, 0.92f));
        RectTransform textRect = _text.transform.TryCast<RectTransform>()!;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(PadX, PadY);
        textRect.offsetMax = new Vector2(-PadX, -FontSize - HeaderGap() - PadY);
        MeasureLines();

        _root.SetActive(_visible);
        _log?.LogInfo($"Overlay ready. {ToggleKey} toggles it; scroll with the mouse wheel; drag the grip to move it.");
    }

    private static void CreateGrip(GameObject panel, float headerHeight)
    {
        var area = new GameObject("Grip");
        area.transform.SetParent(panel.transform, false);
        _gripBack = area.AddComponent<Image>();
        _gripBack.color = GripIdle;
        _gripBack.raycastTarget = false;
        _gripHot = false;

        _grip = area.transform.TryCast<RectTransform>()!;
        _grip.anchorMin = new Vector2(0f, 1f);
        _grip.anchorMax = new Vector2(0f, 1f);
        _grip.pivot = new Vector2(0f, 0.5f);
        _grip.sizeDelta = new Vector2(GripSize, GripSize);
        _grip.anchoredPosition = new Vector2(GripInset, -(PadY + headerHeight * 0.5f));

        const float dot = 3f;
        const float gap = 5f;
        var dotColor = new Color(0.62f, 0.72f, 0.86f, 0.7f);
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                var go = new GameObject("Dot");
                go.transform.SetParent(area.transform, false);
                Image image = go.AddComponent<Image>();
                image.color = dotColor;
                image.raycastTarget = false;

                RectTransform r = go.transform.TryCast<RectTransform>()!;
                r.anchorMin = new Vector2(0.5f, 0.5f);
                r.anchorMax = new Vector2(0.5f, 0.5f);
                r.pivot = new Vector2(0.5f, 0.5f);
                r.sizeDelta = new Vector2(dot, dot);
                r.anchoredPosition = new Vector2((col - 0.5f) * gap, (1 - row) * gap);
            }
        }
    }

    private static Text AddText(GameObject parent, string name, TextAnchor anchor, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        Text text = go.AddComponent<Text>();
        text.font = ResolveFont();
        text.fontSize = FontSize;
        text.color = color;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = true;
        text.raycastTarget = false;
        return text;
    }

    // 한글패치가 교체한 게임 폰트를 쓴다. 씬 전환 뒤에 로드되기도 해서 주기적으로 다시 찾는다.
    private static void RefreshFont()
    {
        if (_text is null) return;
        if (_font is not null && Time.frameCount - _lastFontScan < 600) return;
        _lastFontScan = Time.frameCount;

        Font? found = FindGameFont();
        if (found is null || ReferenceEquals(found, _font)) return;

        _font = found;
        foreach (Text? label in new[] { _text, _header })
        {
            if (label is null) continue;
            label.font = found;
            label.SetVerticesDirty();
            label.SetLayoutDirty();
        }
        MeasureLines();
    }

    private static Font ResolveFont() => FindGameFont() ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

    // 인자 없는 제네릭 오버로드는 interop에서 해석이 모호해 리플렉션으로 부른다.
    private static Font? FindGameFont()
    {
        try
        {
            MethodInfo? generic = typeof(Resources)
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll"
                                     && m.IsGenericMethodDefinition
                                     && m.GetParameters().Length == 0);
            if (generic is null) return null;

            if (generic.MakeGenericMethod(typeof(Font)).Invoke(null, null) is not IEnumerable all) return null;

            foreach (object? item in all)
            {
                if (item is Font font && font.name == PreferredFontName) return font;
            }
        }
        catch { }
        return null;
    }
}
