using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace AboutTime
{
    // ============================================================ 壁纸磨砂（背景虚化）1.7
    // 只抓「壁纸层」：桌面壁纸由 Progman / WorkerW 渲染，PrintWindow 抓它不会把
    // 本程序自己的分层窗口、也不会把别的应用窗口抓进来 —— 这正是「仅针对壁纸层」的要求。
    //
    // 1.7 的三条主线（治 1.6 的「内存 90M+」「看不出模糊」「大幅变白」）：
    //   ① 内存：整幅壁纸只在宿主变化（换壁纸/改分辨率/WorkerW 重建）时 PrintWindow 一次，
    //      立即降采样成 1/4 缩略图缓存（双屏约 910x532 ≈ 1.9MB）后马上释放 31MB 整幅位图。
    //      之后每次重抓只是从缩略图上裁一小块 + 盒式模糊，分配 <4MB、耗时 <10ms。
    //   ② 合成配方：删掉 1.6 的「平均亮度平移到定标值」（LevelToMean，钳 ±60 ——
    //      亮壁纸压不回碳灰的 20，实测卡片被抬到 ~100，这就是「大幅变白」），
    //      改成向主题基色收缩：out = blur*(1-a) + base*a。任何壁纸下玻璃亮度都收敛到
    //      主题色附近，纹理以 (1-a) 振幅保留 —— 即 Windows acrylic 的做法。
    //   ③ 透明度语义：「背景透明度」在磨砂模式下是玻璃浓度 aEff = a + (1-op)*(1-a)，
    //      不再把「清晰的桌面原图」按 (1-op) 掺回来 —— 1.6 用 ColorMatrix 把磨砂层
    //      alpha=op 推上分层窗，op 越低清晰成分越多，模糊天然被稀释（这是「看不出模糊」
    //      最大的一个来源）。op 只影响玻璃浓度，任何档位都不掺清晰像素。
    //
    // 另外模糊前把采样窗口外扩 2r 再裁回，消除盒式模糊边界 clamp 造成的边缘拖影。
    static class WallpaperFrost
    {
        delegate bool EnumProc(IntPtr h, IntPtr lp);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string win);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int idx);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, int flags);

        struct RECT { public int L, T, R, B; }

        // —— 1.8.4：整幅抓图的原生 GDI 面（DIB），31MB 不再进托管大对象堆 ——
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, int usage, out IntPtr bits, IntPtr hSection, int offset);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;                // 注意是 short —— 写成 int 会让整个头错位，CreateDIBSection 直接失败
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }
        const int DIB_RGB_COLORS = 0, SRCCOPY = 0x00CC0020, HALFTONE = 4;

        static string _lastLog = "";

        /// <summary>上一次抓取的结果说明（自检 / 排错用）</summary>
        public static string LastLog { get { return _lastLog; } }

        // —— 1.9：抓图失败的负缓存。PrintWindow 抓不到（DWM 合成失败 / 宿主正在重建）时
        // 老版本每次都往下走、重建 31MB DIB 再失败一次，壁纸轮换期间白烧 CPU 和内存。
        // 失败后同一个「宿主 + 尺寸 + 指纹」5 秒内直接返回 null，不再尝试。
        static string _failKey = "";
        static int _failTicks;
        static double _lastBlurMs;

        /// <summary>1.9：上一次模糊耗时（ms），自检/排错用</summary>
        public static double LastBlurMs { get { return _lastBlurMs; } }

        // —— 整幅壁纸 1/4 缩略缓存 ——
        static Bitmap _wallQ;
        static IntPtr _host = IntPtr.Zero;
        static int _ww, _hh;
        // 1.8.2：壁纸指纹（路径 + 修改时间 + 字节数）。1.8.1 及之前只按「宿主窗口句柄 + 尺寸」
        // 判缓存是否有效 —— 换壁纸时这两个值都不变，缓存永远命中，画面就永远停在第一张壁纸上。
        // 而 SystemEvents.UserPreferenceChanged(Desktop) 在分层窗口下并不总会送达，
        // 于是「换壁纸没反应、拖拽也没反应、关掉磨砂再开也还是老图」。
        static string _sig = "";
        static int _captures;                 // 成功整幅抓取的次数（自检用来证明「真的重抓了」）
        static readonly object _lock = new object();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SystemParametersInfo(int act, int param, StringBuilder buf, int winIni);

        /// <summary>成功整幅抓取的累计次数（自检 / 排错用）</summary>
        public static int CaptureCount { get { return _captures; } }

        /// <summary>
        /// 当前壁纸指纹。
        ///
        /// 2026-11-17 根因修正：原来只读 SPI_GETDESKWALLPAPER 返回的那**一个**文件。
        /// 多屏各自设壁纸（IDesktopWallpaper.SetWallpaper）时，系统是「每块屏各写一个转码文件」
        /// （Themes\Transcoded_000 / Transcoded_001 …），而 SPI 返回的 TranscodedWallpaper 未必跟着更新。
        /// 本机实测（双屏各自幻灯）：Transcoded_000 与屏1、Transcoded_001 与屏2 的当前壁纸相关度都是 1.000，
        /// 而 SPI 指向的 TranscodedWallpaper 与两块屏都对不上（与屏2 仅 0.566）。
        /// 后果：另一块屏换了壁纸 → 指纹不变 → 整幅缓存不失效 → 磨砂停在上一张，且拖到哪都一样。
        ///
        /// 现在把 Themes 目录下所有「Transcoded*」与 slideshow.ini 的名称+修改时间+大小一起拼进指纹，
        /// 任一块屏换图都能被感知；SPI 段保留作为老系统 / 非 per-monitor 环境的补充。
        /// 取不到时仍返回 "?"（调用方按「取不到」处理）。
        /// </summary>
        public static string WallpaperSignature()
        {
            try
            {
                StringBuilder sb = new StringBuilder(512);
                try
                {
                    string themes = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Microsoft\Windows\Themes");
                    System.IO.DirectoryInfo di = new System.IO.DirectoryInfo(themes);
                    if (di.Exists)
                    {
                        System.IO.FileInfo[] all = di.GetFiles();
                        for (int i = 0; i < all.Length; i++)
                        {
                            string n = all[i].Name;
                            if (n.StartsWith("Transcoded", StringComparison.Ordinal) || n == "slideshow.ini")
                                sb.Append(n).Append('=').Append(all[i].LastWriteTimeUtc.Ticks)
                                  .Append('@').Append(all[i].Length).Append(';');
                        }
                    }
                }
                catch (Exception) { }

                StringBuilder sbs = new StringBuilder(1024);
                if (SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, sbs.Capacity, sbs, 0))
                {
                    string p = sbs.ToString();
                    if (p.Length > 0)
                    {
                        sb.Append("spi=").Append(p);
                        System.IO.FileInfo fi = new System.IO.FileInfo(p);
                        if (fi.Exists) sb.Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('@').Append(fi.Length);
                        sb.Append(';');
                    }
                }
                if (sb.Length == 0) return "?";
                return sb.ToString();
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// 壁纸层缓存失效（换壁纸 / 显示拓扑变化 / WorkerW 重建 / 关闭再打开磨砂时调用）。
        /// 只释放那 ~2MB 的缩略图，下一次 Grab 才会重新 PrintWindow 整幅。
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_lock)
            {
                if (_wallQ != null) { try { _wallQ.Dispose(); } catch (Exception) { } _wallQ = null; }
                _host = IntPtr.Zero;
                _ww = 0; _hh = 0;
                _sig = "";                    // 1.8.2：指纹一并清掉，下次 Grab 一定重抓
            }
        }

        /// <summary>
        /// 找壁纸宿主窗口。1.9：改为「面积最大、且不小于虚拟桌面 1/4」的 WorkerW。
        /// 老逻辑取「枚举到的最后一个无 SHELLDLL_DefView 的 WorkerW」，但本机实测 21 个 WorkerW
        /// 里有 20 个是 236×64 / 0×0 的桩窗口（它们同样没有图标层），枚举顺序一变就会选中桩，
        /// 磨砂直接整块消失（Host 返回桩 → 抓出来是空白 → 判 blank capture）。
        /// </summary>
        static IntPtr Host()
        {
            IntPtr prog = FindWindow("Progman", null);
            IntPtr found = IntPtr.Zero;
            RECT vs = VirtualRect();
            long need = Math.Max(1, ((long)(vs.R - vs.L) * (vs.B - vs.T)) / 4);
            long bestArea = 0;
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() != "WorkerW") return true;
                if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return true;   // 有图标层 = 文件视图，不是纯壁纸层
                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                long w = r.R - r.L, hh = r.B - r.T;
                if (w <= 0 || hh <= 0) return true;
                long area = w * hh;
                if (area < need) return true;                    // 桩窗口（0×0 / 236×64）直接排除
                if (area > bestArea) { bestArea = area; found = h; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
            return prog;
        }

        /// <summary>虚拟桌面矩形（多屏并集的边界）</summary>
        static RECT VirtualRect()
        {
            RECT r;
            r.L = GetSystemMetrics(76);                      // SM_XVIRTUALSCREEN
            r.T = GetSystemMetrics(77);                      // SM_YVIRTUALSCREEN
            r.R = r.L + GetSystemMetrics(78);                // SM_CXVIRTUALSCREEN
            r.B = r.T + GetSystemMetrics(79);                // SM_CYVIRTUALSCREEN
            return r;
        }

        /// <summary>记一次抓取失败（写日志 + 负缓存），返回 null 便于直接 return</summary>
        static Bitmap Fail(string msg, string key)
        {
            _lastLog = msg;
            _failKey = key;
            _failTicks = Environment.TickCount;
            return null;
        }

        /// <summary>
        /// 确保缩略缓存有效。宿主窗口 / 尺寸没变就直接复用；变了才 PrintWindow 整幅，
        /// 立即降采样成 1/4 后释放整幅位图（峰值分配只发生在这一刻，且用完即弃）。
        /// 失败返回 null（_lastLog 已写明原因）。
        /// </summary>
        static Bitmap EnsureQuarter(IntPtr host, RECT wr)
        {
            lock (_lock)
            {
                int ww = wr.R - wr.L, wh = wr.B - wr.T;
                // 1.8.2：缓存判据里加壁纸指纹 —— 宿主窗口句柄与尺寸在换壁纸时都不变，
                // 只有指纹能识别「壁纸换了」，否则永远命中第一张。
                string sig = WallpaperSignature();
                if (_wallQ != null && _host == host && _ww == ww && _hh == wh && _sig == sig) return _wallQ;
                // 1.9：负缓存 —— 5 秒内同一个「宿主+尺寸+指纹」刚失败过，就别再重建 31MB DIB 重试
                string fkey = host.ToString() + "|" + ww + "x" + wh + "|" + sig;
                int since = Environment.TickCount - _failTicks;
                if (_failKey.Length > 0 && _failKey == fkey && since >= 0 && since < 5000)
                { _lastLog = "grab backoff: " + (since / 1000) + "s ago failed (" + _failKey.Length + "B key)"; return null; }
                if (_wallQ != null) { try { _wallQ.Dispose(); } catch (Exception) { } _wallQ = null; }

                // 1.8.4：整幅抓图改走原生 DIB + StretchBlt 降采样。
                // 旧做法 new Bitmap(ww,wh) 把 ~31MB 分配在托管大对象堆（LOH）上 —— 壁纸轮播
                // 每换一张就来一次，长时间运行 LOH 碎片把私有内存垫高十几 MB（1.8.1 实测的现象）。
                // DIB 是原生内存，DeleteObject 后立即还给系统，托管堆全程零压力；
                // 降采样用 HALFTONE，缩到 1/4 的中间调质量足够（后面还要盒式模糊，无细节损失）。
                bool ok = false;
                Bitmap q = null;
                IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero, dib = IntPtr.Zero, oldObj = IntPtr.Zero, bits = IntPtr.Zero;
                int qw = Math.Max(4, (ww + 3) / 4), qh = Math.Max(4, (wh + 3) / 4);
                try
                {
                    screenDC = GetDC(IntPtr.Zero);
                    memDC = CreateCompatibleDC(screenDC);
                    BITMAPINFO bmi = new BITMAPINFO();
                    bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                    bmi.bmiHeader.biWidth = ww;
                    bmi.bmiHeader.biHeight = -wh;                     // 自上而下
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0;                  // BI_RGB
                    dib = CreateDIBSection(memDC, ref bmi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero || bits == IntPtr.Zero) return Fail("CreateDIBSection failed", fkey);
                    oldObj = SelectObject(memDC, dib);
                    ok = PrintWindow(host, memDC, 2);                 // PW_RENDERFULLCONTENT
                    if (!ok) ok = PrintWindow(host, memDC, 0);
                    if (!ok) return Fail("PrintWindow failed ok=false", fkey);

                    q = new Bitmap(qw, qh, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(q))
                    {
                        IntPtr qdc = g.GetHdc();
                        try
                        {
                            SetStretchBltMode(qdc, HALFTONE);
                            SetBrushOrgEx(qdc, 0, 0, IntPtr.Zero);
                            StretchBlt(qdc, 0, 0, qw, qh, memDC, 0, 0, ww, wh, SRCCOPY);
                        }
                        finally { g.ReleaseHdc(qdc); }                // Graphics 的 DC 只能配 ReleaseHdc（1.6 误用 ReleaseDC）
                    }
                }
                catch (Exception ex)
                {
                    if (q != null) { try { q.Dispose(); } catch (Exception) { } q = null; }
                    return Fail("grab ex: " + ex.Message, fkey);
                }
                finally
                {
                    if (oldObj != IntPtr.Zero) SelectObject(memDC, oldObj);
                    if (dib != IntPtr.Zero) DeleteObject(dib);        // 原生 31MB 立即还给系统
                    if (memDC != IntPtr.Zero) DeleteDC(memDC);
                    if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC);
                }
                if (!ok || q == null) return Fail("grab failed (no bitmap)", fkey);

                if (FlatExtreme(q)) { q.Dispose(); return Fail("blank capture", fkey); }

                // 2026-11-17：抓取前后指纹一致才锁定缓存。
                // 换壁纸的那一刻，转码文件可能已经写好、而桌面合成器还没把新图贴到 WorkerW 上 ——
                // 这时 PrintWindow 抓回来的是上一张的旧画面。旧实现「抓到什么就存什么，并把新指纹
                // 记为已缓存」，于是「新指纹 + 旧内容」被锁死，之后指纹不再变就再也不会重抓。
                // 现在：两次指纹不一致 → 本次结果照样返回（不退回实心卡片），但**不锁定指纹**，
                // 下一次重绘会重抓，最多一个心跳周期自愈。
                string sigAfter = WallpaperSignature();
                bool stable = (sig == sigAfter);

                _failKey = ""; _failTicks = 0;                        // 抓成功 → 清负缓存
                _wallQ = q; _host = host; _ww = ww; _hh = wh;
                _sig = stable ? sig : "";                             // 不stable → 下次必重抓
                _captures++;
                _lastLog = "captured(DIB) host=" + ClassName(host) + " " + ww + "x" + wh
                         + " -> quarter " + qw + "x" + qh
                         + " sig=" + (sig.Length > 60 ? sig.Substring(0, 60) + "…" : sig)
                         + (stable ? "" : "  [指纹在抓取期间又变了，本次不锁缓存]") + " ";
                return _wallQ;
            }
        }

        /// <summary>
        /// 抓取 screenRect 处的壁纸并做磨砂，返回**整块不透明**的「磨砂玻璃」成品（直接铺卡片底）。
        /// 「背景透明度」= 这块玻璃的遮盖度：op 越小主题色越淡、壁纸糊影越清楚；
        /// op=100 时主题色压满，接近不透明的纯色卡片。
        /// 失败返回 null（调用方退回实心卡片）。style 0 碳灰 / 1 雪白 / 2 藏青。
        /// blurLevel 1~5：1.8.5 起用户可选的模糊强度，3 = 1.8.4 的默认观感。
        /// </summary>
        public static Bitmap Grab(Rectangle screenRect, int style, double scale, double opacity, int blurLevel)
        {
            _lastLog = "";
            if (screenRect.Width <= 0 || screenRect.Height <= 0) return null;
            IntPtr host = Host();
            if (host == IntPtr.Zero) { _lastLog = "no wallpaper host"; return null; }

            RECT wr;
            if (!GetWindowRect(host, out wr)) { _lastLog = "GetWindowRect failed"; return null; }
            int ww = wr.R - wr.L, wh = wr.B - wr.T;
            if (ww <= 0 || wh <= 0) { _lastLog = "empty host rect"; return null; }

            Bitmap wq = EnsureQuarter(host, wr);
            if (wq == null) return null;                              // _lastLog 已写明

            double qs = wq.Width / (double)ww;                        // 物理 → 1/4 的比例
            double fx = (screenRect.X - wr.L) * qs;
            double fy = (screenRect.Y - wr.T) * qs;
            double fw = screenRect.Width * qs, fh = screenRect.Height * qs;

            // 1.9：模糊改在**全分辨率**裁剪块上做。1.8.5 是在 1/4 缩略图上按半径 2.00~4.12 糊的，
            // 换算到全分辨率只差零点几个像素，再被 4× 双线性放大抹平 —— 5 档实测只有 1.5 档的
            // 分辨度（2/3/4 档纹理保留率 5%/5%/5%，与同一张图无异）。现在每档直接给全分辨率半径
            // 1.9：模糊改在**全分辨率**裁剪块上做。1.8.5 是在 1/4 缩略图上按半径 2.00~4.12 糊的，
            // 换算到全分辨率只差零点几个像素，再被 4× 双线性放大抹平 —— 5 档实测只有 1.5 档的
            // 分辨度（2/3/4 档纹理保留率 5%/5%/5%，与同一张图无异）。
            // 标尺（用户 2026-11-13 定）：**3 档 = 1.8.5 的 1 档**（也是他自己在用的那一档），
            // 1/2 档比它更清、4/5 档更糊。全分辨率半径 3/5/8/13/20 px：
            //   3 档单个 r=8 与 1.8.5 的 1 档（quarter r=2，等效内核宽 17px）逐像素对等；
            //   相邻档有效强度比 ≈1.4~1.6 倍，五档一眼五张图。
            int r = RadiusFor(blurLevel, scale);
            // 只有大半径（≥12px，即 4/5 档）才补一遍 r/3 压掉盒式方肩 —— 3 档要保持与旧版 1 档完全一致
            int r2 = (r >= 12) ? Math.Max(1, (int)Math.Round(r / 3.0)) : 0;
            int margin = (r + r2) * 2;                                // 外扩采样，消盒式模糊的边界拖影

            // 卡片矩形与外扩块（壁纸坐标，全分辨率），外扩块钳进壁纸宿主
            int c0x = screenRect.X, c0y = screenRect.Y;
            int c1x = screenRect.X + screenRect.Width, c1y = screenRect.Y + screenRect.Height;
            int e0x = Math.Max(wr.L, c0x - margin), e0y = Math.Max(wr.T, c0y - margin);
            int e1x = Math.Min(wr.R, c1x + margin), e1y = Math.Min(wr.B, c1y + margin);
            int i0x = Math.Max(e0x, c0x), i0y = Math.Max(e0y, c0y);
            int i1x = Math.Min(e1x, c1x), i1y = Math.Min(e1y, c1y);
            if (e1x - e0x <= 0 || e1y - e0y <= 0 || i1x <= i0x || i1y <= i0y)
            { _lastLog = "window outside wallpaper host"; return null; }

            Bitmap small = null, back = null;
            try
            {
                // 从缩略缓存裁出「外扩块」并按全分辨率重采样，模糊只跑在这一小块（约 560x540）上
                small = new Bitmap(e1x - e0x, e1y - e0y, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(wq, new Rectangle(0, 0, small.Width, small.Height),
                                (float)((e0x - wr.L) * qs), (float)((e0y - wr.T) * qs),
                                (float)((e1x - e0x) * qs), (float)((e1y - e0y) * qs), GraphicsUnit.Pixel);
                }
                Stopwatch swb = Stopwatch.StartNew();
                BoxBlur(small, r);
                if (r2 > 0) BoxBlur(small, r2);
                swb.Stop();
                _lastBlurMs = swb.Elapsed.TotalMilliseconds;

                back = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppPArgb);
                Color baseC = ThemeBase(style);
                using (Graphics g = Graphics.FromImage(back))
                {
                    g.Clear(baseC);                                   // 窗口压出屏幕的边缘小条用主题基色兜底
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, i0x - c0x, i0y - c0y,
                                new Rectangle(i0x - e0x, i0y - e0y, i1x - i0x, i1y - i0y), GraphicsUnit.Pixel);
                }

                // 合成：糊壁纸 → 向主题基色收缩。t = 主题色占比，随「背景透明度」单调增长：
                //   op=10%  → t≈0.08：几乎就是一块糊过的壁纸（隔着纱看的观感，糊得清清楚楚）；
                //   op=50%  → t≈0.40：烟色玻璃，壁纸糊影压暗但仍清晰可辨；
                //   op=100% → t≈0.80：主题色压满，接近不透明的纯色卡片。
                // 关键：这一层始终**不透明** —— 「半透明」靠「看清糊影」来表达，
                // 绝不能把清晰的桌面原图按 (1-op) 掺回来（1.7.1 就是这么翻车的：
                // 掺回 50% 的清晰桌面，边缘保持 50% 对比，看上去跟没糊一样）。
                double op = (opacity <= 0) ? 1.0 : (opacity > 1 ? opacity / 100.0 : opacity);
                if (op < 0.10) op = 0.10;
                if (op > 1) op = 1;
                double t = TintOf(style) * op;
                int av = (int)Math.Round(255 * t);
                using (Graphics g = Graphics.FromImage(back))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(av, baseC)))
                    g.FillRectangle(b, 0, 0, back.Width, back.Height);

                _lastLog = "op=" + op.ToString("0.00") + " t=" + t.ToString("0.00") + " lv=" + blurLevel
                         + " r=[" + r + (r2 > 0 ? "," + r2 : "") + "] blur=" + _lastBlurMs.ToString("0.00") + "ms"
                         + " ok host=" + ClassName(host) + " hostRect=" + wr.L + "," + wr.T + "," + ww + "x" + wh
                         + " rect=" + screenRect.X + "," + screenRect.Y + "," + screenRect.Width + "x" + screenRect.Height
                         + " cache=" + wq.Width + "x" + wq.Height;
                return back;
            }
            catch (Exception ex)
            {
                _lastLog = "compose ex: " + ex.Message;
                if (back != null) { try { back.Dispose(); } catch (Exception) { } }
                return null;
            }
            finally { if (small != null) small.Dispose(); }
        }

        /// <summary>
        /// 1.9：磨砂强度 1~5 档 → **全分辨率**盒式模糊半径（px）。
        /// 标尺（用户 2026-11-13 定：「你的 1 档 = 旧版 1 档 = 新作的 3 档」）：
        ///   1 档 3   —— 最清（连壁纸细节的轮廓都还在，只是软化）
        ///   2 档 5
        ///   3 档 8   —— **= 1.8.5 的 1 档**（用户在用的那一档；单遍 r=8，等效内核宽 17px）
        ///   4 档 13  —— 补 r/3 压方肩
        ///   5 档 20  —— 最糊：只留明暗分布，完全看不到内容
        /// 半径按窗口缩放 scale 等比缩放，下限 1。
        /// </summary>
        static int RadiusFor(int level, double scale)
        {
            if (level < 1) level = 1;
            if (level > 5) level = 5;
            int[] preset = new int[] { 3, 5, 8, 13, 20 };
            double s = (scale <= 0 ? 1.0 : scale);
            return Math.Max(1, (int)Math.Round(preset[level - 1] * s));
        }

        /// <summary>兼容旧调用（自检/工具）：按第 3 档（默认观感）抓取</summary>
        public static Bitmap Grab(Rectangle screenRect, int style, double scale, double opacity)
        {
            return Grab(screenRect, style, scale, opacity, 3);
        }

        static Color ThemeBase(int style)
        {
            if (style == 1) return Color.FromArgb(246, 246, 246);     // 雪白
            if (style == 2) return Color.FromArgb(22, 34, 64);        // 藏青 #162240
            return Color.FromArgb(20, 20, 20);                        // 碳灰
        }

        /// <summary>
        /// 「背景透明度」=100% 时主题色的占比：碳灰 0.80 / 藏青 0.78 / 雪白 0.72。
        /// 实际 t = TintOf × op，所以：
        ///   10% → 几乎纯糊壁纸（看得出糊、也看得出是壁纸）；50% → 烟色玻璃；
        ///   100% → 主题色压满，接近不透明的纯色卡片（这就是 100% 该有的样子）。
        /// 单调、语义单一，不再有 1.7.0 那种「数字越小越不透明」的倒挂。
        /// </summary>
        static double TintOf(int style)
        {
            if (style == 1) return 0.72;
            if (style == 2) return 0.78;
            return 0.80;
        }

        static string ClassName(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        /// <summary>
        /// 抓回来的整幅壁纸是不是「整片纯黑 / 纯白」（DWM 合成下 PrintWindow 的典型失败形态）。
        /// 只把均匀且极端的图判为失败 —— 纯色 / 弱渐变壁纸是合法的（1.6 的 max-min&lt;8 会误杀）。
        /// </summary>
        static bool FlatExtreme(Bitmap b)
        {
            int min = 255, max = 0;
            long sum = 0;
            int n = 0;
            for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 24))
                for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 24))
                {
                    Color c = b.GetPixel(x, y);
                    int v = (c.R + c.G + c.B) / 3;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                    n++;
                }
            if (n == 0) return true;
            int mean = (int)(sum / n);
            return (max - min) < 2 && (mean < 6 || mean > 249);
        }

        /// <summary>可分离盒式模糊（横竖各一次 = 一次近似高斯），只跑在裁出的小块上，成本可以忽略</summary>
        static void BoxBlur(Bitmap b, int r)
        {
            if (r < 1) return;
            int w = b.Width, h = b.Height;
            Rectangle rc = new Rectangle(0, 0, w, h);
            BitmapData bd = b.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] buf = new byte[stride * h];
                Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                byte[] tmp = new byte[buf.Length];
                int win = r * 2 + 1;

                // 横向
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int ch = 0; ch < 3; ch++)                 // 1.9：只糊 R/G/B，alpha 保持原值（整块不透明，省 1/4 计算量）
                    {
                        int sum = 0;
                        for (int k = -r; k <= r; k++)
                        {
                            int x = k; if (x < 0) x = 0; if (x >= w) x = w - 1;
                            sum += buf[row + x * 4 + ch];
                        }
                        for (int x = 0; x < w; x++)
                        {
                            tmp[row + x * 4 + ch] = (byte)(sum / win);
                            int add = x + r + 1; if (add >= w) add = w - 1;
                            int del = x - r; if (del < 0) del = 0;
                            sum += buf[row + add * 4 + ch] - buf[row + del * 4 + ch];
                        }
                    }
                }
                // 纵向
                for (int x = 0; x < w; x++)
                {
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int sum = 0;
                        for (int k = -r; k <= r; k++)
                        {
                            int y = k; if (y < 0) y = 0; if (y >= h) y = h - 1;
                            sum += tmp[y * stride + x * 4 + ch];
                        }
                        for (int y = 0; y < h; y++)
                        {
                            buf[y * stride + x * 4 + ch] = (byte)(sum / win);
                            int add = y + r + 1; if (add >= h) add = h - 1;
                            int del = y - r; if (del < 0) del = 0;
                            sum += tmp[add * stride + x * 4 + ch] - tmp[del * stride + x * 4 + ch];
                        }
                    }
                }
                Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
            }
            finally { b.UnlockBits(bd); }
        }
    }
}
