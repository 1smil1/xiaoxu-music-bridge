// StatusForm — tiny WinForms window reachable from the tray icon. Shows the
// host version, PID, listener URL, QQ Music session connectivity, and an
// explicit Exit button. Closing the window (X) hides it back to the tray so
// a stray click doesn't kill the bridge; the user has to use Exit / 退出
// (button or tray menu) to actually terminate the process.
//
// Created in v3.6.1 alongside the system-tray support; lives in
// xiaoxu-music-extension/host/UI/ rather than the legacy
// xiaoxu-music-bridge/UI/ (WinForms) project so the single-file publish
// keeps everything in one exe.

using System.Drawing;
using System.Windows.Forms;
using xiaoxu_music_bridge.Bridge;
using xiaoxu_music_bridge.Common;
using xiaoxu_music_bridge.Media;

namespace xiaoxu_music_bridge.UI;

public sealed class StatusForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _versionLabel;
    private readonly Label _pidLabel;
    private readonly Label _portLabel;
    private readonly Label _statusLabel;
    private readonly Button _refreshButton;
    private readonly Button _exitButton;
    private readonly Func<Task<MediaStatus>> _statusProbe;

    public StatusForm(
        int pid,
        DateTimeOffset startedAt,
        BridgeEndpointSettings endpoint,
        Func<Task<MediaStatus>> statusProbe)
    {
        _statusProbe = statusProbe;

        Text = "xiaoxu-music-bridge";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 210);
        BackColor = Color.White;

        _titleLabel = new Label
        {
            Text = "xiaoxu-music-bridge",
            Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold),
            Location = new Point(16, 14),
            AutoSize = true,
            ForeColor = Color.FromArgb(40, 40, 40),
        };
        _versionLabel = new Label
        {
            Text = $"版本: {BridgeHttpServer.HostVersion}",
            Location = new Point(16, 46),
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80),
        };
        _pidLabel = new Label
        {
            Text = $"PID: {pid}    已运行: {FormatUptime(DateTimeOffset.Now - startedAt)}",
            Location = new Point(16, 70),
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80),
        };
        _portLabel = new Label
        {
            Text = $"监听: {endpoint.ListenerPrefix}",
            Location = new Point(16, 94),
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 80, 80),
        };
        _statusLabel = new Label
        {
            Text = "QQ 音乐: 检测中…",
            Location = new Point(16, 120),
            AutoSize = true,
            ForeColor = Color.FromArgb(40, 40, 40),
        };

        _refreshButton = new Button
        {
            Text = "刷新",
            Location = new Point(16, 158),
            Size = new Size(75, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(245, 245, 245),
            Cursor = Cursors.Hand,
        };
        _refreshButton.FlatAppearance.BorderColor = Color.LightGray;
        _refreshButton.Click += async (_, _) => await RefreshStatusAsync();

        _exitButton = new Button
        {
            Text = "退出",
            Location = new Point(265, 158),
            Size = new Size(80, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(245, 245, 245),
            Cursor = Cursors.Hand,
        };
        _exitButton.FlatAppearance.BorderColor = Color.LightGray;
        _exitButton.Click += (_, _) => Application.Exit();

        Controls.Add(_titleLabel);
        Controls.Add(_versionLabel);
        Controls.Add(_pidLabel);
        Controls.Add(_portLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_refreshButton);
        Controls.Add(_exitButton);

        // Hide-on-close so clicking the X doesn't accidentally tear down the
        // bridge — the user has to explicitly click 退出 (or use the tray
        // context menu's 退出 entry) to actually exit the process.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        Shown += async (_, _) => await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            // Bound the probe so a wedged GSMTC can't freeze the UI thread.
            var probeTask = _statusProbe();
            var winner = await Task.WhenAny(probeTask, Task.Delay(1500));
            if (winner != probeTask)
            {
                _statusLabel.Text = "QQ 音乐: 探测超时";
                _statusLabel.ForeColor = Color.FromArgb(180, 80, 0);
                return;
            }
            var status = await probeTask;
            if (status is null || !status.Connected || string.IsNullOrWhiteSpace(status.Title))
            {
                _statusLabel.Text = "QQ 音乐: 未连接";
                _statusLabel.ForeColor = Color.FromArgb(160, 160, 160);
            }
            else
            {
                var suffix = status.IsPlaying ? "▶ 播放中" : "� 已暂停";
                _statusLabel.Text = $"QQ 音乐: {status.Title} — {status.Artist ?? "未知"}   {suffix}";
                _statusLabel.ForeColor = Color.FromArgb(0, 110, 60);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"QQ 音乐: 错误 ({ex.GetType().Name})";
            _statusLabel.ForeColor = Color.Crimson;
        }
    }

    private static string FormatUptime(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60) return $"{(int)elapsed.TotalSeconds} 秒";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} 分 {(int)elapsed.Seconds} 秒";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} 小时 {(int)elapsed.Minutes} 分";
        return $"{(int)elapsed.TotalDays} 天 {(int)elapsed.Hours} 小时";
    }
}
