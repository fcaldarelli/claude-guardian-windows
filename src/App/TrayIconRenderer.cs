using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ClaudeGuardian
{
    /// <summary>
    /// Draws the notification area icons and the small menu images.
    ///
    /// Like the macOS menu bar label ("⚙ 3  ✋ 1  ☾ 2"), there is one icon per state, each with
    /// its color and count. A notification area icon is only 16 px (20–24 px with display scaling),
    /// so the digits are scaled to fill as much of the square as possible.
    /// </summary>
    public static class TrayIconRenderer
    {
        // App icon palette (icon/make-icon.py).
        public static readonly Color Working = Color.FromArgb(217, 119, 87);   // terracotta
        public static readonly Color Waiting = Color.FromArgb(242, 181, 68);   // amber
        public static readonly Color Idle = Color.FromArgb(138, 129, 120);     // warm grey
        private static readonly Color Dark = Color.FromArgb(43, 38, 34);       // warm charcoal

        public static Color ColorOf(SessionState state)
        {
            switch (state)
            {
                case SessionState.Working: return Working;
                case SessionState.Waiting: return Waiting;
                default: return Idle;
            }
        }

        /// <summary>
        /// Icon with the color and count of the most urgent state: amber if someone is waiting for you,
        /// terracotta if someone is working, grey if all are idle.
        /// The returned Icon owns its HICON: free it with Destroy.
        /// </summary>
        public static Icon Status(int working, int waiting, int idle, int size)
        {
            if (waiting > 0) return Count(SessionState.Waiting, waiting, size);
            if (working > 0) return Count(SessionState.Working, working, size);
            return Count(SessionState.Idle, idle, size);
        }

        /// <summary>
        /// Icon of a state with its count: a square filled with the state's color and a white
        /// (or, on amber, dark) number; when the count is zero, only the outline and the number,
        /// both in the state's color.
        /// The returned Icon owns its HICON: free it with Destroy.
        /// </summary>
        public static Icon Count(SessionState state, int count, int size)
        {
            var color = ColorOf(state);
            var empty = count == 0;
            var textColor = empty ? color : (state == SessionState.Waiting ? Dark : Color.White);

            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    var stroke = Math.Max(1.5f, size / 10f);
                    var inset = empty ? stroke / 2 : 0;
                    var rect = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
                    using (var path = RoundedRect(rect, size * 0.2f))
                    {
                        if (empty)
                        {
                            using (var pen = new Pen(color, stroke))
                                g.DrawPath(pen, path);
                        }
                        else
                        {
                            using (var brush = new SolidBrush(color))
                                g.FillPath(brush, path);
                        }
                    }

                    var label = count > 99 ? "99" : count.ToString();
                    // Digits fill about two thirds of the height, and leave a margin on the sides.
                    var box = new RectangleF(0, 0, size, size);
                    box.Inflate(-size * (label.Length == 1 ? 0.2f : 0.12f), -size * 0.17f);
                    using (var digits = FittedText(label, box))
                    using (var brush = new SolidBrush(textColor))
                        g.FillPath(brush, digits);
                }
                return ToIcon(bitmap);
            }
        }

        /// <summary>Frees an icon created by Status or Count.</summary>
        public static void Destroy(Icon icon) => icon?.Dispose();

        /// <summary>
        /// The text as a shape, scaled to fit the box and centered in it. Text drawn with a font
        /// size leaves the empty space of the font above and below the digits; this uses the
        /// actual outline of the digits instead, so they are as large as possible.
        /// </summary>
        private static GraphicsPath FittedText(string text, RectangleF box)
        {
            var path = new GraphicsPath();
            using (var family = new FontFamily("Segoe UI"))
            {
                var style = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                path.AddString(text, family, (int)style, 100f, PointF.Empty, StringFormat.GenericTypographic);
            }
            var bounds = path.GetBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return path;

            var scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
            using (var matrix = new Matrix())
            {
                matrix.Translate(box.X + (box.Width - bounds.Width * scale) / 2, box.Y + (box.Height - bounds.Height * scale) / 2);
                matrix.Scale(scale, scale);
                matrix.Translate(-bounds.X, -bounds.Y);
                path.Transform(matrix);
            }
            return path;
        }

        /// <summary>
        /// Builds a real 32-bit icon with an alpha channel. Bitmap.GetHicon() handles transparency
        /// poorly, which leaves jagged or dark edges around anti-aliased shapes.
        /// </summary>
        private static Icon ToIcon(Bitmap bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height;
            var pixelBytes = width * height * 4;
            var maskBytes = ((width + 31) / 32) * 4 * height;   // AND mask, unused with alpha but required

            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                // ICONDIR and the single ICONDIRENTRY.
                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write((byte)(width >= 256 ? 0 : width));
                writer.Write((byte)(height >= 256 ? 0 : height));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(40 + pixelBytes + maskBytes);
                writer.Write(6 + 16);

                // BITMAPINFOHEADER: the height counts both the image and the mask.
                writer.Write(40);
                writer.Write(width);
                writer.Write(height * 2);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(0);
                writer.Write(pixelBytes + maskBytes);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);

                // Pixels as BGRA, bottom row first.
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[width * 4];
                    for (var y = height - 1; y >= 0; y--)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        writer.Write(row);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                writer.Write(new byte[maskBytes]);
                writer.Flush();

                stream.Position = 0;
                return new Icon(stream, width, height);
            }
        }

        // MARK: - Menu images

        private static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>();

        /// <summary>Colored state dot; hollow for "no activity" and "unknown".</summary>
        public static Image StateDot(SessionState state, int size)
        {
            var key = "dot:" + state + ":" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var d = size * 0.6f;
                var rect = new RectangleF((size - d) / 2, (size - d) / 2, d, d);
                var color = ColorOf(state);
                if (state == SessionState.Stalled || state == SessionState.Unknown)
                {
                    using (var pen = new Pen(color, Math.Max(1.2f, size / 10f)))
                        g.DrawEllipse(pen, rect);
                }
                else
                {
                    using (var brush = new SolidBrush(color))
                        g.FillEllipse(brush, rect);
                }
            }
            Cache[key] = bitmap;
            return bitmap;
        }

        /// <summary>System folder icon (the same as in File Explorer).</summary>
        public static Image Folder(int size)
        {
            var key = "folder:" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            Image image = null;
            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf(typeof(SHSTOCKICONINFO)) };
            var flags = SHGSI_ICON | (size <= 16 ? SHGSI_SMALLICON : SHGSI_LARGEICON);
            if (SHGetStockIconInfo(SIID_FOLDER, flags, ref info) == 0 && info.hIcon != IntPtr.Zero)
            {
                using (var icon = Icon.FromHandle(info.hIcon))
                using (var bitmap = icon.ToBitmap())
                    image = new Bitmap(bitmap, size, size);
                DestroyIcon(info.hIcon);
            }
            Cache[key] = image;
            return image;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // MARK: - Win32

        private const uint SIID_FOLDER = 3;
        private const uint SHGSI_ICON = 0x000000100;
        private const uint SHGSI_LARGEICON = 0x000000000;
        private const uint SHGSI_SMALLICON = 0x000000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPath;
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetStockIconInfo(uint siid, uint flags, ref SHSTOCKICONINFO info);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
