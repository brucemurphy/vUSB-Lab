using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace vUSB_LAB;

internal sealed class UsbTray : Forms.Form
{
    private readonly Bitmap _art;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolTip _tooltip = new();
    private readonly Forms.Timer _animation = new() { Interval = 15 };
    private readonly Stopwatch _clock = new();
    private readonly Icon _icon;
    private Bitmap? _surface;
    private Rectangle _gear;
    private Point _destination;
    private bool _busy;
    private string _state = "Unknown";

    public event EventHandler? ToggleRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public Point Anchor { get; private set; }

    public UsbTray()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        AccessibleName = "vUSB virtual USB drive";
        AccessibleDescription = "Click to move the disk between your VM and PC. Press S for settings, Escape to dismiss.";
        _art = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Hyper-V VHDX Toggle Manager", "vhdx-toggle-flash-drive.png"));
        using var small = new Bitmap(32, 32, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(_art, new Rectangle(10, 0, 12, 32), new Rectangle(280, 88, 464, 1378), GraphicsUnit.Pixel);
        }
        IntPtr iconHandle = small.GetHicon();
        try { _icon = (Icon)Icon.FromHandle(iconHandle).Clone(); }
        finally { DestroyIcon(iconHandle); }
        _menu.Items.Add("Show USB", null, (_, _) => ShowUsb());
        _menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "vUSB - click to show USB", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) { if (Visible) Hide(); else ShowUsb(); } };
        _tray.BalloonTipClicked += (_, _) => OpenSettings();
        _animation.Tick += (_, _) => Animate();
        Deactivate += (_, _) => { _animation.Stop(); Hide(); };
    }

    protected override Forms.CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x80000 | 0x80; return value; }
    }

    public void UpdateState(string state, string hint, bool busy)
    {
        _state = state;
        _busy = busy;
        _tray.Text = ("vUSB - " + hint)[..Math.Min(127, hint.Length + 7)];
        _tooltip.SetToolTip(this, hint + "\nS: settings | Esc: dismiss");
        if (Visible) { BuildSurface(); Present(255, _destination); }
    }

    public void Notify(string message, bool error = false)
    {
        _tray.ShowBalloonTip(6000, "vUSB LAB", message, error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    public void ShowUsb(bool useCursor = true)
    {
        if (useCursor || Anchor == Point.Empty) Anchor = Forms.Cursor.Position;
        Rectangle work = Forms.Screen.FromPoint(Anchor).WorkingArea;
        Location = new Point(work.Right - 250, work.Bottom - 460);
        float scale = GetDpiForWindow(Handle) / 96f;
        Size = new Size((int)(236 * scale), (int)(440 * scale));
        int x = Math.Clamp(Anchor.X - Width / 2, work.Left + 8, Math.Max(work.Left + 8, work.Right - Width - 8));
        int y = work.Bottom - Height - 10;
        if (Anchor.Y <= work.Top) y = work.Top + 10;
        else if (Anchor.X <= work.Left || Anchor.X >= work.Right)
            y = Math.Clamp(Anchor.Y - Height / 2, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - Height - 8));
        _destination = new Point(x, y);
        BuildSurface();
        Present(0, _destination);
        Show();
        SetForegroundWindow(Handle);
        _clock.Restart();
        _animation.Start();
    }

    private void Animate()
    {
        double progress = Math.Min(1, _clock.Elapsed.TotalMilliseconds / 220);
        double eased = 1 - Math.Pow(1 - progress, 3);
        Present((byte)(255 * eased), new Point(_destination.X, _destination.Y + (int)(24 * (1 - eased))));
        if (progress >= 1) _animation.Stop();
    }

    private void BuildSurface()
    {
        _surface?.Dispose();
        _surface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(_surface);
        g.ScaleTransform(Width / 236f, Height / 440f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_art, new Rectangle(22, 4, 142, 422), new Rectangle(280, 88, 464, 1378), GraphicsUnit.Pixel);
        Color color = _busy ? Color.Gold : _state switch { "VM" => Color.DeepSkyBlue, "Host" => Color.MediumSpringGreen, _ => Color.SlateGray };
        using var led = new SolidBrush(color);
        g.FillEllipse(led, 88, 132, 10, 10);
        using var background = new SolidBrush(Color.FromArgb(245, 36, 38, 43));
        g.FillEllipse(background, 172, 380, 44, 44);
        using var font = new Font("Segoe Fluent Icons", 21, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.WhiteSmoke);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("\uE713", font, brush, new RectangleF(172, 380, 44, 44), format);
        _gear = new Rectangle((int)(172 * Width / 236f), (int)(380 * Height / 440f), (int)(44 * Width / 236f), (int)(44 * Height / 440f));
    }

    private void OpenSettings()
    {
        if (!Visible || Anchor == Point.Empty) Anchor = Forms.Cursor.Position;
        _animation.Stop();
        Hide();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != Forms.MouseButtons.Left) return;
        if (_gear.Contains(e.Location)) OpenSettings();
        else if (!_busy) ToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _busy && !_gear.Contains(e.Location) ? Forms.Cursors.WaitCursor : Forms.Cursors.Hand;
    }

    protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData)
    {
        if (keyData == Forms.Keys.Escape) { Hide(); return true; }
        if (keyData == Forms.Keys.S) { OpenSettings(); return true; }
        if (keyData == Forms.Keys.Enter || keyData == Forms.Keys.Space)
        {
            if (!_busy) ToggleRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == 0x84 && _surface is not null) // Let transparent pixels pass through to the desktop.
        {
            long packed = m.LParam.ToInt64();
            Point p = PointToClient(new Point((short)packed, (short)(packed >> 16)));
            if (p.X < 0 || p.Y < 0 || p.X >= _surface.Width || p.Y >= _surface.Height || _surface.GetPixel(p.X, p.Y).A < 12)
            { m.Result = new IntPtr(-1); return; }
        }
        base.WndProc(ref m);
    }

    private void Present(byte opacity, Point position)
    {
        if (_surface is null) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = _surface.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = SelectObject(memory, bitmap);
        try
        {
            var source = new NativePoint();
            var destination = new NativePoint { X = position.X, Y = position.Y };
            var size = new NativeSize { Width = Width, Height = Height };
            var blend = new Blend { SourceConstantAlpha = opacity, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _tooltip.Dispose();
            _icon.Dispose();
            _surface?.Dispose();
            _art.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dc, ref NativePoint point, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
