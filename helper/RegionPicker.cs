using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

// ============ 拖框选区 ============
// 用户说「框这块」，就让他在屏幕上直接拖一个框，而不是让 AI 猜 x/y/w/h。
// 全屏半透明覆盖窗：25% 黑底（能看到下面的内容）+ 亮绿选框 + 实时尺寸提示。
// Esc 取消。窗口在 STA 线程跑，主线程 Join 等结果。
static class RegionPicker
{
    public sealed class PickResult
    {
        public bool Ok;
        public bool Cancelled;
        public int X, Y, W, H;
    }

    public static PickResult Pick(int timeoutMs = 120000)
    {
        var res = new PickResult { Cancelled = true };
        Rectangle vs = SystemInformation.VirtualScreen;
        Rectangle sel = Rectangle.Empty;
        Point start = Point.Empty;
        bool dragging = false;

        var th = new Thread(() =>
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = vs,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = Color.Black,
                Opacity = 0.25,
                Cursor = Cursors.Cross,
                KeyPreview = true,
            };
            form.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.Lime, 2)) e.Graphics.DrawRectangle(pen, sel);
                if (sel.Width > 1 && sel.Height > 1)
                {
                    using (var br = new SolidBrush(Color.FromArgb(50, 0, 255, 0))) e.Graphics.FillRectangle(br, sel);
                    string tip = sel.Width + " x " + sel.Height;
                    using (var f = new Font("Consolas", 12, FontStyle.Bold))
                    using (var tb = new SolidBrush(Color.Lime))
                        e.Graphics.DrawString(tip, f, tb, sel.X + 6, Math.Max(0, sel.Y - 22));
                }
            };
            form.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { start = e.Location; dragging = true; sel = Rectangle.Empty; form.Invalidate(); } };
            form.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                int x = Math.Min(start.X, e.X), y = Math.Min(start.Y, e.Y);
                sel = new Rectangle(x, y, Math.Abs(e.X - start.X), Math.Abs(e.Y - start.Y));
                form.Invalidate();
            };
            form.MouseUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                if (sel.Width >= 16 && sel.Height >= 16)
                {
                    res.Ok = true; res.Cancelled = false;
                    res.X = vs.X + sel.X; res.Y = vs.Y + sel.Y; res.W = sel.Width; res.H = sel.Height;
                }
                form.Close();
            };
            form.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };
            form.Shown += (s, e) =>
            {
                try { form.Activate(); } catch { }
                // 超时自动关闭，避免主线程 Join 超时后窗口留在屏幕上
                var ttl = new System.Windows.Forms.Timer { Interval = Math.Max(1000, timeoutMs) };
                ttl.Tick += (a, b) => { try { ttl.Stop(); form.Close(); } catch { } };
                ttl.Start();
            };
            try { Application.Run(form); } catch { }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.IsBackground = true;
        th.Start();
        th.Join(timeoutMs);
        return res;
    }
}
