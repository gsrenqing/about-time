// About Time 1.5 —— 干支计时模块：四柱推算 / 模块绘制 / 双击换算框
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AboutTime
{
    // ============================================================ 干支算法
    static class GzCore
    {
        public static readonly string[] Gan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        public static readonly string[] Zhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
        public static readonly string[] Animal = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
        public static readonly string[] KeName = { "初刻", "壹刻", "贰刻", "叁刻" };

        /// <summary>儒略日数（正午制），只用于日柱连续计数</summary>
        public static long Jdn(DateTime d)
        {
            int y = d.Year, m = d.Month, day = d.Day;
            int a = (14 - m) / 12;
            int yy = y + 4800 - a;
            int mm = m + 12 * a - 3;
            return day + (153 * mm + 2) / 5 + 365 * yy + yy / 4 - yy / 100 + yy / 400 - 32045;
        }

        static int IndexOf(int gan, int zhi)
        {
            for (int i = 0; i < 60; i++)
                if (i % 10 == gan && i % 12 == zhi) return i;
            return 0;
        }

        /// <summary>日柱序号 0=甲子（子初 23:00 换日）</summary>
        public static int DayIdx(DateTime d)
        {
            DateTime day = d.Date;
            if (d.Hour >= 23) day = day.AddDays(1);
            long j = Jdn(day);
            long v = (j + 49) % 60;
            if (v < 0) v += 60;
            return (int)v;
        }

        public static string DayName(DateTime d)
        {
            int i = DayIdx(d);
            return Gan[i % 10] + Zhi[i % 12];
        }

        /// <summary>立春时刻（年柱换年点）</summary>
        public static DateTime LiChun(int year) { return SolarTerms.Moment(year, 2); }

        /// <summary>年柱序号（立春换年）</summary>
        public static int YearIdx(DateTime d)
        {
            int y = d.Year;
            if (d < LiChun(y)) y--;
            int g = ((y - 4) % 10 + 10) % 10;
            int z = ((y - 4) % 12 + 12) % 12;
            return IndexOf(g, z);
        }

        public static string YearName(DateTime d)
        {
            int i = YearIdx(d);
            return Gan[i % 10] + Zhi[i % 12];
        }

        public static string AnimalOf(DateTime d)
        {
            return Animal[YearIdx(d) % 12];
        }

        // 十二「节」：小寒(丑) 立春(寅) 惊蛰(卯) 清明(辰) 立夏(巳) 芒种(午)
        //           小暑(未) 立秋(申) 白露(酉) 寒露(戌) 立冬(亥) 大雪(子)
        static readonly int[] JieN = { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22 };
        static readonly int[] JieZhi = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0 };

        /// <summary>月柱序号（按节气换月）</summary>
        public static int MonthIdx(DateTime d)
        {
            DateTime best = DateTime.MinValue;
            int branch = 2;
            for (int yy = d.Year - 1; yy <= d.Year + 1; yy++)
            {
                for (int k = 0; k < JieN.Length; k++)
                {
                    DateTime t = SolarTerms.Moment(yy, JieN[k]);
                    if (t <= d && t > best) { best = t; branch = JieZhi[k]; }
                }
            }
            int yi = YearIdx(d);
            int yearGan = yi % 10;
            int m = ((branch - 2) + 12) % 12;      // 寅月 = 0
            int stem = (2 * yearGan + 2 + m) % 10;
            return IndexOf(stem, branch);
        }

        public static string MonthName(DateTime d)
        {
            int i = MonthIdx(d);
            return Gan[i % 10] + Zhi[i % 12];
        }

        /// <summary>时支序号（子=0）</summary>
        public static int HourZhi(DateTime d) { return ((d.Hour + 1) / 2) % 12; }

        public static bool HourZheng(DateTime d) { return d.Hour % 2 == 0; }

        public static string HourName(DateTime d)
        {
            int z = HourZhi(d);
            int dayGan = DayIdx(d) % 10;
            int stem = (2 * dayGan + z) % 10;
            return Gan[stem] + Zhi[z];
        }

        /// <summary>时辰字样：辰初 / 午正</summary>
        public static string ShiChen(DateTime d) { return Zhi[HourZhi(d)] + (HourZheng(d) ? "正" : "初"); }

        /// <summary>
        /// 时辰内第几刻。96 刻制：一天 96 刻、每刻 15 分钟；初/正各占 1 小时 = 4 刻，
        /// 每小时内再分 初 / 壹 / 贰 / 叁。故 0:00 初刻、0:15 壹刻、0:30 贰刻、0:45 叁刻。
        /// 1.5.1 修正：原先把「正时」额外 +60 分钟再按 30 分钟取整，0:15 被算成贰刻、0:45 也停在叁刻。
        /// </summary>
        public static int KeOf(DateTime d)
        {
            int k = d.Minute / 15;
            if (k < 0) k = 0; if (k > 3) k = 3;
            return k;
        }

        public static string KeNameOf(DateTime d) { return KeName[KeOf(d)]; }

        /// <summary>
        /// 五更：戌=一更(19-21) 亥=二更(21-23) 子=三更(23-01) 丑=四更(01-03) 寅=五更(03-05)；
        /// 其余时辰（卯～酉，05:00-19:00）无更，返回空串。
        /// </summary>
        public static string GengOf(DateTime d)
        {
            switch (HourZhi(d))
            {
                case 10: return "一更";
                case 11: return "二更";
                case 0: return "三更";
                case 1: return "四更";
                case 2: return "五更";
                default: return "";
            }
        }

        /// <summary>百刻制：一日 100 刻，每刻 14.4 分钟，返回 1..100</summary>
        public static int BaiKe(DateTime d)
        {
            int mins = d.Hour * 60 + d.Minute;
            int k = (int)(mins / 14.4) + 1;
            if (k < 1) k = 1; if (k > 100) k = 100;
            return k;
        }

        /// <summary>八字四柱：年 月 日 时</summary>
        public static string BaZi(DateTime d)
        {
            return YearName(d) + " " + MonthName(d) + " " + DayName(d) + " " + HourName(d);
        }
    }

    // ============================================================ 模块绘制
    static class GzView
    {
        public const float X0 = 8f, X1 = 512f;
        public const float LineTop = 7f, LineBot = 33f;

        // 滚动字幕的缓存（同一分钟内不重算）
        static string _sig = "";
        static List<Run> _runs = new List<Run>();
        static float _w;

        sealed class Run
        {
            public string Text;
            public bool Small;      // 小字（年 / 月 / 日 / 百刻）
            public int Tone;        // 0 主字色 / 1 标橙 / 2 次级灰
            public Run(string t, bool sm, int tone) { Text = t; Small = sm; Tone = tone; }
        }

        static void Build(DateTime now, Metrics M)
        {
            string sig = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            if (sig == _sig && _runs.Count > 0) return;
            _sig = sig;
            _runs.Clear();
            _runs.Add(new Run(GzCore.YearName(now), false, 0));
            Dot();
            _runs.Add(new Run(GzCore.AnimalOf(now), false, 0));
            _runs.Add(new Run("年", true, 1));
            Dot();
            _runs.Add(new Run(GzCore.MonthName(now), false, 0));
            _runs.Add(new Run("月", true, 1));
            Dot();
            _runs.Add(new Run(GzCore.DayName(now), false, 0));
            _runs.Add(new Run("日", true, 1));
            Dot();
            _runs.Add(new Run(GzCore.HourName(now), false, 0));
            _runs.Add(new Run("时", true, 1));
            // 1.5.1：入夜后在最尾补「一更…五更」；白天（卯～酉）没有更次，不添字
            string geng = GzCore.GengOf(now);
            if (geng.Length > 0)
            {
                Dot();
                _runs.Add(new Run(geng, true, 1));
            }

            float w = 0f;
            foreach (Run r in _runs)
            {
                if (r == null) { w += 16f; continue; }
                Font f = r.Small ? M.FGzUnit : M.FGzMarq;
                // 小字（年/月/日/时/更）前面留 3px 字距，否则紧贴前一个干支字
                w += (r.Small ? SmallGap : 0f) + Ui.Ink(f, r.Text).Width;
            }
            _w = w;
        }

        /// <summary>小字（年/月/日/时/更）与前一字的字距</summary>
        const float SmallGap = 3f;

        static void Dot() { _runs.Add(null); }

        /// <summary>左侧固定段（时刻 + 冒号 + 刻）与发丝线的右端 x</summary>
        public static float FixedRight(Metrics M, DateTime now)
        {
            float w1 = SegWidth(M.FGzTime, GzCore.ShiChen(now));
            float w2 = SegWidth(M.FGzTime, GzCore.KeNameOf(now));
            return X0 + w1 + ColonW + w2;
        }

        /// <summary>1.6：段内逐字绘制，字距统一 3px（原先整串画，字距由字体自带，叁刻只剩 2px 会粘连）</summary>
        const float TimeGap = 3f;

        /// <summary>冒号槽宽：左 6px + 冒号 4px + 右 6px，冒号正好落在「时刻」两段的水平正中</summary>
        const float ColonW = 16f;

        static float SegWidth(Font f, string s)
        {
            if (s == null || s.Length == 0) return 0f;
            float w = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                w += Ui.Ink(f, s.Substring(i, 1)).Width;
                if (i < s.Length - 1) w += TimeGap;
            }
            return w;
        }

        /// <summary>逐字画一段，返回画完后的游标（以墨迹左沿计）</summary>
        static float DrawSeg(Graphics g, Font f, Brush b, string s, float x, float baseY)
        {
            for (int i = 0; i < s.Length; i++)
            {
                string ch = s.Substring(i, 1);
                Rectangle ink = Ui.Ink(f, ch);
                g.DrawString(ch, f, b, x - ink.X, baseY, StringFormat.GenericTypographic);
                x += ink.Width;
                if (i < s.Length - 1) x += TimeGap;
            }
            return x;
        }

        public static void DrawStatic(Graphics g, Pal P, Metrics M, bool en, DateTime now)
        {
            // 左段：时辰 + 冒号 + 刻 共用同一条文字基线。
            // 1.5：原先两段各自按「墨迹顶」对齐，而「酉正」墨迹 31 高、「贰刻」27 高，
            // 顶对齐的结果是「贰刻」整块往下掉 4~5px，看着像上下错位。
            string shi = GzCore.ShiChen(now);
            string ke = GzCore.KeNameOf(now);
            Rectangle iA = Ui.Ink(M.FGzTime, shi);
            Rectangle iB = Ui.Ink(M.FGzTime, ke);
            int bandTop = Math.Min(iA.Y, iB.Y);
            int bandBot = Math.Max(iA.Y + iA.Height, iB.Y + iB.Height);
            float baseY = (40f - (bandBot - bandTop)) / 2f - bandTop;    // DrawString 原点 = 基线
            float top = baseY + bandTop;                                 // 整段墨迹带上沿
            float h = bandBot - bandTop;
            using (SolidBrush nb = new SolidBrush(P.Acc))
            {
                // 1.6：逐字画，字距固定 3px；游标以「墨迹左沿」计
                float x = DrawSeg(g, M.FGzTime, nb, shi, X0, baseY);
                // 手绘冒号：点阵字库的「:」在这个字号几乎看不见；
                // 左右各留 6px，冒号 4×4 落在正中
                float k = 4f, kg = 6f;
                g.FillRectangle(nb, x + kg, top + h * 0.30f, k, k);
                g.FillRectangle(nb, x + kg, top + h * 0.62f, k, k);
                x += kg * 2f + k;
                DrawSeg(g, M.FGzTime, nb, ke, x, baseY);
            }
            float hx = (float)Math.Round(FixedRight(M, now) + 14f) + 0.5f;
            using (Pen p = new Pen(P.Line, 1f))
                g.DrawLine(p, hx, LineTop, hx, LineBot);

            // 右侧干支串：整串放得下，一次画完，不再滚屏（1.5 定稿）
            Build(now, M);
            float left = hx + 10f;
            float right = X1 - 2f;
            if (right - left < 60f) return;
            RectangleF clip = new RectangleF(left, 2f, right - left, 36f);
            GraphicsState st = g.Save();
            g.SetClip(clip, CombineMode.Replace);
            DrawRuns(g, M, P, left, 29f, 30f);
            g.Restore(st);
        }


        static void DrawRuns(Graphics g, Metrics M, Pal P, float x0, float bottomBig, float bottomSmall)
        {
            float x = x0;
            using (SolidBrush nb = new SolidBrush(P.Num))
            using (SolidBrush ab = new SolidBrush(P.Acc))
            using (SolidBrush db = new SolidBrush(P.Dim))
            {
                foreach (Run r in _runs)
                {
                    if (r == null)
                    {
                        // 中点分隔：圆点，居中于大字行
                        float dot = 4.5f;
                        g.FillEllipse(db, x + 6f, bottomBig - 12f, dot, dot);
                        x += 16f;
                        continue;
                    }
                    Font f = r.Small ? M.FGzUnit : M.FGzMarq;
                    Rectangle ink = Ui.Ink(f, r.Text);
                    float by = r.Small ? bottomSmall : bottomBig;
                    float top = by - ink.Height - ink.Y;
                    Brush b = (r.Tone == 1) ? ab : (r.Tone == 2 ? db : nb);
                    // 小字前面让出 3px 字距（与 Build 的宽度累计保持一致）
                    float gap = r.Small ? SmallGap : 0f;
                    g.DrawString(r.Text, f, b, x + gap - ink.X, top, StringFormat.GenericTypographic);
                    x += gap + ink.Width;
                }
            }
        }

        /// <summary>字幕总宽（自检 / 出图日志用）</summary>
        public static float MarqueeWidth(DateTime now, Metrics M) { Build(now, M); return _w; }
    }

    // ============================================================ 双击换算框
    sealed class GzPopup : Form
    {
        readonly Pal _p;
        readonly bool _en;
        TextBox _y, _m, _d, _h, _mi;
        Label _out;
        Font _f, _fs;

        public GzPopup(Pal p, bool en, DateTime init)
        {
            _p = p; _en = en;
            // 1.5：正文 16 → 24、次要 15 → 22.5（+50%），标题与 × 同步放大
            _f = new Font("Microsoft YaHei UI", 24f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fs = new Font("Microsoft YaHei UI", 22.5f, FontStyle.Regular, GraphicsUnit.Pixel);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            const int W = 500;
            BackColor = p.Dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(246, 246, 246);
            ForeColor = p.Num;
            DoubleBuffered = true;

            Label ttl = new Label();
            ttl.Text = _en ? "GANZHI CONVERSION" : "干支换算";
            ttl.Font = new Font("Microsoft YaHei UI", 27f, FontStyle.Regular, GraphicsUnit.Pixel);
            ttl.ForeColor = p.Acc;
            ttl.BackColor = Color.Transparent;
            ttl.SetBounds(26, 20, 360, 40);
            Controls.Add(ttl);

            Button cls = new Button();
            cls.Text = "×";
            cls.FlatStyle = FlatStyle.Flat;
            cls.FlatAppearance.BorderSize = 0;
            cls.BackColor = Color.Transparent;
            cls.ForeColor = p.Dim;
            cls.Font = new Font("Microsoft YaHei UI", 27f, FontStyle.Regular, GraphicsUnit.Pixel);
            cls.SetBounds(W - 22 - 38, 16, 38, 38);
            cls.Click += new EventHandler(delegate { Close(); });
            Controls.Add(cls);

            // 1.5：输入框分两行 —— 上行 年 / 月 / 日，下行 时 / 分；
            // 标橙的「年 月 日 时 分」跟在各自输入框之后（不在框前）
            int lx = 26, labW = 30, labGap = 8, boxH = 44, fGap = 26;
            int yearW = 110, boxW = 80;
            int y1 = 74, x = lx;

            _y = MakeBox(x, y1, yearW, boxH, init.Year.ToString(CultureInfo.InvariantCulture));
            x += yearW + labGap;
            AddLab("年", x, y1, labW);
            x += labW + fGap;

            _m = MakeBox(x, y1, boxW, boxH, init.Month.ToString(CultureInfo.InvariantCulture));
            x += boxW + labGap;
            AddLab("月", x, y1, labW);
            x += labW + fGap;

            _d = MakeBox(x, y1, boxW, boxH, init.Day.ToString(CultureInfo.InvariantCulture));
            x += boxW + labGap;
            AddLab("日", x, y1, labW);

            // TextBox 单行高度由字体决定，SetBounds 的 h 只是给个上限；实际高度要读回来再排下一行
            int bh = _y.Height;
            int y2 = y1 + bh + 16;
            x = lx;

            _h = MakeBox(x, y2, boxW, boxH, init.Hour.ToString(CultureInfo.InvariantCulture));
            x += boxW + labGap;
            AddLab("时", x, y2, labW);
            x += labW + fGap;

            _mi = MakeBox(x, y2, boxW, boxH, init.Minute.ToString(CultureInfo.InvariantCulture));
            x += boxW + labGap;
            AddLab("分", x, y2, labW);

            // 「换算」放在上半部输入区与下半部结果区之间（居中）
            int goW = 168, goH = 48, goY = y2 + bh + 20;
            Button go = new Button();
            go.Text = _en ? "Convert" : "换算";
            go.FlatStyle = FlatStyle.Flat;
            go.FlatAppearance.BorderColor = p.Line;
            go.BackColor = p.Acc;
            go.ForeColor = Color.White;
            go.Font = _f;
            go.SetBounds((W - goW) / 2, goY, goW, goH);
            go.Click += new EventHandler(delegate { Calc(); });
            Controls.Add(go);

            int outY = goY + goH + 20;
            int lineH = (int)Math.Ceiling(_fs.Size * 1.5f);
            int H = outY + lineH * 7 + 24;
            ClientSize = new Size(W, H);

            _out = new Label();
            _out.Font = _fs;
            _out.ForeColor = p.Num;
            _out.BackColor = Color.Transparent;
            _out.SetBounds(26, outY, W - 52, H - outY - 18);
            Controls.Add(_out);

            AcceptButton = go;
            KeyPreview = true;
            Calc();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }

        TextBox MakeBox(int x, int y, int w, int h, string v)
        {
            TextBox t = new TextBox();
            t.Text = v;
            t.Font = _f;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = _p.Dark ? Color.FromArgb(40, 40, 40) : Color.White;
            t.ForeColor = _p.Num;
            t.SetBounds(x, y, w, h);
            t.TextAlign = HorizontalAlignment.Center;
            Controls.Add(t);
            return t;
        }

        void AddLab(string s, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = s;
            l.Font = _f;
            l.ForeColor = _p.Acc;
            l.BackColor = Color.Transparent;
            // 与输入框垂直居中（24px 字，框高约 40）
            l.SetBounds(x, y + 4, w, 36);
            Controls.Add(l);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(_p.Dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0), 1f))
            using (GraphicsPath gp = RoundP(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f), 12f))
                e.Graphics.DrawPath(p, gp);
        }

        static GraphicsPath RoundP(RectangleF r, float rad)        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        static int Num(TextBox t, int lo, int hi, int def)
        {
            int v;
            if (!int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return def;
            if (v < lo) v = lo; if (v > hi) v = hi;
            return v;
        }

        void Calc()
        {
            int y = Num(_y, 1900, 2100, DateTime.Now.Year);
            int mo = Num(_m, 1, 12, DateTime.Now.Month);
            int da = Num(_d, 1, DateTime.DaysInMonth(y, mo), DateTime.Now.Day);
            int hh = Num(_h, 0, 23, DateTime.Now.Hour);
            int mi = Num(_mi, 0, 59, 0);
            DateTime d = new DateTime(y, mo, da, hh, mi, 0);

            string nl = "";
            LunarInfo li = LunarCal.FromSolar(d);
            if (li != null) nl = li.YearName + " " + li.MonthName + "月" + li.DayName;

            string s = "";
            s += (_en ? "Date  " : "公历  ") + d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\r\n";
            if (nl.Length > 0) s += (_en ? "Lunar " : "农历  ") + nl + "\r\n";
            s += (_en ? "Year  " : "干支年  ") + GzCore.YearName(d) + "（" + GzCore.AnimalOf(d) + "）\r\n";
            s += (_en ? "Month " : "月柱  ") + GzCore.MonthName(d) + "\r\n";
            s += (_en ? "Day   " : "日柱  ") + GzCore.DayName(d) + "\r\n";
            s += (_en ? "Hour  " : "时柱  ") + GzCore.HourName(d)
                 + "（" + GzCore.ShiChen(d) + GzCore.KeNameOf(d) + "）\r\n";
            s += (_en ? "BaZi  " : "八字  ") + GzCore.BaZi(d);
            _out.Text = s;
        }
        /// <summary>自检出图：把换算框离屏画一张（不弹到屏幕上）</summary>
        public void SnapshotSave(string path)
        {
            try
            {
                // DrawToBitmap 对「从未显示过」的窗体只画背景，子控件不出图：
                // 先移到虚拟屏外的位置 Show 一次，布局与绘制都跑过，再抓图关掉。
                Location = new Point(-30000, -30000);
                Show();
                Application.DoEvents();
                PerformLayout();
                using (Bitmap bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception) { }
            finally
            {
                try { Hide(); } catch (Exception) { }
            }
        }
    }
}
