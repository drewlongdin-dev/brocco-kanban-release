using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BroccoKanban;

/// <summary>
/// Static helpers for color math and geometry used throughout the UI.
/// </summary>
public static class Ui
{
    /// <summary>Parses a CSS hex color string (e.g. "#2D6A4F") into a GDI+ <see cref="Color"/>.</summary>
    public static Color ColorFrom(string hex) => ColorTranslator.FromHtml(hex);

    public static SegmentedButton MakeSegmentedButton(bool vertical = false, params ModernButton[] buttons)
    => new(buttons, vertical);

    /// <summary>
    /// Walks up the control parent chain to find the first ancestor with a non-transparent
    /// background color and returns it. Falls back to <see cref="SystemColors.Control"/> if
    /// the entire chain is transparent.
    /// </summary>
    /// <remarks>
    /// Needed because WinForms <see cref="Color.Transparent"/> is not a real color — it means
    /// "inherit from parent". Controls that do their own painting must resolve this themselves
    /// before filling the background, otherwise they paint black or leave artifacts.
    /// </remarks>
    public static Color ResolvedBackColor(Control? control)
    {
        while (control is not null)
        {
            if (control.BackColor != Color.Transparent && control.BackColor != Color.Empty)
            {
                return control.BackColor;
            }
            control = control.Parent;
        }

        return SystemColors.Control;
    }

    /// <summary>
    /// Clips a form's Region to the union of the RoundedPanel body and shadow paths,
    /// matching the exact geometry painted by RoundedPanel.OnPaint so no background bleeds through.
    /// </summary>
    /// <remarks>
    /// The default offsets mirror the hardcoded values in RoundedPanel: body is inset by 7/8 px,
    /// shadow is offset by 5/6 px and inset by 8/9 px.
    /// </remarks>
    public static void SetRoundedRegion(Form form, int radius = 20, bool hasShadow = true)
    {
        int bx = form.DpiScaled(hasShadow ? 7 : 1);
        int by = form.DpiScaled(hasShadow ? 8 : 1);
        using var bodyPath = RoundedRect(
            new Rectangle(0, 0, form.Width - bx, form.Height - by),
            radius);

        if (!hasShadow)
        {
            form.Region = new Region(bodyPath);
            return;
        }

        int sx = form.DpiScaled(5), sy = form.DpiScaled(6);
        int sw = form.DpiScaled(8), sh = form.DpiScaled(9);
        using var shadowPath = RoundedRect(
            new Rectangle(sx, sy, form.Width - sw, form.Height - sh),
            radius);

        var region = new Region(bodyPath);
        region.Union(shadowPath);
        form.Region = region;
    }

    /// <summary>
    /// Linearly interpolates between two colors. <paramref name="amount"/> = 0 returns
    /// <paramref name="a"/>; = 1 returns <paramref name="b"/>; values outside [0, 1] are clamped.
    /// </summary>
    public static Color Blend(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(a.R + ((b.R - a.R) * amount)),
            (int)(a.G + ((b.G - a.G) * amount)),
            (int)(a.B + ((b.B - a.B) * amount)));
    }

    /// <summary>Darkens a color by blending it toward pure black by <paramref name="amount"/> (0–1).</summary>
    public static Color Darken(Color color, float amount) => Blend(color, Color.Black, amount);

    /// <summary>
    /// Creates a <see cref="ModernButton"/> styled with the current palette.
    /// <paramref name="quiet"/> = true produces a muted secondary button;
    /// false (default) produces a filled primary/accent button.
    /// </summary>
    public static ModernButton MakeButton(string text, EventHandler onClick, BoardPalette palette,
        int width = 118, bool quiet = false)
    {
        var button = new ModernButton
        {
            Text = text,
            Width = Scaled(width),
            Height = Scaled(width <= 72 ? 34 : 40),
            Margin = new Padding(0, 0, Scaled(8), 0),
            BackColor = quiet ? ColorFrom(palette.Surface) : ColorFrom(palette.Accent),
            ForeColor = quiet ? ColorFrom(palette.Text) : ColorFrom(palette.AccentText),
            BorderColor = quiet ? ColorFrom(palette.Border) : Color.Transparent
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>Creates a danger-styled (red) button using <see cref="MakeButton"/> as the base.</summary>
    public static ModernButton MakeDangerButton(string text, EventHandler onClick, BoardPalette palette,
        int width = 118)
    {
        var button = MakeButton(text, onClick, palette, width);
        button.BackColor = ColorFrom(palette.Danger);
        button.ForeColor = ColorFrom(palette.AccentText);
        return button;
    }
    /// <summary>
    /// Creates a square icon-only button. <paramref name="icon"/> should be one of the
    /// <see cref="Icons"/> constants. Tooltip text is set via <paramref name="tooltip"/>
    /// so screen readers and mouse hover still provide a label.
    /// </summary>
    public static ModernButton MakeIconButton(string icon, string tooltip, EventHandler onClick,
        BoardPalette palette, int size = 40, float iconSize = 16f,
        Icons.FontType fontType = Icons.FontType.Bold, bool quiet = false, bool danger = false)
    {
        var button = new ModernButton
        {

            Text = icon,
            Font = Icons.Get(iconSize, fontType),
            Width = Scaled(size),
            Height = Scaled(size),
            Margin = new Padding(0, 0, Scaled(8), 0),
            BackColor = danger ? ColorFrom(palette.Danger)
                      : quiet ? ColorFrom(palette.Surface)
                               : ColorFrom(palette.Accent),
            ForeColor = (danger || !quiet) ? ColorFrom(palette.AccentText) : ColorFrom(palette.Text),
            BorderColor = quiet ? ColorFrom(palette.Border) : Color.Transparent,
            AccessibleName = tooltip

        };
        button.Click += onClick;
        new ToolTip().SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>Creates a small × button that closes its parent dialog when clicked.</summary>
    public static ModernButton MakeCloseButton(BoardPalette palette, Point location)
    {
        return new ModernButton
        {
            Text = Icons.X,
            Font = Icons.Get(8, Icons.FontType.Bold),
            Size = new Size(Scaled(32), Scaled(32)),
            Radius = Scaled(8),
            BackColor = ColorFrom(palette.SurfaceAlt),
            ForeColor = ColorFrom(palette.Muted),
            BorderColor = ColorFrom(palette.Border),
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = location
        };
    }

    /// <summary>
    /// Builds a <see cref="GraphicsPath"/> describing a rounded rectangle, which can then be
    /// used with <c>Graphics.FillPath</c> / <c>DrawPath</c> for anti-aliased rounded shapes.
    /// Each corner is a 90-degree arc; the four arcs are connected in clockwise order.
    /// </summary>
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Returns the DPI scale factor of this control relative to the 96-DPI logical baseline.</summary>
    public static float DpiScale(this Control c) => c.DeviceDpi / 96f;

    /// <summary>Scales <paramref name="value"/> from 96-DPI logical pixels to this control's device pixels.</summary>
    public static int DpiScaled(this Control c, int value) => (int)(value * c.DeviceDpi / 96f);

    // -------------------------------------------------------------------------
    // Static DPI store — set once in the Form.Load handler via InitDpi().
    // Factory methods and control constructors that run before a window handle
    // exists (so DeviceDpi == 96) use Scaled() instead of DpiScaled().
    // -------------------------------------------------------------------------

    /// <summary>Stored DPI scale factor, initialised once from the main form's real DeviceDpi.</summary>
    public static float StoredDpiScale { get; private set; } = 1f;

    /// <summary>
    /// Captures the true DPI scale from a live control. Call this exactly once
    /// at the start of the Form.Load handler, before any factory methods run.
    /// </summary>
    public static void InitDpi(Control source) => StoredDpiScale = source.DeviceDpi / 96f;

    /// <summary>
    /// Scales a 96-DPI logical pixel value to device pixels using <see cref="StoredDpiScale"/>.
    /// Use this inside factory/builder methods and control constructors where
    /// <c>DeviceDpi</c> is not yet available.
    /// </summary>
    public static int Scaled(int value) => (int)(value * StoredDpiScale);
}

/// <summary>
/// A panel with rounded corners, an optional drop shadow, and an optional colored band
/// across the top. Used as the visual shell for board cards, column headers, and dialogs.
/// </summary>
/// <remarks>
/// All painting is custom: WinForms' default panel rendering cannot do rounded corners or
/// shadows, so we take full control via <c>SetStyle</c> + <c>OnPaint</c> overrides.
/// </remarks>
public class RoundedPanel : Panel
{
    /// <summary>Corner radius in pixels.</summary>
    public int Radius { get; set; } = Ui.Scaled(14);

    /// <summary>Border stroke color. Set to <see cref="Color.Transparent"/> to suppress the border.</summary>
    public Color BorderColor { get; set; } = Color.Gainsboro;

    /// <summary>Border stroke width in pixels. Set to 0 to suppress the border.</summary>
    public int BorderWidth { get; set; } = 1;

    /// <summary>When true, a blurred-style shadow rectangle is drawn below the panel.</summary>
    public bool Shadow { get; set; }

    /// <summary>Color of the drop shadow (use a semi-transparent dark tone for realism).</summary>
    public Color ShadowColor { get; set; } = Color.FromArgb(35, 0, 0, 0);

    /// <summary>Fill color of the accent band drawn at the top of the panel. Ignored when <see cref="Color.Transparent"/>.</summary>
    public Color TopBandColor { get; set; } = Color.Transparent;

    /// <summary>Height of the top accent band in pixels. Set to 0 to disable.</summary>
    public int TopBandHeight { get; set; } = 0;

    public RoundedPanel()
    {
        // AllPaintingInWmPaint: tells Windows not to send a separate WM_ERASEBKGND message
        //   before WM_PAINT, which would clear the background and cause flicker.
        // OptimizedDoubleBuffer: WinForms renders to an off-screen bitmap and blits it in one
        //   step, preventing half-drawn frames from showing on screen.
        // ResizeRedraw: automatically invalidates the control when it's resized so the
        //   rounded shape repaints at the new size.
        // UserPaint: opts this control into fully custom painting via OnPaint/OnPaintBackground.
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        ResizeRedraw = true;
        BackColor = Color.White;
    }

    /// <summary>
    /// Fills the entire client area with the parent's resolved background color before
    /// OnPaint clips and fills the rounded shape. This erases any leftover pixels from
    /// the previous paint that fell outside the rounded region.
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Ui.ResolvedBackColor(Parent));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = e.Graphics.DpiX / 96f;
        int s5 = (int)(5 * scale), s6 = (int)(6 * scale);
        int s7 = (int)(7 * scale), s8 = (int)(8 * scale), s9 = (int)(9 * scale);

        if (Shadow && Width > s8 && Height > s8)
        {
            // The shadow is a slightly offset, slightly smaller copy of the main shape.
            // Real blur would require more complex rendering; this approximation is fast.
            var shadowRect = new Rectangle(s5, s6, Width - s8, Height - s9);
            using var shadowPath = Ui.RoundedRect(shadowRect, Radius);
            using var shadowBrush = new SolidBrush(ShadowColor);
            e.Graphics.FillPath(shadowBrush, shadowPath);
        }

        // When shadow is on, the body is inset so it doesn't overlap the shadow rectangle.
        var body = Shadow ? new Rectangle(0, 0, Width - s7, Height - s8) : new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Ui.RoundedRect(body, Radius);
        using var brush = new SolidBrush(BackColor);
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.FillPath(brush, path);
        if (TopBandHeight > 0 && TopBandColor != Color.Transparent)
        {
            var savedMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.None;
            // Clip the band fill to the rounded body so its corners don't poke outside.
            e.Graphics.SetClip(path);
            using var bandBrush = new SolidBrush(TopBandColor);
            e.Graphics.FillRectangle(bandBrush, new Rectangle(body.X, body.Y, body.Width + 1, TopBandHeight));
            e.Graphics.ResetClip();
            e.Graphics.SmoothingMode = savedMode;
        }
        if (BorderWidth > 0) e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>
/// A custom-painted drag-drop insertion indicator. Renders a small filled circle
/// on the left joined to a thin horizontal line — matching the column accent color.
/// </summary>
public sealed class DropIndicatorControl : Control
{
    public DropIndicatorControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Height = Ui.Scaled(10); // taller than the old 4px to fit the circle
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Ui.ResolvedBackColor(Parent));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        float scale = e.Graphics.DpiX / 96f;
        int r = (int)(4 * scale);
        int thick = Math.Max(2, (int)(2 * scale));
        int cy = Height / 2;

        var glow = Color.FromArgb(60, ForeColor);

        using var glowBrush = new SolidBrush(glow);
        using var glowPen = new Pen(glow, thick + 4);
        using var brush = new SolidBrush(ForeColor);
        using var pen = new Pen(ForeColor, thick);

        int lineX = r * 2;
        int lineY = cy - (thick / 2);

        // Glow tail
        using var glowGrad = new LinearGradientBrush(
            new Point(lineX, cy), new Point(Width, cy),
            glow, Color.Transparent);
        e.Graphics.FillRectangle(glowGrad, lineX, lineY - 2, Width - lineX, thick + 4);
        e.Graphics.FillEllipse(glowBrush, -2, cy - r - 2, (r + 2) * 2, (r + 2) * 2);

        // Solid tail
        using var lineGrad = new LinearGradientBrush(
            new Point(lineX, cy), new Point(Width, cy),
            ForeColor, Color.Transparent);
        e.Graphics.FillRectangle(lineGrad, lineX, lineY, Width - lineX, thick);
        e.Graphics.FillEllipse(brush, 0, cy - r, r * 2, r * 2);
    }
}

public static class PaletteTransition
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_SETREDRAW = 0x000B;
    private const uint PW_CLIENTONLY = 0x1;
    private const uint PW_RENDERFULLCONTENT = 0x2;

    private class OverlayForm : Form
    {
        private readonly Bitmap _before;
        private Bitmap _after;
        public float Alpha = 1f;

        public OverlayForm(Bitmap before, Bitmap after)
        {
            //this.AutoScaleMode = AutoScaleMode.None;
            _before = before;
            _after = after;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x8000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x20;      // WS_EX_TRANSPARENT — clicks pass through
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public void SetAfter(Bitmap after)
        {
            _after = after;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var dest = new Rectangle(0, 0, Width, Height);
            e.Graphics.DrawImage(_after, dest, 0, 0, _after.Width, _after.Height, GraphicsUnit.Pixel);
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = Alpha });
            e.Graphics.DrawImage(_before, dest, 0, 0, _before.Width, _before.Height, GraphicsUnit.Pixel, attrs);
        }
    }

    public static void Run(Form form, Action applyAndRebuild, int durationMs = 250)
    {
        var size = form.ClientSize;
        var clientOrigin = form.PointToScreen(Point.Empty);

        // Capture before
        var before = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(before))
            g.CopyFromScreen(clientOrigin, Point.Empty, size);

        // Show overlay immediately — covers the form before anything changes
        var overlay = new OverlayForm(before, before) // after=before for now, fully opaque
        {
            Location = clientOrigin,
            Size = size
        };
        overlay.Show(form);
        overlay.Update(); // synchronous paint — overlay is fully visible before we continue

        // Rebuild happens completely hidden underneath
        applyAndRebuild();

        // Capture after state silently via PrintWindow — never touches the screen
        var after = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(after))
        {
            var hdc = g.GetHdc();
            PrintWindow(form.Handle, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }

        overlay.SetAfter(after);

        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        timer.Tick += (_, _) =>
        {
            overlay.Alpha -= 16f / durationMs;
            if (overlay.Alpha <= 0f)
            {
                timer.Stop();
                timer.Dispose();
                overlay.Close();
                overlay.Dispose();
                before.Dispose();
                after.Dispose();
            }
            else
            {
                overlay.Invalidate();
            }
        };
        timer.Start();
    }
}

/// <summary>
/// A plain <see cref="Panel"/> with double-buffering enabled so it doesn't flicker on resize.
/// WinForms panels flicker by default because they erase their background on every resize
/// before repainting; the style flags below suppress that erase step.
/// </summary>
public class SmoothPanel : Panel
{
    public SmoothPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }
}

/// <summary>
/// A <see cref="FlowLayoutPanel"/> with the same double-buffering treatment as <see cref="SmoothPanel"/>.
/// Used for horizontally or vertically flowing control groups that need flicker-free repaints.
/// </summary>
public class SmoothFlowLayoutPanel : FlowLayoutPanel
{
    public SmoothFlowLayoutPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }
}

/// <summary>
/// A button with rounded corners and a subtle lift-and-press animation.
/// The animation is driven by a 15 ms <see cref="System.Windows.Forms.Timer"/> that
/// applies exponential easing each tick until the value settles.
/// </summary>
public sealed class ModernButton : Button
{
    /// <summary>Corner radius in pixels.</summary>
    public int Radius { get; set; } = Ui.Scaled(10);

    /// <summary>Explicit border color. Defaults to <see cref="Color.Transparent"/> (auto-derived from BackColor).</summary>
    public Color BorderColor { get; set; } = Color.Transparent;
    /// <summary>Color drawn behind the button when it is displayed.</summary>
    public Color? CanvasColor { get; set; }

    private readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 15 };

    // animation is the current rendered state [0 = idle, 1 = hovered, >1 = pressed].
    // targetAnimation is where it should settle. Each tick we move animation 35% of the
    // remaining distance (exponential ease-out), then stop when close enough.
    private float animation;
    private float targetAnimation;
    private bool pressed;

    public ModernButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Height = Ui.Scaled(40);
        Width = Ui.Scaled(120);
        animationTimer.Tick += (_, _) =>
        {
            // Exponential ease-out: close 35% of the gap each frame.
            animation += (targetAnimation - animation) * 0.35f;
            if (Math.Abs(targetAnimation - animation) < 0.01f)
            {
                animation = targetAnimation;
                animationTimer.Stop(); // Stop the timer when settled to avoid needless CPU use.
            }
            Invalidate(); // Request a repaint so the new animation frame is drawn.
        };
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        targetAnimation = 1f;
        animationTimer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        pressed = false;
        targetAnimation = 0f;
        animationTimer.Start();
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        pressed = true;
        targetAnimation = 1.6f; // Exceeds 1 to produce a "push down" effect beyond the hover state.
        animationTimer.Start();
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        pressed = false;
        targetAnimation = ClientRectangle.Contains(mevent.Location) ? 1f : 0f;
        animationTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(CanvasColor ?? Ui.ResolvedBackColor(Parent));

        Color depth = Ui.Darken(BackColor, 0.34f);
        Color animatedBack = pressed
            ? Ui.Blend(BackColor, Color.Black, 0.12f)
            : Ui.Blend(BackColor, Color.White, animation * 0.08f);

        // The "depth" rectangle is a darker shape drawn behind the main face, giving a
        // physical button / raised surface appearance. When pressed, the face drops down
        // to meet the depth rectangle, making it look pushed in.
        int faceBottom = (int)(8 * pevent.Graphics.DpiX / 96f);
        int maxDepth = Math.Clamp(Height / 7, Ui.Scaled(3), Ui.Scaled(7));
        int normalDepth = Math.Max(Ui.Scaled(2), maxDepth - Ui.Scaled(2));
        int hoverDepth = maxDepth;
        int pressedDepth = Math.Max(Ui.Scaled(1), maxDepth / 3);
        int lift = pressed ? hoverDepth - pressedDepth : (animation > 0.1f ? 0 : hoverDepth - normalDepth);
        int depthOffset = pressed ? pressedDepth : (animation > 0.1f ? hoverDepth : normalDepth);
        var shadowRect = new Rectangle(0, depthOffset, Width - 1, Height - depthOffset - 1);
        using var shadowPath = Ui.RoundedRect(shadowRect, Radius);
        using var shadowBrush = new SolidBrush(depth);
        pevent.Graphics.FillPath(shadowBrush, shadowPath);

        var rect = new Rectangle(0, lift, Width - 1, Height - faceBottom);
        using var path = Ui.RoundedRect(rect, Radius);
        using var brush = new SolidBrush(animatedBack);
        using var pen = new Pen(
            BorderColor == Color.Transparent
                ? Ui.Blend(BackColor, Color.White, 0.25f)
                : BorderColor,
            1);
        pevent.Graphics.FillPath(brush, path);
        pevent.Graphics.DrawPath(pen, path);

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        var textRect = new RectangleF(0, lift, Width, Height - faceBottom);
        using var foreBrush = new SolidBrush(ForeColor);
        pevent.Graphics.DrawString(Text, Font, foreBrush, textRect, sf);
    }
}

public sealed class AddTaskButton : RoundedPanel
{
    private readonly BoardPalette _palette;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private float _anim;
    private float _target;

    public AddTaskButton(BoardPalette palette)
    {
        _palette = palette;
        Radius = Ui.Scaled(15);
        Height = Ui.Scaled(56);
        Margin = new Padding(0, 0, 0, Ui.Scaled(12));
        Shadow = false;
        BorderColor = Ui.ColorFrom(palette.Border);
        BackColor = Ui.ColorFrom(palette.SurfaceAlt);
        Cursor = Cursors.Hand;

        _timer.Tick += (_, _) =>
        {
            _anim += (_target - _anim) * 0.35f;
            if (Math.Abs(_target - _anim) < 0.01f)
            {
                _anim = _target;
                _timer.Stop();
            }
            Invalidate();
        };
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _target = 1f;
        _timer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _target = 0f;
        _timer.Start();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Ui.ResolvedBackColor(Parent));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var body = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Ui.RoundedRect(body, Radius);

        Color fill = Ui.Blend(Ui.ColorFrom(_palette.SurfaceAlt), Ui.ColorFrom(_palette.Border), _anim);
        Color border = Ui.Blend(Ui.ColorFrom(_palette.Border), Ui.ColorFrom(_palette.Accent), _anim);
        Color icon = Ui.Blend(Ui.ColorFrom(_palette.Border), Ui.ColorFrom(_palette.Accent), _anim);

        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(border, 1);
        e.Graphics.FillPath(fillBrush, path);
        e.Graphics.DrawPath(borderPen, path);

        using var iconFont = Icons.Get(16f, Icons.FontType.Bold);
        using var iconBrush = new SolidBrush(icon);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        e.Graphics.DrawString(Icons.Plus, iconFont, iconBrush, new RectangleF(0, 0, Width, Height), sf);
    }
}

/// <summary>
/// A single rounded-pill control composed from multiple <see cref="ModernButton"/> instances.
/// Each segment retains its source button's width, colors, icon, and click handler.
/// Construct via <see cref="Ui.MakeSegmentedButton"/>.
/// </summary>
public sealed class SegmentedButton : Control
{
    private sealed class Segment
    {
        public readonly ModernButton Source;
        public readonly int X;
        public readonly int Y;
        public float Animation;
        public float TargetAnimation;
        public bool Pressed;

        public Segment(ModernButton source, int x, int y) { Source = source; X = x; Y = y; }
    }

    private readonly List<Segment> _segments = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 2000, InitialDelay = 500, ReshowDelay = 200 };
    private int _hoveredIndex = -1;
    private int _lastTooltipIndex = -1;

    public bool Vertical { get; }
    public int Radius { get; set; } = Ui.Scaled(10);

    public SegmentedButton(IEnumerable<ModernButton> buttons, bool vertical = false)
    {
        Vertical = vertical;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;

        if (!vertical)
        {
            int x = 0; int maxH = 0;
            foreach (var btn in buttons)
            {
                _segments.Add(new Segment(btn, x, 0));
                x += btn.Width;
                maxH = Math.Max(maxH, btn.Height);
            }
            Width = x; Height = maxH;
        }
        else
        {
            int y = 0; int maxW = 0;
            foreach (var btn in buttons)
            {
                _segments.Add(new Segment(btn, 0, y));
                y += btn.Height - Ui.Scaled(8); // step by face height only, not full height
                maxW = Math.Max(maxW, btn.Width);
            }
            Width = maxW;
            Height = y + Ui.Scaled(8); // add shadow space back once for the last segment
        }

        _timer.Tick += (_, _) =>
        {
            bool moving = false;
            foreach (var seg in _segments)
            {
                seg.Animation += (seg.TargetAnimation - seg.Animation) * 0.35f;
                if (Math.Abs(seg.TargetAnimation - seg.Animation) >= 0.01f) moving = true;
                else seg.Animation = seg.TargetAnimation;
            }
            if (!moving) _timer.Stop();
            Invalidate();
        };
    }

    private int HitTest(int x, int y)
    {
        if (!Vertical)
        {
            for (int i = 0; i < _segments.Count; i++)
                if (x >= _segments[i].X && x < _segments[i].X + _segments[i].Source.Width)
                    return i;
        }
        else
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                int segBottom = i < _segments.Count - 1 ? _segments[i + 1].Y : Height;
                if (y >= _segments[i].Y && y < segBottom)
                    return i;
            }
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int idx = HitTest(e.X, e.Y);
        if (idx != _hoveredIndex)
        {
            for (int i = 0; i < _segments.Count; i++)
                if (!_segments[i].Pressed)
                    _segments[i].TargetAnimation = i == idx ? 1f : 0f;
            _hoveredIndex = idx;
            _timer.Start();
        }
        if (idx != _lastTooltipIndex)
        {
            _lastTooltipIndex = idx;
            string? tip = idx >= 0 ? _segments[idx].Source.AccessibleName : null;
            if (!string.IsNullOrEmpty(tip))
                _toolTip.Show(tip, this,
                    Vertical ? Width + 2 : e.X,
                    Vertical ? e.Y : Height + 2, 2000);
            else
                _toolTip.Hide(this);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        foreach (var seg in _segments) { seg.TargetAnimation = 0f; seg.Pressed = false; }
        _hoveredIndex = -1;
        _lastTooltipIndex = -1;
        _toolTip.Hide(this);
        _timer.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int idx = HitTest(e.X, e.Y);
        if (idx < 0) return;
        _segments[idx].Pressed = true;
        _segments[idx].TargetAnimation = 1.6f;
        _timer.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int clickedIdx = HitTest(e.X, e.Y);
        for (int i = 0; i < _segments.Count; i++)
        {
            _segments[i].Pressed = false;
            _segments[i].TargetAnimation = i == _hoveredIndex ? 1f : 0f;
        }
        _timer.Start();
        if (clickedIdx >= 0) _segments[clickedIdx].Source.PerformClick();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Ui.ResolvedBackColor(Parent));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    /// <summary>
    /// Rounded rect with individually controlled corners.
    /// Horizontal: first segment rounds left corners (tl, bl), last rounds right (tr, br).
    /// Vertical:   first segment rounds top corners (tl, tr), last rounds bottom (bl, br).
    /// </summary>
    private static GraphicsPath PartialRoundedRect(Rectangle b, int r, bool tl, bool tr, bool br, bool bl)
    {
        int d = r * 2;
        var path = new GraphicsPath();
        if (tl) path.AddArc(b.Left, b.Top, d, d, 180, 90);
        else path.AddLine(b.Left, b.Top, b.Left, b.Top);
        if (tr) path.AddArc(b.Right - d, b.Top, d, d, 270, 90);
        else path.AddLine(b.Right, b.Top, b.Right, b.Top);
        if (br) path.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        else path.AddLine(b.Right, b.Bottom, b.Right, b.Bottom);
        if (bl) path.AddArc(b.Left, b.Bottom - d, d, d, 90, 90);
        else path.AddLine(b.Left, b.Bottom, b.Left, b.Bottom);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_segments.Count == 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float _scale = g.DpiX / 96f;
        int faceBottom = (int)(8 * _scale);
        int n = _segments.Count;

        // Depth values are computed against the thinner dimension so they scale consistently
        // regardless of orientation — same logic ModernButton uses against Height.
        int thickness = Vertical ? Width : Height;
        int maxDepth = Math.Clamp(thickness / 7, Ui.Scaled(3), Ui.Scaled(7));
        int normalDepth = Math.Max(Ui.Scaled(2), maxDepth - Ui.Scaled(2));
        int hoverDepth = maxDepth;
        int pressedDepth = Math.Max(Ui.Scaled(1), maxDepth / Ui.Scaled(3));

        int[] lifts = new int[n];
        int[] depthOffsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            var seg = _segments[i];
            lifts[i] = seg.Pressed ? hoverDepth - pressedDepth
                : (seg.Animation > 0.1f ? 0 : hoverDepth - normalDepth);
            depthOffsets[i] = seg.Pressed ? pressedDepth
                : (seg.Animation > 0.1f ? hoverDepth : normalDepth);
        }

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        if (!Vertical)
        {
            // Horizontal: shadow below, face lifts upward.
            // Corner rounding: first = left side (tl, bl), last = right side (tr, br).
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < n; i++)
                {
                    bool first = i == 0, last = i == n - 1;
                    int segW = last ? _segments[i].Source.Width - 1 : _segments[i].Source.Width;

                    if (pass == 0) // Shadow
                    {
                        var r = new Rectangle(_segments[i].X, depthOffsets[i], segW, Height - depthOffsets[i] - 1);
                        using var path = PartialRoundedRect(r, Radius, first, last, last, first);
                        using var brush = new SolidBrush(Ui.Darken(_segments[i].Source.BackColor, 0.34f));
                        g.FillPath(brush, path);
                    }
                    else if (pass == 1) // Face + border
                    {
                        Color animated = _segments[i].Pressed
                            ? Ui.Blend(_segments[i].Source.BackColor, Color.Black, 0.12f)
                            : Ui.Blend(_segments[i].Source.BackColor, Color.White, _segments[i].Animation * 0.08f);
                        var r = new Rectangle(_segments[i].X, lifts[i], segW, Height - faceBottom);
                        using var path = PartialRoundedRect(r, Radius, first, last, last, first);
                        using var faceBrush = new SolidBrush(animated);
                        g.FillPath(faceBrush, path);
                        var bc = _segments[i].Source.BorderColor == Color.Transparent
                            ? Ui.Blend(_segments[i].Source.BackColor, Color.White, 0.25f)
                            : _segments[i].Source.BorderColor;
                        using var pen = new Pen(bc, 1);
                        g.DrawPath(pen, path);
                    }
                    else // Icons
                    {
                        var r = new Rectangle(_segments[i].X, lifts[i], segW, Height - faceBottom);
                        using var path = PartialRoundedRect(r, Radius, first, last, last, first);
                        g.SetClip(path);
                        using var brush = new SolidBrush(_segments[i].Source.ForeColor);
                        g.DrawString(_segments[i].Source.Text, _segments[i].Source.Font, brush,
                            new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        g.ResetClip();
                    }
                }
        }
        else
        {
            // Vertical: same shadow-below / face-lifts-up geometry as horizontal.
            // Corner rounding: first segment = top corners (tl, tr), last = bottom corners (bl, br).
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < n; i++)
                {
                    bool first = i == 0, last = i == n - 1;
                    int segH = last ? _segments[i].Source.Height - 1 : _segments[i].Source.Height;

                    if (pass == 0) // Shadow
                    {
                        if (!last && !_segments[i].Pressed && _segments[i].Animation <= 0.1f) continue;
                        var r = new Rectangle(_segments[i].X, _segments[i].Y + depthOffsets[i], Width - 1, segH - depthOffsets[i] - 1);
                        using var path = PartialRoundedRect(r, Radius, first, first, last, last);
                        using var brush = new SolidBrush(Ui.Darken(_segments[i].Source.BackColor, 0.34f));
                        g.FillPath(brush, path);
                    }
                    else if (pass == 1) // Face + border
                    {
                        Color animated = _segments[i].Pressed
                            ? Ui.Blend(_segments[i].Source.BackColor, Color.Black, 0.12f)
                            : Ui.Blend(_segments[i].Source.BackColor, Color.White, _segments[i].Animation * 0.08f);
                        var r = new Rectangle(_segments[i].X, _segments[i].Y + lifts[i], Width - 1, segH - faceBottom);
                        using var path = PartialRoundedRect(r, Radius, first, first, last, last);
                        using var faceBrush = new SolidBrush(animated);
                        g.FillPath(faceBrush, path);
                        var bc = _segments[i].Source.BorderColor == Color.Transparent
                            ? Ui.Blend(_segments[i].Source.BackColor, Color.White, 0.25f)
                            : _segments[i].Source.BorderColor;
                        using var pen = new Pen(bc, 1);
                        g.DrawPath(pen, path);
                    }
                    else // Icons
                    {
                        var r = new Rectangle(_segments[i].X, _segments[i].Y + lifts[i], Width - 1, segH - faceBottom);
                        using var path = PartialRoundedRect(r, Radius, first, first, last, last);
                        g.SetClip(path);
                        using var brush = new SolidBrush(_segments[i].Source.ForeColor);
                        g.DrawString(_segments[i].Source.Text, _segments[i].Source.Font, brush,
                            new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        g.ResetClip();
                    }
                }
        }
    }
}


/// <summary>
/// A narrow control that renders a 2-column × 4-row dot grid to visually signal
/// that the user can drag from this point. Wired to task drag initiation in
/// <see cref="MainForm.BeginTaskDrag"/>.
/// </summary>
public sealed class DragHandle : Control
{
    /// <summary>Color of the 8 painted dots.</summary>
    public Color DotColor { get; set; } = Color.Gray;

    public DragHandle()
    {
        Width = Ui.Scaled(20);
        Cursor = Cursors.SizeAll;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Ui.ResolvedBackColor(Parent));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(DotColor);
        float scale = e.Graphics.DpiX / 96f;
        int dot = Math.Max(1, (int)(4 * scale));
        int colGap = (int)(7 * scale), rowGap = (int)(9 * scale);
        int leftPad = (int)(5 * scale);
        int minTop = (int)(10 * scale), centerOff = (int)(18 * scale);
        int top = Math.Max(minTop, (Height / 2) - centerOff);
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                e.Graphics.FillEllipse(brush, leftPad + (col * colGap), top + (row * rowGap), dot, dot);
            }
        }
    }
}

/// <summary>
/// A borderless, always-on-top floating window that follows the mouse cursor while a
/// task is being dragged. It renders a miniature task card that tilts based on drag velocity,
/// giving the drag gesture a physical feel.
/// </summary>
/// <remarks>
/// Position and rotation are updated by a 15 ms timer using the same exponential easing
/// as <see cref="ModernButton"/>. The form is shown with a magenta background so that
/// non-card pixels are fully transparent (via <see cref="Form.TransparencyKey"/>).
/// </remarks>
public sealed class DragPreviewForm : Form
{
    private const float MaxRotationDeg = 21f;
    private const float RotationSensitivity = 5f;
    private const float LerpSpeed = 0.20f;
    private const int CursorOffsetX = 14;
    private const int CursorOffsetY = 14;
    private const int CardWidth = 250;
    private const int CardHeight = 112;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point32 { public int x, y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Size32 { public int cx, cy; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref Point32 pptDst, ref Size32 psize,
        IntPtr hdcSrc, ref Point32 pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly string title;
    private readonly string notes;
    private readonly BoardPalette palette;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private PointF current;
    private PointF target;
    private PointF previous;
    private float rotation;
    private int cardWidthPx;
    private int cardHeightPx;
    private int paddingX;
    private int paddingY;

    public DragPreviewForm(string title, string notes, BoardPalette palette)
    {
        this.title = title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        this.notes = notes;
        this.palette = palette;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowInTaskbar = false;
        TopMost = true;

        timer.Tick += (_, _) =>
        {
            previous = current;
            current.X += (target.X - current.X) * LerpSpeed;
            current.Y += (target.Y - current.Y) * LerpSpeed;
            float velocity = current.X - previous.X;
            rotation += (Math.Clamp(velocity / RotationSensitivity, -MaxRotationDeg, MaxRotationDeg) - rotation) * LerpSpeed;
            Location = Point.Round(current);
            RenderLayered();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        cardWidthPx = this.DpiScaled(CardWidth);
        cardHeightPx = this.DpiScaled(CardHeight);

        float rad = MaxRotationDeg * (float)(Math.PI / 180.0);
        float cos = Math.Abs((float)Math.Cos(rad));
        float sin = Math.Abs((float)Math.Sin(rad));
        int boundW = (int)Math.Ceiling(cardWidthPx * cos + cardHeightPx * sin);
        int boundH = (int)Math.Ceiling(cardWidthPx * sin + cardHeightPx * cos);

        paddingX = (boundW - cardWidthPx) / 2;
        paddingY = (boundH - cardHeightPx) / 2;

        Width = boundW;
        Height = boundH;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    public void Begin(Point screenPoint)
    {
        target = new PointF(screenPoint.X + CursorOffsetX - paddingX, screenPoint.Y + CursorOffsetY - paddingY);
        current = target;
        previous = current;
        Location = Point.Round(current);
        Show();
        RenderLayered();
        timer.Start();
    }

    public void MoveTo(Point screenPoint)
    {
        target = new PointF(screenPoint.X + CursorOffsetX - paddingX, screenPoint.Y + CursorOffsetY - paddingY);
    }

    public void End()
    {
        timer.Stop();
        Hide();
        Dispose();
    }

    private void RenderLayered()
    {
        using var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            g.TranslateTransform(Width / 2f, Height / 2f);
            g.RotateTransform(rotation);
            g.TranslateTransform(-cardWidthPx / 2f, -cardHeightPx / 2f);

            float scale = this.DeviceDpi / 96f;
            int r14 = (int)(14 * scale);

            var shadowRect = new Rectangle((int)(8 * scale), (int)(12 * scale),
                cardWidthPx - (int)(18 * scale), cardHeightPx - (int)(22 * scale));
            using (var shadowPath = Ui.RoundedRect(shadowRect, r14))
            using (var shadowBrush = new SolidBrush(Ui.ColorFrom(palette.Shadow)))
                g.FillPath(shadowBrush, shadowPath);

            var cardRect = new Rectangle((int)(2 * scale), (int)(2 * scale),
                cardWidthPx - (int)(18 * scale), cardHeightPx - (int)(18 * scale));
            using var path = Ui.RoundedRect(cardRect, r14);
            using var cardBrush = new SolidBrush(Ui.ColorFrom(palette.Card));
            using var borderPen = new Pen(Ui.ColorFrom(palette.Border));
            g.FillPath(cardBrush, path);
            g.DrawPath(borderPen, path);

            using var titleBrush = new SolidBrush(Ui.ColorFrom(palette.Text));
            using var mutedBrush = new SolidBrush(Ui.ColorFrom(palette.Muted));
            g.DrawString(title,
                new Font("Segoe UI", 10F, FontStyle.Bold),
                titleBrush,
                new RectangleF((int)(18 * scale), (int)(16 * scale),
                    cardWidthPx - (int)(46 * scale), (int)(24 * scale)));
            g.DrawString(notes,
                new Font("Segoe UI", 9F, FontStyle.Italic),
                mutedBrush,
                new RectangleF((int)(30 * scale), (int)(44 * scale),
                    cardWidthPx - (int)(58 * scale), (int)(38 * scale)));
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            memDc = CreateCompatibleDC(IntPtr.Zero);
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1
            };

            var formPos = new Point32 { x = Left, y = Top };
            var formSize = new Size32 { cx = Width, cy = Height };
            var srcPos = new Point32 { x = 0, y = 0 };

            UpdateLayeredWindow(Handle, IntPtr.Zero, ref formPos, ref formSize,
                memDc, ref srcPos, 0, ref blend, 2);
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) { SelectObject(memDc, oldBitmap); DeleteObject(hBitmap); }
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
        }
    }
}

/// <summary>
/// A fixed-height color band showing the four column accent colors side by side.
/// Used in the palette browser and editor to give a quick visual impression of a palette
/// without opening a full board. The four segments correspond to <see cref="BoardColumns.All"/> order.
/// </summary>
public sealed class PaletteBandPreview : Control
{
    /// <summary>
    /// Array of four hex color strings, one per column in <see cref="BoardColumns.All"/> order
    /// (Todo, In Progress, Testing, Complete).
    /// </summary>
    public string[] Colors { get; set; } = ["#3B82F6", "#EF4444", "#EAB308", "#22C55E"];

    /// <summary>Corner radius of each color segment.</summary>
    public int Radius { get; set; } = Ui.Scaled(9);

    public PaletteBandPreview()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Height = Ui.Scaled(34);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Ui.ResolvedBackColor(Parent));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = e.Graphics.DpiX / 96f;
        int gap = (int)(8 * scale);
        int yOff = (int)(4 * scale);
        int hExtra = (int)(10 * scale);
        int segmentWidth = Math.Max(gap, (Width - (gap * 3)) / 4);
        for (int i = 0; i < 4; i++)
        {
            int x = i * (segmentWidth + gap);
            var rect = new Rectangle(x, yOff, segmentWidth, Height + hExtra);
            using var path = Ui.RoundedRect(rect, Radius);
            using var brush = new SolidBrush(Ui.ColorFrom(Colors[Math.Min(i, Colors.Length - 1)]));
            e.Graphics.FillPath(brush, path);
        }
    }
}

/// <summary>
/// A rounded, palette-coloured search input with a magnifying-glass icon on the left.
/// Set <see cref="Items"/> to the strings to search across, then subscribe to
/// <see cref="OnSearch"/> to receive filtered index arrays as the user types.
/// </summary>
public sealed class SearchBar : Control
{
    private readonly BoardPalette _palette;
    private readonly TextBox _textBox;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private float _anim;
    private float _target;
    private bool _suppressSearch;

    private int IconWidth => Height;
    private int RightPadding => Ui.Scaled(8);
    private int IconTextGap => Ui.Scaled(6);

    /// <summary>The strings to search across. Reassign to change the dataset.</summary>
    public string[] Items { get; set; } = [];

    /// <summary>
    /// Raised whenever the search results change (including when the box is cleared).
    /// Argument is matching indices into <see cref="Items"/>, or null when the query is
    /// empty (meaning "show everything").
    /// </summary>
    public Action<int[]?>? OnSearch { get; set; }

    public SearchBar(BoardPalette palette)
    {
        _palette = palette;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        ResizeRedraw = true;
        _timer.Tick += (_, _) =>
        {
            _anim += (_target - _anim) * 0.25f;
            if (Math.Abs(_target - _anim) < 0.01f)
            {
                _anim = _target;
                _timer.Stop();
            }
            Invalidate();
        };

        _textBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Ui.ColorFrom(palette.Card),
            ForeColor = Ui.ColorFrom(palette.Text)
        };
        _textBox.TextChanged += (_, _) => { if (!_suppressSearch) RunSearch(); };
        Controls.Add(_textBox);
    }

    /// <summary>Clears the text box without firing a spurious search event.</summary>
    public void Clear()
    {
        _suppressSearch = true;
        _textBox.Text = "";
        _suppressSearch = false;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_textBox is null) return;
        int x = IconWidth + IconTextGap;
        int w = Math.Max(0, Width - x - RightPadding);
        _textBox.Location = new Point(x, (Height - _textBox.Height) / 2);
        _textBox.Width = w;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Ui.ResolvedBackColor(Parent));
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = g.DpiX / 96f;
        int yOff = (int)(2 * scale), hOff = (int)(3 * scale);
        int cornerRadius = (int)(10 * scale);

        Color fill = Ui.ColorFrom(_palette.Card);
        Color border = Ui.Blend(Ui.ColorFrom(_palette.Border), Ui.ColorFrom(_palette.Accent), _anim);
        Color icon = Ui.Blend(Ui.ColorFrom(_palette.Muted), Ui.ColorFrom(_palette.Accent), _anim);

        var rect = new Rectangle(0, yOff, Width - 1, Height - hOff);
        using var path = Ui.RoundedRect(rect, cornerRadius);

        using var fillBrush = new SolidBrush(fill);
        using var borderPen = new Pen(border);
        g.FillPath(fillBrush, path);
        g.DrawPath(borderPen, path);

        using var iconFont = Icons.Get(Height * 0.35f / scale, Icons.FontType.Bold);
        using var iconBrush = new SolidBrush(icon);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(Icons.MagnifyingGlass, iconFont, iconBrush, new RectangleF(0, 0, IconWidth, Height), sf);
    }

    private void RunSearch()
    {
        string q = _textBox.Text.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(q))
        {
            _target = 0f;
            _timer.Start();
        }
        else
        {
            _target = 1f;
            _timer.Start();
        }
        OnSearch?.Invoke(FuzzySearch(q, Items));
    }

    private static int[]? FuzzySearch(string q, string[] items)
    {
        if (string.IsNullOrWhiteSpace(q)) return null;

        var results = new List<(int index, int score)>();
        for (int i = 0; i < items.Length; i++)
        {
            string s = items[i].ToLowerInvariant();
            int qi = 0, firstMatch = -1, lastMatch = -1;
            for (int si = 0; si < s.Length && qi < q.Length; si++)
            {
                if (s[si] == q[qi])
                {
                    if (firstMatch < 0) firstMatch = si;
                    lastMatch = si;
                    qi++;
                }
            }
            if (qi == q.Length)
            {
                int score = -(firstMatch * 100 + (lastMatch - firstMatch));
                results.Add((i, score));
            }
        }
        return [.. results.OrderByDescending(r => r.score).Select(r => r.index)];
    }
}
