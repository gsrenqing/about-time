// About Time 1.0 —— 挂件窗体与绘制
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Drawing.Imaging;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AboutTime
{
    sealed class Row
    {
        public bool Local;
        public bool Cal;     // 日历模块行
        public bool Timer;   // 倒计时 / 秒表模块行
        public bool Gz;      // 干支计时模块行
        public City C;
        public float Y;      // 设计坐标下的行顶
        public float H;      // 该行高度（设计坐标）
        public string MTxt;  // 滚屏文字缓存（城市/语言变化时才失效）
        public float MW;     // 对应的文字宽度
        public bool MDirty = true;
        public string MDate;             // 当日 MM/dd 缓存（按分钟失效，不再每帧 ToString）
        public string MDateMin;

        /// <summary>世界时钟行（只有它带滚屏）</summary>
        public bool World { get { return !Local && !Cal && !Timer && !Gz; } }
    }

    sealed class WidgetForm : Form
    {
        // ---------- 状态
        Settings S;
        Metrics M;
        Pal P;
        List<Row> rows = new List<Row>();
        Dictionary<string, float> off = new Dictionary<string, float>();
        Bitmap staticLayer;
        Bitmap frame;
        Timer tick;
        Timer wdog;
        System.Diagnostics.Stopwatch clockDt = new System.Diagnostics.Stopwatch();
        double lastTickMs;
        string lastMinute = "";
        bool dragging;
        Point dragOrigin;
        bool suppressSave;      // 清理全部设置后抑制回写
        float marqueeGap = 64f;
        List<string> offKeys = new List<string>();                       // 复用：避免每帧 new List
        Dictionary<string, float> offPeriod = new Dictionary<string, float>();
        bool needScroll;                                                 // 是否真的有需要逐帧滚动的行
        bool hiResTimer;                                                 // 是否已把系统定时器精度提到 1ms
        string lastDev = "";                                             // 最近一次确认「窗口在哪块屏上」的设备名
        int tickErrors;                                                  // OnTick 连续异常计数
        IntPtr screenDC = IntPtr.Zero;                                   // 缓存的屏幕 DC（PushLayered 每帧复用，不再每帧 GetDC/ReleaseDC）
        ContextMenuStrip menu;                                           // 常驻右键菜单：每次重建条目，退出时才销毁
        string lastMon = "";                                             // 最近一次确认的显示器 DeviceID（设备名被重排时的副判据）

        // ---------- 点击 / 双击 / 日历状态
        bool pressMoved;                                                 // 本次按下后是否真的移动过（区分点击与拖动）
        Point pressScreen;                                               // 按下时的屏幕坐标
        Point lastClickPt = new Point(-1000, -1000);
        long lastClickMs = -100000;
        int calY, calM;                                                  // 日历当前显示的年月
        bool calNav;                                                     // 用户手动翻过月：不再自动跟随"本月"
        CalPopup popup;
        MonthPopup monthPopup;                                           // 世界行整月小日历
        TimerMod tmr = new TimerMod();                                   // 倒计时 / 秒表运行状态
        TimerPopup timerPopup;                                           // 「时间已到」弹窗

        // ---------- Win32
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNC pblend, int dwFlags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

        struct POINT { public int X, Y; }
        struct SIZE { public int CX, CY; }
        struct RECT { public int Left, Top, Right, Bottom; }

        // 注意：原生顺序是 BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat
        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNC { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        IntPtr memDC = IntPtr.Zero, hDib = IntPtr.Zero, oldDib = IntPtr.Zero, dibBits = IntPtr.Zero;
        Graphics frameG;
        int surW, surH;

        // 1.6：主时钟「时:分」竖直上推量（有节日小字时 9px，否则 0），动态层冒号要保持同一位移
        float _mainTimeShift = 0f;

        // 1.6：背景磨砂 —— 抓一次壁纸层、糊一次、缓存住；只在位置/尺寸/主题/缩放变化或显式失效时重抓
        Bitmap frostBmp;
        bool frostDirty = true;
        bool _frostDrawnNow = false;      // 本帧卡底是否已经由磨砂图铺好（避免再盖实心底）
        bool menuWarmed = false;          // 1.6：子菜单窗口是否已经预建过一次（治首次悬停卡顿）

        const int ULW_ALPHA = 2;
        const int AC_SRC_ALPHA = 1;
        const int GWL_EXSTYLE = -20;
        const uint WS_EX_TOPMOST = 0x8;
        const uint GW_HWNDNEXT = 2;
        // 1.5：看门狗快路径 —— 记住上次"最靠下的普通窗口"，位置没变就不必每 120ms 全 Z 序扫一遍
        IntPtr anchorLow = IntPtr.Zero;
        const uint GW_HWNDPREV = 3;
        const uint SWP_ZFLAGS = 0x1 | 0x2 | 0x10 | 0x200 | 0x400;

        public WidgetForm()
        {
            CityDb.Build();
            S = Settings.Load();
            M = new Metrics();
            P = Pal.Make(S.Style, S.Opacity);
            tmr.Alarm = S.TimerAlarm;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "About Time";
            DoubleBuffered = true;

            BuildRows();
            DateTime mt0 = MainToday();
            calY = mt0.Year; calM = mt0.Month;
            int w = WinW();
            int h = WinH();
            Size = new Size(w, h);
            Point pos = ResolveStartPosition(w, h);
            Location = pos;
            if (S.Parked) TryRestorePark();      // 上次是断屏期间退出的：原屏回来了就直接归位

            tick = new Timer();
            tick.Interval = 16;              // ≈60fps：滚屏更平滑
            tick.Tick += new EventHandler(OnTick);
            clockDt.Start();
            lastTickMs = clockDt.ElapsedMilliseconds;
            tick.Start();

            wdog = new Timer();
            wdog.Interval = 120;             // 层级守护：一次 Z 序扫描很便宜，缩短到 120ms 让 Win+D 几乎无感
            wdog.Tick += new EventHandler(OnWatchdog);
            wdog.Start();

            UpdateTimerMode();
            RememberScreen();
            try { SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; } catch (Exception) { }
            // 1.6：磨砂底是「身后那张壁纸」的拷贝，换壁纸 / 换主题 / 换配色都得重抓
            try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; } catch (Exception) { }
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // 只有「壁纸换了」（Desktop）才需要把壁纸层缓存整个重抓；
            // 其余外观类变化（配色 / 视觉样式）不影响磨砂内容，重渲染即可。
            // 1.6 监听了 General/Window/VisualStyle 等宽类别，General 类事件一来就整幅重抓壁纸 ——
            // 这正是 1.6 磨砂内存暴涨的触发面之一（现在重抓只是裁一块小图，代价已降两个数量级）。
            bool wall = (e.Category == UserPreferenceCategory.Desktop);
            // SystemEvents 的回调在线程池线程上，重绘必须回到 UI 线程
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (IsDisposed) return;
                    // 2026-11-17：「壁纸换了」也走延迟刷新 —— 这个事件常常早于合成器真正换图，
                    // 当场重抓会拿到上一张。交给看门狗，520ms 后指纹稳定了再抓。
                    if (wall) { ScheduleWallRefresh(); return; }
                    staticLayer = null;
                    RenderStatic();
                    PaintFrame();
                }));
            }
            catch (Exception) { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00080000;    // WS_EX_LAYERED
                cp.ExStyle &= ~0x00040000;   // 去掉 WS_EX_APPWINDOW
                cp.Style &= ~0x00020000;     // 去掉 WS_MINIMIZEBOX
                return cp;
            }
        }

        // ==================================================== 行与尺寸
        // 统一模块序列：主时钟(-1)、日历(-2)、世界城市(0..n-1) 同一个槽位序列，
        // MainPos / CalPos 都是"最终序列中的位置"，插入后自然同列排序。
        void BuildRows()
        {
            rows.Clear();
            // 1.5：所有模块位次在同一条 token 序列里算（关闭的模块也占位），
            // 「时钟管理」里的行序因此与实际版面一一对应，关掉 / 重开不会打乱别人的顺序
            List<int> seq = new List<int>();
            for (int i = 0; i < S.Cities.Count; i++) seq.Add(i);
            seq.Insert(ClampPos(S.MainPos, seq.Count), -1);
            seq.Insert(ClampPos(S.CalPos, seq.Count), -2);
            seq.Insert(ClampPos(S.TimerPos, seq.Count), -3);
            seq.Insert(ClampPos(S.GzPos, seq.Count), -4);
            // 1.9 修订：日历常挂位置改由四个锚点决定（全表最上 / 主时钟上方 / 主时钟下方 / 全表最下）。
            // 先按老索引占位，保证计时、干支的通位次照旧，再把日历挪到锚点指的位置 ——
            // 锚点是在「不含日历」的序列上算的，所以先摘掉日历再求下标。
            if (seq.IndexOf(-2) >= 0)
            {
                seq.RemoveAt(seq.IndexOf(-2));
                seq.Insert(CalAnchorIndex(seq), -2);
                int at = seq.IndexOf(-2);
                if (at >= 0) S.CalPos = at;        // 记下最终下标，时钟管理列表与老字段继续可用
            }
            float y = 0;
            for (int k = 0; k < seq.Count; k++)
            {
                Row r = new Row();
                int tok = seq[k];
                if (tok == -1)
                {
                    if (!S.ShowLocal) continue;                 // 关闭：不占版面
                    r.Local = true;
                    r.C = S.MainCity;          // 主时钟可为空（跟随系统本地时间）
                    r.H = Metrics.LocalH;
                }
                else if (tok == -2)
                {
                    if (S.CalMode != 1) continue;
                    r.Cal = true;
                    r.H = Metrics.CalH;
                }
                else if (tok == -3)
                {
                    if (!S.TimerOn) continue;
                    r.Timer = true;
                    r.H = Metrics.TimerH;
                }
                else if (tok == -4)
                {
                    if (!S.GzOn) continue;
                    r.Gz = true;
                    r.H = Metrics.GzH;
                }
                else
                {
                    r.C = S.Cities[tok];
                    r.H = Metrics.WorldH;
                }
                r.Y = y;
                y += r.H;
                rows.Add(r);
            }
        }

        static int ClampPos(int v, int n) { if (v < 0) return 0; if (v > n) return n; return v; }

        /// <summary>
        /// 1.9：日历锚点 → 插入下标（要求传入的序列**不含**日历 token）。
        /// 0 全表最上 = 0；3 全表最下 = 队尾；1/2 = 主时钟的前/后。
        /// 主时钟模块被关掉（S.ShowLocal=false）时：1 退化成 0（全表最上）、2 退化成队尾（全表最下）。
        /// </summary>
        int CalAnchorIndex(List<int> seq)
        {
            int a = S.CalAnchor;
            if (a < 0) a = 0;
            if (a > 3) a = 3;
            int mi = seq.IndexOf(-1);
            bool hasMain = S.ShowLocal && mi >= 0;
            if (a == 0) return 0;
            if (a == 1) return hasMain ? mi : 0;
            if (a == 2) return hasMain ? mi + 1 : seq.Count;
            return seq.Count;
        }
        int WinW() { return (int)Math.Round(Metrics.CardW * S.Scale); }
        int WinH() { return (int)Math.Round((TotalHeight() + Metrics.Pad * 2) * S.Scale); }

        float TotalHeight()
        {
            float h = 0;
            foreach (Row r in rows) h += r.H;
            if (h < Metrics.WorldH) h = Metrics.WorldH;
            return h;
        }

        int CountWorld()
        {
            int n = 0;
            foreach (Row r in rows) if (r.World) n++;
            return n;
        }

        Point ResolveStartPosition(int w, int h)
        {
            Rectangle pb = Screen.PrimaryScreen.Bounds;
            if (S.X == int.MinValue || S.Y == int.MinValue)
            {
                int x0 = pb.Left + (pb.Width - w) / 2;
                int y0 = pb.Top + (int)(80 * S.Scale);
                return new Point(x0, y0);
            }
            Point p = new Point(S.X, S.Y);
            // 越界回收：至少留 40px 在本屏
            Rectangle win = new Rectangle(p.X, p.Y, w, h);
            bool visible = false;
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(win, sc.Bounds);
                if (inter.Width >= 40 && inter.Height >= 40) { visible = true; break; }
            }
            if (!visible)
            {
                p.X = pb.Left + (pb.Width - w) / 2;
                p.Y = pb.Top + (int)(80 * S.Scale);
            }
            return p;
        }

        void Relayout(bool keepAnchor)
        {
            BuildRows();
            int w = WinW();
            int h = WinH();
            int ow = Width, oh = Height;
            if (keepAnchor)
            {
                Rectangle pb = Screen.FromRectangle(new Rectangle(Left, Top, Width, Height)).Bounds;   // 以窗口当前所在屏夹取
                int x = Math.Max(pb.Left, Math.Min(Left, pb.Right - w));
                int y = Math.Max(pb.Top, Math.Min(Top, pb.Bottom - h));
                SetBounds(x, y, w, h);
            }
            else SetBounds(Left, Top, w, h);
            if (ow != w || oh != h)
                FrostInvalidate();               // 1.7：模块增减/换位会改窗口尺寸，磨砂必须按新矩形重裁（1.6 漏了）
            staticLayer = null;
            RenderStatic();
            PaintFrame();
            SyncCalWindow();                     // 模块增减/换位置后，弹出日历的位置与尺寸跟着重算
            SaveSettings();
            UpdateTimerMode();
        }

        void SaveSettings()
        {
            if (suppressSave) return;
            S.X = Left;
            S.Y = Top;
            S.Save();
        }

        // ==================================================== 绘制入口
        /// <summary>自检：时钟管理里三模块的开/关 —— 行数不变、只是标「已关闭」，位次折算不变</summary>
        public string ClockManagerSelfTest()
        {
            System.Text.StringBuilder lg = new System.Text.StringBuilder();
            lg.AppendLine("== v1.5 clock manager selftest ==");
            int n0 = 0, s0 = 0;
            // 自检不依赖注册表里当前开关（用户可能把干支关着）：先统一置为全开，跑完再还原
            bool oL = S.ShowLocal, oT = S.TimerOn, oG = S.GzOn;
            S.ShowLocal = true; S.TimerOn = true; S.GzOn = true;
            ClockManagerDialog d = new ClockManagerDialog(S.ShowLocal, S.TimerOn, S.GzOn,
                S.MainCity, S.MainPos, S.TimerPos, S.GzPos, S.CalMode, S.CalPos, S.Cities);
            try
            {
                n0 = d.ItemCount(); s0 = d.ShownCount();
                lg.AppendLine("初始  主=" + d.ShowMain + " 计时=" + d.TimerOn + " 干支=" + d.GzOn
                    + "  行数=" + d.ItemCount() + "/显示=" + d.ShownCount()
                    + "  Pos 主/历/计/干=" + d.MainPos + "/" + d.CalPos + "/" + d.TimerPos + "/" + d.GzPos);
                lg.AppendLine(d.Dump());
                lg.AppendLine(d.TestToggle(4));
                lg.AppendLine(d.TestToggle(3));
                lg.AppendLine("关掉后 主=" + d.ShowMain + " 计时=" + d.TimerOn + " 干支=" + d.GzOn
                    + "  行数=" + d.ItemCount() + "/显示=" + d.ShownCount());
                lg.AppendLine(d.Dump());
                lg.AppendLine(d.TestToggle(0));
                lg.AppendLine(d.TestToggle(4));
                lg.AppendLine(d.TestToggle(3));
                lg.AppendLine(d.TestToggle(0));
                lg.AppendLine("全开后 主=" + d.ShowMain + " 计时=" + d.TimerOn + " 干支=" + d.GzOn
                    + "  行数=" + d.ItemCount() + "/显示=" + d.ShownCount()
                    + "  Pos 主/历/计/干=" + d.MainPos + "/" + d.CalPos + "/" + d.TimerPos + "/" + d.GzPos);
                lg.AppendLine(d.Dump());
                bool ok = (d.ItemCount() == n0 && d.ShownCount() == s0 && d.ShowMain && d.TimerOn && d.GzOn);
                lg.AppendLine("判定：" + (ok ? "PASS" : "FAIL"));
            }
            finally { try { d.Dispose(); } catch (Exception) { } S.ShowLocal = oL; S.TimerOn = oT; S.GzOn = oG; }
            return lg.ToString();
        }


        /// <summary>自检出图用：指定要渲染的月份</summary>
        public void SetPreviewMonth(int y, int mth)
        {
            if (mth < 1 || mth > 12) return;
            calY = y; calM = mth; calNav = true;
        }

        /// <summary>自检出图用：覆盖缩放比例（只影响本次离屏出图，不写回配置）</summary>
        public void SetPreviewScale(double sc)
        {
            if (sc < 0.6 || sc > 1.2) return;
            S.Scale = sc;
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            Size = new Size(WinW(), WinH());
        }

        /// <summary>自检出图用：覆盖主题（碳灰 / 雪白）；1.6 起另有 SetPreviewStyle 覆盖藏青</summary>
        public void SetPreviewTheme(bool dark)
        {
            S.Dark = dark;
            S.Style = dark ? 0 : 1;
            Settings.CurrentStyle = S.Style;
            P = Pal.Make(S.Style, S.Opacity);
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
        }

        /// <summary>自检出图用：覆盖主题三态（0 碳灰 / 1 雪白 / 2 藏青）</summary>
        public void SetPreviewStyle(int style)
        {
            S.Style = (style < 0 || style > 2) ? 0 : style;
            S.Dark = (S.Style != 1);
            Settings.CurrentStyle = S.Style;
            P = Pal.Make(S.Style, S.Opacity);
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
        }

        /// <summary>自检出图用：覆盖农历开关（看英文版「默认不显示农历」的样子）</summary>
        public void SetPreviewLunar(bool on)
        {
            S.CalLunar = on;
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
        }

        /// <summary>自检出图用：覆盖「总在最前」（只为看菜单项置灰状态，不落注册表）</summary>
        public void SetPreviewTopmost(bool on)
        {
            S.Topmost = on;
            TopMost = on;
        }

        /// <summary>自检：走真实的「切语言」路径（SetLang），再打开时钟管理，核对弹窗文案是否跟着变</summary>
        public string LangSwitchSelfTest(string pngPrefix)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("切换前 CurrentLang = " + Settings.CurrentLang + "，S.Lang = " + S.Lang);
            SetLang("en");
            sb.AppendLine("切到 en 后 CurrentLang = " + Settings.CurrentLang + "，S.Lang = " + S.Lang);
            ClockManagerDialog d1 = new ClockManagerDialog(S.ShowLocal, S.TimerOn, S.GzOn, S.MainCity,
                S.MainPos, S.TimerPos, S.GzPos, S.CalMode, S.CalPos, S.Cities);
            sb.AppendLine("时钟管理弹窗标题 = " + d1.Text);
            sb.AppendLine("第 1 行 = " + RowTextOf(d1, 0));
            sb.AppendLine("提示首行 = " + FirstLabelOf(d1));
            ClockManagerSnapshotSave(pngPrefix + "_en_cmgr.png", 0);
            d1.Dispose();
            SetLang("zh");
            sb.AppendLine("切回 zh 后 CurrentLang = " + Settings.CurrentLang);
            ClockManagerDialog d2 = new ClockManagerDialog(S.ShowLocal, S.TimerOn, S.GzOn, S.MainCity,
                S.MainPos, S.TimerPos, S.GzPos, S.CalMode, S.CalPos, S.Cities);
            sb.AppendLine("切回后标题 = " + d2.Text);
            ClockManagerSnapshotSave(pngPrefix + "_zh_cmgr.png", 0);
            d2.Dispose();
            return sb.ToString();
        }

        static string RowTextOf(Control root, int idx)
        {
            foreach (Control c in root.Controls)
            {
                if (c is ListBox)
                {
                    ListBox lb = (ListBox)c;
                    if (idx < lb.Items.Count) return Convert.ToString(lb.Items[idx]);
                    return "(空)";
                }
                Control r = c;
                if (c.HasChildren)
                {
                    string x = RowTextOf(c, idx);
                    if (x != null) return x;
                }
            }
            return "(未找到列表)";
        }

        static string FirstLabelOf(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Label && c.Text != null && c.Text.Length > 0)
                    return c.Text.Replace("\r\n", " / ").Replace("\n", " / ");
                if (c.HasChildren)
                {
                    string x = FirstLabelOf(c);
                    if (x != null) return x;
                }
            }
            return "(未找到标签)";
        }

        /// <summary>自检用：强制打开磨砂（不看注册表），并立即重做静态层</summary>
        public void PreviewBlurOn()
        {
            S.Blur = true;
            S.Topmost = false;
            TopMost = false;
            WallpaperFrost.InvalidateCache();       // 1.8.2：自检也要拿当前壁纸，不能吃旧缓存
            wallSigLast = "";
            FrostInvalidate();
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            RenderStatic();
            PaintFrame();
        }

        /// <summary>自检抓图用：把预览窗强行顶到最前（不写配置，只为 CopyFromScreen 不被别的窗口挡）</summary>
        public void ForceTopForTest()
        {
            TopMost = true;
            try
            {
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);   // HWND_TOPMOST + NOSIZE / NOMOVE / SHOWWINDOW
                SetForegroundWindow(Handle);
            }
            catch (Exception) { }
        }

        /// <summary>自检用：把当前磨砂底图交出去（抓不到返回 null）</summary>
        public Bitmap FrostBmpForTest() { return FrostBmp(); }

        /// <summary>自检抓图用：把当前合成帧（含磨砂 alpha 与文字）拷一份出去，用于叠到真实桌面上出图</summary>
        public Bitmap CompositeBmpForTest()
        {
            if (frame == null || frame.Width <= 0 || frame.Height <= 0) return null;
            Bitmap copy = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(copy)) g.DrawImageUnscaled(frame, 0, 0);
            return copy;
        }

        /// <summary>自检出图用：覆盖日历显示方式（0 关闭 / 1 常挂 / 2 弹出）</summary>
        public void SetPreviewMode(int mode)
        {
            if (mode < 0 || mode > 2) return;
            S.CalMode = mode;
            S.ShowCal = (mode == 1);
            BuildRows();
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            Size = new Size(WinW(), WinH());
        }

        /// <summary>自检用：覆盖日历常挂位置的四档锚点与主时钟开关，然后重算行序</summary>
        public void SetPreviewCalAnchor(int anchor, bool mainOn)
        {
            if (anchor < 0) anchor = 0;
            if (anchor > 3) anchor = 3;
            S.CalAnchor = anchor;
            S.ShowLocal = mainOn;
            BuildRows();
        }

        /// <summary>自检出图用：覆盖界面语言</summary>
        public static string PreviewCC;   // v2.1 出图用的国家码覆盖（null 表示跟随配置）
        public void SetPreviewLang(string lang)
        {
            if (lang != "zh" && lang != "en") return;
            S.Lang = lang; Settings.CurrentLang = lang;
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            Size = new Size(WinW(), WinH());
        }

        /// <summary>v2.1 自检出图用：强制主时钟/系统地区的国家码（验证四种提示口径），传空则恢复跟随配置</summary>
        public void SetPreviewCC(string cc)
        {
            PreviewCC = (cc != null && cc.Length == 2) ? cc.ToUpperInvariant() : null;
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            Size = new Size(WinW(), WinH());
        }

        /// <summary>2.0 自检出图用：切换主时钟皮肤（0 原样 / 1 中文），并立即重做静态层</summary>
        public void SetPreviewSkin(int v)
        {
            S.MainSkin = (v == 1) ? 1 : 0;
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            RenderStatic();
            PaintFrame();
        }

        /// <summary>自检出图用：覆盖背景透明度（0-100），并立即重做静态层看效果</summary>
        public void SetPreviewOpacity(int pct)
        {
            if (pct < 10) pct = 10;
            if (pct > 100) pct = 100;
            S.Opacity = pct;
            P = Pal.Make(S.Style, S.Opacity);
            FrostInvalidate();
            staticLayer = null;
            _frostDrawnNow = false;
            RenderStatic();
            PaintFrame();
        }

        /// <summary>自检出图用：覆盖干支模块的显隐</summary>
        public void SetPreviewGz(bool on)
        {
            S.GzOn = on;
            if (on && S.GzPos < 0) S.GzPos = 0;
            BuildRows();
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            Size = new Size(WinW(), WinH());
        }

        /// <summary>自检出图用：覆盖「倒计时 / 秒表」模块的显隐</summary>
        public void SetPreviewTimerOn(bool on)
        {
            S.TimerOn = on;
            BuildRows();
            if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
            Size = new Size(WinW(), WinH());
        }

        /// <summary>自检出图用：把干支换算框离屏画一张</summary>
        public void GzPopupSnapshotSave(string path)
        {
            GzPopup f = new GzPopup(P, S.Lang == "en", DateTime.Now);
            try { f.SnapshotSave(path); }
            finally { f.Dispose(); }
        }

        /// <summary>离屏画一张"弹出式日历"整卡（含卡片底 + 收回按钮）</summary>
        public void PopupWindowSnapshotSave(string path, bool paleBg)
        {
            double sc = S.Scale;
            int cw = Metrics.W + Metrics.Pad * 2;
            int chh = Metrics.CalH + Metrics.Pad * 2;
            int w = (int)Math.Round(cw * sc), h = (int)Math.Round(chh * sc);
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    if (paleBg) { g.Clear(Color.FromArgb(236, 236, 238)); }
                    else g.Clear(Color.FromArgb(26, 28, 32));
                    Pal p = Pal.Make(S.Style, 100);
                    int r = (int)Math.Round(Metrics.R * sc);
                    using (GraphicsPath gp = RoundRect(new RectangleF(0, 0, w, h), r))
                    {
                        using (SolidBrush b = new SolidBrush(p.Card)) g.FillPath(b, gp);
                        using (Pen pen = new Pen(p.Edge, 1f)) g.DrawPath(pen, gp);
                    }
                    PopupPaint(g);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>离屏渲染一张成品图（自检 / 出图用，不起窗口、不推分层表面）</summary>
        public void SnapshotSave(string path, bool paleBg)
        {
            RenderStatic();
            try
            {
                System.Text.StringBuilder lg = new System.Text.StringBuilder();
                lg.AppendLine("cc=" + CalCC() + " calMode=" + S.CalMode + " calPos=" + S.CalPos + " lunar=" + S.CalLunar
                              + " dark=" + S.Dark + " op=" + S.Opacity + " lang=" + S.Lang);
                lg.AppendLine("city=" + (S.MainCity != null ? S.MainCity.En + "/" + S.MainCity.CC + "/" + S.MainCity.Tz : "null"));
                lg.AppendLine("holidays ready");
                lg.AppendLine("ink fest4=" + Ui.Ink(M.FCalFest, "退伍军人").Width + " lun2=" + Ui.Ink(M.FCalLun, "廿一").Width
                              + " day2=" + (M.CalDayCell * 2f) + " dayInk=" + M.CalDayInkH + " title=" + Ui.Ink(M.FCalTitle, "2026年11月").Height
                              + " week=" + Ui.Ink(M.FCalWeek, "日").Height);
                CalMonthModel mm = CalModel();
                float h1, h2, h3;
                float gt = CalHeaderBottom(out h1, out h2, out h3);
                float ch = CalCellH(gt, mm.Rows);
                lg.AppendLine("geo gridTop=" + gt.ToString("0.0") + " cellH=" + ch.ToString("0.0") + " rows=" + mm.Rows
                              + " calH=" + Metrics.CalH + " sumBottom=" + (gt + ch * mm.Rows).ToString("0.0"));
                for (int i = 0; i < mm.Cells.Length; i++)
                {
                    CalDay d0 = mm.Cells[i];
                    if (d0 == null) continue;
                    if (i / 7 != mm.Rows - 1) continue;
                    float lh0 = d0.LunarText.Length > 0 ? Ui.Ink(M.FCalLun, d0.LunarText).Height : 0f;
                    lg.AppendLine("lastrow col=" + (i % 7) + " ctop=" + (gt + (i / 7) * ch).ToString("0.0")
                                  + " dayTop=" + (gt + (i / 7) * ch + 2f).ToString("0.0")
                                  + " lunarH=" + lh0.ToString("0.0")
                                  + " lunarTop=" + (gt + (i / 7) * ch + 2f + M.CalDayInkH - lh0).ToString("0.0") + " text=" + d0.LunarText);
                }
                foreach (CalDay d in mm.Cells)
                {
                    if (d == null) continue;
                    lg.AppendLine(d.Date.ToString("MM-dd") + (d.IsAdjacent ? "(adj)" : "") + "  lun=" + d.LunarText
                                  + "  fest=" + string.Join(",", d.Fest.ToArray()) + (d.More > 0 ? " +" + d.More : ""));
                }
                System.IO.File.WriteAllText(path + ".log", lg.ToString(), System.Text.Encoding.UTF8);
            }
            catch (Exception) { }
            if (staticLayer == null) return;
            int w = staticLayer.Width, h = staticLayer.Height;
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics gg = Graphics.FromImage(bmp))
                {
                    gg.Clear(paleBg ? Color.FromArgb(255, 236, 236, 238) : Color.FromArgb(255, 26, 28, 32));
                    gg.DrawImageUnscaled(staticLayer, 0, 0);
                    GraphicsState st = gg.Save();
                    gg.ScaleTransform((float)S.Scale, (float)S.Scale);
                    gg.TranslateTransform(Metrics.Pad, Metrics.Pad);
                    foreach (Row r in rows)
                    {
                        if (r.World) DrawMarquee(gg, r, DateTime.Now);
                        else if (r.Timer)
                        {
                            GraphicsState stT2 = gg.Save();
                            gg.TranslateTransform(0f, r.Y);
                            TimerView.DrawLive(gg, P, M, S.Lang == "en", tmr);
                            gg.Restore(stT2);
                        }
                        else if (r.Local) DrawLocalColon(gg, r, true);   // 截图里冒号常亮
                    }
                    gg.Restore(st);
                    gg.Flush();
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (S.Topmost) TopMost = true;
            RenderStatic();
            PaintFrame();
        }

        protected override void OnPaint(PaintEventArgs e) { }   // 分层窗口不走 WM_PAINT

        void RenderStatic()
        {
            if (IsDisposed) return;
            int w = Width, h = Height;
            if (staticLayer == null || staticLayer.Width != w || staticLayer.Height != h)
            {
                if (staticLayer != null) staticLayer.Dispose();
                staticLayer = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            using (Graphics g = Graphics.FromImage(staticLayer))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // 1.7.2 背景磨砂：磨砂层是「糊过的壁纸 + 主题色」的**不透明**成品，「背景透明度」
                // 已经在 Grab 里按 t = TintOf×op 折进颜色（10% = 几乎只见糊壁纸，100% = 几乎纯色卡）。
                // 这里整块铺底即可，绝不按 alpha 往下压 —— 一旦按 (1-op) 掺回清晰桌面，
                // 壁纸边缘就保持 (1-op) 的对比度，磨砂开到 50% 时看上去和没糊一样（1.7.1 的翻车点）。
                _frostDrawnNow = false;
                Bitmap fb = FrostBmp();
                if (fb != null)
                {
                    float r = (float)Math.Round(Metrics.R * S.Scale);
                    using (GraphicsPath gp = RoundRect(new RectangleF(0f, 0f, w, h), r))
                    {
                        g.SetClip(gp);
                        g.DrawImage(fb, 0, 0, w, h);
                        g.ResetClip();
                    }
                    _frostDrawnNow = true;
                }

                g.ScaleTransform((float)S.Scale, (float)S.Scale);

                int dh = (int)Math.Round(TotalHeight());
                DrawCard(g, dh);                                  // 卡片（含每边 10px 留白）
                g.TranslateTransform(Metrics.Pad, Metrics.Pad);    // 之后全部按内容设计坐标绘制
                foreach (Row r in rows)
                {
                    if (r.Cal)
                    {
                        GraphicsState stCal = g.Save();
                        g.TranslateTransform(0f, r.Y);
                        DrawCal(g, DateTime.Now);
                        g.Restore(stCal);
                    }
                    else if (r.Local)
                    {
                        // 本地模块可能不在最上（主时钟可上下移动），按行槽偏移绘制
                        GraphicsState stRow = g.Save();
                        g.TranslateTransform(0f, r.Y);
                        DrawLocal(g);
                        g.Restore(stRow);
                    }
                    else if (r.Timer)
                    {
                        GraphicsState stT = g.Save();
                        g.TranslateTransform(0f, r.Y);
                        TimerView.DrawStatic(g, P, M, S.Lang == "en", tmr);
                        g.Restore(stT);
                    }
                    else if (r.Gz)
                    {
                        GraphicsState stG = g.Save();
                        g.TranslateTransform(0f, r.Y);
                        GzView.DrawStatic(g, P, M, S.Lang == "en", DateTime.Now);
                        g.Restore(stG);
                    }
                    else DrawWorld(g, r, DateTime.Now);
                }
                DrawSeparators(g);
            }
        }

        /// <summary>模块之间的分隔发丝线</summary>
        void DrawSeparators(Graphics g)
        {
            if (rows.Count < 2) return;
            using (Pen pen = new Pen(P.Line, 1f))
            {
                for (int i = 1; i < rows.Count; i++)
                {
                    float y = (float)Math.Round(rows[i].Y) + 0.5f;
                    g.DrawLine(pen, 18f, y, Metrics.W - 18f, y);
                }
            }
        }

        void PaintFrame()
        {
            if (staticLayer == null || IsDisposed) return;
            CreateSurface(Width, Height);
            if (frameG == null || frame == null) return;      // DIB 创建失败：不再抛空引用，等下一拍重试
            frameG.Clear(Color.Transparent);
            frameG.DrawImageUnscaled(staticLayer, 0, 0);
            GraphicsState st0 = frameG.Save();
            frameG.ScaleTransform((float)S.Scale, (float)S.Scale);
            frameG.TranslateTransform(Metrics.Pad, Metrics.Pad);
            foreach (Row r in rows)
            {
                if (r.World) DrawMarquee(frameG, r, DateTime.Now);
                else if (r.Timer)
                {
                    GraphicsState stT = frameG.Save();
                    frameG.TranslateTransform(0f, r.Y);
                    TimerView.DrawLive(frameG, P, M, S.Lang == "en", tmr);
                    frameG.Restore(stT);
                }
                else if (r.Local) DrawLocalColon(frameG, r, false);
            }
            frameG.Restore(st0);
            frameG.Flush();
            PushLayered();
        }

        void CreateSurface(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            if (memDC != IntPtr.Zero && surW == w && surH == h) return;
            ReleaseSurface();
            IntPtr screen = GetDC(IntPtr.Zero);
            memDC = CreateCompatibleDC(screen);
            ReleaseDC(IntPtr.Zero, screen);

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;      // 自上而下
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;  // BI_RGB
            hDib = CreateDIBSection(memDC, ref bmi, 0, out dibBits, IntPtr.Zero, 0);
            if (hDib == IntPtr.Zero || dibBits == IntPtr.Zero)
            {
                if (memDC != IntPtr.Zero) { DeleteDC(memDC); memDC = IntPtr.Zero; }
                frame = null; frameG = null; surW = 0; surH = 0;
                return;
            }
            oldDib = SelectObject(memDC, hDib);

            try
            {
                frame = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, dibBits);
                frameG = Graphics.FromImage(frame);
            }
            catch (Exception)
            {
                if (frame != null) { frame.Dispose(); frame = null; }
                frameG = null;
                ReleaseSurface();
                return;
            }
            frameG.SmoothingMode = SmoothingMode.AntiAlias;
            frameG.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            frameG.PixelOffsetMode = PixelOffsetMode.HighQuality;
            surW = w; surH = h;
        }

        void ReleaseSurface()
        {
            try { if (frameG != null) { frameG.Dispose(); frameG = null; } } catch (Exception) { }
            try { if (frame != null) { frame.Dispose(); frame = null; } } catch (Exception) { }
            if (oldDib != IntPtr.Zero && memDC != IntPtr.Zero) { SelectObject(memDC, oldDib); oldDib = IntPtr.Zero; }
            if (hDib != IntPtr.Zero) { DeleteObject(hDib); hDib = IntPtr.Zero; }
            if (memDC != IntPtr.Zero) { DeleteDC(memDC); memDC = IntPtr.Zero; }
        }

        /// <summary>屏幕 DC 只取一次，之后每帧复用（UpdateLayeredWindow 的 hdcDst 参数）</summary>
        IntPtr ScreenDC()
        {
            if (screenDC == IntPtr.Zero) screenDC = GetDC(IntPtr.Zero);
            return screenDC;
        }

        void PushLayered()
        {
            if (!IsHandleCreated || memDC == IntPtr.Zero) return;
            IntPtr screen = ScreenDC();
            SIZE size = new SIZE();
            size.CX = surW; size.CY = surH;
            POINT src = new POINT();
            POINT dst = new POINT();
            dst.X = Left; dst.Y = Top;
            BLENDFUNC blend = new BLENDFUNC();
            blend.BlendOp = 0;                              // AC_SRC_OVER
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = (byte)AC_SRC_ALPHA;         // 逐像素 alpha
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, memDC, ref src, 0, ref blend, ULW_ALPHA);
        }

        // ==================================================== 卡片
        void DrawCard(Graphics g, int dh)
        {
            float ch = dh + Metrics.Pad * 2;
            RectangleF rr = new RectangleF(0.5f, 0.5f, Metrics.CardW - 1f, ch - 1f);
            using (GraphicsPath path = RoundRect(rr, Metrics.R))
            {
                // 磨砂已铺好底：只描边，不再盖实心（否则把壁纸糊层全遮住）
                if (!_frostDrawnNow)
                    using (SolidBrush b = new SolidBrush(P.Card)) g.FillPath(b, path);
                else
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(26, P.Card))) g.FillPath(b, path);  // 极薄一层主色，压住壁纸花
                using (Pen pen = new Pen(P.Edge, 1f)) g.DrawPath(pen, path);
            }
        }

        /// <summary>磨砂是否生效：勾了磨砂且没有「总在最前」</summary>
        bool FrostActive() { return S.Blur && !S.Topmost; }

        /// <summary>
        /// 让磨砂底图缓存失效（移动 / 换主题 / 换缩放 / 换壁纸 / 拔插屏 / 开关模块时调用）。
        /// 1.7：整幅壁纸层缓存另由 WallpaperFrost.InvalidateCache() 管，这里只管挂件自己的裁剪成品。
        /// </summary>
        void FrostInvalidate()
        {
            frostDirty = true;
            if (frostBmp != null) { try { frostBmp.Dispose(); } catch (Exception) { } frostBmp = null; }
            CalFrostInvalidate();       // 1.8.3：弹出日历的磨砂底图一并作废（换壁纸/开关磨砂/挪主窗）
        }

        /// <summary>取（必要时重抓）磨砂底图。抓不到就返回 null，退回实心卡片</summary>
        Bitmap FrostBmp()
        {
            if (!FrostActive()) return null;
            if (!Visible || IsDisposed) return null;
            // 1.7：删掉 1.6 的 Left/Top < -1000 一刀切 —— 那会把摆在负坐标副屏（如 -2560）上的
            // 真窗口也误杀。出图隔离改由 WallpaperFrost.Grab 自判：窗口矩形落在壁纸层范围外
            // （--shot 的离屏位置）自然拿不到磨砂，返回 null 退回实心卡片。
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return null;
            if (frostBmp != null && !frostDirty) return frostBmp;
            frostDirty = false;
            if (frostBmp != null) { try { frostBmp.Dispose(); } catch (Exception) { } frostBmp = null; }
            try { frostBmp = WallpaperFrost.Grab(new Rectangle(Left, Top, w, h), S.Style, S.Scale, S.Opacity, S.BlurLevel); }
            catch (Exception) { frostBmp = null; }
            return frostBmp;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ==================================================== 本地模块（主时钟）
        void DrawLocal(Graphics g)
        {
            DateTime now = DateTime.Now;
            if (S.MainCity != null)
            {
                try { now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, S.MainCity.Zone); }
                catch (Exception) { now = DateTime.Now; }
            }
            if (PreviewClock.HasValue)                     // 自检：把主时钟的时刻定死，专看大字体排布
                now = PreviewClock.Value.Date.AddHours(PreviewClock.Value.Hour).AddMinutes(PreviewClock.Value.Minute);

            // 2.0：主时钟中文皮肤，整套换版式（模块高度仍是 Metrics.LocalH，不改变挂件尺寸）
            if (S.MainSkin == 1) { DrawLocalCn(g, now); return; }

            string[] wk = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };
            int today = ((int)now.DayOfWeek + 6) % 7;
            for (int i = 0; i < 7; i++)
            {
                // 每行占一个固定行槽，按各自墨迹高度贴槽底对齐：槽距一致，字大字小都不挤
                float slotBottom = M.WeekTop + (i + 1) * M.WeekPitch;
                bool isToday = (i == today);
                if (isToday)
                {
                    float y = slotBottom - M.WeekInkH;
                    using (SolidBrush b = new SolidBrush(P.AccBright))
                        Ui.DrawAt(g, wk[i], M.FWeek, b, 22f, y);
                }
                else
                {
                    float y = slotBottom - M.WeekDimInkH;
                    using (SolidBrush b = new SolidBrush(P.WeekDim))
                        Ui.DrawAt(g, wk[i], M.FWeekDim, b, 22f, y);
                }
            }
            using (SolidBrush b = new SolidBrush(P.Acc))
                Ui.DrawRight(g, now.ToString("yyyy", CultureInfo.InvariantCulture), M.FYear, b, 478f, 15f);

            string hh = now.ToString("HH", CultureInfo.InvariantCulture);
            string mi = now.ToString("mm", CultureInfo.InvariantCulture);
            // 1.6：日历关闭 / 弹出模式下，时:分下方加一行节日提示小字；
            // v2.1：改用统一口径（语言 × 所在地），字号抬到 22、颜色标橙，
            // 有这行时把时分上抬到星期栏首行的上沿，让「时分 + 小字」整块落回竖中线上
            float tipAvail = Math.Max(120f, M.TimeFreeR - M.TimeFreeL);
            string fest = MainFestTip(M.FFestSmall, tipAvail);
            float midTop = (Metrics.LocalH - M.InkTimeH) / 2f;          // 无提示时的居中位置
            float monTop = M.WeekTop + M.WeekPitch - M.WeekInkH;        // 星期栏首行「MON」的墨迹上沿
            float shift = (fest.Length > 0) ? (midTop - monTop) : 0f;
            if (shift < 0f) shift = 0f;
            _mainTimeShift = shift;
            float inkTop = midTop - shift;
            // 1.4：时分分开画，冒号挪到动态层按秒闪动
            Sgn.Draw(g, hh, M.FTime, M.TimeLeft, inkTop, M.InkTimeH, M.CellTime, 0.42f, P.Num, P.Glow, 2);
            float minX = M.TimeLeft + M.CellTime * 2f + M.CellTime * 0.42f;
            Sgn.Draw(g, mi, M.FTime, minX, inkTop, M.InkTimeH, M.CellTime, 0.42f, P.Num, P.Glow, 2);

            if (fest.Length > 0)
            {
                float cx = M.TimeLeft + M.TimeBlockW / 2f;
                // v2.1：下沿与右侧日期块的下沿（MdTopD + 墨迹高）齐平，颜色标橙
                float tipBottom = M.MdTopD + M.InkMD_H;
                float tipH = (float)Ui.Ink(M.FFestSmall, "元").Height;
                // 2.0：这一行按「·」拆段上色，除夕 / 春节 / 元宵 的那一段标红
                DrawFestCentered(g, fest, M.FFestSmall, cx, tipBottom - tipH, P.Acc);
            }

            DrawMd(g, now.Month, now.Day, M.MdX, M.MdTopM, M.MdTopD, M.FMD, M.CellMD, M.InkMD_H, 1.15f, M.FMdLabel, 478f);
        }

        /// <summary>
        /// 2.0：把「甲·乙」这种节日行按分隔符拆段上色后整体居中（红节日那一段用 FestRed）。
        /// v2.1：段与段之间那个「·」由这里补画（原实现 split 后把点丢了，两条节日会粘在一起）。
        /// </summary>
        void DrawFestCentered(Graphics g, string line, Font f, float cx, float top, Color normal)
        {
            string[] segs = line.Split('·');
            float dotW = Ui.AdvW(f, "·");
            Rectangle dotInk = Ui.Ink(f, "·");
            // v2.1 修（审阅 P1-2）：原实现把「·」的**墨迹顶**对齐到整行墨迹顶，而「·」墨迹只有几像素
            // 且本来就靠上，结果贴到行顶像撇号。改成按墨迹高差在行内竖直居中。
            float lineInkH = (float)Ui.Ink(f, "元").Height;
            float dotDy = (lineInkH - (float)dotInk.Height) / 2f;
            float total = 0f;
            for (int i = 0; i < segs.Length; i++) total += Ui.AdvW(f, segs[i]);
            total += dotW * (segs.Length - 1);
            float x = cx - total / 2f;
            for (int i = 0; i < segs.Length; i++)
            {
                if (i > 0 && dotW > 0f)
                {
                    using (SolidBrush b = new SolidBrush(normal))
                        g.DrawString("·", f, b, x - dotInk.X, top - dotInk.Y + dotDy, StringFormat.GenericTypographic);
                    x += dotW;
                }
                if (segs[i].Length == 0) continue;
                Rectangle ink = Ui.Ink(f, segs[i]);
                Color c = IsRedFest(segs[i].Trim()) ? FestRed : normal;
                using (SolidBrush b = new SolidBrush(c))
                    g.DrawString(segs[i], f, b, x - ink.X, top - ink.Y, StringFormat.GenericTypographic);
                x += Ui.AdvW(f, segs[i]);
            }
        }

        /// <summary>1.4：主时钟的冒号按秒闪（偶数秒亮、奇数秒灭），画在动态层</summary>
        void DrawLocalColon(Graphics g, Row r, bool force)
        {
            // 2.0：中文皮肤里随秒闪的是时分之间那个「·」
            if (S.MainSkin == 1) { DrawLocalCnDot(g, r, force); return; }
            if (!force && (DateTime.Now.Second % 2) == 1) return;
            float inkTop = r.Y + (Metrics.LocalH - M.InkTimeH) / 2f - _mainTimeShift;
            float x = M.TimeLeft + M.CellTime * 2f;
            Sgn.Draw(g, ":", M.FTime, x, inkTop, M.InkTimeH, M.CellTime, 0.42f, P.Num, P.Glow, 2);
        }

        // ==================================================== 2.0 主时钟中文皮肤
        // 版式（设计坐标，内容宽 500 / 模块高 158）：
        //   左竖  公元 + 年份 + 年        中部偏左  四个繁写大字 + 「·」        下方居中  公历·星期·节日
        //   右侧三列（左→右）  节气或传统节日 / 农历月·日 / 农历干支·生肖年
        // 注：qiji 字库里「叁」被映射到了「三」的同一个字形，大写三用同族的「叄」才画得出正确笔画
        static readonly string[] CnDa = { "零", "壹", "貳", "叄", "肆", "伍", "陸", "柒", "捌", "玖" };
        static readonly string[] CnSx = { "〇", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        static readonly string[] WkSx = { "日", "一", "二", "三", "四", "五", "六" };
        static readonly string[] ZhiSx = { "鼠", "牛", "虎", "兔", "龍", "蛇", "馬", "羊", "猴", "雞", "狗", "豬" };
        static readonly string[] ZhiCh = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };

        // 三个节日在程序内一律用这个红（216,0,0）
        public static readonly Color FestRed = Color.FromArgb(216, 0, 0);
        // v2.1 修（审阅 P1-3）：原数组把「春节」写了两遍（无害但冗余）；「元宵节」已被「元宵」前缀覆盖
        static readonly string[] RedFest = { "除夕", "春节", "元宵" };

        // v2.1 修（审阅 P1-3）：英数皮肤走 PrettyEn 后拿到的是英文全称（Spring Festival / Lantern Festival），
        // 只认中文的 StartsWith 匹配不上 → 英文界面下春节/元宵不标红。补一列英文别名。
        static readonly string[] RedFestEn =
        {
            "Chinese New Year's Eve", "New Year's Eve", "Chinese New Year", "Lunar New Year",
            "Spring Festival", "Lantern Festival"
        };

        /// <summary>节日名是否属于「除夕 / 春节 / 元宵」这三个要标红的（中英文名都认）</summary>
        public static bool IsRedFest(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < RedFest.Length; i++)
                if (name.StartsWith(RedFest[i], StringComparison.Ordinal)) return true;
            for (int i = 0; i < RedFestEn.Length; i++)
                if (name.StartsWith(RedFestEn[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 中文皮肤里节日名用简体（下方那行），三列里用繁體
        static readonly string[] TradSrc = { "春节", "元宵节", "端午节", "中元节", "中秋节", "重阳节", "腊八节", "寒衣节", "腊八", "腊月", "闰", "腊" };
        static readonly string[] TradDst = { "春節", "元宵節", "端午節", "中元節", "中秋節", "重陽節", "臘八節", "寒衣節", "臘八", "臘月", "閏", "臘" };
        static readonly string[] TermSrc = { "惊蛰", "谷雨", "小满", "芒种", "处暑" };
        static readonly string[] TermDst = { "驚蟄", "穀雨", "小滿", "芒種", "處暑" };
        static readonly string[] TradFestNames = { "春节", "元宵节", "端午节", "七夕", "中元节", "中秋节", "重阳节", "腊八节", "小年", "寒衣节", "除夕", "清明" };

        static string ToTrad(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            for (int i = 0; i < TradSrc.Length; i++) s = s.Replace(TradSrc[i], TradDst[i]);
            for (int i = 0; i < TermSrc.Length; i++) s = s.Replace(TermSrc[i], TermDst[i]);
            return s;
        }

        static bool IsTradFest(string zh)
        {
            if (string.IsNullOrEmpty(zh)) return false;
            for (int i = 0; i < TradFestNames.Length; i++)
                if (zh == TradFestNames[i] || zh.StartsWith(TradFestNames[i])) return true;
            return false;
        }

        /// <summary>简体中文数字（下方公历行 / 农历月日都用它）</summary>
        static string CnPlain(int v)
        {
            if (v <= 0) return "零";
            if (v < 10) return CnSx[v];
            int t = v / 10, o = v % 10;
            string s = (t == 1) ? "十" : CnSx[t] + "十";
            if (o > 0) s += CnSx[o];
            return s;
        }

        /// <summary>繁写大写数字（时与分的大字）</summary>
        static string CnDaY(int v)
        {
            if (v <= 0) return CnDa[0];
            if (v < 10) return CnDa[v];
            int t = v / 10, o = v % 10;
            string s = (t == 1) ? "拾" : CnDa[t] + "拾";
            if (o > 0) s += CnDa[o];
            return s;
        }

        /// <summary>
        /// 2.0 大字的小时文本（不含「時」，最多两个字）：
        /// 1~10 → 壹…玖、拾；11~20 → 拾壹…拾玖、貳拾（「拾」代十位的 1）；21~23 → 貳壹、貳貳、貳叁
        /// </summary>
        static string CnHourTxt(int hh)
        {
            if (hh <= 0) return CnDa[0];
            if (hh < 10) return CnDa[hh];
            if (hh == 10) return "拾";
            if (hh < 20) return "拾" + CnDa[hh - 10];
            if (hh == 20) return "貳拾";
            return CnDa[hh / 10] + CnDa[hh % 10];           // 21~23：貳壹 / 貳貳 / 貳叁
        }

        /// <summary>
        /// 2.0 大字的分钟文本（逐位取繁写大写，固定两个字）：01~09 → 零壹…零玖；10~59 → 壹零、壹壹…伍玖
        /// </summary>
        static string CnMinTxt(int mm)
        {
            if (mm <= 0) return "";
            if (mm < 10) return CnDa[0] + CnDa[mm];
            return CnDa[mm / 10] + CnDa[mm % 10];
        }

        // 中文皮肤里「·」的位置与整块横向范围（静态层算好，动态层照着画；双击判定也用它）
        float _cnBlockL = 40f, _cnBlockR = 408f;
        float _cnDotCx = 220f, _cnDotCy = 62f;
        bool _cnDotValid;
        public static string CnDebug = "";      // 2.0 自检：把大字排版的中间量记下来

        /// <summary>2.0：主时钟中文皮肤的静态层（「·」不画，留给动态层按秒闪）</summary>
        void DrawLocalCn(Graphics g, DateTime now)
        {
            _cnDotValid = false;
            // 日期类信息统一取「主时钟显示的那一天」（预览时也一致；正常运行时就是今天）
            DateTime day = MainToday();
            // ---------- 左竖：公元 + 年份 + 年 ----------
            string ys = "";
            int yv = day.Year;
            string yss = yv.ToString(CultureInfo.InvariantCulture);
            foreach (char c in yss) ys += CnSx[(int)(c - '0')];
            DrawCnVCol(g, new string[] { "公", "元" }, new Color[] { P.Acc, P.Acc },
                        new string[] { }, new Color[] { }, 22f, 10f);
            // 年份数字逐位竖排（思源宋 Regular）
            float stepY = 20f;
            float ytop = 10f + 2f * stepY;
            for (int i = 0; i < ys.Length; i++)
            {
                using (SolidBrush b = new SolidBrush(P.Num))
                    Ui.DrawCenter(g, ys[i].ToString(), M.FCnYearNum, b, 22f, ytop + i * stepY);
            }
            using (SolidBrush b2 = new SolidBrush(P.Acc))
                Ui.DrawCenter(g, "年", M.FCnYear, b2, 22f, ytop + ys.Length * stepY);

            // ---------- 中部偏左：时、分大字（1~4 个数字位 + 「時」+ 「·」） ----------
            int hh = now.Hour, mm = now.Minute;
            string hs = CnHourTxt(hh);                  // 1~2 字，不含「時」
            string ms = CnMinTxt(mm);                   // 2 字；整点为空
            bool whole = (mm == 0);                     // 整点：只有「X時」，不画「·」
            // 「時」只在两种情况下出现：① 1~10 点（本来就两字，凑够两字位）；② 整点那 1 分钟。
            // 11 点以后非整点一律省掉「時」，保证「时·分」永远不超过 4 个汉字（18:19 = 拾捌·壹玖）。
            bool needShi = (hh <= 10) || whole;

            List<string> items = new List<string>();
            for (int i = 0; i < hs.Length; i++) items.Add(hs.Substring(i, 1));
            if (needShi) items.Add("時");
            Font bigF = M.FCnBig;
            if (whole)
            {
                if (items.Count == 2) bigF = M.FCnBig2;       // 两字：加大一号 + 全角空格
                else if (items.Count == 3) bigF = M.FCnBig3;  // 三字：只加大一号
                // 四字：常态
            }
            else
            {
                items.Add(CnPlainDot);                        // 半个字位的「·」，由动态层按秒闪
                for (int i = 0; i < ms.Length; i++) items.Add(ms.Substring(i, 1));
            }

            float bigInkH = Ui.Ink(bigF, "壹").Height;
            float sumW = 0f;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != CnPlainDot) sumW += Ui.Ink(bigF, items[i]).Width;
            int nCh = 0;
            for (int i = 0; i < items.Count; i++) if (items[i] != CnPlainDot) nCh++;
            float cellAvg = sumW / Math.Max(1, nCh);
            float dotW = whole ? 0f : cellAvg * 0.5f;   // 「·」占半个字符位
            // 两字整点时字间垫一个全角空格（一整个字位），其余只留正常字距
            float gap = (whole && nCh == 2) ? cellAvg : 2.5f;
            float natural = sumW + dotW + gap * (items.Count - 1);
            float avail = _cnBlockR - _cnBlockL;
            // 第五稿：不再常态拉瘦（字号已放大到 70），只在实在排不下时才横向压，保底 0.72
            float sx = (natural > avail) ? (avail / natural) : 1f;
            if (sx > 1f) sx = 1f;
            if (sx < 0.72f) sx = 0.72f;

            float total = natural * sx;
            // 每个字按「墨迹宽度」排，但 DrawString 的原点在字位原点、墨迹还要右偏 ink.X，
            // 且实测渲染的左侧留白比 MeasureString 测到的 ink.X 还多约 10px（约 0.41×墨迹高），
            // 所以起点先补 firstInkX 再把整块左移 0.41×墨迹高，整块才是视觉居中。
            float firstInkX = (items.Count > 0) ? (float)Ui.Ink(bigF, items[0]).X * sx : 0f;
            float x = _cnBlockL + (avail - total) / 2f + firstInkX - bigInkH * CnBigLsbFix + CnBigShiftX;
            // 第五稿微调：整块上移 6px、右移 20px（CnBigShiftX / CnBigShiftY）
            // v2.1：中文皮肤整体回退到 v2.0 第七稿（字号不缩、不加节日提示行），
            //        v2.1 的字号与提示行只在英数皮肤里生效。
            float baseline = 31f + M.CnBigInkH + CnBigShiftY;  // 墨迹底沿（放大字号时往上长）
            float hsep = gap * sx;
            CnDebug = "hs=" + hs + " ms=" + ms + " whole=" + whole + " glyphs=" + items.Count
                + " sumW=" + sumW.ToString("0.0") + " cellAvg=" + cellAvg.ToString("0.0")
                + " dotW=" + dotW.ToString("0.0") + " gap=" + gap.ToString("0.0")
                + " sx=" + sx.ToString("0.00") + " avail=" + avail.ToString("0.0")
                + " bigInkH=" + bigInkH.ToString("0.0") + "\r\n";
            for (int i = 0; i < items.Count; i++)
                if (items[i] != CnPlainDot)
                {
                    Rectangle ir = Ui.Ink(bigF, items[i]);
                    CnDebug += "  g" + i + " " + items[i] + " ink=" + ir.X + "," + ir.Y + "," + ir.Width + "," + ir.Height + "\r\n";
                }
            using (SolidBrush b = new SolidBrush(P.Num))
            using (SolidBrush bg = new SolidBrush(P.Glow))
            {
                // 逐项推进：文字按字位走，遇到「·」只记坐标（留给动态层画）
                for (int i = 0; i < items.Count; i++)
                {
                    float sep = (i < items.Count - 1) ? hsep : 0f;
                    if (items[i] == CnPlainDot)
                    {
                        _cnDotCx = x + dotW * sx / 2f;
                        _cnDotCy = baseline - bigInkH / 2f;
                        _cnDotValid = true;
                        x += dotW * sx + sep;
                    }
                    else
                    {
                        CnBigGlyph(g, items[i][0], ref x, sx, baseline, sep, bigF, b, bg, bigInkH);
                    }
                }
            }

            // ---------- 下方居中：公历月日 · 星期 · 纪念日/节日 ----------
            DrawCnDayLine(g, day);

            // ---------- 右侧三列（左→右：节气或传统节日 / 农历月·日 / 农历干支·生肖年） ----------
            LunarInfo li = null;
            try { li = LunarCal.FromSolar(day); } catch (Exception) { }
            string term = "";
            try { term = SolarTerms.Of(day); } catch (Exception) { }

            // 左1列：节气 + 中国传统节日（农历/节气类，含除夕）—— 这一列管农历与传统的
            List<string> c1 = new List<string>();
            List<Color> c1c = new List<Color>();
            List<char> c1k = new List<char>();
            if (term.Length > 0)
            {
                string t = ToTrad(term);
                for (int i = 0; i < t.Length; i++) { c1.Add(t[i].ToString()); c1c.Add(P.Num); c1k.Add('N'); }
            }
            foreach (string fn in CnTradFestOfDay(day, li, term))
            {
                if (term.Length > 0 && fn == term) continue;   // 清明既是节气又是节日，别画两遍
                if (c1.Count >= 7) break;
                string t = ToTrad(fn);
                Color col = IsRedFest(fn) ? FestRed : P.Num;
                for (int i = 0; i < t.Length && c1.Count < 7; i++) { c1.Add(t[i].ToString()); c1c.Add(col); c1k.Add('N'); }
            }

            // 左2列：农历月 + 日（「月、日」作小橙标目，两者之间不再夹「·」，让月日数字放手放大）
            // 第六稿微调：农历月那一段整体下移 10px（'A'），日那一段再下移 5px（'B' = 共 15px）
            List<string> c2 = new List<string>();
            List<Color> c2c = new List<Color>();
            List<char> c2k = new List<char>();
            if (li != null)
            {
                string mn = ToTrad(li.MonthName);
                for (int i = 0; i < mn.Length; i++) { c2.Add(mn[i].ToString()); c2c.Add(P.Num); c2k.Add('A'); }
                c2.Add("月"); c2c.Add(P.Acc); c2k.Add('a');
                string dn = ToTrad(li.DayName);
                for (int i = 0; i < dn.Length; i++) { c2.Add(dn[i].ToString()); c2c.Add(P.Num); c2k.Add('B'); }
                c2.Add("日"); c2c.Add(P.Acc); c2k.Add('b');
            }

            // 左3列：農曆（小橙）+ 干支（白）+「·」（墨迹 10px）+ 生肖（白）+ 年（小橙，与農曆同大）
            List<string> c3 = new List<string>();
            List<Color> c3c = new List<Color>();
            List<char> c3k = new List<char>();
            if (li != null)
            {
                c3.Add("農"); c3c.Add(P.Acc); c3k.Add('L');
                c3.Add("曆"); c3c.Add(P.Acc); c3k.Add('L');
                string gz = li.YearName;                  // 丙午年
                if (gz.EndsWith("年")) gz = gz.Substring(0, gz.Length - 1);
                // 干支两个字单独往下压 3px（第五稿微调），行槽推进不变，后面生肖/年不动
                for (int i = 0; i < gz.Length; i++) { c3.Add(gz[i].ToString()); c3c.Add(P.Num); c3k.Add('M'); }
                c3.Add(CnGap); c3c.Add(P.Acc); c3k.Add('G');     // 干支与生肖之间留 16px 空档，不画橙点
                string zh = gz.Length > 1 ? gz.Substring(1, 1) : "";
                int zi = Array.IndexOf(ZhiCh, zh);
                string zoo = (zi >= 0) ? ZhiSx[zi] : "";   // 馬 / 羊 / 猴…
                if (zoo.Length > 0) { c3.Add(zoo); c3c.Add(P.Num); c3k.Add('N'); }
                c3.Add("年"); c3c.Add(P.Acc); c3k.Add('L');
            }

            // 第六稿：节气/传统节日这一列改为「靠下、下对齐」，末行墨迹底沿 = 158 - 12 = 146
            DrawCnVList(g, c1, c1c, c1k, 420f, 1f, CnColStep, CnColBottom);
            DrawCnVList(g, c2, c2c, c2k, 452f, 1f, CnColStep);
            DrawCnVList(g, c3, c3c, c3k, 484f, 1f, CnColStep);
        }

        const float CnColBottom = 146f;   // 右侧列「下对齐」的底沿（距模块底 12px）

        // 右侧三列的竖排行距：正文墨迹 22 配 22px 行距（七行 1…151，末行不贴模块底边）
        const float CnColStep = 21f;

        // 大字整块的横向校正：实测渲染的左侧留白 - 测得 ink.X ≈ 0.41×墨迹高（按字号缩放）
        const float CnBigLsbFix = 0.41f;
        // 大字整块的整体微调（第六稿：上移 6px、右移 20px）
        const float CnBigShiftX = 20f;
        const float CnBigShiftY = -6f;

        const string CnPlainDot = "·";
        const string CnGap = "\u200B";          // 空档占位（不画任何东西，只占 CnGapH 的竖直空间）
        const float CnGapH = 16f;
        const string CnGapS = "\u200C";         // 小空档（「星期」与其后那个白色数字之间）
        const float CnGapSH = 5f;

        /// <summary>
        /// 画一个中文大字（横向按 sx 压瘦，墨迹底沿贴 baseline），并把 x 推到下一个字位。
        /// refInkH > 0 时对该字做「墨迹等高归一」：字库里各字墨迹高不等（陸 80.1%、叁 67.6%、
        /// 壹 75.3%），偏高/偏矮的字按 refInkH 压放，纵向变形限制在 ±6% 以内以免笔画走样。
        /// </summary>
        void CnBigGlyph(Graphics g, char ch, ref float x, float sx, float baseline, float hsep, Font f, Brush b, Brush glow, float refInkH)
        {
            string s = ch.ToString();
            Rectangle ink = Ui.Ink(f, s);
            float sy = 1f;
            if (refInkH > 0f && ink.Height > 0)
            {
                sy = refInkH / (float)ink.Height;
                if (sy > 1.12f) sy = 1.12f;
                if (sy < 0.88f) sy = 0.88f;
            }
            GraphicsState st = g.Save();
            g.TranslateTransform(x, baseline);
            g.ScaleTransform(sx, sy);
            float dx = -ink.X;                  // 抵消字形的左侧留白，让 x 真正落在墨迹左缘
            float dy = -(float)ink.Height - ink.Y;
            if (P.Dark) g.DrawString(s, f, glow, dx + 1f, dy + 1f, StringFormat.GenericTypographic);
            g.DrawString(s, f, b, dx, dy, StringFormat.GenericTypographic);
            g.Restore(st);
            x += ink.Width * (float)sx + hsep;
        }

        /// <summary>当天属于「传统节日」的名字（含除夕：农历腊月最后一天）</summary>
        List<string> CnTradFestOfDay(DateTime day, LunarInfo li, string term)
        {
            List<string> outList = new List<string>();
            try
            {
                string cz = (S.MainCity != null) ? S.MainCity.ZhUI : "";
                string ce = (S.MainCity != null) ? S.MainCity.En : "";
                List<Fest> all = HolidayDb.For(day, CalCC(), li, term, S.CalLunar, cz, ce);
                foreach (Fest f in all)
                    if (f.Zh != null && IsTradFest(f.Zh) && !outList.Contains(f.Zh)) outList.Add(f.Zh);
            }
            catch (Exception) { }
            return outList;
        }

        /// <summary>该字型标记是否用小橙标目字号（大写=正文，小写=标目）</summary>
        static bool CnKindLabel(char k)
        {
            return k == 'L' || k == 'a' || k == 'b';
        }

        /// <summary>字型标记的竖直微调量：'M' 干支 +3；'A' 农历月段 +10；'B' 农历日段 +15</summary>
        static float CnKindDy(char k)
        {
            switch (k)
            {
                case 'M': return 3f;
                case 'A': case 'a': return 10f;
                case 'B': case 'b': return 15f;
                default: return 0f;
            }
        }

        /// <summary>
        /// 竖排单字列：'N' 正文（白色 22）/ 'L' 小橙标目（18）/ 'G' 空档（16px，不画东西）/
        /// 'M' 正文但整行下压 3px（「干支」两个字）/ 'A''a' 下移 10px（农历月段）/ 'B''b' 下移 15px（农历日段）。
        /// 每行按「墨迹底沿贴行槽底」对齐，标目与正文混排时基线不歪；
        /// 同一个字在字库里墨迹高不等（農 85%、丙 67%），按本列正文字高做 ±6% 以内的纵向归一。
        /// </summary>
        void DrawCnVList(Graphics g, List<string> lines, List<Color> cols, List<char> kinds, float cx, float top, float step)
        {
            DrawCnVList(g, lines, cols, kinds, cx, top, step, 0f);
        }

        /// <summary>
        /// bottom &gt; 0 时改为「下对齐」：先按行槽算出整列高度，让末行墨迹底沿正好落在 bottom
        /// （第六稿：节气/传统节日那一列底对齐到距模块底 12px，和底部公历行同高）。
        /// </summary>
        void DrawCnVList(Graphics g, List<string> lines, List<Color> cols, List<char> kinds, float cx, float top, float step, float bottom)
        {
            float y = top;
            float stepUse = step, gapUse = CnGapH;
            if (bottom > 0f)
            {
                float total = 0f;
                for (int i = 0; i < lines.Count; i++)
                {
                    char k0 = (i < kinds.Count) ? kinds[i] : 'N';
                    total += (k0 == 'G' || lines[i] == CnGap) ? CnGapH : step;
                }
                // v2.1 修（审阅 P3-8）：整列高过可用高度时按比例压行距。
                // 原实现只按 count*step 估高，7 行 × 21 = 147 > 146 → y = -1，首字会顶出模块上沿。
                float limit = bottom - 2f;
                if (total > limit && total > 0f)
                {
                    float k2 = limit / total;
                    stepUse = step * k2;
                    gapUse = CnGapH * k2;
                    total = limit;
                }
                y = bottom - total;
            }
            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i];
                char k = (i < kinds.Count) ? kinds[i] : 'N';
                Color c = (i < cols.Count) ? cols[i] : P.Num;
                if (k == 'G' || s == CnGap)
                {
                    y += gapUse;                                 // 空档：只推进，不画
                    continue;
                }
                if (k == 'D' || s == CnPlainDot)
                {
                    // 列内「·」墨迹很小，竖直摆在这一行槽的正中
                    Rectangle ink = Ui.Ink(M.FCnColSmall, CnPlainDot);
                    using (SolidBrush b = new SolidBrush(c))
                        g.DrawString(CnPlainDot, M.FCnColSmall, b,
                            cx - ink.Width / 2f - ink.X, y + (stepUse - ink.Height) / 2f - ink.Y,
                            StringFormat.GenericTypographic);
                }
                else
                {
                    Font f = CnKindLabel(k) ? M.FCnColLabel : M.FCnColNum;
                    Rectangle ink = Ui.Ink(f, s);
                    float inkH = (float)ink.Height;
                    float refH = Ui.Ink(f, "壹").Height;
                    float sy = 1f;
                    bool thin = (inkH > 0f && refH > 0f && inkH < refH * 0.5f);
                    if (!thin && inkH > 0f && refH > 0f)
                    {
                        sy = refH / inkH;
                        if (sy > 1.12f) sy = 1.12f;
                        if (sy < 0.88f) sy = 0.88f;
                    }
                    GraphicsState st = g.Save();
                    // 'M' 干支 +3px；'A'/'a' 农历月段 +10px；'B'/'b' 农历日段 +15px
                    float dy = CnKindDy(k);
                    g.TranslateTransform(cx, y + stepUse + dy); // 行槽底 = 墨迹底沿
                    g.ScaleTransform(1f, sy);
                    // v2.1 修：横画字（一 / 二）不高倍放大也不贴槽底，改到行槽竖直中心（与其它字同心）
                    float drawY = thin ? (-(refH + inkH) / 2f - ink.Y) : (-inkH - ink.Y);
                    using (SolidBrush b = new SolidBrush(c))
                        g.DrawString(s, f, b, -ink.Width / 2f - ink.X, drawY,
                            StringFormat.GenericTypographic);
                    g.Restore(st);
                }
                y += stepUse;
            }
        }

        /// <summary>左竖两段（「公元」这种，第二段为空时忽略）</summary>
        void DrawCnVCol(Graphics g, string[] a, Color[] ca, string[] b, Color[] cb, float cx, float top)
        {
            float stepY = 20f;
            float y = top;
            for (int i = 0; i < a.Length; i++)
            {
                using (SolidBrush b0 = new SolidBrush(ca[i]))
                    Ui.DrawCenter(g, a[i], M.FCnYear, b0, cx, y);
                y += stepY;
            }
            for (int i = 0; i < b.Length; i++)
            {
                using (SolidBrush b1 = new SolidBrush(cb[i]))
                    Ui.DrawCenter(g, b[i], M.FCnYear, b1, cx, y);
                y += stepY;
            }
        }

        /// <summary>
        /// 下方居中的公历行：月日 + 星期 + 国际/公历节日（农历与传统节日都归右侧三列）。
        /// 橙色标目（月 / 日 / 「·」/ 星期）保持 14.5，白数字放大到与农历「丙午、初七」同高（墨迹 22），
        /// 整行墨迹底沿落在距模块底 12px（158 - 12 = 146）。
        /// </summary>
        void DrawCnDayLine(Graphics g, DateTime now)
        {
            List<string> seg = new List<string>();
            List<Color> col = new List<Color>();
            List<bool> big = new List<bool>();
            seg.Add(CnPlain(now.Month)); col.Add(P.Num); big.Add(true);
            seg.Add("月"); col.Add(P.Acc); big.Add(false);
            seg.Add(CnPlain(now.Day)); col.Add(P.Num); big.Add(true);
            seg.Add("日"); col.Add(P.Acc); big.Add(false);
            seg.Add(CnGap); col.Add(P.Acc); big.Add(false);                       // 16px 空档（不再画橙点）
            seg.Add("星期"); col.Add(P.Acc); big.Add(false);                      // 「星期」小号标橙
            seg.Add(CnGapS); col.Add(P.Acc); big.Add(false);                      // 5px 小空档
            seg.Add(WkSx[(int)now.DayOfWeek]); col.Add(P.Num); big.Add(true);      // 「六」白色，与月日同大

            string fest = CnFestLine();
            if (fest.Length > 0)
            {
                seg.Add(CnGap); col.Add(P.Acc); big.Add(false);
                seg.Add(fest); col.Add(IsRedFest(fest) ? FestRed : P.Num); big.Add(true);
            }

            float bigInkH = Ui.Ink(M.FCnLineNum, "8").Height;
            float total = 0f;
            for (int i = 0; i < seg.Count; i++)
                total += (seg[i] == CnGap) ? CnGapH : ((seg[i] == CnGapS) ? CnGapSH : Ui.AdvW(big[i] ? M.FCnLineNum : M.FCnLine, seg[i]));
            float avail = _cnBlockR - _cnBlockL;
            float sx = (total > avail) ? (avail / total) : 1f;
            float left = _cnBlockL + (avail - total * sx) / 2f;
            float bottom = 146f;                     // 距模块底 12px
            CnDebug += "day total=" + total.ToString("0.0") + " sx=" + sx.ToString("0.00")
                + " left=" + left.ToString("0.0") + " avail=" + avail.ToString("0.0")
                + " bigInkH=" + bigInkH.ToString("0.0") + " segs=" + seg.Count + "\r\n";
            for (int i = 0; i < seg.Count; i++)
                CnDebug += "  s" + i + " " + (seg[i] == CnGap ? "<gap>" : (seg[i] == CnGapS ? "<gaps>" : seg[i]))
                    + " big=" + big[i] + " col=" + col[i].ToArgb().ToString("X8")
                    + " adv=" + (seg[i] == CnGap ? CnGapH : ((seg[i] == CnGapS) ? CnGapSH : Ui.AdvW(big[i] ? M.FCnLineNum : M.FCnLine, seg[i]))).ToString("0.0")
                    + " inkW=" + ((seg[i] == CnGap || seg[i] == CnGapS) ? 0f : Ui.Ink(big[i] ? M.FCnLineNum : M.FCnLine, seg[i]).Width).ToString("0.0")
                    + "\r\n";

            GraphicsState st = g.Save();
            g.TranslateTransform(left, 0f);
            g.ScaleTransform(sx, 1f);
            float px = 0f;
            for (int i = 0; i < seg.Count; i++)
            {
                if (seg[i] == CnGap) { px += CnGapH; continue; }   // 空档：只推进
                if (seg[i] == CnGapS) { px += CnGapSH; continue; }
                Font f = big[i] ? M.FCnLineNum : M.FCnLine;
                Rectangle ink = Ui.Ink(f, seg[i]);
                float refH = (float)Ui.Ink(f, big[i] ? "8" : "日").Height;
                // v2.1 修：横画字（一 / 二）墨迹只有几像素高，按墨迹底对齐会贴到字高最底部，
                // 改成与其它字共用"字高中心"，视觉上才是正常的居中位置
                float yTop;
                if (ink.Height > 0 && ink.Height < refH * 0.5f)
                    yTop = bottom - refH / 2f - ink.Height / 2f - ink.Y;
                else
                    yTop = bottom - ink.Height - ink.Y;
                using (SolidBrush b2 = new SolidBrush(col[i]))
                    g.DrawString(seg[i], f, b2, px - ink.X, yTop, StringFormat.GenericTypographic);
                px += Ui.AdvW(f, seg[i]);
            }
            g.Restore(st);
        }

        /// <summary>
        /// v2.1：主时钟「时:分」下方那行节日提示（语言 × 所在地 四种口径，全部标橙）。
        ///   中文 + 中国      ：世界性 + 中国（含行业/宣传日）+ 节气 + 传统节日，全都显示
        ///   中文 + 非中国    ：世界性 + 中国（不含行业宣传日）+ 节气 + 传统节日 + 当地国节日
        ///   英文 + 中国      ：世界性 + 中国法定/纪念日（春节中秋端午清明…），不显示节气与无假传统节日
        ///   英文 + 非中国    ：只有世界性 + 当地国节日
        /// 段与段之间用「·」分隔（英文用「 · 」），最长按 availW 截断，排不下就少放一条。
        /// </summary>
        /// <summary>v2.1：数据表里的英文名多为缩写（SpringFest / MidAutumn），提示行里换成完整写法</summary>
        static string PrettyEn(string s)
        {
            switch (s)
            {
                case "SpringFest": return "Spring Festival";
                case "MidAutumn": return "Mid-Autumn Festival";
                case "DragonBoat": return "Dragon Boat Festival";
                case "Lantern": return "Lantern Festival";
                case "National": return "National Day";
                case "TombSweep": return "Tomb-Sweeping Day";
                case "Qingming": return "Tomb-Sweeping Day";
                case "GhostFest": return "Ghost Festival";
                case "Qixi": return "Qixi Festival";
                case "Laba": return "Laba Festival";
                case "XiaoNian": return "Little New Year";
                case "Chongyang": return "Double Ninth Festival";
                case "WinterClothes": return "Winter Clothes Day";
            }
            return s;
        }

        string MainFestTip(Font f, float availW)
        {
            try
            {
                DateTime today = MainToday();
                LunarInfo li = LunarCal.FromSolar(today);
                string term = SolarTerms.Of(today);
                string cz = (S.MainCity != null) ? S.MainCity.ZhUI : "";
                string ce = (S.MainCity != null) ? S.MainCity.En : "";
                string cc = CalCC();
                if (cc == null) cc = "";
                bool en = (S.Lang == "en");
                bool inCn = (cc == "CN" || cc.Length == 0);

                List<Fest> all = HolidayDb.For(today, inCn ? "CN" : cc, li, term, S.CalLunar, cz, ce);
                if (!inCn)
                {
                    // 非中国地区还要把中国节日的部分也拿进来（中文界面要显示春节/中秋/国庆等）
                    List<Fest> cnOnly = HolidayDb.For(today, "CN", li, term, S.CalLunar, cz, ce);
                    foreach (Fest f0 in cnOnly)
                    {
                        bool dup = false;
                        foreach (Fest g0 in all) if (g0.Zh == f0.Zh) { dup = true; break; }
                        if (!dup) all.Add(f0);
                    }
                }

                List<string> names = new List<string>();
                List<Fest> pick = new List<Fest>();
                foreach (Fest fst in all)
                {
                    if (fst.Zh == null || fst.Zh.Length == 0) continue;
                    bool world = (fst.CC == "*" || fst.CC == "通用");
                    bool cnFest = (fst.CC == "CN");
                    if (en && fst.Kind == 'S' && fst.Zh != "清明") continue;   // 英文界面不显示节气（清明属法定假日，例外放行）
                    if (en)
                    {
                        if (!world)
                        {
                            if (cnFest)
                            {
                                // 英文只认中国的法定/公休（春节 除夕 端午 中秋）与纪念日（五四/儿童/建党/建军/公祭…）
                                bool keep = fst.Legal || fst.Zh == "清明"
                                         || (fst.Kind != 'L' && fst.Kind != 'S' && fst.Prio < 4);
                                if (!keep) continue;
                            }
                            else if (fst.CC != cc) continue;
                        }
                    }
                    else
                    {
                        if (!world)
                        {
                            if (cnFest) { if (!inCn && fst.Prio >= 4) continue; }   // 非中国不显示中国的行业/宣传日
                            else if (fst.CC != cc) continue;
                        }
                    }
                    pick.Add(fst);
                }
                // 同一优先级里长名字排前面：「中国人民警察节」放在「110宣传日」之前，
                // 与人的说法顺序一致（也避免短名把长名挤到后面显得局促）
                pick.Sort(delegate(Fest a, Fest b)
                {
                    int c = a.Prio.CompareTo(b.Prio);
                    if (c != 0) return c;
                    return b.Zh.Length.CompareTo(a.Zh.Length);
                });
                foreach (Fest fst in pick)
                {
                    string nm = en ? (string.IsNullOrEmpty(fst.En) ? fst.Zh : PrettyEn(fst.En)) : fst.Zh;
                    if (nm.Length == 0) continue;
                    if (!names.Contains(nm)) names.Add(nm);
                }
                if (names.Count == 0) return "";

                string sep = "·";
                string line = "";
                using (Bitmap tmp = new Bitmap(1, 1))
                using (Graphics gg = Graphics.FromImage(tmp))
                {
                    foreach (string nm in names)
                    {
                        string cand = (line.Length == 0) ? nm : (line + sep + nm);
                        SizeF sz = gg.MeasureString(cand, f, int.MaxValue, StringFormat.GenericTypographic);
                        if (sz.Width > availW) break;
                        line = cand;
                    }
                }
                return line;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>中文皮肤下那行的节日取数（口径沿用原 LocalFestLine，但常挂日历也照样显示）</summary>
        string CnFestLine()
        {
            try
            {
                DateTime today = MainToday();
                LunarInfo li = LunarCal.FromSolar(today);
                string term = SolarTerms.Of(today);
                string cz = (S.MainCity != null) ? S.MainCity.ZhUI : "";
                string ce = (S.MainCity != null) ? S.MainCity.En : "";
                List<Fest> all = HolidayDb.For(today, CalCC(), li, term, S.CalLunar, cz, ce);
                List<string> legal = new List<string>(), world = new List<string>();
                foreach (Fest f in all)
                {
                    if (f.Zh == null || f.Zh.Length == 0) continue;
                    if (f.Kind == 'L' || f.Kind == 'S') continue;     // 农历 / 节气归右侧三列
                    if (IsTradFest(f.Zh)) continue;                   // 传统节日不上底部行
                    if (IsMajorLegal(f)) { if (!legal.Contains(f.Zh)) legal.Add(f.Zh); }
                    else if (f.Prio == 1 && !f.Zh.StartsWith("世界") && !f.Zh.StartsWith("国际"))
                    {
                        if (!world.Contains(f.Zh)) world.Add(f.Zh);
                    }
                }
                if (legal.Count == 0) return (world.Count > 0) ? world[0] : "";
                // 两个节日排得下就一起显示（宽度上限 200，超了只留一个）
                if (legal.Count > 1)
                {
                    string cand = legal[0] + "·" + legal[1];
                    using (Bitmap tmp = new Bitmap(1, 1))
                    using (Graphics gg = Graphics.FromImage(tmp))
                    {
                        SizeF sz = gg.MeasureString(cand, M.FCnLineNum, int.MaxValue, StringFormat.GenericTypographic);
                        if (sz.Width <= 200f) return cand;
                    }
                }
                return legal[0];
            }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// 2.0：中文皮肤里随秒闪的「·」（画在动态层）。
        /// 字号与配色沿用原先右列/底部那个橙色点（FCnColSmall + P.Acc），只是搬到了时、分之间。
        /// </summary>
        void DrawLocalCnDot(Graphics g, Row r, bool force)
        {
            if (!_cnDotValid) return;
            if (!force && (DateTime.Now.Second % 2) == 1) return;
            Rectangle ink = Ui.Ink(M.FCnColSmall, CnPlainDot);
            float cy = _cnDotCy + (r != null ? r.Y : 0f);
            using (SolidBrush b = new SolidBrush(P.Acc))
                g.DrawString(CnPlainDot, M.FCnColSmall, b,
                    _cnDotCx - ink.Width / 2f - ink.X, cy - ink.Height / 2f - ink.Y,
                    StringFormat.GenericTypographic);
        }

        /// <summary>
        /// 1.6：主时钟所在地「今天」的重大法定假日（春节 / 元旦 / 五一 / 国庆…）
        /// 与世界性节日（通用条目的 Prio=1，如劳动节、圣诞节）。
        /// 只在日历模块关闭或弹出模式下显示；常挂日历时不显示（日历自己会标）。
        /// 注意：holidays.txt 的 Prio=4 是「只进当日浮层、不进日历格」的旧口径，
        /// 不能拿来当「重大」的判据，所以这里用一份显式名单认「重大法定假日」。
        /// </summary>
        static readonly string[] MajorLegal =
        {
            "元旦", "新年", "春节", "除夕", "清明", "劳动节", "五一", "端午", "中秋", "国庆",
            "独立日", "加拿大日", "澳洲日", "统一日", "建国纪念", "宪法纪念", "天皇诞辰",
            "感恩节", "圣诞节", "复活节", "耶稣受难", "退伍军人节", "纪念日", "公祭"
        };

        static bool IsMajorLegal(Fest f)
        {
            if (f == null || f.Zh == null) return false;
            for (int i = 0; i < MajorLegal.Length; i++)
                if (f.Zh.StartsWith(MajorLegal[i])) return true;
            return false;
        }

        /// <summary>双击点是否落在主时钟的时分数字上</summary>
        bool InMainClockTime(Row r, float x, float y)
        {
            if (y < r.Y || y > r.Y + r.H) return false;
            // 2.0：中文皮肤的大字块横跨整个中间区，双击它算「点时间」
            if (S.MainSkin == 1) return x >= _cnBlockL - 4f && x <= _cnBlockR + 4f;
            float total = Sgn.Width(M.FTime, "0000", M.CellTime, 0.42f);
            return x >= M.TimeLeft - 4f && x <= M.TimeLeft + total + 4f;
        }

        /// <summary>双击主时钟时间：打开系统「日期和时间」设置（World bedside clock 的做法）</summary>
        void OpenDateTimeSettings()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo("ms-settings:dateandtime");
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception) { }
        }

        void DrawMd(Graphics g, int mm, int dd, float x, float topM, float topD,
                    Font f, float cell, float inkH, float stretch, Font labFont, float labRight)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(0f, topM);
            g.ScaleTransform(1f, stretch);
            Sgn.Draw(g, mm.ToString("00"), f, x, 0f, inkH, cell, 0.42f, P.Num, P.Glow, 1);
            g.Restore(st);

            st = g.Save();
            g.TranslateTransform(0f, topD);
            g.ScaleTransform(1f, stretch);
            Sgn.Draw(g, dd.ToString("00"), f, x, 0f, inkH, cell, 0.42f, P.Num, P.Glow, 1);
            g.Restore(st);

            using (SolidBrush b = new SolidBrush(P.Acc))
            {
                Ui.DrawRight(g, "M", labFont, b, labRight, topM);
                Ui.DrawRight(g, "D", labFont, b, labRight, topD);
            }
        }

        // ==================================================== 世界模块（静态部分）
        float TimeWidthW()
        {
            return Sgn.Width(M.FWTime, "00:00", M.FWCell, 0.42f);
        }

        float DateAdvance(string s)
        {
            float x = 0;
            foreach (char ch in s) x += Ui.Ink(M.FWDate, ch.ToString()).Width + 0.5f;
            return x;
        }

        float DateWidth(string md)
        {
            return DateAdvance(md) * M.TabK;
        }

        float Line1() { return M.FWLeft + TimeWidthW() + 12f; }
        float Line2(string md) { return 478f - DateWidth(md) - 12f; }

        float CnBottom()
        {
            return (Metrics.WorldH + Ui.Ink(M.MCn, "京").Height) / 2f;
        }

        // ==================================================== 日历模块
        static readonly string[] MonEn = { "January", "February", "March", "April", "May", "June",
                                           "July", "August", "September", "October", "November", "December" };
        static readonly string[] WkZh = { "日", "一", "二", "三", "四", "五", "六" };
        static readonly string[] WkEn = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        /// <summary>自检出图用：把「今天」定到某一天（只看节日小字、日历高亮这类日期相关内容）</summary>
        public static DateTime? PreviewDay;

        /// <summary>2.0 自检出图用：把主时钟的时刻定死（只看中文皮肤的大字排布，不影响程序逻辑）</summary>
        public static DateTime? PreviewClock;

        /// <summary>主时钟所在地的"今天"（日历的高亮与默认月份都按它）</summary>
        DateTime MainToday()
        {
            if (PreviewDay.HasValue) return PreviewDay.Value.Date;
            TimeZoneInfo z = (S.MainCity != null) ? S.MainCity.Zone : TimeZoneInfo.Local;
            return TimeZoneInfo.ConvertTime(DateTime.UtcNow, z).Date;
        }

        /// <summary>日历取哪一国节日：auto 跟随主时钟城市，其余为手动指定</summary>
        string CalCC()
        {
            if (PreviewCC != null) return PreviewCC;   // v2.1 出图用覆盖，正规运行时为 null
            if (S.CalCC == "none") return "";
            if (S.CalCC != null && S.CalCC.Length == 2) return S.CalCC;
            if (S.MainCity != null)
            {
                if (string.IsNullOrEmpty(S.MainCity.CC)) CityDb.AttachGeo(S.MainCity);   // 注册表读回的 City 没国家码，按需补齐
                if (!string.IsNullOrEmpty(S.MainCity.CC)) return S.MainCity.CC;
            }
            // 1.4：主时钟跟随系统时没有城市 → 按 Windows 区域补一个国家码。
            // 否则 cc 为空会把整块国别节日（春节外的中元、重阳、记者节、消防宣传…）全过滤掉。
            try
            {
                RegionInfo ri = RegionInfo.CurrentRegion;
                string two = ri.TwoLetterISORegionName;
                if (two.Length == 2) return two.ToUpperInvariant();
            }
            catch (Exception) { }
            return "";
        }

        /// <summary>表头按实际墨迹高度自适应，返回网格起点（绘制与命中测试共用同一套换算）</summary>
        float CalHeaderBottom(out float titleH, out float subH, out float weekH)
        {
            titleH = Ui.Ink(M.FCalTitle, "2026年11月").Height;
            subH = Ui.Ink(M.FCalSub, "丙午年").Height;
            weekH = Ui.Ink(M.FCalWeek, "日").Height;
            return 4f + titleH + 2f + subH + 5f + weekH + 4f;
        }

        float CalCellH(float gridTop, int rowCount)
        {
            // 末行底部留 8px，避免日历不在最顶时下一模块的分隔线贴到最后一格
            return (Metrics.CalH - gridTop - 8f) / (float)Math.Max(1, rowCount);
        }

        CalMonthModel CalModel()
        {
            DateTime today = MainToday();
            if (!calNav && (today.Year != calY || today.Month != calM)) { calY = today.Year; calM = today.Month; }
            string cc = CalCC();
            string zh = (S.MainCity != null) ? S.MainCity.ZhUI : "";
            string en = (S.MainCity != null) ? S.MainCity.En : "";
            return CalMonthModel.Build(new DateTime(calY, calM, 1), today, cc, S.CalLunar, zh, en, S.CalFirstDow, S.Lang);
        }

        /// <summary>中文按 4 字一行折；英文按空格/连字符断词，绝不硬切单词</summary>
        void WrapCell(string s, bool en, List<string> outList)
        {
            outList.Clear();
            if (s == null || s.Length == 0) return;
            if (!en)
            {
                for (int i = 0; i < s.Length; i += 4)
                    outList.Add(s.Substring(i, Math.Min(4, s.Length - i)));
                return;
            }
            string[] words = s.Split(' ');
            string line = "";
            foreach (string w in words)
            {
                if (line.Length == 0) line = w;
                else if (line.Length + 1 + w.Length <= 9) line += " " + w;
                else { outList.Add(line); line = w; }
            }
            if (line.Length > 0) outList.Add(line);
        }

        void DrawCal(Graphics g, DateTime now) { DrawCalCore(g, now, false); }

        // 表头命中区（绘制与点选共用同一套几何）
        RectangleF hPrevYear, hPrevMon, hNextMon, hNextYear, hClose, hTitle;

        void CalHdrLayout(string title, bool inPopup, out float gridTop)
        {
            float titleH, subH, weekH;
            gridTop = CalHeaderBottom(out titleH, out subH, out weekH);
            Rectangle ti = Ui.Ink(M.FCalTitle, title);
            float titleTop = 4f;
            float titleLeft = (Metrics.W - ti.Width) / 2f;
            float midY = titleTop + titleH / 2f;
            hTitle = new RectangleF(titleLeft, titleTop, ti.Width, titleH);
            float y0 = midY - 9f;
            hPrevYear = new RectangleF(8f, y0, 15f, 18f);
            hPrevMon = new RectangleF(25f, y0, 15f, 18f);
            hNextMon = new RectangleF(Metrics.W - 40f, y0, 15f, 18f);
            hNextYear = new RectangleF(Metrics.W - 23f, y0, 15f, 18f);
            hClose = inPopup ? new RectangleF(titleLeft + ti.Width + 6f, midY - 8f, 18f, 16f)
                             : RectangleF.Empty;
        }

        /// <summary>画一整块日历（Widget 常挂 与 弹出窗口 共用）。inPopup=true 时多画收回按钮</summary>
        void DrawCalCore(Graphics g, DateTime now, bool inPopup)
        {
            CalMonthModel m = CalModel();
            bool en = (S.Lang == "en");
            string title = en ? (MonEn[calM - 1] + " " + calY) : (calY + "年" + calM + "月");
            Font fTitle = M.FCalTitle;
            Font fSub = M.FCalSub;
            Font fWeek = M.FCalWeek;
            Font fFest = en ? M.FCalFestEn : M.FCalFest;
            Font fLun = en ? M.FCalLunEn : M.FCalLun;

            float gridTop;
            CalHdrLayout(title, inPopup, out gridTop);
            float cellH = CalCellH(gridTop, m.Rows);
            float titleH = hTitle.Height;

            // 三级文字色：日号 > 节日 > 农历/周头；白底主题下压暗才看得清
            Color cFest = P.Dark ? Color.FromArgb(255, 198, 198, 204) : Color.FromArgb(255, 62, 62, 68);
            Color cLun = P.Dark ? Color.FromArgb(255, 160, 160, 168) : Color.FromArgb(255, 84, 84, 92);
            // 填充日（上/下月）整格标灰
            Color cAdjDay = P.Dark ? Color.FromArgb(255, 130, 130, 136) : Color.FromArgb(255, 150, 150, 156);
            Color cAdjFest = P.Dark ? Color.FromArgb(255, 112, 112, 118) : Color.FromArgb(255, 158, 158, 164);
            Color cAdjLun = P.Dark ? Color.FromArgb(255, 112, 112, 118) : Color.FromArgb(255, 148, 148, 156);

            using (SolidBrush b = new SolidBrush(P.Num))
                Ui.DrawAt(g, title, fTitle, b, hTitle.X, hTitle.Y);

            // 收回按钮（仅弹出模式）：标题右侧的小下三角
            if (inPopup)
            {
                float cx = hClose.X + hClose.Width / 2f, cy = hClose.Y + hClose.Height / 2f;
                PointF[] tri = { new PointF(cx - 6f, cy - 3f), new PointF(cx + 6f, cy - 3f), new PointF(cx, cy + 5f) };
                using (SolidBrush ab = new SolidBrush(P.Dim)) g.FillPolygon(ab, tri);
            }

            // 翻月 / 整年 快进：左 ⏪◀  右 ▶⏩（矢量三角，不依赖字库）
            using (SolidBrush ab = new SolidBrush(P.Dim))
            {
                float myY = hPrevMon.Y + hPrevMon.Height / 2f;
                // 整年（双三角）
                PointF[] yl1 = { new PointF(9f, myY), new PointF(16f, myY - 7f), new PointF(16f, myY + 7f) };
                PointF[] yl2 = { new PointF(15f, myY), new PointF(22f, myY - 7f), new PointF(22f, myY + 7f) };
                g.FillPolygon(ab, yl1); g.FillPolygon(ab, yl2);
                PointF[] yr1 = { new PointF(Metrics.W - 9f, myY), new PointF(Metrics.W - 16f, myY - 7f), new PointF(Metrics.W - 16f, myY + 7f) };
                PointF[] yr2 = { new PointF(Metrics.W - 15f, myY), new PointF(Metrics.W - 22f, myY - 7f), new PointF(Metrics.W - 22f, myY + 7f) };
                g.FillPolygon(ab, yr1); g.FillPolygon(ab, yr2);
                // 翻月（单三角）
                PointF[] ml = { new PointF(28f, myY), new PointF(37f, myY - 9f), new PointF(37f, myY + 9f) };
                PointF[] mr = { new PointF(Metrics.W - 28f, myY), new PointF(Metrics.W - 37f, myY - 9f), new PointF(Metrics.W - 37f, myY + 9f) };
                g.FillPolygon(ab, ml); g.FillPolygon(ab, mr);
            }

            // 次行：左 丙午年，右 November 小字（英文界面不重复月份名）
            float subTop = hTitle.Y + titleH + 2f;
            using (SolidBrush b = new SolidBrush(P.Dim))
            {
                if (!en && m.YearName.Length > 0)
                {
                    // 第六稿：年份后面加农历月份，中间空一个全角空格，字号字体与「丙午年」一致。
                    // 子集里没有 U+3000（全角空格），且整串一次画会被 GDI+ 在空格处断行，
                    // 所以这里逐字画：先画年份，再留一个「年」字的宽度，再一个一个字排月份。
                    float x = 10f;
                    Ui.DrawAt(g, m.YearName, fSub, b, x, subTop);
                    x += Ui.Ink(fSub, m.YearName).Width;
                    if (m.LunarMon.Length > 0)
                    {
                        x += Ui.AdvW(fSub, "年");
                        for (int i = 0; i < m.LunarMon.Length; i++)
                        {
                            string ch = m.LunarMon.Substring(i, 1);
                            Rectangle ci = Ui.Ink(fSub, ch);
                            Ui.DrawAt(g, ch, fSub, b, x, subTop);
                            x += ci.Width;
                        }
                    }
                }
                if (!en) Ui.DrawRight(g, MonEn[calM - 1], fSub, b, Metrics.W - 10f, subTop);
            }

            // 周头
            float subH = Ui.Ink(fSub, "丙午年").Height;
            float wTop = subTop + subH + 5f;
            using (SolidBrush b = new SolidBrush(cLun))
            {
                for (int col = 0; col < 7; col++)
                {
                    int wi = (col + S.CalFirstDow) % 7;
                    string t = en ? WkEn[wi] : WkZh[wi];
                    Ui.DrawCenter(g, t, fWeek, b, Metrics.CalX0 + col * Metrics.CalPitch + Metrics.CalColW / 2f, wTop);
                }
            }

            // 格：日号（七段，缩 30%）+ 农历 2 字贴在日号右侧同一行 + 节日行（与日号隔 3px 起）
            float lunInkH = Ui.Ink(fLun, "廿一").Height;
            float festInkH = Ui.Ink(fFest, "节日").Height;
            float dayH = M.CalDayInkH;
            float lineTop0 = 2f;
            float pitch = (m.Rows >= 6) ? (festInkH + 2.5f) : (festInkH + 3.5f);

            List<string> parts = new List<string>();
            for (int i = 0; i < m.Cells.Length; i++)
            {
                CalDay cd = m.Cells[i];
                if (cd == null) continue;
                int rr = i / 7, col = i % 7;
                float cx = Metrics.CalX0 + col * Metrics.CalPitch;
                float ctop = gridTop + rr * cellH;
                bool adj = cd.IsAdjacent;

                if (cd.IsToday)
                {
                    using (GraphicsPath gp = RoundRect(new RectangleF(cx + 2f, ctop + 1f, Metrics.CalColW - 4f, cellH - 2f), 8f))
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(P.Dark ? 34 : 26, P.Acc)))
                        g.FillPath(tb, gp);
                }

                bool weekend = (cd.Date.DayOfWeek == DayOfWeek.Sunday || cd.Date.DayOfWeek == DayOfWeek.Saturday);
                Color dc = adj ? cAdjDay
                          : (cd.IsToday ? P.AccBright
                             : (weekend ? (P.Dark ? Color.FromArgb(255, 214, 120, 112) : Color.FromArgb(255, 176, 64, 56))
                                        : P.Num));
                string ds = cd.Date.Day.ToString(CultureInfo.InvariantCulture);
                float dtop = ctop + lineTop0;
                // 1.4：日号按「两位字位」靠右对齐（个位数不再贴左）
                float dslot = M.CalDayCell * 2f;
                float dwid = ds.Length * M.CalDayCell;
                Sgn.Draw(g, ds, M.FCalDay, cx + 2f + (dslot - dwid), dtop, dayH, M.CalDayCell, 0.42f, dc,
                         adj ? Color.FromArgb(0, 0, 0, 0) : P.Glow, 0);

                // 农历：右对齐、底边与日号齐平（2 字，廿一…廿九）
                if (cd.LunarText.Length > 0)
                {
                    Color lc = adj ? cAdjLun : cLun;
                    using (SolidBrush lb = new SolidBrush(lc))
                        Ui.DrawRight(g, cd.LunarText, fLun, lb, cx + Metrics.CalColW - 3f, dtop + dayH - lunInkH);
                }

                // 节日：从日号下方 1px 起，剩余高度决定行数（最多 2 行）
                float fy = dtop + dayH + 1f;
                int maxLines = 0;
                while (maxLines < 2 && fy + maxLines * pitch + festInkH <= ctop + cellH - 1f) maxLines++;
                int lineIdx = 0;
                int drawn = 0;
                Color festCol = adj ? cAdjFest : cFest;
                foreach (string name in cd.Fest)
                {
                    // 2.0：除夕 / 春节 / 元宵 一律标红（216,0,0），其余节日保持原色
                    using (SolidBrush fb = new SolidBrush(!adj && IsRedFest(name) ? FestRed : festCol))
                    {
                        WrapCell(name, en, parts);
                        for (int pi = 0; pi < parts.Count && lineIdx < maxLines; pi++, lineIdx++)
                        {
                            Ui.DrawAt(g, parts[pi], fFest, fb, cx + 3f, fy + lineIdx * pitch);
                            drawn++;
                        }
                    }
                    if (lineIdx >= maxLines) break;
                }
                // 画不下的（含模型判定的 More）统一计入角标，避免节日静默消失
                int hidden = cd.Fest.Count - drawn + (adj ? 0 : cd.More);
                if (hidden > 0 && !adj)
                    using (SolidBrush bb = new SolidBrush(cLun))
                        Ui.DrawRight(g, "+" + hidden.ToString(CultureInfo.InvariantCulture), M.FCalBadge, bb,
                                     cx + Metrics.CalColW - 4f, ctop + 1f);
            }
        }

        void DrawWorld(Graphics g, Row r, DateTime now)
        {
            DateTime t = TimeZoneInfo.ConvertTime(DateTime.UtcNow, r.C.Zone);
            float y0 = r.Y;

            string hm = t.ToString("HH:mm", CultureInfo.InvariantCulture);
            float inkTop = y0 + (Metrics.WorldH - M.FWInkH) / 2f;
            Sgn.Draw(g, hm, M.FWTime, M.FWLeft, inkTop, M.FWInkH, M.FWCell, 0.42f, P.Num, P.Glow, 1);

            string md = t.ToString("MM") + "/" + t.ToString("dd");
            float dw = DateWidth(md);
            float dateLeft = 478f - dw;
            float dateTop = y0 + 18f;

            GraphicsState st = g.Save();
            g.TranslateTransform(dateLeft, dateTop);
            g.ScaleTransform(M.TabK, 1f);
            float x = 0;
            foreach (char ch in md)
            {
                string one = ch.ToString();
                Rectangle ink = Ui.Ink(M.FWDate, one);
                using (SolidBrush b = new SolidBrush(ch == '/' ? P.Dim : P.Num))
                    g.DrawString(one, M.FWDate, b, x - ink.X, -ink.Y, StringFormat.GenericTypographic);
                x += ink.Width + 0.5f;
            }
            g.Restore(st);

            // MONTH / DATE 小标
            float mmW = DateAdvance(md.Substring(0, 2)) * M.TabK;
            float ddW = DateAdvance(md.Substring(3, 2)) * M.TabK;
            float labBottom = y0 + CnBottom() + 6f;
            float labTop = labBottom - Ui.Ink(M.FWLabel, "M").Height;
            using (SolidBrush b = new SolidBrush(P.Acc))
            {
                Ui.DrawCenter(g, "MONTH", M.FWLabel, b, dateLeft + mmW / 2f, labTop);
                Ui.DrawCenter(g, "DATE", M.FWLabel, b, dateLeft + dw - ddW / 2f, labTop);
            }

            // 两道发丝线
            float l1 = Line1();
            float l2 = Line2(md);
            using (Pen pen = new Pen(P.Line, 1f))
            {
                g.DrawLine(pen, l1 + 0.5f, y0 + 18f, l1 + 0.5f, y0 + 62f);
                g.DrawLine(pen, l2 + 0.5f, y0 + 18f, l2 + 0.5f, y0 + 62f);
            }
        }

        // ==================================================== 世界模块（滚屏）
        string MarqueeText(City c)
        {
            if (S.Lang == "en")
            {
                // 英文版：城市·州省（仅字母码）·国家三字母，例如 Seattle·WA·USA
                string s = (c.En != null) ? c.En : "";
                if (c.A1 != null && c.A1.Length > 0) s += "·" + c.A1;
                if (c.CC3 != null && c.CC3.Length > 0) s += "·" + c.CC3;
                else if (c.Country != null && c.Country.Length > 0) s += "·" + c.Country;
                return s;
            }
            // 没有中文译名的城市：CnFor 会把中文位填成英文名，别显示成 "Seattle · Seattle"
            if (c.En != null && c.En.Length > 0 && !string.Equals(c.ZhUI, c.En, StringComparison.OrdinalIgnoreCase))
                return c.ZhUI + " · " + c.En;
            // 无中文译名的国外地点：照英文版套路补「地名·中文州名·中文国名」，
            // 州名查不到就只加国名（Kirkland·华盛顿州·美国 / 某小镇·日本）
            string s2 = (c.ZhUI != null && c.ZhUI.Length > 0) ? c.ZhUI : ((c.En != null) ? c.En : "");
            if (s2.Length == 0) return s2;
            string st = GeoZh.StateOf(c.CC, c.A1);
            if (st.Length > 0) s2 += "·" + st;
            string co = GeoZh.CountryOf(c.CC);
            if (co.Length == 0 && c.Country != null) co = c.Country;
            if (co.Length > 0) s2 += "·" + co;
            return s2;
        }

        bool IsCjk(char ch)
        {
            return ch >= 0x2E80;
        }

        float MarqueeWidth(string s)
        {
            float x = 0;
            foreach (char ch in s)
            {
                if (ch == ' ') { x += M.SzEn * 0.20f; continue; }
                if (ch == '·') { x += M.SzEn * 0.11f + 10f; continue; }
                Font f = IsCjk(ch) ? M.MCn : M.MEn;
                x += Ui.Ink(f, ch.ToString()).Width + (IsCjk(ch) ? 1f : 0.5f);
            }
            return x;
        }

        void DrawMarqueeRun(Graphics g, string s, float x0, float bottom, SolidBrush numB, SolidBrush dimB)
        {
            float x = x0;
            foreach (char ch in s)
            {
                if (ch == ' ')
                {
                    x += M.SzEn * 0.20f;
                    continue;
                }
                if (ch == '·')
                {
                    float dot = M.SzEn * 0.11f;
                    float cy = bottom - M.SzCn * 0.5f;
                    g.FillRectangle(dimB, x + 5f, cy - dot / 2f, dot, dot);
                    x += dot + 10f;
                    continue;
                }
                string one = ch.ToString();
                Font f = IsCjk(ch) ? M.MCn : M.MEn;
                Rectangle ink = Ui.Ink(f, one);
                float by = IsCjk(ch) ? bottom : bottom - 2f;   // 拉丁底边再上抬 2px
                g.DrawString(one, f, numB, x - ink.X, by - ink.Height - ink.Y, StringFormat.GenericTypographic);
                x += ink.Width + (IsCjk(ch) ? 1f : 0.5f);
            }
        }

        void DrawMarquee(Graphics g, Row r, DateTime now)
        {
            // 文字与宽度只在城市 / 语言变化后重算：每帧重算是 60fps 下的主要分配来源
            if (r.MDirty || r.MTxt == null)
            {
                r.MTxt = MarqueeText(r.C);
                r.MW = MarqueeWidth(r.MTxt);
                r.MDirty = false;
                r.MDateMin = null;                 // 换城市后日期缓存也作废
            }
            string txt = r.MTxt;
            float w = r.MW;
            // 日期串按分钟缓存：60fps × 每行一次 ToString + 跨时区换算，纯粹是白烧 CPU
            if (r.MDate == null || r.MDateMin != lastMinute)
            {
                r.MDate = TimeZoneInfo.ConvertTime(DateTime.UtcNow, r.C.Zone).ToString("MM/dd", CultureInfo.InvariantCulture);
                r.MDateMin = lastMinute;
            }
            string md = r.MDate;
            float l1 = Line1();
            float l2 = Line2(md);
            RectangleF clip = new RectangleF(l1 + 8f, r.Y + 4f, (l2 - 8f) - (l1 + 8f), 72f);
            if (clip.Width < 20f) return;

            float offset;
            if (!off.TryGetValue(r.C.Key, out offset)) { offset = 0f; off[r.C.Key] = 0f; }
            float period = w + marqueeGap;
            if (period < 40f) period = 40f;
            if (offset >= period || offset < 0f) offset = 0f;           // 切换城市 / 改语言后的脏值直接归零
            offPeriod[r.C.Key] = period;                                 // 供 OnTick 做有界取模

            GraphicsState st = g.Save();
            g.SetClip(clip, CombineMode.Replace);
            float bottom = r.Y + CnBottom();
            float origin = clip.Left - offset;                          // 亚像素推进：GDI+ 抗锯齿插值，运动连续不跳格
            using (SolidBrush numB = new SolidBrush(P.Num))
            using (SolidBrush dimB = new SolidBrush(P.Dim))
            {
                DrawMarqueeRun(g, txt, origin, bottom, numB, dimB);
                if (origin + period < clip.Right)                        // 第二份完全在裁剪区外就不画，省一半绘制
                    DrawMarqueeRun(g, txt, origin + period, bottom, numB, dimB);
            }
            g.Restore(st);
        }

        // ==================================================== 定时器
        void OnTick(object s, EventArgs e)
        {
            // 1.6：右键菜单（含子菜单）开着的时候不推帧 —— 16ms 一拍的全卡逐像素 blit 会跟
            // 下拉窗口的首次量布局抢 UI 线程，用户看到的就是「第一次悬停卡一下」。让出线程即可。
            if (menu != null && menu.Visible && !menu.IsDisposed) return;
            try
            {
                DateTime now = DateTime.Now;
                string minute = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
                if (minute != lastMinute)
                {
                    lastMinute = minute;
                    RenderStatic();
                    try { TimeZoneMatcher.TrimIfIdle(); } catch (Exception) { }   // 1.8.4：城市全表闲置 10 分钟自动卸载
                }
                if (tmr.CdState == 1 && tmr.CdLeftMs() <= 0) TimerFinished();   // 1.4：倒计时到点

                // 1.5：秒表 59:59.999 封顶。状态 3 原先全工程无人赋值，是条死分支；
                // 这里补上真正的判定，免得跑满后继续累加把数显挤歪
                if (tmr.Active == 1 && tmr.SwState == 1 && tmr.SwMs() >= 3599999L)
                {
                    tmr.SwAcc = 3599999L;
                    tmr.SwState = 3;
                    UpdateTimerMode();
                    RenderStatic();
                }

                bool fast = needScroll || tmr.Running || tmr.CdState == 2;
                if (!fast)
                {
                    PaintFrame();                         // 主时钟冒号按秒闪，秒级心跳也要推一帧
                    tickErrors = 0;
                    return;
                }

                float dt = 0.016f;
                double nowMs = clockDt.ElapsedMilliseconds;
                double delta = (double)(nowMs - lastTickMs) / 1000.0;
                lastTickMs = nowMs;
                if (delta > 0.0005 && delta < 0.12) dt = (float)delta;   // 按真实帧间隔推进，速度不随帧率漂移

                float step = S.Speed * dt;
                offKeys.Clear();
                foreach (string k in off.Keys) offKeys.Add(k);           // 复用列表，不每帧分配
                foreach (string k in offKeys)
                {
                    float period;
                    if (!offPeriod.TryGetValue(k, out period) || period < 40f) period = 160f;
                    float v = off[k] + step;
                    // 有界取模：长时间运行后 offset 不再无界增长（否则 while 空转 + float 精度崩塌）
                    v -= period * (float)Math.Floor(v / period);
                    off[k] = v;
                }
                PaintFrame();                             // 每帧都推：滚动是连续的，跳过帧反而一顿一顿
                tickErrors = 0;
            }
            catch (Exception)
            {
                // 吞异常但要有下限：连续失败约 4 秒就停表，避免每 16ms 抛一次、界面永久不刷新
                tickErrors++;
                if (tickErrors > 240)
                {
                    tickErrors = 0;
                    try { tick.Stop(); } catch (Exception) { }
                }
            }
        }

        /// <summary>按「是否真的需要逐帧刷新」决定计时器节奏：滚屏 / 秒表 / 到点闪烁走 16ms，其余 1s 心跳</summary>
        void UpdateTimerMode()
        {
            needScroll = false;
            foreach (Row r in rows) if (r.World) { needScroll = true; break; }
            bool fast = needScroll || tmr.Running || tmr.CdState == 2;
            if (fast)
            {
                if (tick.Interval != 16) tick.Interval = 16;
                if (!hiResTimer)
                {
                    try { timeBeginPeriod(1); hiResTimer = true; } catch (Exception) { }
                }
                if (!tick.Enabled) { clockDt.Stop(); clockDt.Start(); lastTickMs = 0; tick.Start(); }
            }
            else
            {
                if (hiResTimer) { try { timeEndPeriod(1); } catch (Exception) { } hiResTimer = false; }
                if (tick.Interval != 1000) tick.Interval = 1000;
                if (!tick.Enabled) tick.Start();
            }
        }

        void OnWatchdog(object s, EventArgs e)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                RememberScreen();
                if (S.Parked) TryRestorePark();        // 原显示器回来了就归位
                KeepAboveDesktop();
                WatchWallpaper();                      // 1.8.2：磨砂跟随壁纸
            }
            catch (Exception) { }
        }

        int wallSigTick;
        string wallSigLast = "";
        // 自检观察点：看门狗是否在跑、指纹比较是否真的走到
        public int WallChecks { get { return wallChecks; } }
        public int WallHits { get { return wallHits; } }
        int wallChecks, wallHits;

        /// <summary>自检用：立刻做一次指纹核对（不必等 8 个 120ms 心跳）</summary>
        public void CheckWallpaperNowForTest() { wallSigTick = 7; WatchWallpaper(); }

        /// <summary>
        /// 1.8.2：秒级核对壁纸指纹。1.8.1 只靠 SystemEvents.UserPreferenceChanged(Desktop) 失效
        /// 整幅壁纸缓存，该事件在分层窗口下并不总会送达 —— 换壁纸后磨砂画面就停在第一张。
        /// 这里用「路径 + 修改时间 + 大小」的指纹兜底（每次只读一个文件属性，代价可忽略），
        /// 变化即重抓整幅 + 重绘；关闭再打开磨砂走 SetBlur 的显式失效。
        /// </summary>
        // 2026-11-17：换壁纸后的延迟。转码文件写好 ≠ WorkerW 表面贴好，立刻 PrintWindow 容易
        // 抓到上一张（抓到后又会把缓存锁死）。等 WallRefreshDelayMs 再刷，到期时再读一次指纹，
        // 若这期间又变了就再等一轮 —— 让「换图事件」与「表面已更新」对齐。
        const int WallRefreshDelayMs = 520;
        DateTime wallDirtyAt = DateTime.MinValue;
        string wallDirtySig = "";
        DateTime wallLastApply = DateTime.UtcNow;      // 自愈计时基准
        int wallNoSig;                                  // 连续「取不到指纹」的秒数

        /// <summary>排一次延迟刷新：以当前读到的新指纹为「期望稳定值」，520ms 后确认稳定再执行</summary>
        void ScheduleWallRefresh()
        {
            try { wallDirtySig = WallpaperFrost.WallpaperSignature(); } catch (Exception) { wallDirtySig = ""; }
            wallDirtyAt = DateTime.UtcNow.AddMilliseconds(WallRefreshDelayMs);
        }

        void WatchWallpaper()
        {
            if (!FrostActive() || !Visible || IsDisposed) return;

            // ① 到点刷新（每 120ms 一次机会）：先确认指纹已稳定，稳定才真去重抓
            if (wallDirtyAt != DateTime.MinValue && DateTime.UtcNow >= wallDirtyAt)
            {
                wallDirtyAt = DateTime.MinValue;
                string s2 = WallpaperFrost.WallpaperSignature();
                if (s2 != "?" && s2 != wallDirtySig)
                {
                    wallSigLast = s2;
                    wallDirtySig = s2;
                    wallDirtyAt = DateTime.UtcNow.AddMilliseconds(WallRefreshDelayMs);   // 还没稳定，再等一轮
                }
                else
                {
                    if (menu != null && menu.Visible && !menu.IsDisposed) wallPending = true;   // 菜单开着不抢帧，等关掉补刷
                    else ApplyWallpaperChange();
                }
            }

            // ② 自愈：120 秒无条件重抓一次。万一某次真抓到了旧帧，也不会永久停在旧图。
            if (wallLastApply != DateTime.MinValue
                && (DateTime.UtcNow - wallLastApply).TotalSeconds >= 120)
            {
                wallLastApply = DateTime.UtcNow;
                ScheduleWallRefresh();
            }

            if (++wallSigTick < 8) return;                       // 120ms × 8 ≈ 1 秒
            wallSigTick = 0;
            string sig = WallpaperFrost.WallpaperSignature();
            wallChecks++;
            if (sig == "?")
            {
                // 2026-11-17：取不到指纹时不再「永不失效」，每 5 秒兜底重抓一次
                if (++wallNoSig >= 5) { wallNoSig = 0; ScheduleWallRefresh(); }
                return;
            }
            wallNoSig = 0;
            if (wallSigLast.Length == 0) { wallSigLast = sig; return; }   // 首次只登记基线
            if (sig == wallSigLast) return;
            wallHits++;
            wallSigLast = sig;
            ScheduleWallRefresh();
        }

        /// <summary>1.9：菜单开着时换了壁纸 → 记一笔，等菜单关掉再补刷（否则要等下一张壁纸才恢复）</summary>
        bool wallPending;

        /// <summary>
        /// 1.8.5：换壁纸后的统一刷新路径（看门狗与自检共用）。
        /// 1.8.4 及之前只重画主窗 —— 弹出日历得收回重弹才刷新，因为它的磨砂底有自己的缓存，
        /// 且没有任何人通知它重画。这里补上弹窗的 PaintLayer（FrostInvalidate 已作废其缓存）。
        /// </summary>
        void ApplyWallpaperChange()
        {
            wallLastApply = DateTime.UtcNow;                    // 自愈计时基准重置
            WallpaperFrost.InvalidateCache();
            FrostInvalidate();                                  // 内含 CalFrostInvalidate
            staticLayer = null;
            RenderStatic();
            PaintFrame();
            if (calWin != null && !calWin.IsDisposed) calWin.PaintLayer();   // 弹窗立即换磨砂
        }

        /// <summary>自检用：直接走「换壁纸刷新」路径（不受窗口是否可见影响）</summary>
        public void ApplyWallpaperChangeForTest() { ApplyWallpaperChange(); }

        /// <summary>
        /// 自检：--selftest=mn —— 菜单接线验证（1.8.5 修订回归）。
        /// 1.8.5 首版把磨砂档位闭包直接捕在 for 循环变量上，点 1~4 全落第 5 档。
        /// 这里离屏 BuildMenu，逐项 PerformClick，断言档位真的跟着点的那项走。
        /// </summary>
        public string MenuBlurSelfTest()
        {
            StringBuilder lg = new StringBuilder();
            SetTopmost(false);                       // 置顶时磨砂菜单置灰，先关掉
            S.Blur = true; S.BlurLevel = 3;          // 从标准档出发
            foreach (int want in new int[] { 1, 4, 2, 5, 3, 0 })   // 乱序点，0=关闭
            {
                ContextMenuStrip m = BuildMenu();
                ToolStripMenuItem blur = FindBlurSubmenu(m);
                if (blur == null) { lg.AppendLine("mn ERR 找不到磨砂子菜单"); m.Dispose(); break; }
                if (want >= 1 && want <= 5)
                {
                    ToolStripMenuItem it = (ToolStripMenuItem)blur.DropDownItems[want];
                    it.PerformClick();
                    lg.AppendLine("mn 点「" + it.Text + "」→ 实际 " + (S.Blur ? S.BlurLevel + " 档" : "关闭") + " "
                                + (S.Blur && S.BlurLevel == want ? "PASS" : "FAIL"));
                }
                else
                {
                    ToolStripMenuItem it = (ToolStripMenuItem)blur.DropDownItems[0];
                    it.PerformClick();
                    lg.AppendLine("mn 点「" + it.Text + "」→ 实际 " + (S.Blur ? S.BlurLevel + " 档" : "关闭") + " "
                                + (!S.Blur ? "PASS" : "FAIL"));
                }
                m.Dispose();
            }
            SetBlurLevel(3);                         // 还原标准档
            return lg.ToString();
        }

        /// <summary>在菜单里找磨砂子菜单：恰好 6 项、首项文字为「关闭/Off」的那一项</summary>
        ToolStripMenuItem FindBlurSubmenu(ContextMenuStrip m)
        {
            string off = T("关闭", "Off");
            foreach (ToolStripItem x in m.Items)
            {
                ToolStripMenuItem mi = x as ToolStripMenuItem;
                if (mi == null || mi.DropDownItems.Count != 6) continue;
                if (mi.DropDownItems[0].Text == off) return mi;
            }
            return null;
        }

        // ==================================================== Win+D 穿透（桌面层重升）
        static int ZRankOf(IntPtr target)
        {
            IntPtr h = GetTopWindow(IntPtr.Zero);
            int i = 0;
            while (h != IntPtr.Zero)
            {
                if (h == target) return i;
                h = GetWindow(h, GW_HWNDNEXT);
                i++;
                if (i > 10000) break;
            }
            return -1;
        }

        /// <summary>
        /// 真桌面层窗口（承载壁纸的那一个）：桌面层里还有一堆 135×37、14×14、0×0 的
        /// shell 小工具窗（有的还是 Visible），它们混在 Z 序里，拿它们当锚点会把挂件放到
        /// 错误的位置。这里只认覆盖面积够大的那个。
        /// </summary>
        static bool IsBigLayerWindow(IntPtr h)
        {
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            return (r.Right - r.Left) >= 200 && (r.Bottom - r.Top) >= 200;
        }

        static IntPtr TopDesktopWindow()
        {
            IntPtr best = IntPtr.Zero;
            int bestRank = int.MaxValue;
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero && IsBigLayerWindow(progman))
            {
                int r = ZRankOf(progman);
                if (r >= 0 && r < bestRank) { bestRank = r; best = progman; }
            }
            IntPtr desktop = GetDesktopWindow();
            IntPtr w = IntPtr.Zero;
            do
            {
                w = FindWindowEx(desktop, w, "WorkerW", null);
                if (w != IntPtr.Zero && IsBigLayerWindow(w))
                {
                    int r = ZRankOf(w);
                    if (r >= 0 && r < bestRank) { bestRank = r; best = w; }
                }
            } while (w != IntPtr.Zero);
            return best;
        }

        static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static bool IsDesktopLayer(IntPtr h)
        {
            string c = ClassOf(h);
            return c == "Progman" || c == "WorkerW";
        }

        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        const uint WS_EX_TOOLWINDOW = 0x80;

        /// <summary>「普通窗口」= 用户眼里算一个窗口的东西：可见、没最小化、非桌面层、非置顶、非工具窗、非本进程</summary>
        bool IsNormalWindow(IntPtr h, uint myPid)
        {
            if (h == IntPtr.Zero || h == Handle) return false;
            if (!IsWindowVisible(h)) return false;
            if (IsIconic(h)) return false;                                   // Win+D 把窗口全最小化：那不算“挡着我”
            string cls = ClassOf(h);
            if (cls == "Progman" || cls == "WorkerW") return false;
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            if (pid == myPid) return false;                                   // 自己的弹出日历 / 右键菜单
            uint ex = (uint)GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOPMOST) != 0) return false;
            // 别的桌面挂件/浮动工具栏也是工具窗，它们不该把本挂件压到墙纸下面去
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            return true;
        }

        /// <summary>
        /// 让窗口停在「桌面层正上方、所有普通窗口之下」。
        /// 一次扫描同时算出：自己的名次、最高桌面层窗口的名次、以及**桌面层之上**最靠下的
        /// 普通窗口。注意「桌面层之上」这个限定：系统里挂着一些排在桌面层下面的可见窗口
        /// （如 TextInputHost 的 Windows.UI.Core.CoreWindow），若把它当成「最靠下的普通窗口」
        /// 就会把挂件插到它下方 —— 也就是壁纸下面，Win+D 后直接看不见。
        /// </summary>
        void KeepAboveDesktop()
        {
            if (!IsHandleCreated || !Visible || IsDisposed) return;
            if (S.Topmost) return;                     // 置顶模式不需要

            // O(1) 预判：仍紧贴在记下的那个"最靠下普通窗口"上方 ⇒ 层级没变，直接返回
            IntPtr next = GetWindow(Handle, GW_HWNDNEXT);
            if (anchorLow != IntPtr.Zero && next == anchorLow && IsWindow(anchorLow)) return;

            uint myPid = 0;
            GetWindowThreadProcessId(Handle, out myPid);

            int myRank = -1, lowRank = -1, deskRank = int.MaxValue;
            IntPtr low = IntPtr.Zero;
            IntPtr h = GetTopWindow(IntPtr.Zero);
            int i = 0;
            while (h != IntPtr.Zero)
            {
                if (h == Handle) myRank = i;
                else if (IsDesktopLayer(h))
                {
                    // 只认承载壁纸的那个大桌面层窗口；shell 的 135×37 小 WorkerW 不算
                    if (IsBigLayerWindow(h) && i < deskRank) deskRank = i;
                }
                else if (i < deskRank && IsNormalWindow(h, myPid))
                {
                    low = h; lowRank = i;                              // 名次越走越大，最后留下的最靠下
                }
                h = GetWindow(h, GW_HWNDNEXT);
                i++;
                if (i > 4000) break;
            }
            if (lowRank > deskRank) { low = IntPtr.Zero; lowRank = -1; }   // 兜底：候选落到桌面层下面就不算

            if (low != IntPtr.Zero && lowRank >= 0)
            {
                // 有普通窗口：目标 = 紧贴「最靠下的普通窗口」之下
                // （这样无论资源管理器/其他程序谁在上层，挂件都在所有普通窗口之下）
                IntPtr below = GetWindow(low, GW_HWNDNEXT);               // 它下面那个窗口
                if (below != IntPtr.Zero)
                {
                    if (myRank == lowRank + 1) { anchorLow = below; return; }
                    SetWindowPos(Handle, below, 0, 0, 0, 0, SWP_ZFLAGS);  // 插到 below 之上 = low 之下
                    anchorLow = below;
                    KeepCalAboveMain();                                   // 主窗换位了，弹窗跟着贴回去
                    PushLayered();
                    return;
                }
            }

            // 没有可用普通窗口（Win+D 露出桌面 / 空桌面）：贴到最高的桌面层窗口之上
            IntPtr desk = TopDesktopWindow();
            if (desk == IntPtr.Zero) return;
            int dRank = deskRank != int.MaxValue ? deskRank : ZRankOf(desk);
            if (dRank == int.MaxValue || dRank < 0) return;
            if (myRank == dRank - 1) return;
            IntPtr insertAfter = GetWindow(desk, GW_HWNDPREV);            // 桌面层最高窗口正上方那个窗口
            SetWindowPos(Handle, insertAfter == IntPtr.Zero ? desk : insertAfter, 0, 0, 0, 0, SWP_ZFLAGS);
            anchorLow = IntPtr.Zero;                                     // 贴桌面层这条路径不做 O(1) 快判
            KeepCalAboveMain();                                          // 主窗换位了，弹窗跟着贴回去
            PushLayered();
        }

        // ==================================================== 多显示器拔插
        /// <summary>窗口矩形是否还落在某块「当前活跃」的屏幕上（至少露出 40×40）</summary>
        static bool IsOnActiveScreen(Rectangle wr)
        {
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(wr, sc.Bounds);
                if (inter.Width >= 40 && inter.Height >= 40) return true;
            }
            return false;
        }

        /// <summary>记下窗口当前所在屏的设备名；窗口已不在任何活跃屏时保持上一个已知值</summary>
        void RememberScreen()
        {
            Screen sc = ScreenOfWindow(new Rectangle(Left, Top, Width, Height));
            if (sc != null) { lastDev = sc.DeviceName; lastMon = MonitorIdOf(sc); }
        }

        /// <summary>显示器 DeviceID（形如 MONITOR\DEL4098\{…}），设备名被重排后用它兜底</summary>
        static string MonitorIdOf(Screen sc)
        {
            try
            {
                DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(sc.DeviceName, 0, ref dd, 1)) return "";
                return dd.DeviceID != null ? dd.DeviceID.Trim() : "";
            }
            catch (Exception) { return ""; }
        }

        static Screen ScreenByMonitor(string monId)
        {
            if (string.IsNullOrEmpty(monId)) return null;
            foreach (Screen sc in Screen.AllScreens)
            {
                string id = MonitorIdOf(sc);
                if (id.Length > 0 && string.Equals(id, monId, StringComparison.OrdinalIgnoreCase)) return sc;
            }
            return null;
        }

        static Screen ScreenByDevice(string dev)
        {
            if (string.IsNullOrEmpty(dev)) return null;
            foreach (Screen sc in Screen.AllScreens)
                if (string.Equals(sc.DeviceName, dev, StringComparison.OrdinalIgnoreCase)) return sc;
            return null;
        }

        /// <summary>取窗口面积覆盖最大的那块屏（只剩残边时返回 null）</summary>
        static Screen ScreenOfWindow(Rectangle wr)
        {
            Screen best = null;
            long bestArea = 0;
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(wr, sc.Bounds);
                long area = (long)inter.Width * inter.Height;
                if (area > bestArea) { bestArea = area; best = sc; }
            }
            return bestArea > 0 ? best : null;
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // 1.7：显示拓扑变化可能重建 WorkerW / 改虚拟屏尺寸，壁纸层缓存与磨砂一起失效
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (IsDisposed) return;
                    WallpaperFrost.InvalidateCache();
                    FrostInvalidate();
                    HandleDisplaysChanged();
                }));
            }
            catch (Exception) { }
        }

        void HandleDisplaysChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            Rectangle wr = new Rectangle(Left, Top, Width, Height);
            if (IsOnActiveScreen(wr))
            {
                // 屏还在：把它夹回该屏工作区内（分辨率变小时可能越界）
                Screen sc = ScreenOfWindow(wr);
                if (sc == null) return;
                Rectangle pb = sc.WorkingArea;
                int x = Math.Max(pb.Left, Math.Min(Left, pb.Right - Width));
                int y = Math.Max(pb.Top, Math.Min(Top, pb.Bottom - Height));
                if (x != Left || y != Top) { SetBounds(x, y, Width, Height); PushLayered(); }
                RememberScreen();
                TryRestorePark();                     // 原屏回来了（即使现在也在别处）就归位
                return;
            }
            ParkToActiveScreen();
        }

        /// <summary>所在屏断开：记下原屏与坐标，迁到仍活跃的主屏并居中</summary>
        void ParkToActiveScreen()
        {
            Screen cur = ScreenOfWindow(new Rectangle(Left, Top, Width, Height));
            if (!S.Parked)
            {
                S.Parked = true;
                S.ParkDevice = cur != null ? cur.DeviceName : lastDev;   // 屏已经没了，只能靠之前记住的设备名
                S.ParkMon = cur != null ? MonitorIdOf(cur) : lastMon;    // 副判据：显示器 DeviceID
                S.ParkX = Left;
                S.ParkY = Top;
            }
            Rectangle pb = Screen.PrimaryScreen.WorkingArea;
            int x = pb.Left + Math.Max(0, (pb.Width - Width) / 2);
            int y = pb.Top + Math.Max(0, (pb.Height - Height) / 2);
            SetBounds(x, y, Width, Height);
            SaveSettings();
            staticLayer = null; RenderStatic(); PaintFrame();
            KeepAboveDesktop();
        }

        /// <summary>原显示器回来了：回到断开前的位置（按新屏幕工作区夹取）</summary>
        bool TryRestorePark()
        {
            if (!S.Parked) return false;
            Screen sc = ScreenByDevice(S.ParkDevice);
            if (sc == null) sc = ScreenByMonitor(S.ParkMon);         // 设备名被重排：按显示器 DeviceID 找回
            if (sc == null) return false;
            Rectangle pb = sc.WorkingArea;
            int x = S.ParkX, y = S.ParkY;
            if (x == int.MinValue || y == int.MinValue) { x = pb.Left + (pb.Width - Width) / 2; y = pb.Top + 80; }
            x = Math.Max(pb.Left, Math.Min(x, pb.Right - Width));
            y = Math.Max(pb.Top, Math.Min(y, pb.Bottom - Height));
            S.Parked = false;
            SetBounds(x, y, Width, Height);
            SaveSettings();
            staticLayer = null; RenderStatic(); PaintFrame();
            KeepAboveDesktop();
            return true;
        }

        // ==================================================== 交互
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                pressMoved = false;
                pressScreen = Cursor.Position;
                dragOrigin = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                if (!pressMoved)
                {
                    // 位移超过 3px 才认作拖动：否则双击时窗口会被微移、并把点击判成拖拽
                    Point cur = Cursor.Position;
                    if (Math.Abs(cur.X - pressScreen.X) <= 3 && Math.Abs(cur.Y - pressScreen.Y) <= 3) return;
                    pressMoved = true;
                }
                Point p = new Point(Cursor.Position.X - dragOrigin.X, Cursor.Position.Y - dragOrigin.Y);
                Location = p;
                PushLayered();
                if (calWin != null && !calWin.IsDisposed)
                {
                    Rectangle want = CalWindowRect();           // 拖挂件时弹出日历跟着挪
                    if (calWin.Bounds != want) { calWin.Bounds = want; calWin.PaintLayer(); }
                    KeepCalAboveMain();                         // 拖动会把主窗抬到普通窗口层最上面，弹窗要跟着贴住
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false;
                if (pressMoved)
                {
                    SaveSettings();
                    RememberScreen();          // 拖动后重新记录所在屏，拔屏时才知道该记哪块
                    KeepAboveDesktop();        // 拖动会被系统抬到普通窗口之上，松手后拉回桌面层
                    FrostInvalidate();         // 换了位置，身后的壁纸就不是原来那张了
                    RenderStatic();
                    PaintFrame();
                }
                else HandleClick(e.Location);
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowMenu(e.Location);      // 右键在 MouseUp 触发（Windows 惯例）
            }
        }

        /// <summary>判定单击 / 双击（双击再分派给日历）</summary>
        void HandleClick(Point pt)
        {
            long now = Environment.TickCount;
            bool dbl = (now - lastClickMs) <= 450 && Math.Abs(pt.X - lastClickPt.X) <= 6 && Math.Abs(pt.Y - lastClickPt.Y) <= 6;
            lastClickMs = now;
            lastClickPt = pt;
            if (dbl)
            {
                lastClickMs = -100000;
                OnDblClick(pt);
                return;
            }
            HandleSingleClick(pt);
        }

        /// <summary>把客户区坐标换算成设计坐标（未扣卡片左上留白）</summary>
        PointF DesignPt(Point pt)
        {
            float x = (float)(pt.X / S.Scale) - Metrics.Pad;
            float y = (float)(pt.Y / S.Scale) - Metrics.Pad;
            return new PointF(x, y);
        }

        int RowAt(float dy)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (dy >= rows[i].Y && dy < rows[i].Y + rows[i].H) return i;
            }
            return -1;
        }

        void HandleSingleClick(Point pt)
        {
            PointF dp = DesignPt(pt);
            int idx = RowAt(dp.Y);
            if (idx < 0) return;
            Row r = rows[idx];
            if (r.Timer) { TimerClick(dp.X, dp.Y - r.Y); return; }
            if (!r.Cal) return;
            CalClickAt(dp.X, dp.Y - r.Y, false);
        }

        // ============================================ 倒计时 / 秒表交互
        void TimerClick(float x, float y)
        {
            int a = TimerView.Hit(M, S.Lang == "en", tmr, x, y);
            if (a == 0) return;
            // 1.5：切走秒表标签时先把秒表暂停 —— 否则心跳降回 1s、秒表却在后台继续跑，
            // 切回来数字会突然跳一大截
            if (a == 1) { if (tmr.Active == 1) TimerPause(); tmr.Active = 0; tmr.Field = -1; }
            else if (a == 2) { tmr.Active = 1; tmr.Field = -1; }
            else if (a == 3)
            {
                tmr.Alarm = !tmr.Alarm;
                S.TimerAlarm = tmr.Alarm;
                SaveSettings();
            }
            else if (a == 4) TimerStop();
            else if (a == 5) TimerPause();
            else if (a == 6) TimerStart();
            else if (a >= 7)
            {
                tmr.Field = a - 7;
                tmr.FieldDigits = 0;
                if (tmr.Field == 0) tmr.InH = -1;
                else if (tmr.Field == 1) tmr.InM = -1;
                else tmr.InS = -1;
                try { Activate(); } catch (Exception) { }
            }
            RefreshTimer();
        }

        void TimerStart()
        {
            if (tmr.Active == 0)
            {
                if (tmr.CdState == 2) { tmr.CdState = 0; tmr.Field = -1; return; }   // 到点后按 ▶：回到输入
                if (tmr.CdState == 1) return;
                int total = tmr.CdTotalSec();
                if (total <= 0) return;
                tmr.CdEnd = DateTime.Now.AddMilliseconds(total * 1000L);
                tmr.CdState = 1;
                tmr.Field = -1;
            }
            else
            {
                if (tmr.SwState == 1) return;
                if (tmr.SwState == 3) { tmr.SwAcc = 0; tmr.SwState = 0; }
                tmr.SwStart = DateTime.Now;
                tmr.SwState = 1;
            }
        }

        void TimerPause()
        {
            if (tmr.Active != 1) return;
            if (tmr.SwState != 1) return;
            tmr.SwAcc = tmr.SwMs();
            tmr.SwState = 2;
        }

        void TimerStop()
        {
            if (tmr.Active == 0)
            {
                tmr.CdState = 0;
                tmr.Field = -1;
            }
            else
            {
                tmr.SwState = 0;
                tmr.SwAcc = 0;
            }
        }

        /// <summary>倒计时到点：停数显、弹窗、按开关响闹</summary>
        void TimerFinished()
        {
            tmr.CdState = 2;
            tmr.Field = -1;
            CloseTimerPopup();
            TimerPopup p = new TimerPopup(S.Dark, S.Lang == "en", new Action(delegate
            {
                tmr.CdState = 0;
                RefreshTimer();
            }), S.Scale);
            Rectangle wr = Bounds;
            p.Location = new Point(wr.Left + (wr.Width - p.Width) / 2, wr.Top + (wr.Height - p.Height) / 2);
            timerPopup = p;
            p.Show(this);
            if (tmr.Alarm) p.StartAlarm();
        }

        void CloseTimerPopup()
        {
            if (timerPopup == null) return;
            TimerPopup p = timerPopup;
            timerPopup = null;
            try { if (!p.IsDisposed) p.Close(); } catch (Exception) { }
        }

        void RefreshTimer()
        {
            staticLayer = null;
            RenderStatic();
            PaintFrame();
            UpdateTimerMode();
        }

        // ============================================ 倒计时输入（键盘）
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!TimerKey(e.KeyCode)) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        bool TimerKey(Keys k)
        {
            if (tmr.Active != 0 || tmr.CdState != 0) return false;
            // 1.5：Esc 放弃输入 —— 原先只退焦点、数字还留在格子里，界面提示「Esc 放弃」名不副实
            if (k == Keys.Escape)
            {
                if (tmr.Field == 0) tmr.InH = -1;
                else if (tmr.Field == 1) tmr.InM = -1;
                else if (tmr.Field == 2) tmr.InS = -1;
                tmr.Field = -1;
                tmr.FieldDigits = 0;
                RefreshTimer();
                return true;
            }
            if (k == Keys.Back)
            {
                if (tmr.Field < 0) return false;
                if (tmr.Field == 0) tmr.InH = -1;
                else if (tmr.Field == 1) tmr.InM = -1;
                else tmr.InS = -1;
                tmr.FieldDigits = 0;
                RefreshTimer();
                return true;
            }
            if (k == Keys.Enter)
            {
                if (tmr.CdTotalSec() > 0) { TimerStart(); RefreshTimer(); }
                return true;
            }
            int d = -1;
            if (k >= Keys.D0 && k <= Keys.D9) d = (int)k - (int)Keys.D0;
            else if (k >= Keys.NumPad0 && k <= Keys.NumPad9) d = (int)k - (int)Keys.NumPad0;
            if (d < 0) return false;
            if (tmr.Field < 0) { tmr.Field = 0; tmr.FieldDigits = 0; }
            int f = tmr.Field;
            int cur = (f == 0) ? tmr.InH : ((f == 1) ? tmr.InM : tmr.InS);
            int nv = (tmr.FieldDigits == 0) ? d : ((cur < 0) ? d : cur * 10 + d);
            int lim = (f == 0) ? 9 : 59;
            if (nv > lim) nv = d;                       // 超上限：当作重新输入
            if (f == 0) tmr.InH = nv; else if (f == 1) tmr.InM = nv; else tmr.InS = nv;
            tmr.FieldDigits++;
            int maxDigits = (f == 0) ? 1 : 2;
            if (tmr.FieldDigits >= maxDigits)
            {
                tmr.FieldDigits = 0;
                if (f < 2) tmr.Field = f + 1;
            }
            RefreshTimer();
            return true;
        }

        /// <summary>日历内的单击：翻月 / 整年 / 回本月 / 收回。x,y 是日历模块内的设计坐标</summary>
        bool CalClickAt(float x, float y, bool inPopup)
        {
            float dummy;
            CalHdrLayout((S.Lang == "en") ? (MonEn[calM - 1] + " " + calY) : (calY + "年" + calM + "月"), inPopup, out dummy);
            if (inPopup && hClose.Width > 0 && hClose.Contains(x, y)) { CloseCalWindow(); return true; }
            if (hPrevYear.Contains(x, y)) { ShiftYear(-1); return true; }
            if (hNextYear.Contains(x, y)) { ShiftYear(1); return true; }
            if (hPrevMon.Contains(x, y)) { ShiftMonth(-1); return true; }
            if (hNextMon.Contains(x, y)) { ShiftMonth(1); return true; }
            if (hTitle.Contains(x, y))
            {
                calNav = false;                        // 点标题：回到"本月"
                RefreshCal();
                return true;
            }
            return false;
        }

        void RefreshCal()
        {
            if (calWin != null) calWin.Redraw();
            else
            {
                staticLayer = null;
                RenderStatic();
                PaintFrame();
            }
        }

        void ShiftMonth(int delta)
        {
            int ny = calY, nm = calM + delta;
            while (nm < 1) { nm += 12; ny--; }
            while (nm > 12) { nm -= 12; ny++; }
            if (ny < 1901 || ny > 2099) return;
            calY = ny; calM = nm; calNav = true;
            RefreshCal();
        }

        /// <summary>整年前翻 / 后翻</summary>
        void ShiftYear(int delta)
        {
            int ny = calY + delta;
            if (ny < 1901 || ny > 2099) return;
            calY = ny; calNav = true;
            RefreshCal();
        }

        void OnDblClick(Point pt)
        {
            PointF dp = DesignPt(pt);
            int act = DblAction(dp.X, dp.Y);

            // 1.5.1 弹出模式只认「日期区域」的双击来开关整块日历。
            // 原先 CalMode==2 时任何双击都被这块日历吃掉：主时钟的「日期和时间」设置、
            // 干支模块的换算框全失效，连弹出窗里双击某一天也只是把它自己收起来。
            if (act == ACT_TOGCAL)
            {
                if (calWin != null) CloseCalWindow(); else OpenCalWindow();
                return;
            }
            int idx = RowAt(dp.Y);
            if (idx < 0) return;
            Row r = rows[idx];
            if (act == ACT_GZPOP)
            {
                OpenGzPopup(PointToScreen(pt));
                return;
            }
            if (act == ACT_SETTINGS) { OpenDateTimeSettings(); return; }
            if (act == ACT_MONTH) { ShowMonthPopup(r.C, PointToScreen(pt)); return; }

            if (!r.Cal) return;
            CalDay cd = CalDayAt(dp.X, dp.Y - r.Y);
            if (cd == null) return;
            if (cd.IsAdjacent) { ShiftMonth(cd.Date < new DateTime(calY, calM, 1) ? -1 : 1); return; }
            ShowDayPopup(cd, PointToScreen(pt));
        }

        internal const int ACT_NONE = 0, ACT_TOGCAL = 1, ACT_GZPOP = 2, ACT_MONTH = 3, ACT_SETTINGS = 4;

        /// <summary>双击该做什么（设计坐标）—— 与 OnDblClick 分派同源，便于自检断言</summary>
        internal int DblAction(float x, float y)
        {
            int idx = RowAt(y);
            if (idx < 0) return ACT_NONE;
            Row r = rows[idx];
            if (S.CalMode == 2 && InDateArea(r, x, y)) return ACT_TOGCAL;   // 弹出模式下只认日期区域
            if (r.Gz) return ACT_GZPOP;
            if (r.Cal) return ACT_NONE;                                      // 日历格另算（见 OnDblClick）
            if (r.Local && InMainClockTime(r, x, y)) return ACT_SETTINGS;
            if (r.C != null) return ACT_MONTH;
            return ACT_NONE;
        }

        /// <summary>日历模块内某点落在哪一格（row 内相对 y；弹出窗里就是绝对 y）</summary>
        CalDay CalDayAt(float x, float y)
        {
            CalMonthModel m = CalModel();
            float titleH, subH, weekH;
            float gridTop = CalHeaderBottom(out titleH, out subH, out weekH);
            if (y < gridTop) return null;
            float cellH = CalCellH(gridTop, m.Rows);
            int rr = (int)((y - gridTop) / cellH);
            int col = (int)((x - Metrics.CalX0) / Metrics.CalPitch);
            if (rr < 0 || rr >= m.Rows || col < 0 || col > 6) return null;
            return m.Cells[rr * 7 + col];
        }

        /// <summary>
        /// 双击点是否落在「日期区域」：主时钟右侧的大字月/日块，或世界行右侧的 MM/dd 块。
        /// 弹出式日历只认这块区域（1.5.1），其余位置保留各模块自己的双击功能。
        /// </summary>
        bool InDateArea(Row r, float x, float y)
        {
            if (y < r.Y || y > r.Y + r.H) return false;
            // 2.0：中文皮肤里「日期区域」= 大字下方那行公历·星期·节日
            if (S.MainSkin == 1 && r.Local) return y >= r.Y + 100f;
            if (r.Local) return x >= M.MdX - 8f && x <= 484f;
            if (r.C != null)
            {
                DateTime t;
                try { t = TimeZoneInfo.ConvertTime(DateTime.UtcNow, r.C.Zone); }
                catch (Exception) { t = DateTime.Now; }
                float dateLeft = 478f - DateWidth(t.ToString("MM") + "/" + t.ToString("dd"));
                return x >= dateLeft - 6f && x <= 484f;
            }
            return false;
        }

        /// <summary>弹出窗里的双击：落在某一天就弹当日详情（1.5.1 原先这里直接把弹出日历收起来）</summary>
        internal void PopupDblClick(Point clientPt, Point screenPt)
        {
            float x = (float)(clientPt.X / S.Scale) - Metrics.Pad;
            float y = (float)(clientPt.Y / S.Scale) - Metrics.Pad;
            CalDay cd = CalDayAt(x, y);
            if (cd == null) return;
            if (cd.IsAdjacent) { ShiftMonth(cd.Date < new DateTime(calY, calM, 1) ? -1 : 1); return; }
            ShowDayPopup(cd, screenPt);
        }

        // ============================================ 弹出式日历（完整模块，不是简约版）
        CalWindow calWin;

        /// <summary>弹出日历该占的矩形（尺寸随缩放、位置按「堆叠总高>500 且侧边有空间→侧弹，否则→下方弹」）</summary>
        Rectangle CalWindowRect()
        {
            double sc = S.Scale;
            int pw = (int)Math.Round((Metrics.W + Metrics.Pad * 2) * sc);
            int ph = (int)Math.Round((Metrics.CalH + Metrics.Pad * 2) * sc);

            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Rectangle wr = Bounds;
            int gap = 8;
            int x, y;
            // 1.9 修订②：优先贴着挂件上下弹（下方 → 上方 → 兜底贴底），不再「总高 > 500 就一律甩到侧边」。
            // 拖到屏幕中下部时侧弹，弹窗会跑到挂件旁边去，看着就像错位（用户截图 2 那一条）。
            // 侧弹只在上下都放不下时才用，并且底对齐，视觉上仍贴着挂件。
            bool roomBelow = (wa.Bottom - wr.Bottom) >= ph + gap;
            bool roomAbove = (wr.Top - wa.Top) >= ph + gap;
            bool roomRight = (wa.Right - wr.Right) >= pw + gap;
            bool roomLeft = (wr.Left - wa.Left) >= pw + gap;

            if (roomBelow) { x = wr.Left; y = wr.Bottom + gap; }
            else if (roomAbove) { x = wr.Left; y = wr.Top - ph - gap; }
            else if (roomRight) { x = wr.Right + gap; y = wr.Bottom - ph; }
            else if (roomLeft) { x = wr.Left - pw - gap; y = wr.Bottom - ph; }
            else { x = wr.Left; y = Math.Max(wa.Top, wa.Bottom - ph); }

            if (x + pw > wa.Right) x = wa.Right - pw;
            if (x < wa.Left) x = wa.Left;
            if (y + ph > wa.Bottom) y = wa.Bottom - ph;
            if (y < wa.Top) y = wa.Top;
            return new Rectangle(x, y, pw, ph);
        }

        void OpenCalWindow()
        {
            if (calWin != null) { try { calWin.Close(); } catch (Exception) { } calWin = null; }
            DateTime mt = MainToday();
            if (!calNav || calY == 0) { calY = mt.Year; calM = mt.Month; }
            calWin = new CalWindow(this);
            calWin.Bounds = CalWindowRect();
            calWin.Show();
            // 层级必须在句柄建出来之后再定一次：窗还没建时设的 TopMost 可能被 Owner 关系刷掉
            if (calWin.TopMost != TopMost) calWin.TopMost = TopMost;
            KeepCalAboveMain();          // 1.9 修订②：新窗口默认落在普通窗口层最上面，必须插回主窗正上方
            calWin.PaintLayer();
        }

        /// <summary>设置变了（透明/主题/缩放/语言/主窗位置）→ 弹出日历跟着走</summary>
        void SyncCalWindow()
        {
            if (calWin == null || calWin.IsDisposed) return;
            // 1.9 修订：层级跟主窗走（主窗勾/取消「总在最前」，弹窗同步，不再自己常驻最前）
            if (calWin.TopMost != TopMost) calWin.TopMost = TopMost;
            Rectangle want = CalWindowRect();
            if (calWin.Bounds != want) calWin.Bounds = want;
            KeepCalAboveMain();
            calWin.PaintLayer();
        }

        /// <summary>
        /// 1.9 修订②：把弹出日历插回「主窗正上方」那一格。
        /// 主窗被 KeepAboveDesktop() 压在普通窗口层之下，而弹窗是新建窗口 —— 新窗口默认落在普通窗口层
        /// 的最上面，于是它盖住了挂件之外的所有应用（用户截图里「主窗层级正常、弹窗却盖住别的窗口」
        /// 就是这一条）。这里按主窗当前 Z 序位置把弹窗挪到它正上方，二者始终同一层、同进同退。
        /// </summary>
        void KeepCalAboveMain()
        {
            if (calWin == null || calWin.IsDisposed || !calWin.IsHandleCreated || !IsHandleCreated) return;
            if (S.Topmost) return;                        // 顶层模式下两者同在最前层，顺序由 Owned 关系保证
            IntPtr above = GetWindow(Handle, GW_HWNDPREV); // 主窗上面那个窗口（0 = 主窗已在最上）
            SetWindowPos(calWin.Handle, above, 0, 0, 0, 0, SWP_ZFLAGS);   // 插在它之下 = 紧贴主窗上方
        }

        void CloseCalWindow()
        {
            if (calWin == null) return;
            try { calWin.Close(); } catch (Exception) { }
            calWin = null;
        }

        /// <summary>供弹出窗口取绘制与点击逻辑</summary>
        internal void PopupPaint(Graphics g)
        {
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.ScaleTransform((float)S.Scale, (float)S.Scale);
            g.TranslateTransform(Metrics.Pad, Metrics.Pad);
            DrawCalCore(g, DateTime.Now, true);
            g.Restore(st);
        }

        /// <summary>弹出日历用的离屏画布（逐像素 alpha，与主窗同一套 P：透明度和主题实时跟随）</summary>
        Bitmap calBuf;

        // 1.8.3：弹出日历的磨砂底图缓存。key 含位置 —— 挪一步就要换一块壁纸区域；
        // 主窗 FrostInvalidate（换壁纸/开关磨砂）时一并作废。
        Bitmap calFrost;
        string calFrostKey = "";
        bool calFrostDrawn;
        int calFrostGrabs;      // 1.8.5：弹窗自己成功抓磨砂的次数（整幅计数不区分调用方，这里单独记）

        /// <summary>自检观察点：上一次 RenderCalCard 是否真的铺了磨砂底</summary>
        public bool CalFrostDrawnForTest { get { return calFrostDrawn; } }

        /// <summary>自检观察点：弹窗磨砂成功抓取次数（验证「换壁纸后弹窗自己重抓了」）</summary>
        public int CalFrostGrabsForTest { get { return calFrostGrabs; } }

        /// <summary>自检用：把当前弹窗磨砂底图存成 PNG（null 存不了返回 false）</summary>
        public bool CalFrostSnapshotForTest(string path)
        {
            if (calFrost == null) return false;
            try { calFrost.Save(path, ImageFormat.Png); return true; } catch (Exception) { return false; }
        }

        void CalFrostInvalidate()
        {
            if (calFrost != null) { try { calFrost.Dispose(); } catch (Exception) { } calFrost = null; }
            calFrostKey = "";
        }

        /// <summary>把弹出日历整卡（卡片底 + 内容）画进 calBuf，尺寸为屏幕像素。
        /// 1.8.3：left/top 传窗口在屏幕上的位置（离屏快照传 int.MinValue），磨砂开启且
        /// 位置在屏时先铺「糊壁纸 + 主题色」的不透明磨砂底（与主窗 RenderStatic 同一配方），
        /// 抓不到（离屏 / PrintWindow 失败）退回实心 P.Card —— 与主窗的降级路径一致。</summary>
        Bitmap RenderCalCard(int w, int h) { return RenderCalCard(w, h, int.MinValue, int.MinValue); }

        Bitmap RenderCalCard(int w, int h, int left, int top)
        {
            if (w <= 0 || h <= 0) return null;
            if (calBuf == null || calBuf.Width != w || calBuf.Height != h)
            {
                if (calBuf != null) calBuf.Dispose();
                calBuf = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            Graphics g = Graphics.FromImage(calBuf);
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // 1.8.3：磨砂底（先铺，内容后画）—— 半径/配方全走 WallpaperFrost.Grab，
                // 「背景透明度」已在 Grab 里按 t = TintOf×op 折进颜色，整块铺底、绝不按 alpha 压
                calFrostDrawn = false;
                if (FrostActive() && left > -30000 && top > -30000)
                {
                    string key = left + "," + top + "," + w + "," + h + "," + S.Style + "," + S.Scale + "," + S.Opacity;
                    if (calFrost == null || calFrostKey != key)
                    {
                        Bitmap nf = null;
                        try { nf = WallpaperFrost.Grab(new Rectangle(left, top, w, h), S.Style, S.Scale, S.Opacity, S.BlurLevel); }
                        catch (Exception) { nf = null; }
                        if (nf != null)
                        {
                            if (calFrost != null) { try { calFrost.Dispose(); } catch (Exception) { } }
                            calFrost = nf; calFrostKey = key;
                            calFrostGrabs++;
                        }
                        // nf == null：保留旧图（挪出屏又挪回来时不闪实心），key 不更新
                    }
                    if (calFrost != null) calFrostDrawn = true;
                }

                g.ScaleTransform((float)S.Scale, (float)S.Scale);
                RectangleF rr = new RectangleF(0.5f, 0.5f, Metrics.W + Metrics.Pad * 2 - 1f, Metrics.CalH + Metrics.Pad * 2 - 1f);
                using (GraphicsPath path = RoundRect(rr, Metrics.R))
                {
                    if (calFrostDrawn)
                    {
                        // 与主窗 DrawCard 同一条路：磨砂铺底后只描边 + 极薄一层主色压壁纸花
                        float rpx = (float)Math.Round(Metrics.R * S.Scale);
                        using (GraphicsPath clip = RoundRect(new RectangleF(0f, 0f, Metrics.W + Metrics.Pad * 2, Metrics.CalH + Metrics.Pad * 2), rpx))
                        {
                            g.SetClip(clip);
                            g.DrawImage(calFrost, 0, 0, Metrics.W + Metrics.Pad * 2, Metrics.CalH + Metrics.Pad * 2);
                            g.ResetClip();
                        }
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(26, P.Card))) g.FillPath(b, path);
                    }
                    else
                    {
                        using (SolidBrush b = new SolidBrush(P.Card)) g.FillPath(b, path);
                    }
                    using (Pen pen = new Pen(P.Edge, 1f)) g.DrawPath(pen, path);
                }
                GraphicsState st = g.Save();
                g.TranslateTransform(Metrics.Pad, Metrics.Pad);
                DrawCalCore(g, DateTime.Now, true);
                g.Restore(st);
                g.Flush();
            }
            finally { g.Dispose(); }
            return calBuf;
        }

        /// <summary>把弹出日历整卡重画并推进它的分层窗口</summary>
        internal void PaintCalWindowLayer(IntPtr target, int w, int h, int left, int top)
        {
            if (target == IntPtr.Zero)
            {
                CWLog("push skip: target=0");
                return;
            }
            Bitmap card = RenderCalCard(w, h, left, top);      // 1.8.3：传屏幕位置，磨砂按窗口身后那块壁纸裁
            if (card == null) { CWLog("push skip: card=null"); return; }
            if (!CreateCalSurface(w, h)) { CWLog("push skip: DIB 创建失败"); return; }
            // 和主窗同一条路：先画到独立 PArgb 位图，再整块拷进 DIB 推给 UpdateLayeredWindow
            // （直接把 GDI+ 位图 GetHdc 交给 UpdateLayeredWindow 会得到一片空白）
            calFrameG.Clear(Color.Transparent);
            calFrameG.DrawImageUnscaled(card, 0, 0);
            calFrameG.Flush();

            bool ok = false;
            int err = 0;
            try
            {
                IntPtr screen = ScreenDC();
                SIZE size = new SIZE(); size.CX = w; size.CY = h;
                POINT src = new POINT(); src.X = 0; src.Y = 0;
                POINT dst = new POINT(); dst.X = left; dst.Y = top;
                BLENDFUNC blend = new BLENDFUNC();
                blend.BlendOp = 0;
                blend.SourceConstantAlpha = 255;                 // 透明度已经画进 Card 的 alpha 里
                blend.AlphaFormat = (byte)AC_SRC_ALPHA;
                ok = UpdateLayeredWindow(target, screen, ref dst, ref size, calDC, ref src, 0, ref blend, ULW_ALPHA);
                if (!ok) err = Marshal.GetLastWin32Error();
            }
            catch (Exception ex) { CWLog("push throw: " + ex.Message); }
            CWLog("push " + w + "x" + h + "@" + left + "," + top + " ret=" + ok + " err=" + err
                  + " target=" + target + " visible=" + IsWindowVisible(target));
        }

        /// <summary>AT_TRACE=1 时把弹出日历推像素的情况写进 %TEMP%\at_cw.log</summary>
        static void CWLog(string s)
        {
            if (Environment.GetEnvironmentVariable("AT_TRACE") != "1") return;
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "at_cw.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
            }
            catch (Exception) { }
        }

        // ---- 弹出日历专用的离屏面：DIB + 包一层的 Bitmap（和主窗 CreateSurface 同一套路）
        IntPtr calDC, calDib, calOld, calBits;
        Bitmap calFrame;
        Graphics calFrameG;
        int calW, calH;

        void ReleaseCalSurface()
        {
            try { if (calFrameG != null) { calFrameG.Dispose(); calFrameG = null; } } catch (Exception) { }
            try { if (calFrame != null) { calFrame.Dispose(); calFrame = null; } } catch (Exception) { }
            if (calOld != IntPtr.Zero && calDC != IntPtr.Zero) { SelectObject(calDC, calOld); calOld = IntPtr.Zero; }
            if (calDib != IntPtr.Zero) { DeleteObject(calDib); calDib = IntPtr.Zero; }
            if (calDC != IntPtr.Zero) { DeleteDC(calDC); calDC = IntPtr.Zero; }
            calW = 0; calH = 0;
        }

        bool CreateCalSurface(int w, int h)
        {
            if (calDC != IntPtr.Zero && calFrameG != null && calW == w && calH == h) return true;
            ReleaseCalSurface();
            IntPtr screen = GetDC(IntPtr.Zero);
            calDC = CreateCompatibleDC(screen);
            ReleaseDC(IntPtr.Zero, screen);
            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;      // 自上而下
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;  // BI_RGB
            calDib = CreateDIBSection(calDC, ref bmi, 0, out calBits, IntPtr.Zero, 0);
            if (calDib == IntPtr.Zero || calBits == IntPtr.Zero) { ReleaseCalSurface(); return false; }
            calOld = SelectObject(calDC, calDib);
            try
            {
                calFrame = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, calBits);
                calFrameG = Graphics.FromImage(calFrame);
            }
            catch (Exception)
            {
                ReleaseCalSurface();
                return false;
            }
            calW = w; calH = h;
            return true;
        }

        /// <summary>自检：把弹出日历整卡的像素存成 PNG，回卡片底色处的不透明度（0..255）</summary>
        public int CalCardSnapshot(string path)
        {
            double sc = S.Scale;
            int w = (int)Math.Round((Metrics.W + Metrics.Pad * 2) * sc);
            int h = (int)Math.Round((Metrics.CalH + Metrics.Pad * 2) * sc);
            Bitmap bmp = RenderCalCard(w, h);
            if (bmp == null) return -1;
            try { bmp.Save(path, ImageFormat.Png); } catch (Exception) { }
            Color c = bmp.GetPixel(8, h / 2);
            return c.A;
        }

        /// <summary>
        /// 自检：打开弹出日历，再依次改透明度 / 主题 / 缩放 / 拖主窗，
        /// 记录卡片 alpha 与窗口矩形，证明「弹出窗跟随整体设置」这套线真的通
        /// </summary>
        public string CalWinSelfTest(string stem)
        {
            bool keep = suppressSave;
            suppressSave = true;
            System.Text.StringBuilder lg = new System.Text.StringBuilder();
            try
            {
                OpenCalWindow();
                lg.AppendLine("open   bounds=" + calWin.Bounds + "  cardAlpha=" + CalCardSnapshot(stem + "_cw_open.png"));
                // 1.9 修订：弹窗层级必须与主窗同一层（不再自己 TopMost），且被主窗 owner
                lg.AppendLine("layer  主窗 TopMost=" + TopMost + " 弹窗 TopMost=" + calWin.TopMost
                            + " Owner=" + (calWin.Owner == null ? "(无)" : "已挂主窗")
                            + " → " + ((calWin.TopMost == TopMost && calWin.Owner == this) ? "PASS" : "FAIL"));
                // 1.9 修订②：Z 序实况 —— 弹窗必须恰好插在主窗正上方一格（同层，且不压别的应用）
                {
                    int zCal = ZRankOf(calWin.Handle), zMain = ZRankOf(Handle);
                    lg.AppendLine("zorder 弹窗名次=" + zCal + " 主窗名次=" + zMain
                                + " 差=" + (zMain - zCal)
                                + " → " + ((zCal >= 0 && zMain >= 0 && zCal + 1 == zMain) ? "PASS" : "FAIL"));
                }
                KeepAboveDesktop();                       // 走一遍「压回普通窗口层之下」的真实路径
                {
                    int zCal = ZRankOf(calWin.Handle), zMain = ZRankOf(Handle);
                    lg.AppendLine("zorder 压回桌面层后 弹窗名次=" + zCal + " 主窗名次=" + zMain
                                + " 差=" + (zMain - zCal)
                                + " → " + ((zCal >= 0 && zMain >= 0 && zCal + 1 == zMain) ? "PASS" : "FAIL"));
                }
                SetOpacity(60);
                lg.AppendLine("op=60  bounds=" + calWin.Bounds + "  cardAlpha=" + CalCardSnapshot(stem + "_cw_op60.png"));
                SetStyle(0);
                lg.AppendLine("dark   bounds=" + calWin.Bounds + "  cardAlpha=" + CalCardSnapshot(stem + "_cw_dark.png"));
                SetScale(1.2);
                lg.AppendLine("sc=1.2 bounds=" + calWin.Bounds + "  cardAlpha=" + CalCardSnapshot(stem + "_cw_sc120.png"));
                SetOpacity(100);
                lg.AppendLine("op=100 bounds=" + calWin.Bounds + "  cardAlpha=" + CalCardSnapshot(stem + "_cw_op100.png"));
                Location = new Point(Left + 60, Top + 40);
                Rectangle want = CalWindowRect();
                lg.AppendLine("move   主窗=" + Bounds + " 弹出窗期望=" + want);
                SyncCalWindow();
                lg.AppendLine("move   弹出窗实际=" + calWin.Bounds);
                CloseCalWindow();

                // 1.8.3：磨砂段 —— 开磨砂重开弹出窗，验证「弹出卡也铺磨砂底」这条线
                PreviewBlurOn();
                OpenCalWindow();
                lg.AppendLine("frost  drawn=" + CalFrostDrawnForTest
                            + "  cardAlpha=" + CalCardSnapshot(stem + "_cw_frost.png"));
                lg.AppendLine("frost  底图=" + (CalFrostSnapshotForTest(stem + "_cw_frost_bmp.png") ? "已存" : "null"));
                SetOpacity(100);
                lg.AppendLine("frost  op=100 drawn=" + CalFrostDrawnForTest);

                // 1.8.5：弹出日历磨砂跟随换壁纸 —— 弹着的时候改壁纸指纹，主窗+弹窗应各重抓一次
                //（1.8.4 及之前弹窗必须收回重弹才刷新，根因是 WatchWallpaper 只重画主窗）
                int fc0 = WallpaperFrost.CaptureCount, pg0 = calFrostGrabs;
                string fsig = WallpaperFrost.WallpaperSignature();
                // 2.1.1：指纹不再是「路径|时间|大小」，路径改从 spi= 段取
                string fwp = "";
                int fsi = fsig.IndexOf("spi=", StringComparison.Ordinal);
                if (fsi >= 0)
                {
                    fwp = fsig.Substring(fsi + 4);
                    int fbar = fwp.IndexOf('|'); if (fbar >= 0) fwp = fwp.Substring(0, fbar);
                    int fsemi = fwp.IndexOf(';'); if (fsemi >= 0) fwp = fwp.Substring(0, fsemi);
                }
                if (fsig == "?" || fwp.Length == 0 || !System.IO.File.Exists(fwp))
                {
                    lg.AppendLine("popupfollow SKIP 取不到壁纸文件");
                }
                else
                {
                    System.IO.FileInfo ffi = new System.IO.FileInfo(fwp);
                    DateTime fold = ffi.LastWriteTimeUtc;
                    ffi.LastWriteTimeUtc = fold.AddMinutes(5);   // 视觉不变、指纹变
                    ApplyWallpaperChangeForTest();               // 与看门狗同一条路径
                    int fc1 = WallpaperFrost.CaptureCount, pg1 = calFrostGrabs;
                    ffi.LastWriteTimeUtc = fold;                 // 还原
                    lg.AppendLine("popupfollow 整幅抓取 " + fc0 + "→" + fc1
                                + " 弹窗抓取 " + pg0 + "→" + pg1 + " "
                                + (fc1 > fc0 && pg1 > pg0 ? "PASS" : "FAIL") + "（主窗重抓整幅、弹窗重抓自身）");
                }
                CloseCalWindow();
                SetBlur(false);          // 还原，免得影响后续自检段
                staticLayer = null; RenderStatic(); PaintFrame();
            }
            catch (Exception ex) { lg.AppendLine("ERR " + ex.Message); }
            finally { suppressSave = keep; }
            return lg.ToString();
        }

        internal void PopupClick(Point pt)
        {
            float x = (float)(pt.X / S.Scale) - Metrics.Pad;
            float y = (float)(pt.Y / S.Scale) - Metrics.Pad;
            CalClickAt(x, y, true);
        }

        /// <summary>世界行 / 主时钟的日期双击：弹该城市当地“当月”小日历</summary>
        void ShowMonthPopup(City c, Point anchor)
        {
            try
            {
                if (popup != null) { try { popup.Close(); } catch (Exception) { } popup = null; }
                DateTime local;
                try { local = TimeZoneInfo.ConvertTime(DateTime.UtcNow, c.Zone); }
                catch (Exception) { local = DateTime.Now; }
                MonthPopup mp = new MonthPopup(S.Dark, local.Date, S.Lang != "en", S.CalFirstDow);
                Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
                int px = anchor.X + 14, py = anchor.Y + 18;
                if (px + mp.Width > wa.Right) px = anchor.X - mp.Width - 14;
                if (py + mp.Height > wa.Bottom) py = anchor.Y - mp.Height - 12;
                if (px < wa.Left) px = wa.Left + 4;
                if (py < wa.Top) py = wa.Top + 4;
                mp.Location = new Point(px, py);
                mp.Show();
                monthPopup = mp;
            }
            catch (Exception) { monthPopup = null; }
        }

        /// <summary>离屏把世界行整月小日历画成 PNG（自检用）</summary>
        public void MonthPopupSnapshotSave(string path, int y, int mth)
        {
            DateTime day = new DateTime(y, mth, 1);
            DateTime local = DateTime.Now;
            if (S.MainCity != null)
            {
                try { local = TimeZoneInfo.ConvertTime(DateTime.UtcNow, S.MainCity.Zone); } catch (Exception) { }
            }
            MonthPopup mp = new MonthPopup(S.Dark, new DateTime(local.Year, local.Month, local.Day), S.Lang != "en", S.CalFirstDow);
            Bitmap bmp = new Bitmap(mp.Width, mp.Height);
            mp.Show();
            Application.DoEvents();
            mp.DrawToBitmap(bmp, new Rectangle(0, 0, mp.Width, mp.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
            mp.Close();
            mp.Dispose();
        }

        void ShowDayPopup(CalDay cd, Point anchor)
        {
            try
            {
                if (popup != null)
                {
                    try { popup.Close(); } catch (Exception) { }
                    popup = null;
                }
                string title;
                List<string> lines = BuildDayInfo(cd, out title);
                Font pf = new Font("Microsoft YaHei UI", 10f);
                popup = new CalPopup(S.Dark, title, lines, pf);
                Point c = anchor;
                Rectangle wa = Screen.FromPoint(c).WorkingArea;
                int px = c.X + 14, py = c.Y + 18;
                if (px + popup.Width > wa.Right) px = c.X - popup.Width - 14;
                if (py + popup.Height > wa.Bottom) py = c.Y - popup.Height - 12;
                if (px < wa.Left) px = wa.Left + 4;
                if (py < wa.Top) py = wa.Top + 4;
                popup.Location = new Point(px, py);
                popup.Show();
            }
            catch (Exception) { popup = null; }
        }

        /// <summary>当日详情文字（浮层标题 + 内容行）——浮层与离屏自检共用</summary>
        List<string> BuildDayInfo(CalDay cd, out string title)
        {
            DateTime today = MainToday();
            LunarInfo li = LunarCal.FromSolar(cd.Date);
            string term = SolarTerms.Of(cd.Date);
            List<string> lines = new List<string>();
            string cc = CalCC();
            string zh = (S.MainCity != null) ? S.MainCity.ZhUI : "";
            string en = (S.MainCity != null) ? S.MainCity.En : "";
            List<Fest> fests = HolidayDb.For(cd.Date, cc, li, term, S.CalLunar, zh, en);

            if (S.CalLunar && li != null)
                lines.Add(T("农历", "Lunar") + "：" + li.YearName + " " + li.MonthName + "月" + li.DayName);
            if (term.Length > 0) lines.Add(T("节气", "Solar term") + "：" + term);
            if (fests.Count == 0) lines.Add(T("无节日记录", "No festival"));
            else
            {
                foreach (Fest f in fests)
                    // v2.1 修（审阅 P1-3）：原来英文界面直接吐数据表缩写（SpringFest），
                    // 既与提示行的「Spring Festival」不一致，也让 Calendar 的 IsRedFest 认不出来。
                    lines.Add("· " + (S.Lang == "en" ? PrettyEn(f.En) : f.Zh));
            }
            if (cd.Date == today) lines.Add(T("（今天）", "(Today)"));

            string wk = S.Lang == "en" ? cd.Date.ToString("ddd", CultureInfo.GetCultureInfo("en-US"))
                                       : "星期" + WkZh[(int)cd.Date.DayOfWeek];
            title = cd.Date.Year + (S.Lang == "en" ? "/" : "年") + cd.Date.Month + (S.Lang == "en" ? "/" : "月")
                  + cd.Date.Day + (S.Lang == "en" ? "" : "日") + " " + wk;
            return lines;
        }

        /// <summary>离屏把当日详情浮层画成 PNG（自检用，不建窗口）</summary>
        public void PopupSnapshotSave(string path, DateTime day)
        {
            calY = day.Year; calM = day.Month; calNav = true;
            CalMonthModel m = CalModel();
            CalDay cd = null;
            foreach (CalDay c in m.Cells)
            {
                if (c != null && c.Date == day) { cd = c; break; }
            }
            if (cd == null) throw new Exception("date not in model: " + day.ToString("yyyy-MM-dd"));
            string title;
            List<string> lines = BuildDayInfo(cd, out title);
            Font pf = new Font("Microsoft YaHei UI", 10f);
            CalPopup pop = new CalPopup(S.Dark, title, lines, pf);
            Bitmap bmp = new Bitmap(pop.Width, pop.Height);
            pop.Show();                      // 需要窗口句柄才能 DrawToBitmap
            Application.DoEvents();
            pop.DrawToBitmap(bmp, new Rectangle(0, 0, pop.Width, pop.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
            pop.Close();
            pop.Dispose();
        }

        int RowIndexAt(Point clientPt)
        {
            float y = (float)(clientPt.Y / S.Scale) - Metrics.Pad;
            for (int i = 0; i < rows.Count; i++)
            {
                if (y >= rows[i].Y && y < rows[i].Y + rows[i].H) return i;
            }
            if (rows.Count == 0) return -1;
            return y < rows[0].Y ? 0 : rows.Count - 1;      // 落在上下留白里按最近的行算
        }

        // ==================================================== 菜单
        string T(string zh, string en) { return S.Lang == "en" ? en : zh; }

        void ShowMenu(Point clientPt)
        {
            int idx = RowIndexAt(clientPt);
            if (idx < 0) return;
            Row r = rows[idx];

            ContextMenuStrip m = BuildMenu();
            WarmMenu();                    // 1.6：子菜单窗口已预建，首次悬停不再现建
            // 菜单是独立窗口：先把自己提到前台，菜单收起逻辑才正常
            try { SetForegroundWindow(Handle); } catch (Exception) { }
            m.Show(this, clientPt);
        }

        /// <summary>重建菜单条目并返回（右键菜单与离屏自检共用）</summary>
        ContextMenuStrip BuildMenu()
        {
            // 常驻菜单：每次只重建条目，绝不在 Closed 回调里 Dispose。
            // 在 Closed 里销毁菜单，随后仍在执行的菜单消息会去访问已释放对象，表现为
            // 「任何菜单操作（含退出）都弹『无法访问已释放的对象。对象名:ContextMenuStrip』」。
            if (menu == null)
            {
                menu = new ContextMenuStrip();
                menu.ShowCheckMargin = true;    // 左侧勾选列（与前作 World bedside clock 一致）
                menu.ShowImageMargin = false;
                menu.Closed += new ToolStripDropDownClosedEventHandler(delegate {
                    KeepAboveDesktop();
                    // 1.9：菜单开着期间换过壁纸 → 关掉后补刷一次磨砂（否则要等下一张壁纸才恢复）
                    if (wallPending) { wallPending = false; try { ApplyWallpaperChange(); } catch (Exception) { } }
                });
            }
            ContextMenuStrip m = menu;
            // 1.6：旧条目要显式释放，否则每次右键都积一批 ToolStripItem（子菜单首次悬停时
            // 恰好赶上回收，看着就是「卡一下才弹开」）
            while (m.Items.Count > 0)
            {
                ToolStripItem it0 = m.Items[0];
                m.Items.RemoveAt(0);
                try { it0.Dispose(); } catch (Exception) { }
            }

            ToolStripMenuItem style = new ToolStripMenuItem(T("窗口样式", "Window Style"));
            ToolStripMenuItem dark = new ToolStripMenuItem(T("碳灰", "Charcoal"), null, new EventHandler(delegate { SetStyle(0); }));
            ToolStripMenuItem white = new ToolStripMenuItem(T("雪白", "Snow White"), null, new EventHandler(delegate { SetStyle(1); }));
            ToolStripMenuItem navy = new ToolStripMenuItem(T("藏青", "Navy"), null, new EventHandler(delegate { SetStyle(2); }));
            dark.Checked = (S.Style == 0); white.Checked = (S.Style == 1); navy.Checked = (S.Style == 2);
            style.DropDownItems.Add(dark);
            style.DropDownItems.Add(white);
            style.DropDownItems.Add(navy);
            m.Items.Add(style);

            ToolStripMenuItem size = new ToolStripMenuItem(T("窗口大小", "Window Size"));
            int[] pcts = { 120, 110, 100, 90, 80, 70, 60 };
            for (int i = 0; i < pcts.Length; i++)
            {
                int v = pcts[i];
                ToolStripMenuItem it = new ToolStripMenuItem(v + "%", null, new EventHandler(delegate { SetScale(v / 100.0); }));
                it.Checked = Math.Abs(S.Scale - v / 100.0) < 0.005;
                size.DropDownItems.Add(it);
            }
            m.Items.Add(size);

            ToolStripMenuItem opa = new ToolStripMenuItem(T("背景透明度", "Background Opacity"));
            for (int pct = 10; pct <= 100; pct += 10)
            {
                int v = pct;
                ToolStripMenuItem it = new ToolStripMenuItem(v + "%", null, new EventHandler(delegate { SetOpacity(v); }));
                it.Checked = (S.Opacity == v);
                opa.DropDownItems.Add(it);
            }
            m.Items.Add(opa);

            ToolStripMenuItem spd = new ToolStripMenuItem(T("文字滚动速度", "Scroll Speed"));
            int[] vals = { 20, 25, 30, 35, 40, 45, 50, 55 };   // 档位 1~8，不直接暴露 px/s
            for (int i = 0; i < vals.Length; i++)
            {
                int v = vals[i];
                int tier = i + 1;
                ToolStripMenuItem it = new ToolStripMenuItem(T("速度 ", "Speed ") + tier, null,
                    new EventHandler(delegate { S.Speed = v; SaveSettings(); }));
                it.Checked = (S.Speed == v);
                spd.DropDownItems.Add(it);
            }
            m.Items.Add(spd);

            ToolStripMenuItem top = new ToolStripMenuItem(T("总在最前", "Always on Top"));
            top.Checked = S.Topmost;
            top.Click += new EventHandler(delegate { SetTopmost(!S.Topmost); });
            m.Items.Add(top);

            // 1.6：背景磨砂（只针对壁纸层）。1.8.5：改为 6 项子菜单 —— 关闭 + 1~5 档模糊强度，
            // 第 3 档是 1.8.4 的默认观感。「总在最前」勾着时整组置灰（壁纸被窗口遮住抓不到）
            ToolStripMenuItem blur = new ToolStripMenuItem(T("磨砂效果（仅壁纸层）", "Frosted effect (wallpaper only)"));
            blur.Enabled = !S.Topmost;
            // 1.9 修订：不再挂气泡提示 —— 悬停在这项上会弹一个挡住下方子菜单的 Tip，
            // 反倒妨碍选档。置灰状态本身就说明「总在最前」时磨砂不生效。
            for (int lv = 0; lv <= 5; lv++)
            {
                int cur = lv;   // 1.8.5 修订：for 的循环变量整个循环共享，闭包直接捕 lv 的话
                                // 循环结束时 lv==6，点 1~4（含「关闭」）全落第 5 档。
                                // 与上方「窗口大小/透明度」同款做法：先拷进局部变量再捕获。
                string label = cur == 0 ? T("关闭", "Off") : cur.ToString();
                ToolStripMenuItem it = new ToolStripMenuItem(label, null,
                    new EventHandler(delegate { SetBlurLevel(cur); }));
                it.Checked = (cur == 0) ? !S.Blur : (S.Blur && S.BlurLevel == cur);
                blur.DropDownItems.Add(it);
            }
            m.Items.Add(blur);

            ToolStripMenuItem auto = new ToolStripMenuItem(T("开机自启", "Start with Windows"));
            auto.Checked = Settings.IsAutoStartOn();
            auto.Click += new EventHandler(delegate
            {
                bool on = !Settings.IsAutoStartOn();
                Settings.ApplyAutoStart(on);
                S.AutoStart = on;
                SaveSettings();
            });
            m.Items.Add(auto);

            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(new ToolStripMenuItem(T("时钟管理", "clocks configuration"), null, new EventHandler(delegate { ManageClocks(); })));

            // ---------------- 日历设置（第三类模块）
            ToolStripMenuItem cal = new ToolStripMenuItem(T("日历设置", "Calendar"));
            ToolStripMenuItem cAlways = new ToolStripMenuItem(T("常挂显示", "Always shown"), null,
                new EventHandler(delegate { SetCalMode(1); }));
            ToolStripMenuItem cPopup = new ToolStripMenuItem(T("弹出（双击日期弹出）", "Popup (double-click date)"), null,
                new EventHandler(delegate { SetCalMode(2); }));
            ToolStripMenuItem cOff = new ToolStripMenuItem(T("关闭", "Off"), null, new EventHandler(delegate { SetCalMode(0); }));
            cAlways.Checked = (S.CalMode == 1);
            cPopup.Checked = (S.CalMode == 2);
            cOff.Checked = (S.CalMode == 0);
            cal.DropDownItems.Add(cAlways);
            cal.DropDownItems.Add(cPopup);
            cal.DropDownItems.Add(cOff);
            cal.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem cPos = new ToolStripMenuItem(T("模块位置", "Position"));
            // 1.9 修订：固定四档锚点，不再按「第 1/2/3 位」序号排（序号随模块增减而变，很难选准）
            string[] aph = { "全表最上", "主时钟上方", "主时钟下方", "全表最下" };
            string[] ape = { "Very top", "Above main clock", "Below main clock", "Very bottom" };
            for (int i = 0; i < 4; i++)
            {
                int v = i;
                ToolStripMenuItem it = new ToolStripMenuItem(T(aph[i], ape[i]), null,
                    new EventHandler(delegate { SetCalAnchor(v); }));
                it.Checked = (S.CalAnchor == i && S.CalMode == 1);
                cPos.DropDownItems.Add(it);
            }
            cal.DropDownItems.Add(cPos);

            ToolStripMenuItem cDow = new ToolStripMenuItem(T("每周首日", "First day"));
            ToolStripMenuItem dSun = new ToolStripMenuItem(T("周日", "Sunday"), null, new EventHandler(delegate { SetCalFirstDow(0); }));
            ToolStripMenuItem dMon = new ToolStripMenuItem(T("周一", "Monday"), null, new EventHandler(delegate { SetCalFirstDow(1); }));
            dSun.Checked = (S.CalFirstDow == 0); dMon.Checked = (S.CalFirstDow == 1);
            cDow.DropDownItems.Add(dSun);
            cDow.DropDownItems.Add(dMon);
            cal.DropDownItems.Add(cDow);

            ToolStripMenuItem cLun = new ToolStripMenuItem(T("农历与节气", "Lunar & terms"));
            ToolStripMenuItem lOn = new ToolStripMenuItem(T("显示", "Show"), null, new EventHandler(delegate { SetCalLunar(true); }));
            ToolStripMenuItem lOff = new ToolStripMenuItem(T("不显示", "Hide"), null, new EventHandler(delegate { SetCalLunar(false); }));
            lOn.Checked = S.CalLunar; lOff.Checked = !S.CalLunar;
            cLun.DropDownItems.Add(lOn);
            cLun.DropDownItems.Add(lOff);
            cal.DropDownItems.Add(cLun);

            ToolStripMenuItem cCC = new ToolStripMenuItem(T("节日地区", "Region"));
            string[] ccs = { "auto", "CN", "US", "NZ", "GB", "JP", "DE", "FR", "CA", "AU", "none" };
            string[] cczh = { "跟随主时钟", "中国", "美国", "新西兰", "英国", "日本", "德国", "法国", "加拿大", "澳大利亚", "不显示" };
            string[] ccen = { "Follow main", "China", "USA", "NZ", "UK", "Japan", "Germany", "France", "Canada", "Australia", "None" };
            for (int i = 0; i < ccs.Length; i++)
            {
                string v = ccs[i];
                ToolStripMenuItem it = new ToolStripMenuItem(T(cczh[i], ccen[i]), null, new EventHandler(delegate { SetCalCC(v); }));
                it.Checked = (S.CalCC == v);
                cCC.DropDownItems.Add(it);
            }
            cal.DropDownItems.Add(cCC);

            m.Items.Add(cal);

            // 1.5：倒计时 / 秒表、干支计时不再单独开菜单项 —— 开关与排序全部并入「时钟管理」


            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem help = new ToolStripMenuItem(T("使用说明", "Help"), null, new EventHandler(delegate { ShowHelp(); }));
            m.Items.Add(help);

            m.Items.Add(new ToolStripMenuItem(T("关于", "About"), null, new EventHandler(delegate { ShowAbout(); })));

            m.Items.Add(new ToolStripMenuItem(S.Lang == "en" ? "中文" : "English", null,
                new EventHandler(delegate { SetLang(S.Lang == "en" ? "zh" : "en"); })));

            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(new ToolStripMenuItem(T("临时退出程序", "Quit (keep settings)"), null,
                new EventHandler(delegate { suppressSave = false; Close(); })));

            m.Items.Add(new ToolStripMenuItem(T("关闭全部窗口并清除全部配置", "Close All & Clear Settings"), null, new EventHandler(delegate { ClearAllAndExit(); })));

            return m;
        }

        /// <summary>
        /// 1.6：治「日历设置 → 模块位置」这类子菜单首次悬停卡顿。
        /// ToolStripDropDown 的下拉窗口是第一次 Show 时才 CreateWindow + 量布局的，
        /// 那份开销正好落在用户第一次把鼠标扫过父项的时候。这里在启动时把每层下拉的
        /// 窗口先建出来（用户看不见），首次悬停就只剩一个纯 Show。
        /// </summary>
        void WarmMenu()
        {
            if (menuWarmed || menu == null) return;
            menuWarmed = true;
            try
            {
                menu.CreateControl();
                WarmItems(menu.Items);
            }
            catch (Exception) { }
        }

        int WarmItems(ToolStripItemCollection items)
        {
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ToolStripMenuItem mi = items[i] as ToolStripMenuItem;
                if (mi == null || mi.DropDown == null || mi.DropDownItems.Count == 0) continue;
                try
                {
                    mi.DropDown.CreateControl();
                    mi.DropDown.PerformLayout();
                    n++;
                    n += WarmItems(mi.DropDownItems);
                }
                catch (Exception) { }
            }
            return n;
        }

        /// <summary>
        /// 自检用：量「子菜单第一次 Show」到底要多久 —— 这才是鼠标第一次扫过父项时真实付的钱。
        /// 分两组：A 组全新菜单不预热，B 组先 WarmItems 再 Show。
        /// </summary>
        public string MenuHoverBench()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int round = 0; round < 3; round++)
            {
                // A：不预热
                ContextMenuStrip a = BuildMenu();
                ToolStripDropDown da = PickCalSub(a);
                System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                sw.Start();
                try { da.Show(Point.Empty); } catch (Exception) { }
                sw.Stop();
                long firstShow = sw.ElapsedMilliseconds;
                sw.Reset(); sw.Start();
                try { da.Close(); } catch (Exception) { }
                sw.Stop();
                long closeMs = sw.ElapsedMilliseconds;
                sw.Reset(); sw.Start();
                try { da.Show(Point.Empty); } catch (Exception) { }
                sw.Stop();
                long secondShow = sw.ElapsedMilliseconds;
                try { da.Close(); } catch (Exception) { }
                sb.AppendLine("A 未预热 #" + (round + 1) + "：首次 Show " + firstShow + "ms，Close " + closeMs + "ms，再次 Show " + secondShow + "ms");
                a.Dispose();

                // B：预热后再 Show
                ContextMenuStrip b = BuildMenu();
                ToolStripDropDown db = PickCalSub(b);
                sw.Reset(); sw.Start();
                WarmItems(b.Items);
                sw.Stop();
                long warm = sw.ElapsedMilliseconds;
                sw.Reset(); sw.Start();
                try { db.Show(Point.Empty); } catch (Exception) { }
                sw.Stop();
                long showW = sw.ElapsedMilliseconds;
                try { db.Close(); } catch (Exception) { }
                sb.AppendLine("B 预热 #" + (round + 1) + "：预热 " + warm + "ms，首次 Show " + showW + "ms");
                b.Dispose();
            }
            return sb.ToString();
        }

        // 取「日历设置」那颗的子菜单（含模块位置那层）
        ToolStripDropDown PickCalSub(ContextMenuStrip m)
        {
            for (int i = 0; i < m.Items.Count; i++)
            {
                ToolStripMenuItem mi = m.Items[i] as ToolStripMenuItem;
                if (mi == null) continue;
                for (int j = 0; j < mi.DropDownItems.Count; j++)
                {
                    ToolStripMenuItem sub = mi.DropDownItems[j] as ToolStripMenuItem;
                    if (sub == null || sub.DropDownItems.Count == 0) continue;
                    string txt = sub.Text;
                    if (txt.IndexOf("模块位置") >= 0 || txt.IndexOf("Position") >= 0) return sub.DropDown;
                }
            }
            for (int i = 0; i < m.Items.Count; i++)
            {
                ToolStripMenuItem mi = m.Items[i] as ToolStripMenuItem;
                if (mi != null && mi.DropDownItems.Count > 0) return mi.DropDown;
            }
            return ((ToolStripMenuItem)m.Items[0]).DropDown;
        }

        /// <summary>自检用：量一帧全卡 blit 有多贵（16ms 一拍的定时器就是靠它占着 UI 线程）</summary>
        public void PaintFrameForTest() { PaintFrame(); }
        public void RenderStaticForTest() { RenderStatic(); }

        /// <summary>自检用：量「冷建一份全新菜单 + 把每层下拉窗口都建出来」要多久（就是原先首次悬停要付的钱）</summary>
        public string MenuBench()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
            int n = 0;
            long first = 0;
            for (int round = 0; round < 4; round++)
            {
                ContextMenuStrip fresh = BuildMenu();
                sw.Reset();
                sw.Start();
                n = WarmItems(fresh.Items);
                sw.Stop();
                if (round == 0) first = sw.ElapsedMilliseconds;
                sb.AppendLine("第 " + (round + 1) + " 次冷建：下拉窗口 " + n + " 个，耗时 " + sw.ElapsedMilliseconds + "ms");
                fresh.Dispose();
            }
            sb.AppendLine("首次（原来落在鼠标第一次悬停那一刻）= " + first + "ms");
            // 重建菜单本身的耗时（每次右键都会重建一遍）
            sw.Reset(); sw.Start();
            for (int i = 0; i < 30; i++) { ContextMenuStrip t = BuildMenu(); t.Dispose(); }
            sw.Stop();
            sb.AppendLine("重建菜单 30 次 共 " + sw.ElapsedMilliseconds + "ms，平均 " + (sw.ElapsedMilliseconds / 30.0).ToString("0.00") + "ms/次");
            return sb.ToString();
        }

        /// <summary>离屏把右键菜单（含两个子菜单）画成 PNG（自检用，不留在屏幕）</summary>
        /// <summary>离屏把「时钟管理」对话框画一张（自检用，不留在屏幕上）</summary>
        public void ClockManagerSnapshotSave(string path, int offMask)
        {
            ClockManagerDialog d = new ClockManagerDialog(
                (offMask & 1) == 0 && S.ShowLocal, (offMask & 2) == 0 && S.TimerOn, (offMask & 4) == 0 && S.GzOn,
                S.MainCity, S.MainPos, S.TimerPos, S.GzPos, S.CalMode, S.CalPos, S.Cities, S.MainSkin);
            try
            {
                d.StartPosition = FormStartPosition.Manual;
                d.Location = new Point(-30000, -30000);
                d.Show();
                Application.DoEvents();
                d.PerformLayout();
                using (Bitmap bmp = new Bitmap(d.Width, d.Height))
                {
                    d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
            }
            catch (Exception) { }
            finally
            {
                try { d.Hide(); d.Dispose(); } catch (Exception) { }
            }
        }

        public void MenuSnapshotSave(string prefix)
        {
            ContextMenuStrip m = BuildMenu();
            m.Show(this, new Point(0, 0));
            Application.DoEvents();
            SnapStrip(m, prefix + "_menu.png");
            SnapDrop(m.Items[0] as ToolStripMenuItem, prefix + "_menu_style.png");
            ToolStripMenuItem calM = FindMenu(m, "日历设置", "Calendar");
            SnapDrop(calM, prefix + "_menu_cal.png");
            // 1.9：日历设置 → 模块位置 那一层单独出图（四档锚点：全表最上/主时钟上方/主时钟下方/全表最下）
            SnapDrop(FindSub(calM, "模块位置", "Position"), prefix + "_menu_calpos.png");
            m.Close();
        }

        static ToolStripMenuItem FindSub(ToolStripMenuItem parent, string zh, string en)
        {
            if (parent == null) return null;
            foreach (ToolStripItem it in parent.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                if (mi.Text == zh || mi.Text == en) return mi;
            }
            return null;
        }

        static void SnapStrip(Control c, string path)
        {
            Size sz = c.Size;
            if (sz.Width <= 0 || sz.Height <= 0) return;
            Bitmap bmp = new Bitmap(sz.Width, sz.Height);
            c.DrawToBitmap(bmp, new Rectangle(0, 0, sz.Width, sz.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
        }

        static ToolStripMenuItem FindMenu(ContextMenuStrip m, string zh, string en)
        {
            foreach (ToolStripItem it in m.Items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                if (mi.Text == zh || mi.Text == en) return mi;
            }
            return null;
        }

        void SnapDrop(ToolStripMenuItem it, string path)
        {
            if (it == null) return;
            ToolStripDropDown dd = it.DropDown;
            it.ShowDropDown();               // 由父项展开：owner 正确，子项才会真正布局
            for (int i = 0; i < 6; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(40);
            }
            SnapStrip(dd, path);
            dd.Close();
        }

        // ==================================================== 设置变更

        void SetCalMode(int mode)
        {
            if (mode < 0 || mode > 2) mode = 0;
            int old = S.CalMode;
            S.CalMode = mode;
            S.ShowCal = (mode == 1);
            if (mode != old) CloseCalWindow();
            if (mode == 1)
            {
                if (S.CalAnchor < 0) S.CalAnchor = 0;
                if (S.CalAnchor > 3) S.CalAnchor = 3;
                DateTime mt = MainToday();
                if (!calNav) { calY = mt.Year; calM = mt.Month; }
            }
            if (mode == 2)
            {
                DateTime mt2 = MainToday();
                if (!calNav) { calY = mt2.Year; calM = mt2.Month; }
            }
            SaveSettings();
            Relayout(true);
        }

        /// <summary>1.9：设置日历常挂位置的锚点（0 全表最上 / 1 主时钟上方 / 2 主时钟下方 / 3 全表最下）</summary>
        void SetCalAnchor(int a)
        {
            if (a < 0) a = 0;
            if (a > 3) a = 3;
            S.CalAnchor = a;
            S.ShowCal = (S.CalMode == 1);
            Relayout(true);
            SaveSettings();
        }




        // ---------- 干支计时模块
        GzPopup gzPopup;

        void OpenGzPopup(Point screenPt)
        {
            if (gzPopup != null) { try { gzPopup.Close(); } catch (Exception) { } gzPopup = null; }
            GzPopup f = new GzPopup(P, S.Lang == "en", DateTime.Now);
            int x = screenPt.X - 40;
            int y = screenPt.Y + 18;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (x + f.Width > wa.Right) x = wa.Right - f.Width - 8;
            if (y + f.Height > wa.Bottom) y = screenPt.Y - f.Height - 18;
            if (x < wa.Left + 8) x = wa.Left + 8;
            f.Location = new Point(x, y);
            gzPopup = f;
            f.FormClosed += new FormClosedEventHandler(delegate { gzPopup = null; });
            f.Show(this);
        }



        /// <summary>自检出图：把「时间已到」弹窗离屏画一张</summary>
        internal void TimerPopupSnapshotSave(string path, bool white)
        {
            TimerPopup p = new TimerPopup(!white, S.Lang == "en", null, 1.0);
            try { p.SnapshotSave(path); }
            finally { p.Dispose(); }
        }

        /// <summary>自检：键盘逐位输入、按钮命中、起跑/暂停/停止/到点全部跑一遍，返回文字结果</summary>
        internal string SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== v1.4 timer selftest ==");
            tmr = new TimerMod();
            sb.AppendLine("init: cdState=" + tmr.CdState + " field=" + tmr.Field + " total=" + tmr.CdTotalSec());

            // 1) 键盘逐位输入 1:23:45
            foreach (Keys k in new Keys[] { Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5 })
                TimerKey(k);
            sb.AppendLine("type 1 2 3 4 5 -> " + tmr.InH + ":" + tmr.InM + ":" + tmr.InS +
                          " field=" + tmr.Field + " total=" + tmr.CdTotalSec());
            sb.AppendLine("expect 1:23:45 field=2 total=5025 " +
                          ((tmr.InH == 1 && tmr.InM == 23 && tmr.InS == 45 && tmr.CdTotalSec() == 5025) ? "PASS" : "FAIL"));

            // 2) Enter 起跑
            TimerKey(Keys.Enter);
            sb.AppendLine("enter -> cdState=" + tmr.CdState + " left=" + tmr.CdLeftMs() + " " +
                          ((tmr.CdState == 1 && tmr.CdLeftMs() > 4520 * 1000 - 2000) ? "PASS" : "FAIL"));

            // 3) 暂停态按钮无效（倒计时没有暂停）
            TimerPause();
            sb.AppendLine("pause on countdown (应无变化) cdState=" + tmr.CdState);

            // 4) ■ 停止 → 回输入态
            TimerStop();
            sb.AppendLine("stop -> cdState=" + tmr.CdState + " keep=" + tmr.InH + ":" + tmr.InM + ":" + tmr.InS +
                          ((tmr.CdState == 0 && tmr.InH == 1 && tmr.InM == 23) ? " PASS" : " FAIL"));

            // 5) 到点判定
            tmr.CdState = 1;
            tmr.CdEnd = DateTime.Now.AddMilliseconds(-10);
            sb.AppendLine("finish -> left=" + tmr.CdLeftMs() + " " + (tmr.CdLeftMs() <= 0 ? "PASS" : "FAIL"));

            // 6) 秒表跑起来 300ms
            tmr = new TimerMod();
            tmr.Active = 1;
            TimerStart();
            System.Threading.Thread.Sleep(300);
            long ms = tmr.SwMs();
            TimerPause();
            long p1 = tmr.SwMs();
            System.Threading.Thread.Sleep(200);
            long p2 = tmr.SwMs();
            sb.AppendLine("stopwatch run 300ms -> " + ms + "ms state=" + tmr.SwState + " frozen=" + (p2 == p1) +
                          ((ms >= 250 && ms < 500 && p2 == p1) ? " PASS" : " FAIL"));
            TimerStop();
            sb.AppendLine("stopwatch stop -> state=" + tmr.SwState + " acc=" + tmr.SwMs() + " " +
                          ((tmr.SwState == 0 && tmr.SwMs() == 0) ? "PASS" : "FAIL"));
            sb.AppendLine("key in stopwatch mode ignored -> " +
                          (TimerKey(Keys.D5) ? "FAIL" : "PASS"));

            // 7) 命中测试（几何坐标 → 控件编号）
            tmr = new TimerMod();
            bool en = S.Lang == "en";
            TimerGeom d = TimerGeom.Make(M, en, tmr);
            int[] hit = new int[6];
            hit[0] = TimerView.Hit(M, en, tmr, d.BoxL[0] + d.BoxW[0] / 2f, TimerGeom.BoxTop + TimerGeom.BoxH / 2f);
            hit[1] = TimerView.Hit(M, en, tmr, d.BoxL[1] + d.BoxW[1] / 2f, TimerGeom.BoxTop + TimerGeom.BoxH / 2f);
            hit[2] = TimerView.Hit(M, en, tmr, d.BoxL[2] + d.BoxW[2] / 2f, TimerGeom.BoxTop + TimerGeom.BoxH / 2f);
            hit[3] = TimerView.Hit(M, en, tmr, d.SpkX + TimerGeom.SPK / 2f, 20f);
            hit[4] = TimerView.Hit(M, en, tmr, d.Btn1 + TimerGeom.BtnS / 2f, 20f);
            hit[5] = TimerView.Hit(M, en, tmr, d.Btn3 + TimerGeom.BtnS / 2f, 20f);
            sb.AppendLine("hit boxes=" + hit[0] + "," + hit[1] + "," + hit[2] + " spk=" + hit[3] +
                          " btnStop=" + hit[4] + " btnGo=" + hit[5] + " (期望 7,8,9,3,4,6)" +
                          (((hit[0] == 7 && hit[1] == 8 && hit[2] == 9 && hit[3] == 3) &&
                            (hit[4] == 4 && hit[5] == 6)) ? " PASS" : " FAIL"));
            tmr.Active = 1;
            TimerGeom d2 = TimerGeom.Make(M, en, tmr);
            int hStop = TimerView.Hit(M, en, tmr, d2.Btn1 + TimerGeom.BtnS / 2f, 20f);
            int hPause = TimerView.Hit(M, en, tmr, d2.Btn2 + TimerGeom.BtnS / 2f, 20f);
            int hGo = TimerView.Hit(M, en, tmr, d2.Btn3 + TimerGeom.BtnS / 2f, 20f);
            sb.AppendLine("hit stopwatch buttons=" + hStop + "," + hPause + "," + hGo + " (期望 4,5,6) " +
                          ((hStop == 4 && hPause == 5 && hGo == 6) ? "PASS" : "FAIL"));
            tmr.Active = 0;
            int labL = TimerView.Hit(M, en, tmr, TimerGeom.X0 + 4f, 20f);
            int labR = TimerView.Hit(M, en, tmr, TimerGeom.X1 - 4f, 20f);
            sb.AppendLine("hit labels left=" + labL + " right=" + labR + " (期望 1,2) " +
                          ((labL == 1 && labR == 2) ? "PASS" : "FAIL"));
            sb.AppendLine("hit empty area -> " + TimerView.Hit(M, en, tmr, 250f, 2f));
            return sb.ToString();
        }

        /// <summary>自检出图用：把计时器摆到指定状态（sw:毫秒 / swfull / swp / cd:时:分:秒 / cdin / cdalarm / cdfin）</summary>
        internal string GzMetrics()
        {
            DateTime now = DateTime.Now;
            return "gz ink: time=" + Ui.Ink(M.FGzTime, GzCore.ShiChen(now)).Height
                   + " marq=" + Ui.Ink(M.FGzMarq, "丙午").Height
                   + " small=" + Ui.Ink(M.FGzUnit, "年").Height
                   + "  fixedRight=" + GzView.FixedRight(M, now).ToString("0.0")
                   + "  marqW=" + GzView.MarqueeWidth(now, M).ToString("0.0");
        }

        static DateTime GzSheetTime(int h, int mi) { return new DateTime(2026, 11, 11, h, mi, 0); }

        /// <summary>
        /// 干支行的「刻制 + 更次」时刻表出图：同一行在 11 个时间点各画一遍叠起来。
        /// 不依赖系统当前时间，用来核对 0:00/0:15/0:30/0:45 与五更标注是否正确。
        /// </summary>
        internal void GzRowSheet(string path)
        {
            string[] lbs = { "0:00", "0:15", "0:30", "0:45", "16:23",
                             "19:30", "22:30", "23:30", "02:30", "04:30", "09:30" };
            DateTime[] ts = { GzSheetTime(0, 0), GzSheetTime(0, 15), GzSheetTime(0, 30), GzSheetTime(0, 45),
                              GzSheetTime(16, 23), GzSheetTime(19, 30), GzSheetTime(22, 30),
                              GzSheetTime(23, 30), GzSheetTime(2, 30), GzSheetTime(4, 30), GzSheetTime(9, 30) };
            const int W = 520, pitch = 52;
            using (Bitmap bmp = new Bitmap(W * 2, (14 + lbs.Length * pitch) * 2))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.FromArgb(255, 28, 28, 28));
                g.ScaleTransform(2f, 2f);
                using (Font lf = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush lb = new SolidBrush(P.Dim))
                {
                    for (int i = 0; i < lbs.Length; i++)
                    {
                        float y = 10f + i * pitch;
                        g.DrawString(lbs[i], lf, lb, 8f, y);
                        GraphicsState st = g.Save();
                        g.TranslateTransform(0f, y + 14f);
                        GzView.DrawStatic(g, P, M, false, ts[i]);
                        g.Restore(st);
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        /// <summary>
        /// 自检：双击分派（弹出模式只认日期区）+ 日历格命中 + 当日详情用全称。
        /// 全程只做判定，不弹任何窗口、不写注册表。
        /// </summary>
        internal string DblClickSelfTest()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int oldMode = S.CalMode;
            bool oldLocal = S.ShowLocal, oldGz = S.GzOn, oldTimer = S.TimerOn;
            try
            {
                S.ShowLocal = true; S.GzOn = true; S.TimerOn = true;

                // ── 一、弹出模式（CalMode=2）：只有日期区域开关日历，其余双击保持原功能
                S.CalMode = 2; BuildRows();
                int li = -1, gi = -1, wi = -1, ti = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];
                    if (r.Local) li = i;
                    else if (r.Gz) gi = i;
                    else if (r.Timer) ti = i;
                    else if (!r.Cal && wi < 0) wi = i;
                }
                sb.AppendLine("rows(2) " + RowOrder());
                if (li >= 0 && gi >= 0 && wi >= 0)
                {
                    Row lo = rows[li], gz = rows[gi], wo = rows[wi];
                    sb.AppendLine(Chk("干支模块双击 → 换算框", DblAction(30f, gz.Y + 20f), ACT_GZPOP));
                    sb.AppendLine(Chk("主时钟日期区双击 → 开/收日历", DblAction(M.MdX + 20f, lo.Y + 90f), ACT_TOGCAL));
                    sb.AppendLine(Chk("主时钟时分数字双击 → 日期时间设置", DblAction(M.TimeLeft + 30f, lo.Y + 80f), ACT_SETTINGS));
                    sb.AppendLine(Chk("主时钟小时区外的正文双击 → 不动作", DblAction(M.TimeLeft - 30f, lo.Y + 80f), ACT_NONE));
                    sb.AppendLine(Chk("世界行日期区双击 → 开/收日历", DblAction(470f, wo.Y + 30f), ACT_TOGCAL));
                    sb.AppendLine(Chk("世界行城市名双击 → 该城市小日历", DblAction(60f, wo.Y + 30f), ACT_MONTH));
                    if (ti >= 0)
                        sb.AppendLine(Chk("计时器行双击 → 不动作", DblAction(200f, rows[ti].Y + 12f), ACT_NONE));
                }
                else sb.AppendLine("行定位失败 li=" + li + " gi=" + gi + " wi=" + wi);

                // ── 二、常挂模式（CalMode=1）：日期区不再吞双击
                S.CalMode = 1; BuildRows();
                gi = -1; li = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Local) li = i;
                    else if (rows[i].Gz) gi = i;
                }
                if (gi >= 0)
                    sb.AppendLine(Chk("常挂模式 干支双击仍是换算框", DblAction(30f, rows[gi].Y + 20f), ACT_GZPOP));
                if (li >= 0)
                {
                    int a = DblAction(M.MdX + 20f, rows[li].Y + 90f);
                    sb.AppendLine("常挂模式 主时钟日期区双击 -> " + a + "（应为 0 或 3，不该是 1）"
                                  + ((a != ACT_TOGCAL) ? " PASS" : " FAIL"));
                }

                // ── 三、日历格命中（弹出窗与常挂共用 CalDayAt）
                calY = 2026; calM = 11; calNav = true;
                CalMonthModel m = CalModel();
                float th, sh, wh, gridTop = CalHeaderBottom(out th, out sh, out wh);
                float cellH = CalCellH(gridTop, m.Rows);
                DateTime today = MainToday();
                DateTime probe = new DateTime(2026, 11, 18);
                int hit = -1;
                for (int i = 0; i < m.Cells.Length; i++)
                    if (m.Cells[i] != null && m.Cells[i].Date == probe && !m.Cells[i].IsAdjacent) { hit = i; break; }
                if (hit >= 0)
                {
                    int col = hit % 7, row = hit / 7;
                    float cx = Metrics.CalX0 + col * Metrics.CalPitch + Metrics.CalColW / 2f;
                    float cy = gridTop + cellH * (row + 0.5f);
                    CalDay got = CalDayAt(cx, cy);
                    string okd = (got != null && got.Date == probe) ? "PASS" : "FAIL";
                    sb.AppendLine("日历格命中 2026-11-18 (" + row + "," + col + ") -> "
                                  + ((got != null) ? got.Date.ToString("yyyy-MM-dd") : "null") + " " + okd);
                    sb.AppendLine("抬头区（标题/翻页/收回） -> " + (CalDayAt(cx, gridTop - 4f) == null ? "null PASS" : "FAIL"));
                }
                else sb.AppendLine("2026-11-18 没在模型里 FAIL");

                // ── 四、当日详情用全称（且仍不进农历格）
                string t1; List<string> l1 = DayInfoOf(new DateTime(2026, 11, 9), out t1);
                string t2; List<string> l2 = DayInfoOf(new DateTime(2026, 12, 2), out t2);
                string t3; List<string> l3 = DayInfoOf(new DateTime(2026, 12, 1), out t3);
                sb.AppendLine("11-09 详情 " + Join(l1) + " -> "
                              + ((Has(l1, "消防安全宣传日") && !Has(l1, "消防宣传")) ? "PASS" : "FAIL"));
                sb.AppendLine("12-02 详情 " + Join(l2) + " -> "
                              + ((Has(l2, "全国交通安全日")) ? "PASS" : "FAIL"));
                sb.AppendLine("12-01 详情 " + Join(l3) + " -> "
                              + ((Has(l3, "世界艾滋病日")) ? "PASS" : "FAIL"));
                string grid11 = GridFestsOf(new DateTime(2026, 11, 9));
                sb.AppendLine("11-09 格内节日 = [" + grid11 + "] -> "
                              + ((!grid11.Contains("消防")) ? "PASS" : "FAIL"));
            }
            catch (Exception ex) { sb.AppendLine("ERR " + ex); }
            finally
            {
                S.CalMode = oldMode; S.ShowLocal = oldLocal; S.GzOn = oldGz; S.TimerOn = oldTimer;
                BuildRows();
            }
            return sb.ToString();
        }

        static string Chk(string what, int got, int want)
        {
            return what + " -> " + got + "（应 " + want + "） " + ((got == want) ? "PASS" : "FAIL");
        }
        static string Join(List<string> l)
        {
            System.Text.StringBuilder s = new System.Text.StringBuilder();
            foreach (string one in l) { if (s.Length > 0) s.Append(" / "); s.Append(one); }
            return s.ToString();
        }
        static bool Has(List<string> l, string want)
        {
            foreach (string one in l) if (one.StartsWith("· ") ? one.Substring(2).Trim() == want : one.Trim() == want) return true;
            return false;
        }
        List<string> DayInfoOf(DateTime day, out string title)
        {
            calY = day.Year; calM = day.Month; calNav = true;
            CalMonthModel m = CalModel();
            foreach (CalDay c in m.Cells)
                if (c != null && c.Date == day && !c.IsAdjacent) return BuildDayInfo(c, out title);
            title = "";
            return new List<string>();
        }
        string GridFestsOf(DateTime day)
        {
            calY = day.Year; calM = day.Month; calNav = true;
            CalMonthModel m = CalModel();
            foreach (CalDay c in m.Cells)
                if (c != null && c.Date == day && !c.IsAdjacent)
                {
                    System.Text.StringBuilder s = new System.Text.StringBuilder();
                    foreach (string f in c.Fest) { if (s.Length > 0) s.Append(" / "); s.Append(f); }
                    return s.ToString();
                }
            return "";
        }

        internal string RowOrder()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Row r in rows)
            {
                if (r.Local) sb.Append("L ");
                else if (r.Cal) sb.Append("C ");
                else if (r.Timer) sb.Append("T ");
                else if (r.Gz) sb.Append("G ");
                else sb.Append("W ");
            }
            return "rows=" + sb.ToString().Trim() + "  MainPos=" + S.MainPos + " CalPos=" + S.CalPos
                   + " TimerPos=" + S.TimerPos + " GzPos=" + S.GzPos + " GzOn=" + S.GzOn + " TimerOn=" + S.TimerOn
                   + " cities=" + S.Cities.Count;
        }
        internal void SetPreviewTimer(string spec)
        {
            string s = spec.Trim().ToLowerInvariant();
            if (s.StartsWith("sw"))
            {
                tmr.Active = 1;
                int ms = 0;
                if (s.StartsWith("swfull")) ms = 59 * 60000 + 59999;
                else if (s.StartsWith("sw:")) int.TryParse(s.Substring(3), out ms);
                tmr.SwAcc = ms;
                // swfull 直接置「到上限」态（3），否则模拟出来的只是"还差 1ms"、显示仍是白色
                tmr.SwState = s.StartsWith("swfull") ? 3 : (s.StartsWith("swp") ? 2 : 1);
                tmr.SwStart = DateTime.Now;
            }
            else if (s.StartsWith("cd"))
            {
                tmr.Active = 0;
                if (s == "cdfin") { tmr.CdState = 2; tmr.InH = 0; tmr.InM = 0; tmr.InS = 0; }
                else if (s == "cdalarm") { tmr.CdState = 0; tmr.Alarm = true; }
                else if (s.StartsWith("cd:"))
                {
                    string[] p = s.Substring(3).Split(':');
                    int hh = 0, mm = 0, ss = 0;
                    if (p.Length >= 3)
                    {
                        int.TryParse(p[0], out hh);
                        int.TryParse(p[1], out mm);
                        int.TryParse(p[2], out ss);
                    }
                    tmr.InH = hh; tmr.InM = mm; tmr.InS = ss;
                    tmr.CdEnd = DateTime.Now.AddSeconds(hh * 3600 + mm * 60 + ss);
                    tmr.CdState = 1;
                }
                else { tmr.CdState = 0; }
            }
        }

        void SetCalFirstDow(int d)
        {
            S.CalFirstDow = d;
            RefreshCal();
            SaveSettings();
        }

        void SetCalLunar(bool on)
        {
            S.CalLunar = on;
            RefreshCal();
            SaveSettings();
        }

        void SetCalCC(string cc)
        {
            S.CalCC = cc;
            RefreshCal();
            SaveSettings();
        }

        void SetStyle(int style)
        {
            if (style < 0 || style > 2) style = 0;
            S.Style = style;
            S.Dark = (style != 1);
            Settings.CurrentStyle = style;
            P = Pal.Make(S.Style, S.Opacity);
            FrostInvalidate();
            staticLayer = null;
            RenderStatic();
            PaintFrame();
            SyncCalWindow();                       // 弹出日历换色
            SaveSettings();
        }

        void SetOpacity(int pct)
        {
            S.Opacity = pct;
            P = Pal.Make(S.Style, S.Opacity);
            FrostInvalidate();                     // 1.7.2：磨砂成品里含 t=TintOf*op，透明度变了必须重抓
            staticLayer = null;
            RenderStatic();
            PaintFrame();
            SyncCalWindow();                       // 弹出日历跟随透明度
            SaveSettings();
        }

        void SetScale(double sc)
        {
            S.Scale = sc;
            staticLayer = null;
            int w = WinW();
            int h = WinH();
            Rectangle pb = Screen.FromRectangle(new Rectangle(Left, Top, Math.Max(Width, 1), Math.Max(Height, 1))).Bounds;
            int x = Math.Max(pb.Left, Math.Min(Left, pb.Right - w));
            int y = Math.Max(pb.Top, Math.Min(Top, pb.Bottom - h));
            SetBounds(x, y, w, h);
            FrostInvalidate();                     // 1.7：窗口尺寸变了，磨砂必须按新矩形重裁（1.6 漏了，会拿旧图拉伸）
            RenderStatic();
            PaintFrame();
            SyncCalWindow();                       // 弹出日历跟着缩放
            SaveSettings();
        }

        void SetTopmost(bool on)
        {
            S.Topmost = on;
            TopMost = on;
            // 1.6：「总在最前」与「背景磨砂」互斥 —— 置顶时磨砂自动失效并置灰，
            // 取消置顶后按用户原先的设置恢复（不清掉勾选状态，只是不生效）
            FrostInvalidate();
            staticLayer = null;
            RenderStatic();
            PaintFrame();
            SaveSettings();
            PushLayered();
        }

        /// <summary>1.6：切换「背景磨砂」</summary>
        void SetBlur(bool on)
        {
            SetBlurLevel(on ? 3 : 0);        // 1.8.5：开关式入口统一走档位（自检/兼容路径默认第 3 档）
        }

        /// <summary>
        /// 1.8.5：磨砂强度档位（0 关闭 / 1~5，数字越大越糊，3 = 标准档）。
        /// 关↔开都显式作废整幅壁纸缓存与挂件裁剪缓存（1.8.2 的教训），并同步弹出日历。
        /// </summary>
        public void SetBlurLevel(int lv)
        {
            if (lv < 0) lv = 0;
            if (lv > 5) lv = 5;
            if (S.Topmost && lv > 0) return;                 // 与旧菜单置灰一致：置顶时开不了磨砂
            if (lv == 0) S.Blur = false;
            else { S.Blur = true; S.BlurLevel = lv; }
            WallpaperFrost.InvalidateCache();
            wallSigLast = "";                                // 重登指纹基线，避免看门狗二次重抓
            FrostInvalidate();                               // 内含弹出日历磨砂缓存作废（1.8.3）
            staticLayer = null;
            RenderStatic();
            PaintFrame();
            SyncCalWindow();                                 // 弹出日历跟着换档
            SaveSettings();
        }

        void SetLang(string lang)
        {
            S.Lang = lang;
            // BUG 修正：只改 S.Lang 不够 —— 四个弹窗（时钟管理 / 城市 / 时区 / 手动时区）
            // 的文案全部走 Settings.CurrentLang，而 CurrentLang 只在 Settings.Load 里赋过值，
            // 所以「切到英文版」之后弹窗仍然是中文。这里同步过去。
            Settings.CurrentLang = lang;
            foreach (Row r in rows) r.MDirty = true;      // 字幕语言变了，缓存文字要重算
            off.Clear();
            offPeriod.Clear();
            SaveSettings();
            RenderStatic();
            PaintFrame();
            SyncCalWindow();                              // 弹出日历的月份/星期文案也要换语言
        }

        // ==================================================== 时钟管理（增删 / 改地点 / 排序 / 主时钟开关）
        void ManageClocks()
        {
            // 1.5：主时钟 / 干支计时 / 倒计时·秒表 的开关与排序统一在这里管理
            ClockManagerDialog d = new ClockManagerDialog(S.ShowLocal, S.TimerOn, S.GzOn, S.MainCity,
                S.MainPos, S.TimerPos, S.GzPos, S.CalMode, S.CalPos, S.Cities, S.MainSkin);
            if (d.ShowDialog(this) != DialogResult.OK) return;
            S.ShowLocal = d.ShowMain;
            S.TimerOn = d.TimerOn;
            S.GzOn = d.GzOn;
            S.MainSkin = d.MainSkin;          // 2.0：主时钟中/英（中文皮肤）切换
            S.MainCity = d.MainCity;
            S.MainPos = d.MainPos;
            S.TimerPos = d.TimerPos;
            S.GzPos = d.GzPos;
            // 1.9：日历最终位置由四档锚点决定。时钟管理里拖行只折算成「全表最上/最下」或
            // 「在主时钟上方/下方」—— 否则序号式位置会与锚点互相打架。
            {
                int nCity = d.Cities != null ? d.Cities.Count : 0;
                int botIdx = nCity + (d.ShowMain ? 1 : 0);
                if (d.CalPos <= 0) S.CalAnchor = 0;
                else if (d.CalPos >= botIdx) S.CalAnchor = 3;
                else if (d.ShowMain) S.CalAnchor = (d.CalPos <= d.MainPos) ? 1 : 2;
                else S.CalAnchor = 3;
            }
            S.CalPos = d.CalPos;
            S.Cities = d.Cities;
            off.Clear();
            Relayout(true);
            KeepAboveDesktop();
        }

        void ClearAllAndExit()
        {
            if (MessageBox.Show(T("将关闭窗口并清除全部设置（含开机自启），确定吗？", "Close and clear all settings?"),
                "About Time", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            suppressSave = true;
            Settings.ClearAll();
            Close();
        }

        /// <summary>关于框：版式与前作 World bedside clock 对齐（标题「关于」+ 信息图标 + 版本号 + 数据来源）</summary>
        void ShowAbout()
        {
            string txt =
                "About Time " + Settings.AppVersion + "\r\n" +
                T("版本号：", "Version: ") + Settings.AppVersion + "\r\n\r\n" +
                T("城市时区数据来源：", "City / time zone data sources:") + "\r\n" +
                T("· GeoNames 全球地理数据库（geonames.org），\r\n  数据依据 CC-BY 4.0 许可使用；\r\n" +
                  "· 中国行政区划数据源自民政部\r\n  《中华人民共和国行政区划代码》。",
                  "· GeoNames global geographic database (geonames.org),\r\n  used under the CC-BY 4.0 license;\r\n" +
                  "· China administrative division data from the Ministry of Civil Affairs\r\n  \"Administrative Division Codes of the PRC\".");
            MessageBox.Show(this, txt, T("关于", "About"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>使用说明：1.6 起按用户给定文案整体替换（版权声明 / 清除方式 / 数据来源）</summary>
        void ShowHelp()
        {
            string zh =
                "★About Time 是༺格桑仁青༻ & Old Newbie 借助豆包及CodeBuddy编写的免费绿色开源Windows桌面挂件(widgets)." +
                "此程序仅限非商业用途的学习、测试。作者对程序使用过程中出现的任何问题不予负责且不承诺后续版本的升级。\r\n\r\n" +
                "★★清除本程序无需卸载，但删除程序执行文件前请务必先在程序右键菜单内选择“关闭全部窗口并清除所有配置”，" +
                "以彻底清除全部系统残留。\r\n\r\n" +
                "本程序数据来源：\r\n" +
                "城市时区数据依据CC-BY4.0许可使用GeoNames全球地理数据库(geonames.org);\r\n" +
                "中国行政区划数据源自民政部《中华人民共和国行政区划代码》\r\n\r\n" +
                "2026年11月·四川省甘孜藏族自治州";
            string en =
                "★About Time is a free, portable and open-source Windows desktop widget (widgets) written by " +
                "༺格桑仁青༻ & Old Newbie with the help of Doubao and CodeBuddy. " +
                "This program is for non-commercial learning and testing only. The authors accept no responsibility " +
                "for any problem arising from its use, and make no promise of upgrades in later versions.\r\n\r\n" +
                "★★ No uninstaller is needed to remove this program, but before deleting the executable please first choose " +
                "\"Close All & Clear Settings\" from the program's right-click menu, so that every trace left in the system is wiped out.\r\n\r\n" +
                "Data sources:\r\n" +
                "City and time zone data are used under the CC-BY 4.0 license from the GeoNames global geographic database (geonames.org);\r\n" +
                "China administrative division data come from the Ministry of Civil Affairs' \"Administrative Division Codes of the People's Republic of China\"\r\n\r\n" +
                "November 2026 · Garzê Tibetan Autonomous Prefecture, Sichuan Province";
            MessageBox.Show(this, S.Lang == "en" ? en : zh, T("使用说明", "Help"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch (Exception) { }
            try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch (Exception) { }
            if (hiResTimer) { try { timeEndPeriod(1); } catch (Exception) { } hiResTimer = false; }
            if (tick != null) tick.Stop();
            if (wdog != null) wdog.Stop();
            try { if (menu != null) { menu.Dispose(); menu = null; } } catch (Exception) { }
            if (screenDC != IntPtr.Zero)
            {
                try { ReleaseDC(IntPtr.Zero, screenDC); } catch (Exception) { }
                screenDC = IntPtr.Zero;
            }
            SaveSettings();
            try { AlarmSound.Release(); } catch (Exception) { }
            ReleaseSurface();
            base.OnFormClosing(e);
        }
    }
}
