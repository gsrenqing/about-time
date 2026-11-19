// About Time 1.4 —— 倒计时 / 秒表模块（右端标签切换 + 自绘按钮 + 时间到弹窗 + 响闹）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AboutTime
{
    // ============================================================ 状态
    sealed class TimerMod
    {
        public bool Alarm;              // 响闹开（默认静音）
        public int Active = 0;          // 0 倒计时 / 1 秒表（当前选中的一侧）
        public int CdState;             // 0 输入 / 1 运行 / 2 到点
        public int SwState;             // 0 空闲 / 1 运行 / 2 暂停 / 3 到上限
        public int InH = -1, InM = -1, InS = -1;   // 输入：时 / 分 / 秒（-1 = 空）
        public int Field = -1;          // 输入焦点格 0/1/2
        public int FieldDigits;         // 当前格已敲了几位（0/1/2）
        public DateTime CdEnd;          // 倒计时结束时刻
        public long SwAcc;              // 秒表已累计毫秒
        public DateTime SwStart;        // 本段起点

        public bool SwRunning { get { return SwState == 1; } }
        public bool Running { get { return (Active == 0 ? CdState == 1 : SwState == 1); } }

        public long SwMs()
        {
            long v = SwAcc;
            if (SwState == 1) v += (long)((DateTime.Now - SwStart).TotalMilliseconds);
            return v;
        }

        public long CdLeftMs()
        {
            long v = (long)(CdEnd - DateTime.Now).TotalMilliseconds;
            return v > 0 ? v : 0;
        }

        public int CdTotalSec()
        {
            int h = InH < 0 ? 0 : InH;
            int m = InM < 0 ? 0 : InM;
            int s = InS < 0 ? 0 : InS;
            return h * 3600 + m * 60 + s;
        }
    }

    // ============================================================ 版面
    sealed class TimerGeom
    {
        // 1.5：左右标签与干支模块左右沿对齐 —— 干支 X0=8；「倒」字墨迹左沿落在卡片左框内 20px 处，
        // 「表」字墨迹右沿也落在离右框 20px 处（模块整体还带 10px 右向平移，故右锚点取 490）
        public const float X0 = 8f, X1 = 493f, Shift = 0f;
        public const float LineTop = 7f, LineBot = 33f;
        public const float BtnY = 9.5f, BtnS = 21f, BtnGap = 8f;   // 1.5：三个按钮统一 21px 方框、等距 8px
        public const float BoxH = 26f, BoxTop = 7f;
        public const float SPK = 21f;
        public const float ColonSlot = 12f;

        public bool CdSel;
        public float Lx, RR, HairL, HairR, PnlL, PnlR;
        public float SpkX, Btn1, Btn2, Btn3;      // 喇叭 / ■ / 暂停 / ▶
        public float BoxX;                        // 三格起点
        public float[] BoxL = new float[3];
        public float[] BoxW = new float[3];
        public float DigitL;                      // 数显区左界

        public static TimerGeom Make(Metrics M, bool en, TimerMod t)
        {
            TimerGeom d = new TimerGeom();
            d.CdSel = (t.Active == 0);
            string L = en ? "TIMER" : "倒计时";
            string R = en ? "STOPWATCH" : "秒表";
            Font fL = en ? M.FTimerLabelEn : M.FTimerLabel;
            float lw = Ui.AdvW(fL, L), rw = Ui.AdvW(fL, R);
            d.Lx = X0 + (d.CdSel ? Shift : 0f);
            d.RR = X1 - (d.CdSel ? 0f : Shift);
            d.HairL = d.Lx + lw + 9f;          // 1.5：标签与发丝线间距 15 → 9，把左侧标签整体往左收
            d.HairR = d.RR - rw - 9f;
            d.PnlL = d.HairL + 6f;
            d.PnlR = d.HairR - 6f;

            float bx = d.PnlL + 4f;
            if (d.CdSel)
            {
                d.SpkX = bx;
                // 喇叭墨迹 19px、方块/三角 15px：按墨迹等距微调（实测 11 / 14 → 13 / 13）
                d.Btn1 = bx + SPK + BtnGap + 2f;                 // ■
                d.Btn2 = -1f;
                d.Btn3 = bx + SPK + BtnGap + BtnS + BtnGap + 1f;  // ▶
                d.DigitL = d.Btn3 + BtnS + 12f;
                d.BoxX = d.Btn3 + BtnS + 12f;
            }
            else
            {
                d.SpkX = -1f;
                d.Btn1 = bx;                          // ■
                d.Btn2 = bx + BtnS + BtnGap;          // ❚❚
                d.Btn3 = d.Btn2 + BtnS + BtnGap;      // ▶
                d.DigitL = d.Btn3 + BtnS + 12f;
                d.BoxX = d.DigitL;
            }

            // 三格：去掉「时/分/秒」标注后，格子按剩余宽度摊开
            float avail = d.PnlR - 2f - d.BoxX;
            if (avail < 120f) avail = 120f;
            float rest = avail - ColonSlot * 2f;
            float w0 = rest * 0.29f;
            float w1 = (rest - w0) / 2f;
            d.BoxL[0] = d.BoxX;                    d.BoxW[0] = w0;
            d.BoxL[1] = d.BoxL[0] + w0 + ColonSlot; d.BoxW[1] = w1;
            d.BoxL[2] = d.BoxL[1] + w1 + ColonSlot; d.BoxW[2] = w1;
            return d;
        }
    }

    // ============================================================ 可见部分（静态 / 动态）
    sealed class TimerView
    {
        // ---------- 数字串（':' 冒号；'|' 毫秒撇）。返回总宽
        public static float DigitWidth(string s, float cell, float colonR)
        {
            float w = 0;
            foreach (char ch in s)
            {
                if (ch == ':') w += cell * colonR;
                else if (ch == '|') w += cell * colonR;
                else w += cell;
            }
            return w;
        }

        static void Tick(Graphics g, float x, float top, float slot, float h, Color col)
        {
            using (Pen p = new Pen(col, 2.4f))
            {
                p.StartCap = LineCap.Flat; p.EndCap = LineCap.Flat;
                g.DrawLine(p, x + slot * 0.42f, top + h * 0.10f, x + slot * 0.66f, top + h * 0.34f);
            }
        }

        public static void DrawDigits(Graphics g, string s, Font f, float x, float top, float inkH,
                                      float cell, float colonR, Color col, Color glow, int spread)
        {
            float cx = x;
            string chunk = "";
            foreach (char ch in s)
            {
                if (ch == ':' || ch == '|')
                {
                    if (chunk.Length > 0)
                    {
                        Sgn.Draw(g, chunk, f, cx, top, inkH, cell, colonR, col, glow, spread);
                        cx += chunk.Length * cell;
                        chunk = "";
                    }
                    float slot = cell * colonR;
                    if (ch == ':')
                        Sgn.Draw(g, ":", f, cx, top, inkH, cell, colonR, col, glow, spread);
                    else
                        Tick(g, cx, top, slot, inkH, col);
                    cx += slot;
                }
                else chunk += ch;
            }
            if (chunk.Length > 0)
                Sgn.Draw(g, chunk, f, cx, top, inkH, cell, colonR, col, glow, spread);
        }

        // ---------- 按钮小图形（1.5：全部按 21px 方框光学居中，视觉大小一致）
        static void FillSquare(Graphics g, Color c, float x, float y, float s)
        {
            float k = 15f;
            float ox = x + (s - k) / 2f, oy = y + (s - k) / 2f;
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, ox, oy, k, k);
        }

        static void Pause(Graphics g, Color c, float x, float y, float s)
        {
            float bw = 4.5f, k = 14f;
            float ox = x + (s - k) / 2f;
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillRectangle(b, ox, y + 1.5f, bw, s - 3f);
                g.FillRectangle(b, ox + bw + 5f, y + 1.5f, bw, s - 3f);
            }
        }

        static void Play(Graphics g, Color c, float x, float y, float s, bool dim)
        {
            float k = 15f;
            float ox = x + (s - k) / 2f;
            using (SolidBrush b = new SolidBrush(c))
            {
                PointF[] pts = new PointF[] {
                    new PointF(ox, y + 1.5f), new PointF(ox, y + s - 1.5f), new PointF(ox + k, y + s / 2f) };
                g.FillPolygon(b, pts);
            }
        }

        static void Speaker(Graphics g, Pal P, float x, float y, float s, bool on)
        {
            Color c = on ? P.Acc : P.Dim;
            if (on)
            {
                // 响闹开：喇叭 + 两道声波
                float u = s / 17f;
                float x0 = x + 1.1f * u;              // 腔体+锥形在 21px 方框里光学居中
                using (SolidBrush b = new SolidBrush(c))
                {
                    g.FillRectangle(b, x0 + 1f * u, y + 6.4f * u, 2.8f * u, 4.2f * u);      // 腔体
                    PointF[] cone = new PointF[] {
                        new PointF(x0 + 3.8f * u, y + 6.2f * u),
                        new PointF(x0 + 8.2f * u, y + 1.4f * u),
                        new PointF(x0 + 8.2f * u, y + 15.6f * u),
                        new PointF(x0 + 3.8f * u, y + 10.6f * u) };
                    g.FillPolygon(b, cone);
                }
                using (Pen p = new Pen(c, 1.7f))
                {
                    g.DrawArc(p, x0 + 6.0f * u, y + 5.2f * u, 5.0f * u, 7.0f * u, -55f, 110f);
                    g.DrawArc(p, x0 + 7.6f * u, y + 3.4f * u, 7.4f * u, 10.6f * u, -55f, 110f);
                }
                return;
            }
            // 静音：圆圈 + 斜杠 + 圈内喇叭（按用户给的参考图重画）
            float cx = x + s / 2f, cy = y + s / 2f;
            float r = s / 2f - 0.6f;
            using (SolidBrush b = new SolidBrush(c))
            {
                float bodyW = 3.2f, bodyH = 4.8f;
                float bl = cx - 6.2f;
                g.FillRectangle(b, bl, cy - bodyH / 2f, bodyW, bodyH);                    // 腔体
                PointF[] cone = new PointF[] {
                    new PointF(bl + bodyW, cy - 2.6f),
                    new PointF(cx + 3.6f, cy - 5.2f),
                    new PointF(cx + 3.6f, cy + 5.2f),
                    new PointF(bl + bodyW, cy + 2.6f) };
                g.FillPolygon(b, cone);
            }
            using (Pen p = new Pen(c, 1.5f))
                g.DrawEllipse(p, cx - r, cy - r, r * 2f, r * 2f);
            using (Pen p = new Pen(c, 2.1f))
            {
                float d = r * 0.84f;
                g.DrawLine(p, cx - d, cy + d, cx + d, cy - d);                            // 静音斜杠
            }
        }

        static void Box(Graphics g, Pal P, float x, float y, float w, float h, bool focus)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(P.Dark ? 26 : 20, P.Num)))
                g.FillRectangle(b, x, y, w, h);
            using (Pen p = new Pen(focus ? P.Acc : P.Line, 1f))
                g.DrawRectangle(p, x + 0.5f, y + 0.5f, w - 1f, h - 1f);
        }

        static string TwoDigits(int v) { return v.ToString("00", CultureInfo.InvariantCulture); }

        // ---------- 静态层：标签、发丝线、按钮、输入格
        public static void DrawStatic(Graphics g, Pal P, Metrics M, bool en, TimerMod t)
        {
            TimerGeom d = TimerGeom.Make(M, en, t);
            string L = en ? "TIMER" : "倒计时";
            string R = en ? "STOPWATCH" : "秒表";
            Font fL = en ? M.FTimerLabelEn : M.FTimerLabel;
            // 1.5：两个标签各按自身墨迹高度居中 —— 之前左边用「T」的墨迹高（14px）、右边用自己的（23px），
            // 左右基线差 4.5px，「倒计时」看起来比「秒表」低一截
            Rectangle inkL = Ui.Ink(fL, L);
            Rectangle inkR = Ui.Ink(fL, R);

            using (SolidBrush b = new SolidBrush(t.Active == 0 ? P.Acc : P.Dim))
                Ui.DrawAt(g, L, fL, b, d.Lx, (40f - inkL.Height) / 2f);
            using (SolidBrush b = new SolidBrush(t.Active == 1 ? P.Acc : P.Dim))
                Ui.DrawRightA(g, R, fL, b, d.RR, (40f - inkR.Height) / 2f);

            using (Pen p = new Pen(P.Line, 1f))
            {
                float x1 = (float)Math.Round(d.HairL) + 0.5f;
                float x2 = (float)Math.Round(d.HairR) + 0.5f;
                g.DrawLine(p, x1, TimerGeom.LineTop, x1, TimerGeom.LineBot);
                g.DrawLine(p, x2, TimerGeom.LineTop, x2, TimerGeom.LineBot);
            }

            if (t.Active == 0)
            {
                bool run = (t.CdState == 1 || t.CdState == 2);
                Speaker(g, P, d.SpkX, TimerGeom.BtnY, TimerGeom.BtnS, t.Alarm);
                FillSquare(g, run ? P.Acc : P.Dim, d.Btn1, TimerGeom.BtnY, TimerGeom.BtnS);
                Play(g, run ? P.Dim : P.Acc, d.Btn3, TimerGeom.BtnY, TimerGeom.BtnS, false);

                if (!run) DrawInputs(g, P, M, en, t, d);
            }
            else
            {
                FillSquare(g, (t.SwState == 1 || t.SwState == 2) ? P.Acc : P.Dim, d.Btn1, TimerGeom.BtnY, TimerGeom.BtnS);
                Pause(g, t.SwState == 2 ? P.Acc : P.Dim, d.Btn2, TimerGeom.BtnY, TimerGeom.BtnS);
                Play(g, (t.SwState == 1) ? P.Dim : P.Acc, d.Btn3, TimerGeom.BtnY, TimerGeom.BtnS, false);
            }
        }

        static void DrawInputs(Graphics g, Pal P, Metrics M, bool en, TimerMod t, TimerGeom d)
        {
            Color ghost = Color.FromArgb(138, P.Dim);
            string[] ph = { "0", "00", "00" };
            int[] val = { t.InH, t.InM, t.InS };
            float gh = M.TimerGhostH;
            float top = TimerGeom.BoxTop + (TimerGeom.BoxH - gh) / 2f;
            for (int i = 0; i < 3; i++)
            {
                Box(g, P, d.BoxL[i], TimerGeom.BoxTop, d.BoxW[i], TimerGeom.BoxH, t.Field == i);
                string s;
                bool empty = (val[i] < 0);
                if (i == 0) s = empty ? ph[0] : val[0].ToString(CultureInfo.InvariantCulture);
                else s = empty ? ph[i] : TwoDigits(val[i]);
                float w = s.Length * M.TimerGhostW;
                float x = d.BoxL[i] + (d.BoxW[i] - w) / 2f;   // 格内居中（幽灵数与输入数同位置）
                using (SolidBrush b = new SolidBrush(empty ? ghost : P.Num))
                    Sgn.Draw(g, s, M.FTimerGhost, x, top, gh, M.TimerGhostW, 0.42f,
                             empty ? ghost : P.Num, Color.FromArgb(0, 0, 0, 0), 0);
            }
            // 两处冒号
            float ch = M.TimerGhostH;
            float ctop = TimerGeom.BoxTop + (TimerGeom.BoxH - ch) / 2f;
            using (SolidBrush b = new SolidBrush(P.Dim))
            {
                float cx1 = d.BoxL[0] + d.BoxW[0];
                float cx2 = d.BoxL[1] + d.BoxW[1];
                Ui.DrawCenter(g, ":", M.FTimerGhost, b, cx1 + TimerGeom.ColonSlot / 2f, ctop);
                Ui.DrawCenter(g, ":", M.FTimerGhost, b, cx2 + TimerGeom.ColonSlot / 2f, ctop);
            }
        }

        // ---------- 动态层：数显（每帧重画）+ 焦点光标
        public static void DrawLive(Graphics g, Pal P, Metrics M, bool en, TimerMod t)
        {
            TimerGeom d = TimerGeom.Make(M, en, t);
            string s;
            Color col = P.Num;
            Color glow = P.Glow;
            if (t.Active == 0)
            {
                if (t.CdState != 1 && t.CdState != 2) return;
                long left = (t.CdState == 2) ? 0 : t.CdLeftMs();
                int sec = (int)((left + 999) / 1000);
                int h = sec / 3600, m = (sec / 60) % 60, ss = sec % 60;
                s = h.ToString(CultureInfo.InvariantCulture) + ":" + TwoDigits(m) + ":" + TwoDigits(ss);
                if (t.CdState == 2)
                {
                    col = P.Acc; glow = Color.FromArgb(0, 0, 0, 0);
                    if ((DateTime.Now.Millisecond % 1000) > 500) col = Color.FromArgb(150, P.Acc);
                }
                float w = DigitWidth(s, M.TimerCell, 0.42f);
                float span = d.PnlR - 2f - d.DigitL;
                float x = d.DigitL + Math.Max(0f, (span - w) / 2f);
                float top = (40f - M.TimerInkH) / 2f;
                DrawDigits(g, s, M.FTimerDigits, x, top, M.TimerInkH, M.TimerCell, 0.42f, col, glow, 2);
            }
            else
            {
                if (t.SwState == 0 && t.SwAcc <= 0) return;
                long ms = t.SwMs();
                if (ms > 3599999L) ms = 3599999L;
                int mm = (int)(ms / 60000);
                int ss = (int)((ms / 1000) % 60);
                int mmm = (int)(ms % 1000);
                s = mm.ToString(CultureInfo.InvariantCulture) + "|" + TwoDigits(ss) + "|"
                    + mmm.ToString("000", CultureInfo.InvariantCulture);
                if (t.SwState == 3) { col = P.Acc; glow = Color.FromArgb(0, 0, 0, 0); }
                float w = DigitWidth(s, M.TimerCell, 0.42f);
                float x = Math.Max(d.DigitL, d.PnlR - 2f - w);
                float top = (40f - M.TimerInkH) / 2f;
                DrawDigits(g, s, M.FTimerDigits, x, top, M.TimerInkH, M.TimerCell, 0.42f, col, glow, 2);
            }

            // 输入焦点：闪烁竖线
            if (t.Active == 0 && t.CdState == 0 && t.Field >= 0 && (DateTime.Now.Millisecond % 1000) < 620)
            {
                int[] val = { t.InH, t.InM, t.InS };
                string txt;
                bool empty = (val[t.Field] < 0);
                if (t.Field == 0) txt = empty ? "0" : val[0].ToString(CultureInfo.InvariantCulture);
                else txt = empty ? "00" : TwoDigits(val[t.Field]);
                float w = txt.Length * M.TimerGhostW;
                float x = d.BoxL[t.Field] + d.BoxW[t.Field] - 6f - w - 3f;
                float gh = M.TimerGhostH;
                float top = TimerGeom.BoxTop + (TimerGeom.BoxH - gh) / 2f;
                using (SolidBrush b = new SolidBrush(P.Acc))
                    g.FillRectangle(b, x, top, 1.6f, gh);
            }
        }

        // ---------- 命中：返回动作
        // 1 切倒计时 2 切秒表 3 喇叭 4 ■ 5 暂停 6 ▶ 7/8/9 焦点格
        public static int Hit(Metrics M, bool en, TimerMod t, float x, float y)
        {
            TimerGeom d = TimerGeom.Make(M, en, t);
            float lw = Ui.AdvW(en ? M.FTimerLabelEn : M.FTimerLabel, en ? "TIMER" : "倒计时");
            float rw = Ui.AdvW(en ? M.FTimerLabelEn : M.FTimerLabel, en ? "STOPWATCH" : "秒表");
            if (y >= 2f && y <= 38f)
            {
                if (x >= d.Lx - 8f && x <= d.Lx + lw + 8f) return 1;
                if (x >= d.RR - rw - 8f && x <= d.RR + 8f) return 2;
            }
            if (y < TimerGeom.BtnY - 4f || y > TimerGeom.BtnY + TimerGeom.BtnS + 4f) return 0;
            float half = TimerGeom.BtnS / 2f + 4f;
            if (t.Active == 0)
            {
                if (x >= d.SpkX - half && x <= d.SpkX + TimerGeom.BtnS + half) return 3;
                if (Math.Abs(x - (d.Btn1 + half)) <= half) return 4;
                if (Math.Abs(x - (d.Btn3 + half)) <= half) return 6;
                if (t.CdState == 0)
                    for (int i = 0; i < 3; i++)
                        if (x >= d.BoxL[i] && x <= d.BoxL[i] + d.BoxW[i]) return 7 + i;
            }
            else
            {
                if (Math.Abs(x - (d.Btn1 + half)) <= half) return 4;
                if (Math.Abs(x - (d.Btn2 + half)) <= half) return 5;
                if (Math.Abs(x - (d.Btn3 + half)) <= half) return 6;
            }
            return 0;
        }
    }

    // ============================================================ 响闹
    static class AlarmSound
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern bool PlaySound(IntPtr data, IntPtr hMod, uint flags);

        const uint SND_ASYNC = 0x0001;
        const uint SND_MEMORY = 0x0004;
        const uint SND_NODEFAULT = 0x0002;

        static byte[] _a1, _a2;
        static IntPtr _p1, _p2;
        static GCHandle _h1, _h2;

        static byte[] Load(string res)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(res))
            {
                if (s == null) return null;
                byte[] buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return buf;
            }
        }

        static void Prepare()
        {
            if (_a1 != null) return;
            _a1 = Load("alarm1.wav");
            _a2 = Load("alarm2.wav");
            if (_a1 != null) { _h1 = GCHandle.Alloc(_a1, GCHandleType.Pinned); _p1 = _h1.AddrOfPinnedObject(); }
            if (_a2 != null) { _h2 = GCHandle.Alloc(_a2, GCHandleType.Pinned); _p2 = _h2.AddrOfPinnedObject(); }
        }

        public static void Play(int which)
        {
            try
            {
                Prepare();
                IntPtr p = (which == 1 && _p2 != IntPtr.Zero) ? _p2 : _p1;
                if (p == IntPtr.Zero) return;
                PlaySound(p, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
            catch (Exception) { }
        }

        public static void Stop()
        {
            try { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); } catch (Exception) { }
        }

        /// <summary>退出时释放两块被 Pin 的 wav 缓冲区（PlaySound 需要它们在整个播放期常驻）</summary>
        public static void Release()
        {
            try { Stop(); } catch (Exception) { }
            try { if (_h1.IsAllocated) _h1.Free(); } catch (Exception) { }
            try { if (_h2.IsAllocated) _h2.Free(); } catch (Exception) { }
            _p1 = _p2 = IntPtr.Zero;
            _a1 = _a2 = null;
        }
    }

    // ============================================================ 「时间已到」弹窗
    sealed class TimerPopup : Form
    {
        Timer _loop;
        int _which = 1, _plays;
        bool _dark;
        Rectangle _btn;
        bool _hover;
        Action _onStop;
        float _k = 1f;                       // 1.5：跟随挂件缩放，原先是硬编码 320×152

        public TimerPopup(bool dark, bool en, Action onStop, double scale)
        {
            _dark = dark;
            _onStop = onStop;
            _k = (float)((scale <= 0) ? 1.0 : scale);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            int pw = (int)Math.Round(320 * _k), ph = (int)Math.Round(152 * _k);
            Size = new Size(pw, ph);
            using (GraphicsPath gp = RoundRect(pw, ph, (int)Math.Round(14 * _k)))
                Region = new Region(gp);
            _btn = new Rectangle((int)Math.Round((320 - 116) / 2.0 * _k), (int)Math.Round(104 * _k),
                                 (int)Math.Round(116 * _k), (int)Math.Round(30 * _k));

            _loop = new Timer();
            _loop.Interval = 1700;
            _loop.Tick += new EventHandler(delegate
            {
                _plays++;
                if (_plays > 17) { _loop.Stop(); AlarmSound.Stop(); return; }   // ≈30 秒后自动停，弹窗保留
                AlarmSound.Play(_which);
                _which = (_which == 1) ? 2 : 1;
            });
        }

        public void StartAlarm()
        {
            _plays = 0;
            AlarmSound.Play(_which);
            _which = 2;
            _loop.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = _btn.Contains((int)(e.X / _k), (int)(e.Y / _k));
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_btn.Contains((int)(e.X / _k), (int)(e.Y / _k))) return;
            _loop.Stop();
            AlarmSound.Stop();
            if (_onStop != null) _onStop();
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _loop.Stop();
            AlarmSound.Stop();
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 卡片仍按设计坐标 320×152 画，靠 ScaleTransform 放大到实际尺寸
            if (_k != 1f) e.Graphics.ScaleTransform(_k, _k);
            PaintCard(e.Graphics, new Rectangle(0, 0, 320, 152), false);
        }

        /// <summary>离屏出图：透明底 + 圆角卡片（自检 / 文档用）</summary>
        public void SnapshotSave(string path)
        {
            Bitmap bmp = new Bitmap(320, 152);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    PaintCard(g, new Rectangle(0, 0, 320, 152), true);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            finally { bmp.Dispose(); }
        }

        static GraphicsPath RoundRect(int w, int h, int r)
        {
            GraphicsPath gp = new GraphicsPath();
            gp.AddArc(0, 0, r, r, 180, 90);
            gp.AddArc(w - r, 0, r, r, 270, 90);
            gp.AddArc(w - r, h - r, r, r, 0, 90);
            gp.AddArc(0, h - r, r, r, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        void PaintCard(Graphics g, Rectangle rc, bool transparent)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            Color bg = _dark ? Color.FromArgb(248, 52, 52, 55) : Color.FromArgb(250, 236, 236, 240);
            Color n = _dark ? Color.FromArgb(246, 246, 249) : Color.FromArgb(24, 24, 28);
            Color acc = _dark ? Color.FromArgb(228, 140, 54) : Color.FromArgb(190, 98, 30);
            GraphicsPath gp = RoundRect(rc.Width, rc.Height, 14);
            using (SolidBrush b = new SolidBrush(bg))
            {
                if (transparent) g.FillPath(b, gp);
                else g.FillRectangle(b, rc);
            }
            using (Pen p = new Pen(_dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(80, 0, 0, 0), 1f))
                g.DrawPath(p, gp);
            gp.Dispose();

            bool en = Settings.CurrentLang == "en";
            string t1 = en ? "TIME UP" : "时间已到";
            Font f1 = Ui.Make(en ? Ui.Seg : Ui.Cn, en ? 28f : 32f);
            float w = Ui.Ink(f1, t1).Width;
            using (SolidBrush b = new SolidBrush(acc))
                Ui.DrawAt(g, t1, f1, b, (320f - w) / 2f, 20f);

            Font f2 = Ui.Make(Ui.Cn, 14f);
            string sub = en ? "COUNTDOWN FINISHED" : "倒计时已结束";
            using (SolidBrush b = new SolidBrush(_dark ? Color.FromArgb(190, 190, 198) : Color.FromArgb(96, 96, 104)))
                Ui.DrawCenter(g, sub, f2, b, 160f, 70f);

            using (Pen p = new Pen(_hover ? acc : (_dark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(100, 0, 0, 0)), 1f))
                g.DrawRectangle(p, _btn.X + 0.5f, _btn.Y + 0.5f, _btn.Width - 1f, _btn.Height - 1f);

            Font fb = Ui.Make(Ui.Cn, 17f);
            string lb = en ? "STOP" : "停止";
            float lw = Ui.Ink(fb, lb).Width;
            float total = 14f + 6f + lw;
            float x0 = _btn.X + (_btn.Width - total) / 2f;
            float y0 = _btn.Y + (_btn.Height - Ui.Ink(fb, lb).Height) / 2f;
            using (SolidBrush b = new SolidBrush(n))
            {
                g.FillRectangle(b, x0, y0 + 2f, 12f, 13f);      // ■
                Ui.DrawAt(g, lb, fb, b, x0 + 14f + 6f, y0);
            }
        }
    }
}
