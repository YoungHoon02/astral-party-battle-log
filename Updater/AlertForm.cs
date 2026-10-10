using System.Drawing;
using System.Windows.Forms;

namespace AstralPartyBattleLog.Updater;

// OK = 업데이트하러 가기, Ignore = 이번 버전은 무시, 닫기(X) = Cancel이라 다음 실행에 다시 알린다.
internal sealed class AlertForm : Form
{
    public AlertForm(string latest, string current)
    {
        Text = "Astral Party Battle Log";
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        // 게임과 함께 뜨므로, 맨 위에 두지 않으면 게임 창 뒤에 깔려 알림을 못 본다.
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 12);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        var message = new Label
        {
            AutoSize = true,
            Text = $"새 버전 v{latest}이 출시되었습니다.\n지금 설치된 버전은 v{current}입니다.",
            Margin = new Padding(0, 0, 0, 16),
        };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        var open = new Button { Text = "업데이트하러 가기", AutoSize = true, DialogResult = DialogResult.OK };
        var skip = new Button { Text = "이번 버전은 무시", AutoSize = true, DialogResult = DialogResult.Ignore };
        buttons.Controls.Add(open);
        buttons.Controls.Add(skip);

        layout.Controls.Add(message);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = open;
    }
}
