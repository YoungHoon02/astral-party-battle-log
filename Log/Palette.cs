using System.Text.RegularExpressions;

namespace AstralPartyBattleLog.Log;

// 줄은 색상 태그를 붙인 채 한 번만 만들고, 파일로 나갈 때 Strip으로 걷어낸다.
internal static class Palette
{
    // Core.GameConfig.slotColor
    private static readonly string[] SlotColors =
    {
        "FF4646",
        "94FF46",
        "4386F4",
        "FFB346",
        "A053D4",
    };

    private const string MonsterColor = "B9C2D0";

    private const string AttackColor = "FF7B7B";
    private const string DefenseColor = "6FB6FF";

    private const string DamageColor = "FF9A8A";
    private const string HealColor = "8BE0A0";

    private const string GoldColor = "D9A441";

    private static readonly Regex TagPattern = new("</?color[^>]*>", RegexOptions.Compiled);

    public static string Wrap(string text, string hex) => $"<color=#{hex}>{text}</color>";

    public static string Player(string label, int slot) =>
        Wrap(label, slot < 0 || slot >= SlotColors.Length
            ? (slot < 0 ? MonsterColor : SlotColors[^1])
            : SlotColors[slot]);

    public static string Attack(string text) => Wrap(text, AttackColor);

    public static string Defense(string text) => Wrap(text, DefenseColor);

    public static string Delta(string text, bool isDamage) => Wrap(text, isDamage ? DamageColor : HealColor);

    public static string Gold(string text) => Wrap(text, GoldColor);

    // GameLogic.BattleCardMessage.GetCardMsg
    public static string Card(string text, string? cardType) => Wrap(text, cardType switch
    {
        "Attack" => "FF0000",
        "Defend" => "0099FF",
        _ => "00CC00",
    });

    // GameLogic.BattleRelicMessage
    public static string Relic(string text, string? grade) => Wrap(text, grade switch
    {
        "Blue" => "4D8BFF",     // 게임 값 #004DFF는 어두운 패널에서 안 보여 한 톤 올렸다
        "Purple" => "9700E6",
        "Orange" => "EE8F00",
        _ => "C9D4E3",
    });

    public static string Event(string text) => Wrap(text, "FFCC33");

    public static string Strip(string line) =>
        line.IndexOf('<') < 0 ? line : TagPattern.Replace(line, "");
}
