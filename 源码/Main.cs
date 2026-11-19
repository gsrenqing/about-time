// About Time 1.0  ——  桌面时钟挂件（本地时间 + 世界时间滚屏）
// 目标框架 .NET Framework 4.8 / C# 5，单文件 EXE，双层窗口（UpdateLayeredWindow 逐像素 alpha）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AboutTime
{
    // ============================================================ 调色板
    sealed class Pal
    {
        public Color Card, Edge, Num, Sec, Acc, Dim, Glow, Line, WeekDim, AccBright;
        public Color Solid;            // 不透明主色（弹窗 / 日历卡这类不吃透明度的底）
        public bool Dark;              // 「深色系」：碳灰 与 藏青 都算，文字配色一律沿用碳灰
        public int Style;              // 0 碳灰 / 1 雪白 / 2 藏青

        public static Pal Make(bool dark, int opacityPct)
        {
            return Make(dark ? 0 : 1, opacityPct);
        }

        public static Pal Make(int style, int opacityPct)
        {
            int a = (int)Math.Round(255.0 * opacityPct / 100.0);
            if (a < 26) a = 26; if (a > 255) a = 255;
            Pal p = new Pal();
            p.Style = style;
            p.Dark = (style != 1);
            if (style == 2)
            {
                // 1.6 藏青：底色换成 #162240 一档的深藏青，其余文字色 / 橙 / 灰阶全部照抄碳灰，
                // 只有卡底、描边、柔光、发丝线跟着底色走（要求「文字颜色一律沿用碳黑的模式不变」）
                p.Card = Color.FromArgb(a, 22, 34, 64);
                p.Solid = Color.FromArgb(255, 22, 34, 64);
                p.Edge = Color.FromArgb(40, 176, 198, 250);
                p.Num = Color.FromArgb(255, 246, 246, 246);
                p.Sec = Color.FromArgb(255, 208, 208, 208);
                p.Acc = Color.FromArgb(255, 255, 136, 0);
                p.Dim = Color.FromArgb(255, 158, 158, 162);
                p.WeekDim = Color.FromArgb(255, 176, 176, 180);
                p.AccBright = Color.FromArgb(255, 255, 170, 58);
                p.Glow = Color.FromArgb(36, 132, 168, 244);
                p.Line = Color.FromArgb(42, 170, 196, 246);
                return p;
            }
            if (style == 0)
            {
                // 1.5：碳灰底压到 20/20/20，主字 246/246/246，橙统一 255/136/0，灰阶整体提亮以提高对比
                p.Card = Color.FromArgb(a, 20, 20, 20);
                p.Solid = Color.FromArgb(255, 20, 20, 20);
                p.Edge = Color.FromArgb(30, 255, 255, 255);
                p.Num = Color.FromArgb(255, 246, 246, 246);
                p.Sec = Color.FromArgb(255, 208, 208, 208);
                p.Acc = Color.FromArgb(255, 255, 136, 0);
                p.Dim = Color.FromArgb(255, 158, 158, 162);
                p.WeekDim = Color.FromArgb(255, 176, 176, 180);   // 未选中星期：灰度回补，仍明显低于当日橙与白色时分
                p.AccBright = Color.FromArgb(255, 255, 170, 58);  // 当日星期：更亮
                p.Glow = Color.FromArgb(34, 210, 218, 232);
                p.Line = Color.FromArgb(38, 255, 255, 255);
            }
            else
            {
                p.Card = Color.FromArgb(a, 246, 246, 246);
                p.Solid = Color.FromArgb(255, 246, 246, 246);
                p.Edge = Color.FromArgb(32, 255, 255, 255);
                p.Num = Color.FromArgb(255, 1, 1, 1);
                p.Sec = Color.FromArgb(255, 40, 40, 40);
                // 1.5.1 雪白专属橙：246 底上 255,136,0 偏刺眼，改压到 200,96,0（只雪白这一档改，
                // 碳灰 / 藏青仍是 255,136,0）
                p.Acc = Color.FromArgb(255, 200, 96, 0);
                p.Dim = Color.FromArgb(255, 84, 84, 88);
                p.WeekDim = Color.FromArgb(255, 122, 122, 128);
                p.AccBright = Color.FromArgb(255, 214, 110, 0);
                p.Glow = Color.FromArgb(0, 0, 0, 0);
                p.Line = Color.FromArgb(44, 0, 0, 0);
            }
            return p;
        }
    }


    // ============================================================ 字库与墨迹测量
    static class Ui
    {
        static PrivateFontCollection _pSeg, _pDot, _pCn, _pQiji, _pSong;
        static List<IntPtr> _heap = new List<IntPtr>();
        public static FontFamily Seg, DotF, Cn;
        public static FontFamily Qiji;      // 2.0：qiji-combo 子集（中文皮肤的大字与右侧三列）
        public static FontFamily Song;      // 2.0：思源宋（Noto Serif SC）子集，Regular + Bold 同一族

        static Bitmap _scratchBmp = new Bitmap(8, 8);
        static Graphics _scratch = Graphics.FromImage(_scratchBmp);
        static Dictionary<string, Rectangle> _ink = new Dictionary<string, Rectangle>();
        static Dictionary<string, float> _adv = new Dictionary<string, float>();

        static FontFamily LoadFam(string res, out PrivateFontCollection pfc)
        {
            pfc = new PrivateFontCollection();
            AddTo(pfc, res);
            return pfc.Families[0];
        }

        /// <summary>把一个内嵌字库读进指定的字体集合（内存块登记在 _heap 里，进程内不再释放）</summary>
        static void AddTo(PrivateFontCollection pfc, string res)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(res))
            {
                if (s == null) throw new ArgumentException("缺资源: " + res);
                byte[] buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                IntPtr p = Marshal.AllocHGlobal(buf.Length);
                Marshal.Copy(buf, 0, p, buf.Length);
                _heap.Add(p);
                pfc.AddMemoryFont(p, buf.Length);
            }
        }

        /// <summary>2.0：Regular 与 Bold 是两个文件、同一个族名，装进同一个集合才会合成一族两式</summary>
        static FontFamily LoadFamPair(string resR, string resB, out PrivateFontCollection pfc)
        {
            pfc = new PrivateFontCollection();
            AddTo(pfc, resR);
            AddTo(pfc, resB);
            foreach (FontFamily fam in pfc.Families)
                if (fam.IsStyleAvailable(FontStyle.Regular) && fam.IsStyleAvailable(FontStyle.Bold))
                    return fam;
            return pfc.Families[0];
        }

        public static void Load()
        {
            Seg = LoadFam("tfb.ttf", out _pSeg);
            DotF = LoadFam("clock.ttf", out _pDot);
            Cn = LoadFam("aura.ttf", out _pCn);
            Qiji = LoadFam("qiji-sub.ttf", out _pQiji);
            Song = LoadFamPair("song-reg.ttf", "song-bold.ttf", out _pSong);
            _scratch.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        public static Font Make(FontFamily fam, float px)
        {
            return new Font(fam, px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        /// <summary>2.0：粗体（思源宋 Bold / 同族有 Bold 式时生效，否则退回常规）</summary>
        public static Font MakeBold(FontFamily fam, float px)
        {
            try
            {
                if (fam != null && fam.IsStyleAvailable(FontStyle.Bold))
                    return new Font(fam, px, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch (Exception) { }
            return Make(fam, px);
        }

        /// <summary>返回文字实际墨迹包围盒（相对 DrawString 原点）</summary>
        public static Rectangle Ink(Font f, string s)
        {
            string k = f.FontFamily.Name + "|" + f.Size.ToString("0.###") + "|" + f.Style + "|" + s;
            Rectangle cached;
            if (_ink.TryGetValue(k, out cached)) return cached;

            SizeF sz = _scratch.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);
            // 点阵字库的 MeasureString 比实际步进窄，留 12px 会切掉尾部字形 → 留 56px 再扫描
            int bw = Math.Max(8, (int)Math.Ceiling(sz.Width) + 56);
            int bh = Math.Max(8, (int)Math.Ceiling(sz.Height) + 56);
            int minx = int.MaxValue, miny = int.MaxValue, maxx = -1, maxy = -1;

            using (Bitmap b = new Bitmap(bw, bh, PixelFormat.Format32bppArgb))
            {
                using (Graphics gg = Graphics.FromImage(b))
                {
                    gg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    gg.Clear(Color.Transparent);
                    gg.DrawString(s, f, Brushes.White, 12, 12, StringFormat.GenericTypographic);
                }
                BitmapData d = b.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int[] px = new int[bw * bh];
                    Marshal.Copy(d.Scan0, px, 0, px.Length);
                    for (int y = 0; y < bh; y++)
                    {
                        int row = y * bw;
                        for (int x = 0; x < bw; x++)
                        {
                            int a = (px[row + x] >> 24) & 0xFF;
                            if (a > 8)
                            {
                                if (x < minx) minx = x;
                                if (x > maxx) maxx = x;
                                if (y < miny) miny = y;
                                if (y > maxy) maxy = y;
                            }
                        }
                    }
                }
                finally { b.UnlockBits(d); }
            }
            if (maxx < 0) { minx = 12; miny = 12; maxx = 12; maxy = 12; }
            Rectangle r = new Rectangle(minx - 12, miny - 12, maxx - minx + 1, maxy - miny + 1);
            _ink[k] = r;
            return r;
        }

        /// <summary>字符串的排布宽度（GDI+ 实际步进），比例字体下比墨迹宽，右对齐用它才不越界</summary>
        public static float AdvW(Font f, string s)
        {
            // v2.1 修（审阅 P3-6）：缓存键原来只有「族|字号|字符串」，而 Ink() 的键是含 f.Style 的。
            // 同一族同号但字重不同的两个 Font 会串用宽度（Bold 一般更宽），右对齐可能差几像素。
            string k = f.FontFamily.Name + "|" + f.Size.ToString("0.###") + "|" + f.Style + "|" + s;
            float v;
            if (_adv.TryGetValue(k, out v)) return v;
            v = _scratch.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width;
            _adv[k] = v;
            return v;
        }

        /// <summary>按排布宽度右对齐（右侧不会因为墨迹比步进窄而溢出）</summary>
        public static void DrawRightA(Graphics g, string s, Font f, Brush b, float right, float top)
        {
            // 1.5：原先只减 AdvW，忽略了墨迹左侧偏移 ink.X —— 实测「秒表」右沿冲出去约 7px，
            // 几乎贴到窗口边上。改成墨迹右沿与排布宽右沿取更靠左者，任何字都不会越界
            Rectangle ink = Ink(f, s);
            float xInk = right - ink.Width - ink.X;      // 墨迹右沿正好落在 right
            float xAdv = right - AdvW(f, s);             // 排布宽右沿落在 right
            float x = Math.Min(xInk, xAdv);
            g.DrawString(s, f, b, x, top - ink.Y, StringFormat.GenericTypographic);
        }

        /// <summary>二分找出墨迹高度 ≈ targetH 的字号（像素），再在附近细化挑最接近者</summary>
        public static float FitH(FontFamily fam, string s, float targetH)
        {
            float lo = 4f, hi = 640f;
            for (int i = 0; i < 24; i++)
            {
                float mid = (lo + hi) / 2f;
                float h = Ink(Make(fam, mid), s).Height;
                if (h < targetH) lo = mid; else hi = mid;
            }
            float best = lo, bestErr = float.MaxValue;
            for (int k = -8; k <= 8; k++)
            {
                float cand = lo + k * 0.25f;
                if (cand < 2f) continue;
                float h = Ink(Make(fam, cand), s).Height;
                float err = Math.Abs(h - targetH);
                if (err < bestErr - 0.01f) { bestErr = err; best = cand; }
            }
            return Math.Max(2f, best);
        }

        public static void DrawAt(Graphics g, string s, Font f, Brush b, float x, float y)
        {
            Rectangle ink = Ink(f, s);
            g.DrawString(s, f, b, x - ink.X, y - ink.Y, StringFormat.GenericTypographic);
        }

        public static void DrawRight(Graphics g, string s, Font f, Brush b, float right, float top)
        {
            Rectangle ink = Ink(f, s);
            g.DrawString(s, f, b, right - ink.Width - ink.X, top - ink.Y, StringFormat.GenericTypographic);
        }

        public static void DrawCenter(Graphics g, string s, Font f, Brush b, float cx, float top)
        {
            Rectangle ink = Ink(f, s);
            g.DrawString(s, f, b, cx - ink.Width / 2f - ink.X, top - ink.Y, StringFormat.GenericTypographic);
        }
    }

    // ============================================================ 七段数显（固定格 + 底边对齐）
    static class Sgn
    {
        static float PassWidth(Font f, string s, float cellW, float colonRatio)
        {
            float w = 0;
            foreach (char ch in s) w += (ch == ':') ? cellW * colonRatio : cellW;
            return w;
        }

        public static float Width(Font f, string s, float cellW, float colonRatio)
        {
            return PassWidth(f, s, cellW, colonRatio);
        }

        static void Pass(Graphics g, string s, Font f, float x0, float inkTop, float inkH, float cellW, float colonRatio, Brush br)
        {
            float x = x0;
            foreach (char ch in s)
            {
                string one = ch.ToString();
                Rectangle ink = Ui.Ink(f, one);
                if (ch == ':')
                {
                    float cw = cellW * colonRatio;
                    float dx = (cw - ink.Width) / 2f;          // 冒号在自身格内居中
                    float cy = inkTop + (inkH - ink.Height) / 2f;
                    g.DrawString(one, f, br, x + dx - ink.X, cy - ink.Y, StringFormat.GenericTypographic);
                    x += cw;
                }
                else
                {
                    float dx = (cellW - ink.Width) / 2f;       // 字形墨迹在固定格内水平居中（"1" 不再贴左）
                    float cy = inkTop + inkH - ink.Height;     // 底边对齐
                    g.DrawString(one, f, br, x + dx - ink.X, cy - ink.Y, StringFormat.GenericTypographic);
                    x += cellW;
                }
            }
        }

        /// <summary>先绘柔光再绘字形；返回绘制结束时的 x</summary>
        public static float Draw(Graphics g, string s, Font f, float x0, float inkTop, float inkH,
                                 float cellW, float colonRatio, Color col, Color glow, int glowSpread)
        {
            if (glow.A > 0 && glowSpread > 0)
            {
                using (SolidBrush sb = new SolidBrush(glow))
                {
                    for (int dx = -glowSpread; dx <= glowSpread; dx++)
                    {
                        for (int dy = -glowSpread; dy <= glowSpread; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (dx * dx + dy * dy > glowSpread * glowSpread) continue;
                            Pass(g, s, f, x0 + dx, inkTop + dy, inkH, cellW, colonRatio, sb);
                        }
                    }
                }
            }
            using (SolidBrush sb = new SolidBrush(col))
            {
                Pass(g, s, f, x0, inkTop, inkH, cellW, colonRatio, sb);
            }
            return x0 + PassWidth(f, s, cellW, colonRatio);
        }
    }

    // ============================================================ 繁简转换（Windows LCMapString）
    static class ZhTxt
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int LCMapStringW(int locale, uint flags, string src, int cchSrc, System.Text.StringBuilder dst, int cchDst);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int LocaleNameToLCID(string name, uint flags);

        const uint LCMAP_SIMPLIFIED = 0x02000000;
        const uint LCMAP_TRADITIONAL = 0x04000000;
        static int _lcid = -1;
        static Dictionary<string, string> _cache = new Dictionary<string, string>();

        static int Locale()
        {
            if (_lcid >= 0) return _lcid;
            _lcid = 0x0804;
            try
            {
                int l = LocaleNameToLCID("zh-CN", 0);
                if (l != 0) _lcid = l;
            }
            catch (Exception) { }
            return _lcid;
        }

        static string Map(string s, uint flags, string tag)
        {
            if (s == null || s.Length == 0) return s;
            string key = tag + s;
            string hit;
            if (_cache.TryGetValue(key, out hit)) return hit;
            string res = s;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length * 2 + 16);
                int n = LCMapStringW(Locale(), flags, s, s.Length, sb, sb.Capacity);
                if (n > 0) res = sb.ToString(0, n);
            }
            catch (Exception) { }
            if (res == null || res.Length == 0) res = s;
            _cache[key] = res;
            return res;
        }

        public static string ToSimp(string s) { return Map(s, LCMAP_SIMPLIFIED, "s:"); }

        // LCMapString 少数常用字不转换，做最小人工补丁
        static readonly string[] FixSimp = { "圣" };
        static readonly string[] FixTrad = { "聖" };

        public static string ToTrad(string s)
        {
            string t = Map(s, LCMAP_TRADITIONAL, "t:");
            if (t == null || t.Length == 0) return s;
            for (int i = 0; i < FixSimp.Length; i++)
                t = t.Replace(FixSimp[i], FixTrad[i]);
            return t;
        }
    }

    // ============================================================ 城市
    sealed class City
    {
        public string Key, Zh, En, Country, Tz;
        public string CC, A1, CC3;        // 国家码（ISO 3166-1 alpha-2）、州省码（US 两字母）、国家三字母码（USA）

        public City(string zh, string en, string tz, string country)
        {
            Zh = ZhTxt.ToTrad(zh);
            En = en;
            Tz = tz;
            Country = country != null ? country : "";
            Key = (en != null ? en.Replace("@", "") : "") + "@" + (tz != null ? tz : "");
        }

        TimeZoneInfo _zone;
        bool _zoneDone;
        public bool ZoneFailed;          // 时区 ID 非法（已回退 UTC），时钟管理里可提示

        /// <summary>界面用中文名：库里统一存繁体（兼容旧配置），简体界面显示前转一次（ZhTxt 有缓存）</summary>
        public string ZhUI
        {
            get
            {
                if (Zh == null) return "";
                if (Settings.CurrentLang == "en") return Zh;
                return ZhTxt.ToSimp(Zh);
            }
        }

        public TimeZoneInfo Zone
        {
            get
            {
                if (!_zoneDone)
                {
                    _zoneDone = true;
                    try { _zone = TimeZoneInfo.FindSystemTimeZoneById(Tz); }
                    catch (Exception) { _zone = TimeZoneInfo.Utc; ZoneFailed = true; }
                }
                return _zone;
            }
        }

        /// <summary>当前 UTC 偏移文本，如 UTC+08:00</summary>
        public string UtcText()
        {
            TimeSpan o = Zone.GetUtcOffset(DateTime.UtcNow);
            int total = (int)Math.Round(o.TotalMinutes);
            string sign = total >= 0 ? "+" : "-";
            int abs = Math.Abs(total);
            return "UTC" + sign + (abs / 60).ToString("00") + ":" + (abs % 60).ToString("00");
        }
    }

    // ============================================================ 中文名补位（中文版字幕给无译名的国外地点补州名/国名）
    static class GeoZh
    {
        static Dictionary<string, string> _states;      // "CC|A1" → 中文州省名
        static Dictionary<string, string> _ccs;         // CC → 中文国名（简称）

        static string[] Lines(string res)
        {
            string t = ResourceBox.ReadText(res);
            string[] all = t.Replace("\r", "").Split('\n');
            List<string> ok = new List<string>();
            foreach (string l in all)
            {
                string s = l.Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                ok.Add(s);
            }
            return ok.ToArray();
        }

        static void Load()
        {
            if (_ccs == null)
            {
                _ccs = new Dictionary<string, string>();
                foreach (string line in Lines("cn_cc.txt"))
                {
                    string[] f = line.Split('|');
                    if (f.Length < 2) continue;
                    string k = f[0].Trim().ToUpper();
                    if (k.Length > 0) _ccs[k] = f[1].Trim();
                }
            }
            if (_states == null)
            {
                _states = new Dictionary<string, string>();
                foreach (string line in Lines("cn_states.txt"))
                {
                    string[] f = line.Split('|');
                    if (f.Length < 3) continue;
                    string cc = f[0].Trim().ToUpper(), a1 = f[1].Trim().ToUpper();
                    if (cc.Length == 0 || a1.Length == 0) continue;
                    _states[cc + "|" + a1] = f[2].Trim();
                }
            }
        }

        /// <summary>国家码 + 州省码 → 中文州省名；查不到返回空串</summary>
        public static string StateOf(string cc, string a1)
        {
            Load();
            if (cc == null || a1 == null) return "";
            if (cc.Length == 0 || a1.Length == 0) return "";
            string k = cc.ToUpper() + "|" + a1.ToUpper();
            string v;
            return _states.TryGetValue(k, out v) ? v : "";
        }

        /// <summary>国家码 → 中文国名（简称）；查不到返回空串</summary>
        public static string CountryOf(string cc)
        {
            Load();
            if (cc == null || cc.Length == 0) return "";
            string v;
            return _ccs.TryGetValue(cc.ToUpper(), out v) ? v : "";
        }
    }

    static class CityDb
    {
        // 内置默认城市（同时用于旧配置键迁移与快速命中，名称统一为繁体）
        public static List<City> Legacy = new List<City>();
        static Dictionary<string, City> _legacyKey = new Dictionary<string, City>();
        static Dictionary<string, string> _tzCountry;
        static bool _built;

        public static void Build()
        {
            if (_built) return;
            _built = true;
            Add("Tokyo", "東京", "Tokyo", "Japan", "Tokyo Standard Time");
            Add("London", "倫敦", "London", "UK", "GMT Standard Time");
            Add("NewYork", "紐約", "New York", "USA", "Eastern Standard Time");
            Add("Beijing", "北京", "Beijing", "China", "China Standard Time");
            Add("HongKong", "香港", "Hong Kong", "China", "China Standard Time");
            Add("Taipei", "台北", "Taipei", "Taiwan", "Taipei Standard Time");
            Add("Seoul", "首爾", "Seoul", "Korea", "Korea Standard Time");
            Add("Singapore", "新加坡", "Singapore", "Singapore", "Singapore Standard Time");
            Add("Bangkok", "曼谷", "Bangkok", "Thailand", "SE Asia Standard Time");
            Add("Paris", "巴黎", "Paris", "France", "Romance Standard Time");
            Add("Berlin", "柏林", "Berlin", "Germany", "W. Europe Standard Time");
            Add("Rome", "羅馬", "Rome", "Italy", "W. Europe Standard Time");
            Add("Madrid", "馬德里", "Madrid", "Spain", "Romance Standard Time");
            Add("Moscow", "莫斯科", "Moscow", "Russia", "Russian Standard Time");
            Add("Dubai", "迪拜", "Dubai", "UAE", "Arabian Standard Time");
            Add("Cairo", "開羅", "Cairo", "Egypt", "Egypt Standard Time");
            Add("Johannesburg", "約翰尼斯堡", "Johannesburg", "South Africa", "South Africa Standard Time");
            Add("LosAngeles", "洛杉磯", "Los Angeles", "USA", "Pacific Standard Time");
            Add("SanFrancisco", "舊金山", "San Francisco", "USA", "Pacific Standard Time");
            Add("Chicago", "芝加哥", "Chicago", "USA", "Central Standard Time");
            Add("Toronto", "多倫多", "Toronto", "Canada", "Eastern Standard Time");
            Add("Vancouver", "溫哥華", "Vancouver", "Canada", "Pacific Standard Time");
            Add("MexicoCity", "墨西哥城", "Mexico City", "Mexico", "Central Standard Time (Mexico)");
            Add("SaoPaulo", "聖保羅", "Sao Paulo", "Brazil", "E. South America Standard Time");
            Add("BuenosAires", "布宜諾斯艾利斯", "Buenos Aires", "Argentina", "Argentina Standard Time");
            Add("Sydney", "雪梨", "Sydney", "Australia", "AUS Eastern Standard Time");
            Add("Auckland", "奧克蘭", "Auckland", "New Zealand", "New Zealand Standard Time");
            Add("Honolulu", "檀香山", "Honolulu", "USA", "Hawaiian Standard Time");
        }

        static void Add(string key, string zh, string en, string country, string tz)
        {
            City c = new City(zh, en, tz, country);
            Legacy.Add(c);
            _legacyKey[key] = c;
        }

        /// <summary>旧配置里的键（Tokyo / London / …）→ 城市对象</summary>
        public static City Get(string key)
        {
            Build();
            if (key == null) return null;
            City c = _legacyKey.ContainsKey(key) ? _legacyKey[key] : null;
            return c;
        }

        /// <summary>时区 → 国家（取内嵌 countries.txt 的首个命中；找不到返回空串）</summary>
        // ---- 国家码 / 州省码：英文版滚屏用「Seattle·WA·USA」----
        static Dictionary<string, string[]> _ccName;     // cc -> [英文国名, ISO3]

        // 1.9：彻底去掉「5.5 万行全量索引」。1.5 起有 _geo（名@时区）+ _geoByName（按名）两套字典，
        // 实测常驻 ~35MB，而且 _geoScans 累计到 6 次就无条件建一次 —— 在「最多 6 个城市」的上限下，
        // 等于每加一个地点就把整张表装进内存并且**永不释放**（这正是用户看到的「添加地点后内存居高不下」）。
        // 现在只保留一张极小的命中缓存（每个城市两个短串，几十字节），需要补 N 个城市时用**一趟**
        // 解析全部解决（原来是一个城市一趟全文件扫描）。匹配/对话框结束由 ReleaseAfterMatch() 收尾。
        static Dictionary<string, string[]> _geoHit = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        static List<City> _geoBatch;                      // 批量模式：先登记待补城市，收尾时一趟解析
        static readonly object _geoLock = new object();

        /// <summary>1.9：进入批量模式（启动时读回 N 个城市 → 只扫一趟文件，而不是 N 趟）</summary>
        public static void BeginGeoBatch() { lock (_geoLock) { _geoBatch = new List<City>(); } }

        /// <summary>1.9：结束批量模式，一趟解析补齐全部待补城市</summary>
        public static void CommitGeoBatch()
        {
            List<City> list;
            lock (_geoLock) { list = _geoBatch; _geoBatch = null; }
            if (list != null && list.Count > 0) Warm(list);
        }

        /// <summary>
        /// 1.9：一趟解析 worldcities3.txt，为 list 里所有缺国家码的城市补齐并写入 _geoHit。
        /// 命中规则与老版本一致：en@tz 精确优先；否则「同名且只属一国」才采用（同名多国不猜）。
        /// 只关心目标名的行（HashSet 比对），不为整表建字典 —— 峰值只是一个 3MB 文本串。
        /// </summary>
        static void Warm(List<City> list)
        {
            lock (_geoLock)
            {
                LoadCc();
                List<City> need = new List<City>();
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (City c in list)
                {
                    if (c == null || string.IsNullOrEmpty(c.En)) continue;
                    string k = c.En + "@" + (c.Tz ?? "");
                    if (_geoHit.ContainsKey(k)) continue;
                    need.Add(c);
                    names.Add(c.En);
                }
                if (need.Count == 0) return;

                Dictionary<string, string[]> hitTz = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string[]> hitNm = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> nmCc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> amb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in ResourceBox.ReadText("worldcities3.txt").Replace("\r", "").Split('\n'))
                {
                    if (line.Length == 0) continue;
                    // 1.9：先用 IndexOf 粗筛（零分配），命中才 Split。整表 5.5 万行逐行 Split 会为每行
                    // 造一个 string[] + 4~5 个短串，补 6 个城市的瞬间产生几十 MB 临时垃圾
                    //（实测 Private 一次冲到 90MB，虽然随后被回收，但用户看到的就是「内存暴涨」）。
                    bool maybe = false;
                    foreach (string nm0 in names)
                        if (line.IndexOf(nm0, StringComparison.OrdinalIgnoreCase) >= 0) { maybe = true; break; }
                    if (!maybe) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 5) continue;
                    string en = p[1].Trim(), tz = p[2].Trim(), cc = p[3].Trim(), a1 = p[4].Trim();
                    if (en.Length == 0 || cc.Length == 0) continue;
                    if (!names.Contains(en)) continue;
                    string[] v = new string[] { cc, a1 };
                    string tzk = en + "@" + tz;
                    if (!hitTz.ContainsKey(tzk)) hitTz[tzk] = v;
                    string cur;
                    if (!nmCc.TryGetValue(en, out cur)) { nmCc[en] = cc; hitNm[en] = v; }
                    else if (cur != cc) amb.Add(en);                       // 同名多国：不猜
                }
                foreach (City c in need)
                {
                    string k = c.En + "@" + (c.Tz ?? "");
                    string[] res;
                    if (!hitTz.TryGetValue(k, out res))
                    {
                        if (amb.Contains(c.En)) res = null;
                        else if (hitNm.TryGetValue(c.En, out res)) { }
                        else res = null;
                    }
                    _geoHit[k] = res;                                     // 查不到也记 null，下次不再扫
                    ApplyGeo(c, res);
                }
            }
        }

        /// <summary>把 [国家码, 州省码] 落到 City 上（含国名 / ISO3 回填）</summary>
        static void ApplyGeo(City c, string[] g)
        {
            if (c == null || g == null) return;
            c.CC = g[0];
            // 州省码只在真的是 2~3 位字母时才有意义（US 的 WA、CA；GB 的 ENG 等），数字区划码不用
            string a1 = g[1] == null ? "" : g[1];
            bool letters = a1.Length >= 2 && a1.Length <= 3;
            foreach (char ch in a1) if (ch < 'A' || ch > 'Z') letters = false;
            c.A1 = letters ? a1 : "";
            string[] nm;
            if (c.CC != null && _ccName.TryGetValue(c.CC, out nm))
            {
                c.CC3 = nm[1];
                if (c.Country == null || c.Country.Length == 0) c.Country = nm[0];
            }
        }

        /// <summary>
        /// 1.9：匹配/添加地点结束后的收尾。命中缓存（几个短串）保留，让挂件重画时零重扫；
        /// 真正要还的是「世界城市全表」那块（TimeZoneMatcher 的 _world，55k 行字符串），
        /// 老版本要等闲置 10 分钟才卸载 —— 用户点完「添加地点」，内存就一直挂在那儿。
        /// </summary>
        public static void ReleaseAfterMatch()
        {
            try { TimeZoneMatcher.TrimIfIdle(true); } catch (Exception) { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>1.9：国家码命中缓存条数（自检用 —— 它就是「留在内存里的全部」，几个短串）</summary>
        public static int GeoHitCount { get { lock (_geoLock) { return _geoHit.Count; } } }

        static void LoadCc()
        {
            if (_ccName != null) return;
            _ccName = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in ResourceBox.ReadText("cc_names.txt").Replace("\r", "").Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] p = line.Split('|');
                if (p.Length < 3) continue;
                _ccName[p[0].Trim()] = new string[] { p[1].Trim(), p[2].Trim() };
            }
        }

        /// <summary>给城市补上国家码 / 州省码（不影响时区换算，仅用于字幕显示）</summary>
        public static void AttachGeo(City c)
        {
            if (c == null) return;
            // 1.9：批量模式下只登记，等 CommitGeoBatch 一趟解析（启动读回 N 个城市时省掉 N-1 趟扫描）
            bool batched = false;
            lock (_geoLock)
            {
                if (_geoBatch != null)
                {
                    if (!_geoBatch.Contains(c)) _geoBatch.Add(c);
                    batched = true;
                }
            }
            if (batched) return;
            if (string.IsNullOrEmpty(c.En)) return;
            LoadCc();
            string key = c.En + "@" + (c.Tz ?? "");
            string[] g = null;
            lock (_geoLock) { if (!_geoHit.TryGetValue(key, out g)) g = null; }
            if (g == null)
            {
                List<City> one = new List<City>();
                one.Add(c);
                Warm(one);                                   // 未预热：为这一个城市走一趟
                return;
            }
            ApplyGeo(c, g);
        }

        public static string CountryForTz(string tz)        {
            if (_tzCountry == null)
            {
                _tzCountry = new Dictionary<string, string>();
                string txt = ResourceBox.ReadText("countries.txt");
                foreach (string line in txt.Replace("\r", "").Split('\n'))
                {
                    if (line.Length == 0) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 3) continue;
                    string id = p[2].Trim();
                    if (id.Length == 0 || _tzCountry.ContainsKey(id)) continue;
                    _tzCountry[id] = p[1].Trim();
                }
            }
            string v = _tzCountry.ContainsKey(tz) ? _tzCountry[tz] : "";
            return v;
        }
    }


    // ============================================================ 设置（HKCU\Software\AboutTime）
    sealed class Settings
    {
        public const string Root = @"Software\AboutTime";
        // 1.6：真正的读写路径。自检时可以指到别的子键上验「无键启动」，绝不碰用户真实配置
        public static string RootPath = Root;
        static Settings()
        {
            string ov = Environment.GetEnvironmentVariable("ABOUTTIME_ROOT");
            if (!string.IsNullOrEmpty(ov)) RootPath = ov;
        }
        public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunName = "AboutTime";
        // 1.5：版本号不再手写常量（曾长期写着 1.4，而 exe 已是 1.5.0.0），改读程序集版本
        public static readonly string AppVersion = ReadVersion();

        static string ReadVersion()
        {
            try
            {
                System.Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) return v.Major + "." + v.Minor;
            }
            catch (Exception) { }
            return "1.5";
        }

        public bool Dark = true;
        public int Style = 0;             // 0 碳灰 / 1 雪白 / 2 藏青（Dark = Style != 1）
        public bool Blur = false;         // 背景磨砂（仅壁纸层）；「总在最前」时失效
        public int BlurLevel = 3;         // 1.8.5：磨砂强度 1~5 档，3 = 标准档（数字越大越糊）；Blur=false 时仅作记忆
        public int Opacity = 70;          // 10..100
        public int Speed = 30;            // px/s：20/25/…/55（对应菜单「速度 1~8」）
        public double Scale = 1.0;        // 0.6..1.2，步进 0.1
        public bool Topmost = false;
        public bool AutoStart = true;
        public string Lang = "zh";
        public bool ShowLocal = true;
        // 2.0：主时钟皮肤。0 = 原样（英数七段 + 星期栏 + 月日），1 = 纯中文皮肤
        // （左竖公元年 · 中部四个繁写大字 · 下方公历行 · 右侧三列农历）。与界面语言 Lang 解耦：
        // 英文界面也能开中文皮肤，中文界面也能保持原样。
        public int MainSkin = 0;
        public City MainCity = null;      // 主时钟地点；null = 跟随系统本地时间
        public int MainPos = 0;           // 主时钟在所有模块中的位置（0 = 最上）
        // 日历模块（第三类模块）
        public int CalMode = 0;           // 0 关闭 / 1 常挂（占一行） / 2 弹出（双击日期区域弹出整模块）
        public bool ShowCal = false;      // 兼容旧配置：等价于 CalMode==1
        public int CalPos = 0;            // 在统一模块序列中的位置（1.9 起由 CalAnchor 推算，仅作缓存/兼容）
        // 1.9 修订：日历常挂位置改为四个固定锚点，不再按 1/2/3 序号排。
        // 0 = 全表最上 · 1 = 主时钟上方 · 2 = 主时钟下方 · 3 = 全表最下（默认 3：开常挂即落在主窗最下方）
        // 主时钟模块被关掉时：1 等同 0（全表最上）、2 等同 3（全表最下）
        public int CalAnchor = 3;
        public bool CalLunar = true;      // 是否显示农历 / 节气
        public int CalFirstDow = 0;       // 每周首日：0 周日 / 1 周一
        public string CalCC = "auto";     // auto 跟随主时钟城市 / 两位国家码 / none 不显示国别节日
        // 倒计时 / 秒表模块（第四类模块）
        public bool TimerOn = true;       // 是否常挂显示
        public int TimerPos = 0;          // 在统一模块序列中的位置
        public bool TimerAlarm = false;   // 响闹开关（默认静音）
        // 干支计时模块（第五类模块）
        public bool GzOn = true;          // 是否常挂显示
        public int GzPos = 1;             // 在统一模块序列中的位置
        public List<City> Cities = new List<City>();
        public int X = int.MinValue, Y = int.MinValue;

        // 多显示器拔插：所在屏断开时窗口临时迁到活跃屏并居中，原屏回来后归位
        public bool Parked;
        public string ParkDevice = "";
        public string ParkMon = "";              // 断开时那块屏的显示器 DeviceID（设备名被重排时的副判据）
        public int ParkX = int.MinValue, ParkY = int.MinValue;

        const char Sep = '\u001f';

        public static string CurrentLang = "zh";     // 供对话框等非挂件代码取当前界面语言
        public static int CurrentStyle = 0;          // 0 碳灰 / 1 雪白 / 2 藏青（弹窗取底色用）

        /// <summary>系统时区是否为中国（英文版据此默认显示中国节日）</summary>
        public static bool IsChinaZone()
        {
            try
            {
                string id = TimeZoneInfo.Local.Id;
                return id == "China Standard Time" || id == "Asia/Shanghai" || id == "Asia/Urumqi";
            }
            catch (Exception) { return false; }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            CityDb.Build();
            s.Cities.Add(CityDb.Get("Tokyo"));
            s.Cities.Add(CityDb.Get("London"));
            s.Cities.Add(CityDb.Get("NewYork"));
            // 1.9：下面是「读回 N 个城市」的过程，每个城市都要补国家码/州省码。
            // 老版本是每个城市各扫一遍 5.5 万行的文件；这里改为批量，最后只扫一趟。
            CityDb.BeginGeoBatch();
            // 1.6：启动自判语言。整条 AboutTime 子键都不存在（第一次运行）时走下面 hadKey==false 的
            // 推导，靠字段初值是不行的 —— 字段初值永远是中文/农历开/auto，英文系统首启会错成中文版。
            string defLang = (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh") ? "zh" : "en";
            bool hadKey = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RootPath, false))
                {
                    if (k != null)
                    {
                        hadKey = true;
                        string v = Convert.ToString(k.GetValue("Style", "dark"));
                        // 1.6：主题三态。老配置只有 dark / white，按原样迁移
                        if (v == "navy") s.Style = 2;
                        else if (v == "white") s.Style = 1;
                        else s.Style = 0;
                        s.Dark = (s.Style != 1);
                        s.Blur = Convert.ToString(k.GetValue("Blur", "0")) == "1";
                        s.BlurLevel = Clamp(ToInt(k.GetValue("BlurLevel", 3), 3), 1, 5);   // 1.8.5：旧配置无此键 → 3 = 原 2.5 档观感，平滑兼容
                        // 1.9：磨砂档的标尺重排过（新 3 档 = 1.8.5 的 1 档），档位数字的含义跟着变了。
                        // 按「等效强度」一次性改映射（老 1/2 档 → 新 3 档，老 3/4 档 → 新 4 档，老 5 档 → 新 5 档），
                        // 让升级后的观感与升级前一模一样。落盘交给 Save()：它把 BlurLevel 与
                        // BlurScaleV=2 一起写，不会出现「只写标记没写档位」的半截状态。
                        object bs = k.GetValue("BlurScaleV", null);
                        if (bs == null || ToInt(bs, 0) < 2)
                        {
                            int[] remap = new int[] { 3, 3, 4, 4, 5 };
                            s.BlurLevel = remap[Clamp(s.BlurLevel, 1, 5) - 1];
                        }
                        // 1.7：不再把 Topmost 时的 Blur 落盘归零（1.6 会把勾选状态悄悄丢掉，关掉
                        // 「总在最前」后还得手动重勾）。生效判据始终是 FrostActive() = Blur && !Topmost
                        s.Opacity = Clamp(ToInt(k.GetValue("Opacity", 70), 70), 10, 100);
                        s.Speed = NormSpeed(ToInt(k.GetValue("Speed", 30), 30));
                        double d;
                        if (double.TryParse(Convert.ToString(k.GetValue("Scale", "1.00")), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                            s.Scale = NormScale(d);
                        s.Topmost = Convert.ToString(k.GetValue("Topmost", "0")) == "1";
                        s.AutoStart = Convert.ToString(k.GetValue("AutoStart", "1")) == "1";
                        // 1.6：启动按系统语言自判 —— 中文系统 → 中文版，非中文系统 → 英文版
                        s.Lang = Convert.ToString(k.GetValue("Lang", defLang));
                        if (s.Lang != "zh" && s.Lang != "en") s.Lang = defLang;
                        s.ShowLocal = Convert.ToString(k.GetValue("ShowLocal", "1")) == "1";
                        // 2.0：主时钟中文皮肤开关（旧配置没有这个键 → 0 = 原样，升级不改观感）
                        s.MainSkin = Clamp(ToInt(k.GetValue("MainSkin", 0), 0), 0, 1);
                        string mc = Convert.ToString(k.GetValue("MainCity", ""));
                        if (!string.IsNullOrEmpty(mc)) s.MainCity = DecodeCity(mc.Trim());
                        int x = ToInt(k.GetValue("X", int.MinValue), int.MinValue);
                        int y = ToInt(k.GetValue("Y", int.MinValue), int.MinValue);
                        s.X = x; s.Y = y;
                        s.MainPos = ToInt(k.GetValue("MainPos", 0), 0);
                        s.ShowCal = Convert.ToString(k.GetValue("ShowCal", "0")) == "1";
                        // 1.3：显示方式三态；旧配置只有 ShowCal，按它迁移
                        object mo = k.GetValue("CalMode", null);
                        if (mo != null) s.CalMode = ToInt(mo, 0);
                        else s.CalMode = s.ShowCal ? 1 : 0;
                        if (s.CalMode < 0 || s.CalMode > 2) s.CalMode = 0;
                        s.ShowCal = (s.CalMode == 1);
                        s.CalPos = ToInt(k.GetValue("CalPos", 0), 0);
                        // 1.9：日历常挂位置的四档锚点。旧配置没有这个键 → 3（全表最下），
                        // 也就是「打开常挂就落在主窗最下方」这个默认行为
                        s.CalAnchor = Clamp(ToInt(k.GetValue("CalAnchor", 3), 3), 0, 3);
                        // 1.6：英文版农历默认关（可手动开）；英文 + 系统时区在中国 → 节日默认按中国
                        object lunarRaw = k.GetValue("CalLunar", null);
                        s.CalLunar = (lunarRaw != null) ? (Convert.ToString(lunarRaw) == "1") : (s.Lang != "en");
                        s.CalFirstDow = ToInt(k.GetValue("CalFirstDow", 0), 0);
                        if (s.CalFirstDow < 0 || s.CalFirstDow > 1) s.CalFirstDow = 0;
                        object ccRaw = k.GetValue("CalCC", null);
                        if (ccRaw != null)
                        {
                            string calcc = Convert.ToString(ccRaw);
                            s.CalCC = (calcc.Length == 0) ? "auto" : calcc;
                        }
                        else s.CalCC = IsChinaZone() ? "CN" : "auto";
                        s.TimerOn = Convert.ToString(k.GetValue("TimerOn", "1")) == "1";
                        s.TimerPos = ToInt(k.GetValue("TimerPos", 0), 0);
                        s.TimerAlarm = Convert.ToString(k.GetValue("TimerAlarm", "0")) == "1";
                        // 1.6：干支计时默认开（英文版同样默认开）
                        s.GzOn = Convert.ToString(k.GetValue("GzOn", "1")) == "1";
                        s.GzPos = ToInt(k.GetValue("GzPos", 1), 1);
                        s.Parked = Convert.ToString(k.GetValue("Parked", "0")) == "1";
                        s.ParkDevice = Convert.ToString(k.GetValue("ParkDevice", ""));
                        s.ParkMon = Convert.ToString(k.GetValue("ParkMon", ""));
                        s.ParkX = ToInt(k.GetValue("ParkX", int.MinValue), int.MinValue);
                        s.ParkY = ToInt(k.GetValue("ParkY", int.MinValue), int.MinValue);
                        // "没这个值" 与 "值是空串" 要分开：空串表示用户明确把世界时钟全删了
                        object citiesRaw = k.GetValue("Cities", null);
                        if (citiesRaw != null)
                        {
                            s.Cities.Clear();
                            foreach (string t in Convert.ToString(citiesRaw).Split('|'))
                            {
                                City c = DecodeCity(t.Trim());
                                if (c != null) s.Cities.Add(c);
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            // 第一次运行（注册表里连子键都没有）：按系统语言 / 系统时区推导三个默认
            //  —— 界面语言跟系统语言；英文版默认不显示农历（用户可手动打开）；
            //  —— 系统时区在中国 → 节日默认按中国（CN），否则跟随主时钟城市（auto）；
            //  —— 干支计时默认开，英文版同样开。
            if (!hadKey)
            {
                s.Lang = defLang;
                s.CalLunar = (defLang != "en");
                s.CalCC = IsChinaZone() ? "CN" : "auto";
                s.GzOn = true;
            }
            s.Cities = CleanCities(s.Cities);
            if (!s.ShowLocal && s.Cities.Count == 0) { s.ShowLocal = true; s.MainPos = 0; }   // 至少保留主时钟，避免空窗口
            if (s.MainPos < 0) s.MainPos = 0;
            if (s.MainPos > s.Cities.Count) s.MainPos = s.Cities.Count;
            CityDb.CommitGeoBatch();                 // 1.9：一趟补齐所有城市（含主时钟），然后关掉批量模式
            CurrentLang = s.Lang;
            return s;
        }

        static List<City> CleanCities(List<City> src)
        {
            List<City> clean = new List<City>();
            foreach (City c in src)
            {
                if (c == null || c.Tz == null || c.Tz.Length == 0) continue;
                if (clean.Count > 0 && FindSame(clean, c.Key) >= 0) continue;   // 去重
                clean.Add(c);
            }
            if (clean.Count == 0 && src.Count > 0) clean.Add(CityDb.Get("Tokyo"));
            return clean;
        }

        public static int FindSame(List<City> list, string key)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Key == key) return i;
            return -1;
        }

        static City DecodeCity(string raw)
        {
            if (raw.Length == 0) return null;
            if (raw.IndexOf(Sep) < 0) return CityDb.Get(raw);      // 旧配置：只有键名
            string[] p = raw.Split(Sep);
            if (p.Length < 3) return null;
            string country = p.Length >= 4 ? p[3] : "";
            City c = new City(p[0], p[1], p[2], country);
            // 配置里只存了 中文名/英文名/时区/国名，国家码与州省码要按城市库补回来，
            // 否则重启后英文版少州缩写、中文版补不出中文州名（1.3 修）
            CityDb.AttachGeo(c);
            return c;
        }

        static string EncodeCity(City c)
        {
            return c.Zh + Sep + c.En + Sep + c.Tz + Sep + c.Country;
        }

        static int NormSpeed(int v)
        {
            if (v >= 55) return 55;             // 旧档位迁移（50/60/80 → 新顶档）
            if (v <= 20) return 20;
            int[] steps = { 20, 25, 30, 35, 40, 45, 50, 55 };
            int best = steps[0];
            for (int i = 0; i < steps.Length; i++)
                if (Math.Abs(v - steps[i]) < Math.Abs(v - best)) best = steps[i];
            return best;
        }

        static double NormScale(double d)
        {
            if (d < 0.6) d = 0.6;
            if (d > 1.2) d = 1.2;
            return Math.Round(d * 10.0) / 10.0;
        }

        public void Save()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RootPath))
                {
                    k.SetValue("Style", Style == 2 ? "navy" : (Style == 1 ? "white" : "dark"));
                    k.SetValue("Blur", Blur ? "1" : "0");   // 1.7：按勾选原样存；置顶时只是「不生效」，不改配置
                    k.SetValue("BlurLevel", BlurLevel);     // 1.8.5：磨砂强度档位（1~5）
                    k.SetValue("BlurScaleV", 2);            // 1.9：磨砂档标尺版本标记（2 = 新五档标尺，升级时不必再改映射）
                    k.SetValue("Opacity", Opacity);
                    k.SetValue("Speed", Speed);
                    k.SetValue("Scale", Scale.ToString("0.00", CultureInfo.InvariantCulture));
                    k.SetValue("Topmost", Topmost ? "1" : "0");
                    k.SetValue("AutoStart", AutoStart ? "1" : "0");
                    k.SetValue("Lang", Lang);
                    k.SetValue("ShowLocal", ShowLocal ? "1" : "0");
                    k.SetValue("MainSkin", MainSkin);        // 2.0：主时钟皮肤 0 原样 / 1 纯中文
                    k.SetValue("MainCity", MainCity != null ? EncodeCity(MainCity) : "");
                    k.SetValue("MainPos", MainPos);
                    k.SetValue("ShowCal", ShowCal ? "1" : "0");
                    k.SetValue("CalMode", CalMode);
                    k.SetValue("CalPos", CalPos);
                    k.SetValue("CalAnchor", CalAnchor);     // 1.9：日历常挂位置的四档锚点
                    k.SetValue("CalLunar", CalLunar ? "1" : "0");
                    k.SetValue("CalFirstDow", CalFirstDow);
                    k.SetValue("CalCC", CalCC);
                    k.SetValue("TimerOn", TimerOn ? "1" : "0");
                    k.SetValue("TimerPos", TimerPos);
                    k.SetValue("TimerAlarm", TimerAlarm ? "1" : "0");
                    k.SetValue("GzOn", GzOn ? "1" : "0");
                    k.SetValue("GzPos", GzPos);
                    k.SetValue("X", X);
                    k.SetValue("Y", Y);
                    k.SetValue("Parked", Parked ? "1" : "0");
                    k.SetValue("ParkDevice", ParkDevice);
                    k.SetValue("ParkMon", ParkMon);
                    k.SetValue("ParkX", ParkX);
                    k.SetValue("ParkY", ParkY);
                    string joined = "";
                    foreach (City c in Cities) joined += (joined.Length > 0 ? "|" : "") + EncodeCity(c);
                    k.SetValue("Cities", joined);
                }
            }
            catch (Exception) { }
        }

        public static int ToInt(object o, int def)
        {
            if (o == null) return def;
            int v;
            if (int.TryParse(Convert.ToString(o), out v)) return v;
            return def;
        }

        static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo; if (v > hi) return hi; return v;
        }

        public static bool IsAutoStartOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RunName) != null;
                }
            }
            catch (Exception) { return false; }
        }

        public static void ApplyAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 自启项若还指向旧副本（典型：早先装在 C:\AboutTime.exe 的 1.3 版），开机跑的就不是用户
        /// 刚打开的这份程序。启动时静默改写为当前 exe，普通权限即可（只写 HKCU）。
        /// </summary>
        public static void HealAutoStartPath()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath, true))
                {
                    if (k == null) return;
                    object o = k.GetValue(RunName);
                    if (o == null) return;                       // 没开自启，不擅自动
                    string cur = Convert.ToString(o).Trim().Trim('"');
                    string want = Application.ExecutablePath;
                    if (string.Equals(cur, want, StringComparison.OrdinalIgnoreCase)) return;
                    k.SetValue(RunName, "\"" + want + "\"");
                }
            }
            catch (Exception) { }
        }

        public static void ClearAll()
        {
            try { Settings.ApplyAutoStart(false); } catch (Exception) { }
            try { Registry.CurrentUser.DeleteSubKeyTree(RootPath, false); } catch (Exception) { }
        }
    }

    // ============================================================ 版面度量（设计坐标 500 宽）
    sealed class Metrics
    {
        public const int W = 500;             // 内容宽（设计坐标）
        public const int Pad = 10;            // 卡片四周留白（每边 10px）
        public const int CardW = W + Pad * 2; // 卡片实宽 520
        public const int LocalH = 158;        // 本地模块高（178 → 158，行距再压一档后继续收）
        public const int WorldH = 80;
        public const int TimerH = 40;         // 倒计时 / 秒表模块高
        public const int GzH = 40;            // 干支计时模块高
        public const int CalH = 500;          // 日历模块高（1.3 起压缩到 500，5/6 行共用同一高度）
        public const int CalWeekH = 21;       // 周头
        public const int CalX0 = 5;           // 网格左边距
        public const int CalPitch = 70;       // 列距（7 列 = 490）
        public const int CalColW = 70;
        public const int R = 14;

        // 本地
        public Font FWeek, FWeekDim, FTime, FYear, FMD, FMdLabel;
        public float CellTime, InkTimeH, TimeLeft, InkMD_H, CellMD, MdX, MdTopM, MdTopD;
        public float TimeBlockW, TimeFreeL, TimeFreeR, TimeShift;   // 1.6：时分居中 / 节日小字上推
        public Font FFestSmall;                                     // 1.6：时:分下方的节日小字
        public float FestSmallH;
        public float WeekInkH, WeekDimInkH, WeekTop, WeekPitch;

        // 2.0：主时钟中文皮肤
        public Font FCnBig, FCnBig2, FCnBig3, FCnCol, FCnColLabel, FCnColNum, FCnColSmall, FCnYear, FCnYearNum, FCnLine, FCnLineNum, FCnDot;
        public float CnBigInkH;

        // 日历
        public Font FCalTitle, FCalSub, FCalWeek, FCalDay, FCalFest, FCalFestEn, FCalLun, FCalLunEn, FCalBadge;
        public float CalDayInkH, CalDayCell;

        // 世界
        public Font FWTime, FWDate, FWLabel, MCn, MEn;
        public float FWCell, FWInkH, FWLeft, FWDateInkH;
        public float TabK;

        // 倒计时 / 秒表
        public Font FTimerLabel, FTimerLabelEn, FTimerDigits, FTimerGhost;
        public float TimerCell, TimerInkH, TimerGhostH, TimerGhostW;

        // 干支计时
        public Font FGzTime, FGzMarq, FGzUnit;

        public float SzCn, SzEn;

        /// <summary>农历用的系统字体：内嵌点阵宋(aura)缺「廿」，而 1.3 起农历 21–29 要写「廿X」，只能换宋体</summary>
        static Font MakeLunFont(float px)
        {
            try { return new Font("SimSun", px, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch (Exception) { }
            try { return new Font("宋体", px, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch (Exception) { }
            return new Font("Microsoft YaHei UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public Metrics()
        {            // 本地模块
            FWeek = Ui.Make(Ui.DotF, 26f);         // 当日：+8%，更醒目
            FWeekDim = Ui.Make(Ui.DotF, 20f);      // 未选中：-15%（26 → 20），行距同步收紧
            WeekInkH = Ui.Ink(FWeek, "MON").Height;
            WeekDimInkH = Ui.Ink(FWeekDim, "MON").Height;
            WeekTop = 20f;
            WeekPitch = 17f;                        // 行槽距 20 → 17，七行共 119px，间隙真减半
            FYear = Ui.Make(Ui.DotF, 21f);
            FMdLabel = Ui.Make(Ui.DotF, 15f);
            float t = Ui.FitH(Ui.Seg, "8", 80.1f); // v2.1：时分字号再缩 10%（89 → 80.1）
            FTime = Ui.Make(Ui.Seg, t);
            InkTimeH = Ui.Ink(FTime, "8").Height;
            CellTime = Ui.Ink(FTime, "8").Width + 7f;
            TimeBlockW = Sgn.Width(FTime, "0000", CellTime, 0.42f) + CellTime * 0.42f;
            // 1.6：时分不再按一个写死的 236 定位（那会偏左约 10px），改成
            // 「星期栏右沿 ↔ 月日块左沿」这段空档的几何中点，随字号/缩放自动居中
            TimeLeft = 0f;

            float md = Ui.FitH(Ui.Seg, "8", 29f);  // 月日随高度缩至 29（原 34）
            FMD = Ui.Make(Ui.Seg, md);
            InkMD_H = Ui.Ink(FMD, "8").Height;
            CellMD = Ui.Ink(FMD, "8").Width + 3f;
            float inkMW = Ui.Ink(FMdLabel, "M").Width;
            MdX = 478f - inkMW - 5f - CellMD * 2f;
            MdTopM = 57f;
            MdTopD = 112f;

            float weekRight = 22f + Math.Max(Ui.Ink(FWeek, "MON").Width, Ui.Ink(FWeekDim, "MON").Width);
            TimeFreeL = weekRight + 8f;
            TimeFreeR = MdX - 12f;
            TimeLeft = (TimeFreeL + TimeFreeR) / 2f - TimeBlockW / 2f;
            TimeShift = 0f;                        // 有节日小字时把时分上推，见 DrawLocal
            // v2.1：这一行抬高到 22（与倒计时标签同大），并从灰色改成橙色
            FFestSmall = Ui.Make(Ui.Cn, 22f);
            FestSmallH = Ui.Ink(FFestSmall, "元旦").Height;

            // —— 2.0 主时钟中文皮肤 ——
            // 大字：qiji 子集，墨迹高度 70（第五稿上调 2px；横向不再压瘦，按原字宽排）
            FCnBig = Ui.Make(Ui.Qiji, Ui.FitH(Ui.Qiji, "壹", 70f));   // v2.1：中文皮肤回退到 2.0 的 70
            CnBigInkH = Ui.Ink(FCnBig, "壹").Height;
            // 整点只剩「X時」：两字加大一号（中间垫全角空格）、三字加大一号、四字用常态
            FCnBig2 = Ui.Make(Ui.Qiji, Ui.FitH(Ui.Qiji, "壹", 90f));  // v2.1：中文皮肤回退到 90
            FCnBig3 = Ui.Make(Ui.Qiji, Ui.FitH(Ui.Qiji, "壹", 80f));  // v2.1：中文皮肤回退到 80
            // 右侧三列：橙色标目 18（比正文小 4），白色正文 22，列内「·」墨迹 10px
            FCnColLabel = Ui.Make(Ui.Qiji, Ui.FitH(Ui.Qiji, "壹", 18f));
            FCnColNum = Ui.Make(Ui.Qiji, Ui.FitH(Ui.Qiji, "壹", 22f));
            FCnCol = FCnColNum;                               // 旧名保留，等同正文那一档
            FCnColSmall = Ui.MakeBold(Ui.Song, Ui.FitH(Ui.Song, "·", 10f)); // 三列里的「·」（思源宋）
            FCnYear = Ui.MakeBold(Ui.Song, Ui.FitH(Ui.Song, "年", 17f));   // 「公元」「年」
            FCnYearNum = Ui.Make(Ui.Song, Ui.FitH(Ui.Song, "年", 17f));    // 年份数字
            FCnLine = Ui.Make(Ui.Song, 14.5f);                // 下方行橙色标目（月 / 日 / 星期）
            FCnLineNum = Ui.Make(Ui.Song, Ui.FitH(Ui.Song, "8", 22f));     // 下方行白数字，与农历同大
            FCnDot = Ui.MakeBold(Ui.Song, 30f);               // 时分之间的「·」，占半个字位

            // 世界模块
            float wt = Ui.FitH(Ui.Seg, "8", 41.4f);   // v2.1：地点模块时分再缩 10%（46 → 41.4）
            FWTime = Ui.Make(Ui.Seg, wt);
            FWInkH = Ui.Ink(FWTime, "8").Height;
            FWCell = Ui.Ink(FWTime, "8").Width + 6f;
            FWLeft = 16f;

            float wd = Ui.FitH(Ui.DotF, "8", 24f);
            FWDate = Ui.Make(Ui.DotF, wd);
            FWDateInkH = Ui.Ink(FWDate, "8").Height;
            FWLabel = Ui.Make(Ui.DotF, 11f);
            TabK = 0.94f;

            SzCn = 35f;
            MCn = Ui.Make(Ui.Cn, SzCn);
            SzEn = 42f;
            MEn = Ui.Make(Ui.Cn, SzEn);

            // 倒计时 / 秒表（模块高 40：标签中文 22、英文 28、数显字高 32）
            FTimerLabel = Ui.Make(Ui.Cn, 22f);
            FTimerLabelEn = Ui.Make(Ui.Seg, 28f);
            float td = Ui.FitH(Ui.Seg, "8", 32f);
            FTimerDigits = Ui.Make(Ui.Seg, td);
            TimerInkH = Ui.Ink(FTimerDigits, "8").Height;
            TimerCell = Ui.Ink(FTimerDigits, "8").Width + 3.6f;
            float tg = Ui.FitH(Ui.Seg, "8", 18f);
            FTimerGhost = Ui.Make(Ui.Seg, tg);
            TimerGhostH = Ui.Ink(FTimerGhost, "8").Height;
            TimerGhostW = Ui.Ink(FTimerGhost, "8").Width + 1.6f;

            // 干支计时（模块高 40：固定时辰字高 26、滚动字幕 20、年/月/日 小字 13）
            FGzTime = Ui.Make(Ui.Cn, 26f);
            FGzMarq = Ui.Make(Ui.Cn, 20f);
            FGzUnit = Ui.Make(Ui.Cn, 13f);

            // 日历模块（案 B3：内容 500×640）
            FCalTitle = Ui.Make(Ui.Cn, 27.5f);                                 // 1.3：栏头字放大 20%
            FCalSub = Ui.Make(Ui.Cn, 14f);
            FCalWeek = Ui.Make(Ui.Cn, 18f);
            FCalDay = Ui.Make(Ui.Seg, Ui.FitH(Ui.Seg, "8", 24f));               // 1.3：日号缩 30%（原 40 → 28，再压到 24 才放得下 2 字农历）
            CalDayInkH = Ui.Ink(FCalDay, "8").Height;
            CalDayCell = Ui.Ink(FCalDay, "8").Width + 2f;
            FCalFest = Ui.Make(Ui.Cn, 18f);      // 1.3：节日字号放大，4 汉字 ≈ 格宽
            FCalFestEn = Ui.Make(Ui.Cn, 11f);
            FCalLun = MakeLunFont(17f);          // 1.3：农历 +20%，且要用带「廿」的系统宋体
            FCalLunEn = MakeLunFont(13f);        // 英文界面里的农历同样要「廿」，也用系统宋体
            FCalBadge = Ui.Make(Ui.Cn, 10f);
        }
    }
}
