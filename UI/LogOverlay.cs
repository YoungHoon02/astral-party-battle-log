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
/// 전투 로그를 게임 화면에 겹쳐 보여준다. 라운드 하나가 한 페이지이고, 커서를 올린 채
/// 마우스 휠로 스크롤한다.
///
/// <b>IMGUI가 아니라 uGUI</b>다 — <c>Canvas</c>/<c>Image</c>/<c>Text</c>는 게임에 이미
/// 있는 컴포넌트라 <c>AddComponent</c>로 붙일 수 있고, 이 게임에서 확정 크래시인
/// <c>ClassInjector.RegisterTypeInIl2Cpp</c>가 필요 없다.
/// (설계 참조: astral-party-korean-patch의 <c>OverlayUi</c>, 작성자 허락 받음 —
/// https://github.com/maynut02/astral-party-korean-patch)
///
/// 줄은 소켓 IO 스레드에서 들어오므로 큐로 넘겨 <see cref="Pump"/>가 프레임마다 비운다.
/// </summary>
internal static class LogOverlay
{
    private const string PreferredFontName = "Afacad-Regular";
    private const int MaxPages = 60;

    /// <summary>창 안쪽 여백. 높이 계산이 이 값에 맞물려 있으니 한 곳에서만 고친다.</summary>
    private const float PadX = 12f;
    private const float PadY = 10f;

    private const float ReferenceHeight = 1080f;
    private const float GripSize = 24f;
    private const float GripInset = 3f;
    /// <summary>머리줄 왼쪽 여백. 그립 자리를 비워둔다.</summary>
    private const float HeaderLeft = GripInset + GripSize + 3f;

    /// <summary>저장된 위치가 없을 때 창을 화면 좌하단에서 띄울 거리.</summary>
    private const float DefaultMargin = 24f;

    /// <summary>설정값이 이것이면(음수) 저장된 위치가 없는 것으로 본다.</summary>
    public const float AutoPosition = -1f;

    private static readonly Color GripIdle = new(1f, 1f, 1f, 0f);
    private static readonly Color GripHot = new(1f, 1f, 1f, 0.12f);

    private enum SignalKind { Line, Page, GameStart }

    /// <summary>
    /// 소켓 스레드 → 메인 스레드 전달 통로에 실리는 신호.
    /// 줄과 경계를 같은 큐로 보내야 순서가 어긋나지 않는다.
    /// </summary>
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
    /// <summary>
    /// 처음엔 숨어 있다가 라운드가 시작되면 저절로 뜬다. 게임을 켜자마자 로비에
    /// 전투 로그창이 떠 있으면 방해만 된다.
    /// </summary>
    private static bool _visible;

    /// <summary>사용자가 직접 껐다. 이 경우 자동으로 다시 켜지 않는다.</summary>
    private static bool _userHidden;

    /// <summary>판 안에 있다고 본 상태. 이 값이 켜지는 순간에만 <see cref="_userHidden"/>을 푼다.</summary>
    private static bool _inGame;

    /// <summary>
    /// 판은 시작됐지만 아직 픽창이다. 픽창을 벗어나는 씬 전환에서 창을 띄운다.
    /// </summary>
    private static bool _awaitingBattle;
    private static string? _pickScene;
    private static int _roundsBeforeBattle;

    /// <summary>
    /// 지금 기록된 전투 씬에서 라운드를 받았다. 받기 전의 씬 전환은 로딩을 거치는 중으로
    /// 보고 숨기지 않는다.
    /// </summary>
    private static bool _roundSeen;

    /// <summary>
    /// 전투가 벌어지고 있는 씬 이름. 씬 전환 자체를 "게임을 나갔다"로 보면 안 된다 —
    /// 로딩 씬을 거치느라 전투 중에도 씬이 바뀐다. <b>이 씬을 벗어날 때만</b> 나간 것으로 본다.
    /// </summary>
    private static string? _battleScene;

    /// <summary>오버레이를 꺼도 씬 감지는 돌므로 표시 설정과 무관하게 불린다.</summary>
    public static Action? OnLeftGame;
    private static int _lastFontScan;

    /// <summary>
    /// 보고 있는 위치 — 라운드와 그 안에서 맨 위에 보이는 줄 번호. 한 라운드가 50줄을
    /// 넘어서 라운드 단위로만 넘기면 대부분을 볼 수 없다. 스크롤은 둘을 연속으로 훑는다.
    /// </summary>
    private static int _view;
    private static int _offset;

    /// <summary>맨 끝을 보고 있으면 새 줄을 따라간다.</summary>
    private static bool _following = true;

    /// <summary>
    /// 글자 크기당 텍스트 폭(px)과 별도인 고정 여백. 실측(619px @ FontSize 22)엔
    /// <see cref="PadX"/>×2(본문) 또는 <see cref="HeaderLeft"/>+<see cref="PadX"/>(헤더,
    /// 그립 자리 포함) 중 하나가 이미 섞여 있었다 — 폰트 크기와 무관한 상수라 비례시키면
    /// 안 되므로 따로 뗀다. 둘 중 더 큰 헤더 쪽을 기준으로 잡아, 작은 FontSize에서도
    /// 여백이 줄어들어 잘리는 일이 없게 한다.
    /// </summary>
    private const float WidthMargin = HeaderLeft + PadX;

    /// <summary>긴 스킬 PK 줄이 기존 폭에서 잘려 여유를 늘렸다. 폴백 폰트는 별도 실측이 필요하다.</summary>
    private const float TextWidthPerFontSize = 32f;

    public static int MaxLines = 14;
    public static int FontSize = 15;
    public static KeyCode ToggleKey = KeyCode.F9;
    public static int ScrollLines = 3;

    /// <summary>
    /// 사용자가 둔 창 <b>좌상단</b> 위치 (x = 왼쪽에서, y = 위에서 아래로, 높이 1080 기준
    /// 캔버스 좌표). 화면에 실제로 놓이는 위치는 이 값을 경계 안으로 보정한 것이고, 이 값
    /// 자체는 바꾸지 않는다 — 해상도를 낮췄다 올렸을 때 원래 자리로 돌아오게.
    /// </summary>
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

    /// <summary>소켓 IO 스레드에서 호출된다. Unity를 건드리지 않는다.</summary>
    public static void Enqueue(string line)
    {
        if (!_failed) Pending.Enqueue(Signal.Line(line));
    }

    public static void NewPage(int round)
    {
        if (!_failed) Pending.Enqueue(Signal.Page(round));
    }

    /// <summary>
    /// 새 판이 시작됐다. 판 시작 신호는 <b>픽창에서</b> 오므로 여기서는
    /// 페이지만 비우고, 창은 픽창을 벗어나는 씬 전환(<see cref="OnSceneChanged"/>)에서 띄운다.
    /// 소켓 스레드에서 부르므로 큐를 거쳐 <see cref="Pump"/>가 처리한다.
    /// </summary>
    public static void GameStarted()
    {
        if (!_failed) Pending.Enqueue(Signal.GameStart());
    }

    /// <summary>
    /// 씬이 바뀌었다. 창을 띄우고 숨기는 기준이 둘 다 씬 전환이다.
    /// <list type="bullet">
    /// <item>픽창을 벗어남 → 전투 씬에 들어간 것. 창을 띄운다.</item>
    /// <item>그 뒤 라운드를 받기 전의 전환 → 로딩을 거치는 중. 전투 씬 기록만 옮긴다.</item>
    /// <item>라운드를 받은 뒤 전투 씬을 벗어남 → 판을 나간 것. 창을 숨긴다.</item>
    /// </list>
    /// 내용은 지우지 않는다 — 씬 전환에 걸어 지우면 전투 중 로딩 씬 전환에 휩쓸려 페이지가
    /// 날아간다. 메인 스레드(프레임 펌프)에서 부르는 것을 전제로 한다.
    /// </summary>
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

        // 픽창 씬으로 돌아왔으면 라운드를 받았든 아니든 판을 나간 것이다.
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
        // 지난 판이나 로비에서 F9로 꺼둔 것이 새 판까지 이어지면 안 된다.
        _userHidden = false;
    }

    private static void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (!visible) EndDrag();
        if (_root is not null) _root.SetActive(visible);
    }

    /// <summary>메인 스레드에서 프레임당 한 번.</summary>
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
            // 오버레이 때문에 게임이 죽으면 안 된다. 한 번 실패하면 조용히 포기한다.
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

        // 커서가 창 위에 있을 때만 휠을 먹는다. 아니면 게임 조작까지 스크롤이 된다.
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

    /// <summary>
    /// 그립 드래그. 이벤트를 받을 수 없으니(EventTrigger·MonoBehaviour 불가) 버튼 상태를
    /// 폴링한다. 입력을 소비하지는 않으므로 게임도 같은 클릭을 받는다.
    /// </summary>
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

    /// <summary>드래그를 끝내고, 위치가 바뀌었으면 그때 한 번만 설정 파일에 쓴다.</summary>
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
            // 저장 실패로 오버레이 전체가 꺼지면 안 된다.
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

    /// <summary>
    /// 실제 폰트로 잰 첫 줄 높이와 줄 간격. 고정 배수(1.45)는 창이 내용에 맞춰 늘던 시절의
    /// 넉넉한 추정이라, 창 크기를 고정한 뒤로는 아래에 빈 공간이 남았다. 폰트가 바뀌면 다시 잰다.
    /// </summary>
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

    /// <summary>
    /// 화면 픽셀 → 캔버스 단위. <c>matchWidthOrHeight = 1</c>이면 CanvasScaler의 배율은
    /// 정확히 <c>Screen.height / 1080</c>이다. <c>canvas.scaleFactor</c>를 읽지 않는 이유:
    /// 스케일러의 Update가 한 번 돌기 전(생성 직후, 숨겨진 동안)에는 1로 남아 있어 낮은
    /// 해상도에서 위치를 엉뚱하게 보정한다.
    /// </summary>
    private static float CanvasScale() => Math.Max(Screen.height, 1) / ReferenceHeight;

    private static void ApplyPosition()
    {
        Vector2 pos = ClampToScreen(CurrentDesired());
        _panel!.anchoredPosition = new Vector2(pos.x, -pos.y);
    }

    /// <summary>저장된 위치가 없으면 창이 화면 좌하단에서 24만큼 떨어지는 자리.</summary>
    private static Vector2 CurrentDesired()
    {
        if (!_autoPlace) return _desired;
        float canvasH = Screen.height / CanvasScale();
        // 창이 자라도 좌상단이 흔들리지 않도록 최대 높이 기준으로 잡는다.
        return new Vector2(DefaultMargin, canvasH - DefaultMargin - HeightFor(MaxLines));
    }

    /// <summary>
    /// 높이가 <see cref="FontSize"/> × <see cref="LineHeight"/> 배수로 고정되는 것과 같은
    /// 이유로, 폭도 <see cref="FontSize"/> 비례로 고정한다. 다만 <see cref="WidthMargin"/>은
    /// 폰트 크기와 무관한 고정값이라 비례항과 분리했다 — 합쳐서 하나의 배수로 두면 작은
    /// FontSize에서 여백까지 같이 줄어들어 잘릴 수 있다. 내용에 맞춰 매 렌더마다
    /// 계산하면 창이 커졌다 작아져 읽기 어렵다.
    /// </summary>
    private static Vector2 PanelSize() =>
        new(FontSize * TextWidthPerFontSize + WidthMargin, HeightFor(MaxLines));

    /// <summary>창이 화면보다 커져도 다시 끌 수 있도록 그립이 있는 위쪽과 왼쪽을 살린다.</summary>
    private static Vector2 ClampToScreen(Vector2 pos)
    {
        Vector2 size = PanelSize();
        Vector2 canvas = CanvasSize();

        float x = Math.Max(0f, Math.Min(pos.x, canvas.x - size.x));
        float y = Math.Max(0f, Math.Min(pos.y, canvas.y - size.y));
        return new Vector2(x, y);
    }

    /// <summary>
    /// 줄 단위로 위아래. 라운드 경계를 만나면 이웃 라운드로 이어진다 — 경계를
    /// 의식하지 않고 계속 굴리면 된다.
    /// </summary>
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
        // round > 0 = 진짜 라운드가 시작됐다 = 게임 안이다. round 0은 라운드 신호보다
        // 줄이 먼저 올 때 만드는 임시 페이지라, 그걸로 창을 띄우면 로비에서 떠버린다.
        // 1라운드 신호는 전투 씬이 로드되기 전, 아직 픽창 씬일 때 온다(실측). 그걸로 전투
        // 씬을 기록하면 곧 이어지는 진짜 전투 씬 전환을 "판을 나감"으로 읽어 창을 숨긴다.
        // 그래서 픽창에서 받은 라운드는 씬 전환을 기다리고, 두 번째 라운드까지 전환이 없을
        // 때만 씬이 안 바뀌는 판으로 보고 띄운다. 재접속(RunningGameS2C)은 판 시작 신호가
        // 없어 곧바로 띄운다. 매 라운드 현재 씬을 다시 기록한다.
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
        // **높이에 맞춘다(1.0).** 폭을 섞으면 울트라와이드에서 배율만 올라가고 글자는
        // 안 길어져서 빈 배경이 넓어진다.
        scaler.matchWidthOrHeight = 1f;

        var panel = new GameObject("Panel");
        panel.transform.SetParent(_root.transform, false);
        Image background = panel.AddComponent<Image>();
        background.color = new Color(0.02f, 0.03f, 0.06f, 0.62f);
        background.raycastTarget = false;
        // 창 크기가 고정이라 긴 줄은 경계에서 잘라야 한다. 줄바꿈은 스크롤이 세는 논리 줄
        // 수와 그려지는 줄 수를 어긋나게 하므로 쓰지 않는다.
        panel.AddComponent<RectMask2D>();

        // Graphic을 붙이면 RectTransform이 자동으로 생긴다. Unity 객체는 "가짜 null"이라
        // ?? 널 병합이 제대로 안 먹으므로 직접 캐스팅한다.
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

    /// <summary>
    /// 머리줄 왼쪽의 이동 손잡이. 판정 영역은 24×24이고 보이는 건 2×3 점뿐이다.
    /// 전부 <c>raycastTarget = false</c> — 판정은 <see cref="HandleDrag"/>가 좌표로 한다.
    /// </summary>
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
        text.supportRichText = true;   // 색상 태그
        text.raycastTarget = false;
        return text;
    }

    /// <summary>
    /// 게임 기본 폰트 <c>Afacad-Regular</c>를 그대로 쓴다 — 한글패치가 이 폰트를 교체하므로
    /// 같은 것을 쓰면 오버레이도 한글이 나온다. 씬이 바뀐 뒤 로드되기도 해서 주기적으로 다시 찾는다.
    /// </summary>
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

    /// <summary>
    /// 리플렉션으로 부른다 — Il2CppInterop에서 인자 없는 제네릭 오버로드는 직접 호출하면
    /// 해석이 모호하다.
    /// </summary>
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
        catch
        {
            // 폰트를 못 찾으면 기본 폰트로 간다. 한글은 깨지지만 로그는 보인다.
        }
        return null;
    }
}
