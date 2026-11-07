namespace GhostScreen;

internal sealed class BlackoutOverlayForm : Form
{
    private byte _alpha = 255;

    public bool IsAffinityVerified { get; private set; }

    public BlackoutOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Bounds = NativeMethods.GetVirtualScreenPhysicalBounds();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                        | NativeMethods.WS_EX_TRANSPARENT
                        | NativeMethods.WS_EX_TOPMOST
                        | NativeMethods.WS_EX_TOOLWINDOW
                        | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyStealthAttributes();
    }

    public void SetOverlayAlpha(byte alpha)
    {
        _alpha = alpha;
        if (IsHandleCreated)
        {
            NativeMethods.SetLayeredWindowAttributes(Handle, 0, _alpha, NativeMethods.LWA_ALPHA);
        }
    }

    public void ApplyStealthAttributes()
    {
        if (!IsHandleCreated) return;

        int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(
            Handle,
            NativeMethods.GWL_EXSTYLE,
            exStyle
            | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE);

        NativeMethods.SetLayeredWindowAttributes(Handle, 0, _alpha, NativeMethods.LWA_ALPHA);
        NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        if (NativeMethods.GetWindowDisplayAffinity(Handle, out uint affinity))
        {
            IsAffinityVerified = affinity == NativeMethods.WDA_EXCLUDEFROMCAPTURE;
        }
    }

    public void EnforceStealthAndTopmost()
    {
        if (!IsHandleCreated || !Visible) return;

        if (NativeMethods.GetWindowDisplayAffinity(Handle, out uint affinity) &&
            affinity != NativeMethods.WDA_EXCLUDEFROMCAPTURE)
        {
            NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        Rectangle targetBounds = NativeMethods.GetVirtualScreenPhysicalBounds();
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            targetBounds.X,
            targetBounds.Y,
            targetBounds.Width,
            targetBounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}
