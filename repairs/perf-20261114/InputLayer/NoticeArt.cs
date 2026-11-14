using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClawInput {
    // Folia StatusToast: 9px 16px padding, 26px line, 22px radius,
    // 18px Sparkles, 10px gap, 14px text, white 10% glass / 8% edge.
    // Source measurements are recorded in docs/folia-notice-20261023.md.
    public static class NoticeArt {
        public const int Margin = 48, Height = 46, Radius = 22, MaxWidth = 640;
        static readonly string Face = FontFace();
        static readonly string MediumFace = MediumFontFace();
        static string MediumFontFace() {
            foreach (var family in FontFamily.Families) if (family.Name == "Noto Sans SC Medium") return family.Name;
            return Face;
        }
        static string FontFace() {
            foreach (var family in FontFamily.Families) if (family.Name == "Noto Sans SC") return family.Name;
            return "Microsoft YaHei";
        }
        static StringFormat TextFormat() {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            return format;
        }
        public static Bitmap Render(string kind, string title, string detail, float scale) {
            if (scale < .5f || scale > 4 || float.IsNaN(scale)) throw new ArgumentException("DPI 无效");
            detail = (detail ?? "").Trim();
            float titleWidth, noteWidth, cardWidth;
            using (var measure = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(measure))
            using (var font = new Font(Face, 14, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var label = new Font(MediumFace, 14, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var format = TextFormat()) {
                titleWidth = (float)Math.Ceiling(g.MeasureString(title, label, int.MaxValue, format).Width);
                noteWidth = detail.Length == 0 ? 0 : (float)Math.Ceiling(g.MeasureString(detail, font, int.MaxValue, format).Width);
                cardWidth = Math.Min(MaxWidth, 34 + 18 + 10 + titleWidth + (noteWidth > 0 ? 21 + noteWidth : 0));
            }
            var bitmap = new Bitmap((int)Math.Ceiling((cardWidth + Margin * 2) * scale),
                (int)Math.Ceiling((Height + Margin * 2) * scale), PixelFormat.Format32bppPArgb);
            using (var shadow = Shadow(cardWidth, scale))
            using (var g = Graphics.FromImage(bitmap)) {
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(shadow, 0, 0);
                g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var card = new RectangleF(Margin + .5f, Margin + .5f, cardWidth - 1, Height - 1);
                using (var path = Rounded(card, Radius - .5f)) {
                    // CSS outer shadows are clipped out of the border box.
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var clear = new SolidBrush(Color.Transparent)) g.FillPath(clear, path);
                    g.CompositingMode = CompositingMode.SourceOver;
                    // A separate native backdrop supplies the live glass behind this tint.
                    using (var fill = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) g.FillPath(fill, path);
                    using (var edge = new Pen(Color.FromArgb(20, 255, 255, 255), 1)) g.DrawPath(edge, path);
                }
                Icon(g, kind, Margin + 17, Margin + 14);
                float x = Margin + 45, available = cardWidth - 62;
                float labelWidth = Math.Min(available, titleWidth);
                using (var font = new Font(Face, 14, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var label = new Font(MediumFace, 14, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var ink = new SolidBrush(Color.White))
                using (var secondary = new SolidBrush(Color.FromArgb(163, 255, 255, 255)))
                using (var format = TextFormat()) {
                    g.DrawString(title, label, ink, new RectangleF(x, Margin + 10, labelWidth + .5f, 26), format);
                    if (noteWidth > 0 && available - labelWidth > 21) {
                        x += labelWidth + 10;
                        using (var hair = new SolidBrush(Color.FromArgb(33, 255, 255, 255))) g.FillRectangle(hair, x, Margin + 17, 1, 12);
                        x += 11;
                        g.DrawString(detail, font, secondary, new RectangleF(x, Margin + 10, available - labelWidth - 21, 26), format);
                    }
                }
            }
            return bitmap;
        }
        // CSS's 0 12px 32px rgba(0,0,0,.38): Gaussian sigma = half the blur radius.
        static Bitmap Shadow(float width, float scale) {
            int w = (int)Math.Ceiling((width + Margin * 2) * scale), h = (int)Math.Ceiling((Height + Margin * 2) * scale);
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(scale, scale);
                using (var path = Rounded(new RectangleF(Margin, Margin + 12, width, Height), Radius))
                using (var brush = new SolidBrush(Color.FromArgb(97, 0, 0, 0))) g.FillPath(brush, path);
            }
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try {
                byte[] bytes = new byte[data.Stride * h]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var first = new float[w * h]; var second = new float[w * h];
                // Three box passes approximate Gaussian blur, once per notice.
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) first[y * w + x] = bytes[y * data.Stride + x * 4 + 3];
                int radius = Math.Max(1, (int)Math.Round(16 * scale));
                for (int pass = 0; pass < 3; pass++) {
                    for (int y = 0; y < h; y++) {
                        float sum = 0;
                        for (int x = -radius; x <= radius; x++) if (x >= 0 && x < w) sum += first[y * w + x];
                        for (int x = 0; x < w; x++) {
                            second[y * w + x] = sum / (radius * 2 + 1);
                            if (x - radius >= 0) sum -= first[y * w + x - radius];
                            if (x + radius + 1 < w) sum += first[y * w + x + radius + 1];
                        }
                    }
                    for (int x = 0; x < w; x++) {
                        float sum = 0;
                        for (int y = -radius; y <= radius; y++) if (y >= 0 && y < h) sum += second[y * w + x];
                        for (int y = 0; y < h; y++) {
                            first[y * w + x] = sum / (radius * 2 + 1);
                            if (y - radius >= 0) sum -= second[(y - radius) * w + x];
                            if (y + radius + 1 < h) sum += second[(y + radius + 1) * w + x];
                        }
                    }
                }
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) bytes[y * data.Stride + x * 4 + 3] = (byte)Math.Round(first[y * w + x]);
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            } finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        static void Icon(Graphics g, string kind, float x, float y) {
            var state = g.Save(); g.TranslateTransform(x, y); g.ScaleTransform(.75f, .75f);
            var color = kind == "error" ? Color.FromArgb(248, 113, 113) : kind == "warning" ? Color.FromArgb(251, 191, 36)
                : kind == "success" ? Color.FromArgb(74, 222, 128) : Color.FromArgb(96, 165, 250);
            using (var p = new Pen(color, 2)) {
                p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                switch (kind) {
                    case "success": g.DrawLines(p, new[] { new PointF(4, 12), new PointF(9, 17), new PointF(20, 6) }); break;
                    case "error": g.DrawEllipse(p, 3, 3, 18, 18); g.DrawLine(p, 8, 8, 16, 16); g.DrawLine(p, 16, 8, 8, 16); break;
                    case "warning": g.DrawPolygon(p, new[] { new PointF(12, 3), new PointF(22, 21), new PointF(2, 21) }); g.DrawLine(p, 12, 9, 12, 14); g.DrawEllipse(p, 11.5f, 17, 1, 1); break;
                    case "charging": g.DrawLines(p, new[] { new PointF(6, 3), new PointF(6, 8), new PointF(18, 8), new PointF(18, 3) }); g.DrawArc(p, 5, 7, 14, 12, 0, 180); g.DrawLine(p, 12, 19, 12, 23); break;
                    case "battery": g.DrawRectangle(p, 2, 6, 18, 12); g.DrawLine(p, 23, 10, 23, 14); g.DrawLine(p, 6, 10, 6, 14); break;
                    case "gamepad": case "game": g.DrawArc(p, 2, 6, 20, 14, 150, 240); g.DrawLine(p, 6, 12, 12, 12); g.DrawLine(p, 9, 9, 9, 15); g.DrawEllipse(p, 16, 10, 1, 1); g.DrawEllipse(p, 18, 14, 1, 1); break;
                    case "desktop": g.DrawRectangle(p, 2, 3, 20, 14); g.DrawLine(p, 12, 17, 12, 21); g.DrawLine(p, 7, 21, 17, 21); break;
                    case "library": g.DrawRectangle(p, 3, 4, 5, 16); g.DrawRectangle(p, 10, 4, 5, 16); g.DrawLine(p, 18, 4, 22, 20); break;
                    case "account": g.DrawEllipse(p, 8, 3, 8, 8); g.DrawArc(p, 4, 14, 16, 14, 180, 180); break;
                    case "update": g.DrawArc(p, 3, 3, 18, 18, 40, 280); g.DrawLines(p, new[] { new PointF(22, 3), new PointF(22, 10), new PointF(15, 10) }); break;
                    case "image": g.DrawRectangle(p, 3, 3, 18, 18); g.DrawEllipse(p, 7, 7, 3, 3); g.DrawLines(p, new[] { new PointF(4, 19), new PointF(11, 12), new PointF(15, 16), new PointF(20, 11) }); break;
                    case "sound": g.DrawPolygon(p, new[] { new PointF(3, 9), new PointF(7, 9), new PointF(12, 4), new PointF(12, 20), new PointF(7, 15), new PointF(3, 15) }); g.DrawArc(p, 10, 5, 12, 14, -65, 130); break;
                    case "power": g.DrawArc(p, 3, 3, 18, 18, -50, 280); g.DrawLine(p, 12, 2, 12, 11); break;
                    case "settings": g.DrawLine(p, 3, 6, 21, 6); g.DrawLine(p, 3, 12, 21, 12); g.DrawLine(p, 3, 18, 21, 18); g.DrawEllipse(p, 7, 4, 4, 4); g.DrawEllipse(p, 14, 10, 4, 4); g.DrawEllipse(p, 5, 16, 4, 4); break;
                    default: g.DrawEllipse(p, 3, 3, 18, 18); g.DrawLine(p, 12, 11, 12, 17); g.DrawEllipse(p, 11.5f, 7, 1, 1); break;
                }
            }
            g.Restore(state);
        }
        static void Sparkles(Graphics g, float x, float y, Color color) {
            var state = g.Save(); g.TranslateTransform(x, y); g.ScaleTransform(.75f, .75f);
            using (var pen = new Pen(color, 2)) {
                pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                // Lucide 1.48 Sparkles; the four concave 2px arcs and 1px tips.
                // ISC license: docs/licenses/lucide.txt.
                using (var star = new GraphicsPath()) {
                    star.AddArc(11, 2, 2, 2, 190.72f, 158.56f);
                    star.AddArc(14, 6, 4, 4, 169.28f, -68.56f);
                    star.AddArc(20, 11, 2, 2, 280.72f, 158.56f);
                    star.AddArc(14, 14, 4, 4, 259.28f, -68.56f);
                    star.AddArc(11, 20, 2, 2, 10.72f, 158.56f);
                    star.AddArc(6, 14, 4, 4, 349.28f, -68.56f);
                    star.AddArc(2, 11, 2, 2, 100.72f, 158.56f);
                    star.AddArc(6, 6, 4, 4, 79.28f, -68.56f);
                    star.CloseFigure(); g.DrawPath(pen, star);
                }
                g.DrawLine(pen, 20, 2, 20, 6); g.DrawLine(pen, 18, 4, 22, 4);
                g.DrawEllipse(pen, 2, 18, 4, 4);
            }
            g.Restore(state);
        }
        public static GraphicsPath Rounded(RectangleF r, float radius) {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
    }
}
