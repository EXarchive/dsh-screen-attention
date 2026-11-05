using System;

/// <summary>
/// 逐帧画面变化分。**因果**：只看过去，不看未来。
///
/// 移植自 dsh-see-now 的 cuts.js（Adobe storyboard 的 scan.js 口径）：
///   - 64×36 网格，每格取 4 个采样点求平均（降噪，且只花 9k 次读）
///   - RGB → HSV，色相按环形处理，低饱和像素的色相降权
///   - 两帧 HSV 的加权绝对差均值 = 0~255 的分数
///
/// 为什么用 HSV 而不是纯像素差：同亮度换色（换主题、换壁纸）纯 RGB 会看漏，
/// 色相通道能抓住。实测分数刻度（他们那台机器）：
///   静止桌面 0.00 / 光标移动 0.7 / 色调接近的照片切换 16~18
///   滚网页 30~42 / 完全不同的照片切换 46~63
/// 所以切点地板取 12：照片切换抓得住，光标和微小抖动碰不到。
/// </summary>
static class Cuts
{
    public const int ScanW = 64;
    public const int ScanH = 36;
    public const int N = ScanW * ScanH;

    /// <summary>64×36 网格：每格 4 点采样求平均（BGRA 输入）。</summary>
    /// <remarks>曾试过 3×3 九点采样想提高小区域敏感度，结果实测 score 恒为 0（全场景失灵），已回滚。</remarks>
    public static void GridRgb(byte[] bgra, int w, int h, float[] outGrid)
    {
        double sx = (double)w / ScanW, sy = (double)h / ScanH;
        for (int gy = 0; gy < ScanH; gy++)
        {
            int y0 = (int)((gy + 0.25) * sy), y1 = (int)((gy + 0.75) * sy);
            if (y0 >= h) y0 = h - 1; if (y1 >= h) y1 = h - 1;
            int r0 = y0 * w * 4, r1 = y1 * w * 4;
            for (int gx = 0; gx < ScanW; gx++)
            {
                int x0 = (int)((gx + 0.25) * sx), x1 = (int)((gx + 0.75) * sx);
                if (x0 >= w) x0 = w - 1; if (x1 >= w) x1 = w - 1;
                int a = r0 + x0 * 4, b = r0 + x1 * 4, cc = r1 + x0 * 4, d = r1 + x1 * 4;
                int i = (gy * ScanW + gx) * 3;
                outGrid[i]     = (bgra[a + 2] + bgra[b + 2] + bgra[cc + 2] + bgra[d + 2]) * 0.25f;
                outGrid[i + 1] = (bgra[a + 1] + bgra[b + 1] + bgra[cc + 1] + bgra[d + 1]) * 0.25f;
                outGrid[i + 2] = (bgra[a]     + bgra[b]     + bgra[cc]     + bgra[d])     * 0.25f;
            }
        }
    }

    /// <summary>RGB → HSV（各通道归一到 0–255，H 由角度缩放）。就地写进 out。</summary>
    public static void RgbToHsv(float[] rgb, float[] outHsv)
    {
        for (int i = 0; i < N * 3; i += 3)
        {
            float r = rgb[i] / 255f, g = rgb[i + 1] / 255f, b = rgb[i + 2] / 255f;
            float max = r > g ? (r > b ? r : b) : (g > b ? g : b);
            float min = r < g ? (r < b ? r : b) : (g < b ? g : b);
            float d = max - min;
            float hh = 0f;
            if (d != 0f)
            {
                if (max == r) hh = ((g - b) / d) % 6f;
                else if (max == g) hh = (b - r) / d + 2f;
                else hh = (r - g) / d + 4f;
                hh *= 60f;
                if (hh < 0f) hh += 360f;
            }
            outHsv[i] = (hh / 360f) * 255f;
            outHsv[i + 1] = max == 0f ? 0f : (d / max) * 255f;
            outHsv[i + 2] = max * 255f;
        }
    }

    /// <summary>两帧 HSV 的加权绝对差均值，0–255。色相环形处理，低饱和像素的色相降权。</summary>
    public static double HsvDiff(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < N * 3; i += 3)
        {
            double dh = Math.Abs(a[i] - b[i]);
            if (dh > 127.5) dh = 255 - dh;          // 色相环形
            double s = (a[i + 1] + b[i + 1]) * 0.5; // 低饱和时色相不可靠，降权
            double w = 1.0 - (s / 255.0) * 0.6;
            sum += dh * w + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }
        return sum / N / 3.0;
    }

    /// <summary>运动补偿：同位置先测，再扫邻域。返回「补偿后仍变化」的格数占比 0–255。</summary>
    public static double McRatio(float[] a, float[] b, int radius = 4, double tau = 15)
    {
        // 先测同位置（屏幕最常见的状态是"没变"，省掉整圈扫描）
        int same = 0;
        for (int i = 0; i < N; i++)
        {
            int o = i * 3;
            if (Math.Abs(a[o] - b[o]) <= tau && Math.Abs(a[o + 1] - b[o + 1]) <= tau && Math.Abs(a[o + 2] - b[o + 2]) <= tau) same++;
        }
        if (same >= N * 0.97) return 0;   // 几乎全同，直接返回

        int changed = 0;
        for (int gy = 0; gy < ScanH; gy++)
        {
            for (int gx = 0; gx < ScanW; gx++)
            {
                int i = (gy * ScanW + gx) * 3;
                bool moved = false;
                for (int dy = -radius; dy <= radius && !moved; dy++)
                {
                    int yy = gy + dy;
                    if (yy < 0 || yy >= ScanH) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int xx = gx + dx;
                        if (xx < 0 || xx >= ScanW) continue;
                        int j = (yy * ScanW + xx) * 3;
                        if (Math.Abs(a[i] - b[j]) <= tau && Math.Abs(a[i + 1] - b[j + 1]) <= tau && Math.Abs(a[i + 2] - b[j + 2]) <= tau)
                        { moved = true; break; }
                    }
                }
                if (!moved) changed++;
            }
        }
        return changed * 255.0 / N;
    }

    /// <summary>
    /// 打分器：维护上一帧 HSV，逐帧给分。
    /// </summary>
    public sealed class Scorer
    {
        float[] _prevHsv = new float[N * 3];
        float[] _curHsv = new float[N * 3];
        float[] _grid = new float[N * 3];
        bool _has;

        /// <summary>返回 (score, mc)；首帧返回 (-1, 0)。</summary>
        public (double score, double mc) Push(byte[] bgra, int w, int h)
        {
            GridRgb(bgra, w, h, _grid);
            RgbToHsv(_grid, _curHsv);
            if (!_has)
            {
                Array.Copy(_curHsv, _prevHsv, _curHsv.Length);
                _has = true;
                return (-1, 0);
            }
            double score = HsvDiff(_prevHsv, _curHsv);
            double mc = McRatio(_prevHsv, _curHsv);
            Array.Copy(_curHsv, _prevHsv, _curHsv.Length);
            return (score, mc);
        }

        public void Reset() { _has = false; }
    }
}
