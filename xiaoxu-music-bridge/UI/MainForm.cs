using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace xiaoxu_music_bridge.UI;

public class MainForm : Form
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Label _statusLabel;
    private readonly Label _songLabel;
    private readonly Button _exitButton;
    private readonly bool _networkError;

    public MainForm(bool networkError = false)
    {
        _networkError = networkError;

        // Form setup
        Text = "xiaoxu-music-bridge";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(320, 160);
        BackColor = Color.White;

        // Status label
        _statusLabel = new Label
        {
            Text = networkError ? "网络错误" : "运行中",
            Location = new Point(20, 20),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
            ForeColor = networkError ? Color.Crimson : Color.ForestGreen,
        };
        Controls.Add(_statusLabel);

        // Song info label
        _songLabel = new Label
        {
            Text = networkError
                ? "未检测到网络连接，请确认网络已连接后重启本程序"
                : "正在监听 http://127.0.0.1:17888",
            Location = new Point(20, 55),
            MaximumSize = new Size(280, 0),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9),
            ForeColor = networkError ? Color.FromArgb(180, 0, 0) : Color.Gray,
        };
        Controls.Add(_songLabel);

        // System tray icon (create before exit button handler references it)
        _notifyIcon = new NotifyIcon
        {
            Text = "xiaoxu-music-bridge",
            Visible = true,
            Icon = CreateTrayIcon(),
        };

        _notifyIcon.DoubleClick += (_, _) => ToggleWindowVisibility();
        _notifyIcon.ContextMenuStrip = CreateContextMenu();

        // Exit button
        _exitButton = new Button
        {
            Text = "退出",
            Location = new Point(230, 110),
            Size = new Size(60, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 240, 240),
            Cursor = Cursors.Hand,
        };
        _exitButton.FlatAppearance.BorderSize = 1;
        _exitButton.FlatAppearance.BorderColor = Color.LightGray;
        _exitButton.Click += (_, _) =>
        {
            _notifyIcon.Dispose();
            Application.Exit();
        };
        Controls.Add(_exitButton);

        // Close button → minimize to tray
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示窗口");
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _notifyIcon.Dispose();
            Application.Exit();
        };
        menu.Items.Add(exitItem);

        return menu;
    }

    private static Icon CreateTrayIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Draw a music note icon
        using var fillBrush = new SolidBrush(Color.FromArgb(70, 130, 180));
        using var pen = new Pen(Color.FromArgb(70, 130, 180), 2.5f);

        // Note head (filled ellipse)
        g.FillEllipse(fillBrush, 8, 18, 10, 8);

        // Stem
        g.DrawLine(pen, 18, 18, 18, 5);

        // Flag
        g.DrawBezier(pen, 18, 5, 18, 3, 26, 7, 24, 13);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private void ToggleWindowVisibility()
    {
        if (Visible)
        {
            Hide();
        }
        else
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
