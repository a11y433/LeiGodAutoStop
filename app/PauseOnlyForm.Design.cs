namespace LeiGodAutoStop;

public sealed partial class PauseOnlyForm
{
    private readonly Label _followStatus = new();
    private static readonly Color Canvas = Color.FromArgb(15, 21, 32);
    private static readonly Color Card = Color.FromArgb(24, 33, 48);
    private static readonly Color Ink = Color.FromArgb(233, 240, 250);
    private static readonly Color Muted = Color.FromArgb(154, 173, 197);
    private static readonly Color Accent = Color.FromArgb(115, 218, 200);

    private static Label Caption(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, AutoSize = false, Dock = DockStyle.Fill, ForeColor = color,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
    };

    private static TableLayoutPanel Stack(Color color, params int[] rows)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = color, ColumnCount = 1, RowCount = rows.Length, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in rows) panel.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        return panel;
    }

    private static void StyleButton(Button button, bool primary = false)
    {
        button.AutoSize = false;
        button.Size = new Size(196, 38);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(64, 83, 106);
        button.BackColor = primary ? Accent : Card;
        button.ForeColor = primary ? Canvas : Ink;
        button.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 2, 12, 6);
    }

    private void BuildDashboard()
    {
        SuspendLayout();
        Text = "雷神时长管家 · 自动暂停";
        ClientSize = new Size(980, 784);
        MinimumSize = new Size(940, 800);
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 10);
        var root = Stack(Canvas, 86, 268, 18, 282, 0);
        root.Padding = new Padding(28, 14, 28, 14);
        Controls.Add(root);

        var header = Stack(Canvas, 38, 32);
        header.Controls.Add(Caption("雷神时长管家", 22, Ink, true), 0, 0);
        header.Controls.Add(Caption("安心开局，结束后自动暂停时长。", 10, Muted), 0, 1);
        root.Controls.Add(header, 0, 0);

        var state = Stack(Card, 24, 55, 84, 47, 0);
        state.Padding = new Padding(24, 18, 24, 14);
        state.Controls.Add(Caption("●  运行状态", 10, Accent, true), 0, 0);
        _headline.AutoSize = false; _headline.Dock = DockStyle.Fill;
        _headline.Font = new Font("Microsoft YaHei UI", 21, FontStyle.Bold);
        _headline.ForeColor = Ink; _headline.TextAlign = ContentAlignment.MiddleLeft;
        _headline.Text = "等待雷神启动";
        state.Controls.Add(_headline, 0, 1);
        _details.AutoSize = false; _details.MaximumSize = Size.Empty; _details.Dock = DockStyle.Fill;
        _details.ForeColor = Muted; _details.Font = new Font("Microsoft YaHei UI", 10);
        _details.Text = "雷神打开后识别当前加速游戏。\n游戏仍在运行时保持计时；全部退出后等待 120 秒。";
        state.Controls.Add(_details, 0, 2);
        _result.AutoSize = false; _result.MaximumSize = Size.Empty; _result.Dock = DockStyle.Fill;
        _result.BackColor = Color.FromArgb(32, 45, 63); _result.ForeColor = Ink;
        _result.Padding = new Padding(12, 0, 8, 0); _result.TextAlign = ContentAlignment.MiddleLeft;
        _result.Text = "账户状态将在需要暂停时读取并确认";
        state.Controls.Add(_result, 0, 3);
        state.Controls.Add(Caption("识别游戏   →   等待 120 秒   →   暂停并确认账户", 9, Muted), 0, 4);
        root.Controls.Add(state, 0, 1);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var monitor = Stack(Card, 29, 31, 43, 43, 43, 43, 0);
        monitor.Padding = new Padding(20, 14, 12, 12); monitor.Margin = new Padding(0, 0, 9, 0);
        monitor.Controls.Add(Caption("监控与识别", 12, Ink, true), 0, 0);
        _enabled.ForeColor = Ink; _enabled.Font = new Font("Microsoft YaHei UI", 10);
        _enabled.Margin = Padding.Empty; _enabled.Text = "自动暂停账户时长";
        monitor.Controls.Add(_enabled, 0, 1);
        StyleButton(_pauseMonitor); StyleButton(_confirmMapping); StyleButton(_manageMappings);
        monitor.Controls.Add(_pauseMonitor, 0, 2);
        monitor.Controls.Add(_confirmMapping, 0, 3);
        monitor.Controls.Add(_manageMappings, 0, 4);
        StyleButton(_runDiagnostics);
        monitor.Controls.Add(_runDiagnostics, 0, 5);
        _diagnosticStatus.Dock = DockStyle.Fill; _diagnosticStatus.ForeColor = Muted;
        _diagnosticStatus.Font = new Font("Microsoft YaHei UI", 8.5f);
        _diagnosticStatus.Text = "自动检查安装包、雷神环境并开始监控";
        monitor.Controls.Add(_diagnosticStatus, 0, 6);
        options.Controls.Add(monitor, 0, 0);

        var follow = Stack(Card, 29, 31, 43, 0);
        follow.Padding = new Padding(20, 14, 12, 12); follow.Margin = new Padding(9, 0, 0, 0);
        follow.Controls.Add(Caption("跟随雷神", 12, Ink, true), 0, 0);
        _followStatus.Dock = DockStyle.Fill; _followStatus.ForeColor = Muted;
        _followStatus.Font = new Font("Microsoft YaHei UI", 9);
        follow.Controls.Add(_followStatus, 0, 1);
        StyleButton(_installFollow, true); StyleButton(_removeFollow);
        follow.Controls.Add(_installFollow, 0, 2);
        follow.Controls.Add(_removeFollow, 0, 3);
        options.Controls.Add(follow, 1, 0);
        root.Controls.Add(options, 0, 3);
        root.Controls.Add(Caption("启用跟随后：雷神完全退出，监控随之关闭  ·  缩到托盘时继续监控", 9, Muted), 0, 4);
        if (FollowLeiGod.PointsToCurrentExecutable) _runDiagnostics.Text = "开始监控";
        RefreshFollowUi();
        ResumeLayout(true);
    }

    private void RefreshFollowUi()
    {
        _followStatus.Text = !FollowLeiGod.Enabled
            ? "一键设置自动跟随 · 按提示授权即可"
            : FollowLeiGod.PointsToCurrentExecutable
                ? "已启用 · 随雷神打开，随完全退出关闭"
                : "任务仍指向旧目录 · 点下方更新以使用当前版本";
        _installFollow.Text = FollowLeiGod.Enabled ? "更新跟随启动设置" : "启用随雷神启动和退出";
        _removeFollow.Visible = FollowLeiGod.Enabled;
        _installFollow.Enabled = !_workflow;
        _removeFollow.Enabled = !_workflow;
    }
}
