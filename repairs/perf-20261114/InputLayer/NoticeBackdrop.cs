using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace ClawInput {
    // Window-wide Acrylic ignores the HWND's rounded region on Windows 11.
    // Clip a host backdrop visual instead: pixels outside the capsule never
    // contribute a backdrop, and opacity follows the content's animation.
    sealed class NoticeBackdrop : Form {
        [StructLayout(LayoutKind.Sequential)] struct QueueOptions { public int Size, Thread, Apartment; }
        [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface DesktopInterop {
            void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool topmost,
                [MarshalAs(UnmanagedType.IInspectable)] out object target);
        }

        readonly float radius;
        IntPtr queue;
        Compositor compositor;
        DesktopWindowTarget target;
        ContainerVisual root;
        SpriteVisual glass;
        CompositionBackdropBrush brush;
        CompositionRoundedRectangleGeometry geometry;
        CompositionGeometricClip clip;

        public NoticeBackdrop(Rectangle bounds, float scale) {
            radius = NoticeArt.Radius * scale;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            Text = "Claw 提示背景"; StartPosition = FormStartPosition.Manual; Bounds = bounds;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                var p = base.CreateParams;
                // NOACTIVATE | TOOLWINDOW | TRANSPARENT | TOPMOST | NOREDIRECTIONBITMAP.
                // The compositor owns the alpha surface; no GDI background or layered-window fill.
                p.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8 | 0x00200000;
                return p;
            }
        }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            var options = new QueueOptions { Size = Marshal.SizeOf(typeof(QueueOptions)), Thread = 2, Apartment = 0 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out queue));
            compositor = new Compositor();
            object desktop;
            ((DesktopInterop)(object)compositor).CreateDesktopWindowTarget(Handle, true, out desktop);
            target = (DesktopWindowTarget)desktop;
            root = compositor.CreateContainerVisual();
            root.Size = new Vector2(Width, Height);
            geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Size = root.Size;
            geometry.CornerRadius = new Vector2(radius, radius);
            clip = compositor.CreateGeometricClip(geometry);
            root.Clip = clip;
            int enable = 1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle, 17, ref enable, 4)); // USE_HOSTBACKDROPBRUSH
            brush = compositor.CreateHostBackdropBrush();
            glass = compositor.CreateSpriteVisual();
            glass.Size = root.Size;
            glass.Brush = brush;
            glass.Opacity = 0;
            root.Children.InsertAtTop(glass);
            target.Root = root;
        }
        public void Present(IntPtr content, int top, byte alpha) {
            glass.Opacity = alpha / 255f;
            SetWindowPos(Handle, content, Left, top, Width, Height, 0x0010 | 0x0040);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (target != null) target.Root = null;
                if (root != null) { root.Children.RemoveAll(); root.Clip = null; }
                if (glass != null) { glass.Brush = null; glass.Dispose(); glass = null; }
                if (brush != null) { brush.Dispose(); brush = null; }
                if (clip != null) { clip.Dispose(); clip = null; }
                if (geometry != null) { geometry.Dispose(); geometry = null; }
                if (root != null) { root.Dispose(); root = null; }
                if (target != null) { target.Dispose(); target = null; }
                if (compositor != null) { compositor.Dispose(); compositor = null; }
                if (queue != IntPtr.Zero) { Marshal.Release(queue); queue = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }
}
