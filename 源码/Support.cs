// About Time 1.0 —— 内嵌资源读取 / 地点搜索匹配对话框 / 程序入口
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AboutTime
{
    // ============================================================ 内嵌文本资源
    static class ResourceBox
    {
        public static string ReadText(string res)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                {
                    if (s == null) return "";
                    using (StreamReader r = new StreamReader(s, System.Text.Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch (Exception) { return ""; }
        }
    }

    // ============================================================ DPI 折算
    // 进程已声明 DPI 感知，自排布局按系统 DPI 等比放大，避免高缩放下对话框文字溢出
    static class Dpi
    {
        public static float K = 1f;

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int idx);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr h, IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        internal static extern IntPtr MemProbe_GetCurrentProcess();
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetGuiResources")]
        internal static extern uint MemProbe_GetGuiResources(IntPtr hProc, uint uiFlags);   // 0=GDI 对象数 1=USER 对象数

        public static void Init()
        {
            float k = 1f;
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    try { k = GetDeviceCaps(dc, 88) / 96f; }       // LOGPIXELSX
                    finally { ReleaseDC(IntPtr.Zero, dc); }
                }
            }
            catch (Exception) { }
            try
            {
                // 系统缩放设置：Graphics 偶尔报 96（字体却按真实 DPI 渲染），用它兜底
                using (Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser
                          .OpenSubKey(@"Control Panel\Desktop\WindowMetrics"))
                {
                    if (rk != null)
                    {
                        object o = rk.GetValue("AppliedDPI");
                        if (o != null) k = Math.Max(k, Convert.ToSingle(o) / 96f);
                    }
                }
            }
            catch (Exception) { }
            if (k < 0.9f) k = 1f;
            if (k > 4f) k = 4f;
            K = k;
        }

        public static int S(int v) { return (int)Math.Round(v * K); }

        public static Size Sz(int w, int h) { return new Size(S(w), S(h)); }

        public static void Set(Control c, int x, int y, int w, int h) { c.SetBounds(S(x), S(y), S(w), S(h)); }
    }

    // ============================================================ 添加 / 设置地点：输入 → 匹配 → 点选
    /// <summary>界面文案二选一（中文 / 英文），按当前界面语言取</summary>
    static class L
    {
        public static string B(string zh, string en) { return Settings.CurrentLang == "en" ? en : zh; }
    }

    sealed class CityDialog : Form
    {
        public City Result;

        TextBox edit;

        /// <summary>弹出对话框；返回 null 表示用户取消</summary>
        public static City Pick(IWin32Window owner, City current)
        {
            // 1.9：打开就后台预热世界城市全表（55k 行，约 0.1~2s），用户点「匹配」时表已就绪，
            // 不会再出现「闲置 10 分钟卸载后第一次打字卡 1~2 秒」。关掉对话框立刻回收
            // （老版本要等闲置满 10 分钟）—— 这正是「添加地点后内存居高不下」的根。
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { TimeZoneMatcher.EnsureWorld(); } catch (Exception) { } });
            CityDialog d = new CityDialog(current);
            d.ShowDialog(owner);
            City r = d.Result;
            d.Dispose();
            CityDb.ReleaseAfterMatch();
            return r;
        }

        CityDialog(City current)
        {
            bool isAdd = (current == null);
            string title = isAdd ? L.B("添加地点", "Add location") : L.B("设置地点", "Set location");

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Dpi.Sz(400, 170);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label tip = new Label();
            tip.AutoSize = false;
            Dpi.Set(tip, 14, 12, 372, 40);
            tip.Text = L.B("请输入地点名称（中文或英文均可），\r\n然后点“匹配”，按地点自动匹配所在时区：",
                            "Type a place name (Chinese or English).\r\nThen click Match to look up its time zone automatically:");
            Controls.Add(tip);

            edit = new TextBox();
            Dpi.Set(edit, 14, 58, 372, 28);
            edit.Font = new Font("Microsoft YaHei UI", 12f);
            if (current != null)
                edit.Text = (current.En != null && current.En.Length > 0) ? current.En : current.Zh;
            Controls.Add(edit);

            Button bMatch = new Button();
            bMatch.Text = L.B("匹配", "Match");
            Dpi.Set(bMatch, 176, 118, 100, 32);
            bMatch.Click += new EventHandler(delegate { DoMatch(); });
            Controls.Add(bMatch);
            AcceptButton = bMatch;      // 回车只由 AcceptButton 触发一次

            Button bCancel = new Button();
            bCancel.Text = L.B("取消", "Cancel");
            Dpi.Set(bCancel, 288, 118, 98, 32);
            bCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(bCancel);
            CancelButton = bCancel;

            Load += new EventHandler(delegate { edit.Focus(); edit.SelectAll(); });
        }

        void DoMatch()
        {
            string raw = edit.Text.Trim();
            if (raw.Length == 0)
            {
                Warn(L.B("请输入地点名称。", "Please type a place name."));
                return;
            }
            if (raw.Length > 40) raw = raw.Substring(0, 40);

            // ① 先查内置常用城市（命中可保留繁体中文名与既定译名）
            City builtin = FindBuiltin(raw);
            if (builtin != null) { Done(builtin); return; }

            // ⓪ 1.8.1：世界城市全表懒加载 —— 只有走到「时区库匹配」这一步才需要它；
            // 后台先起加载线程，下面的 Match 首次调用会等锁（3MB 解析约 1~2 秒，仅第一次）。
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { TimeZoneMatcher.EnsureWorld(); } catch (Exception) { } });

            // ② 时区库匹配（库表为简体，查询串先转简体）
            List<MatchResult> res;
            try { res = TimeZoneMatcher.Match(ZhTxt.ToSimp(raw)); }
            catch (Exception) { res = new List<MatchResult>(); }
            if (res.Count == 0)
            {
                try { res = TimeZoneMatcher.Match(raw); }
                catch (Exception) { res = new List<MatchResult>(); }
            }

            // 过滤时区 ID 非法的候选：宁可让用户手选时区，也不要静默按 UTC 显示
            List<MatchResult> valid = new List<MatchResult>();
            foreach (MatchResult r in res) if (IsValidTz(r.TzId)) valid.Add(r);
            res = valid;

            if (res.Count == 0)
            {
                // 匹配不到 → 手动指定时区兜底
                ManualTzDialog dlg = new ManualTzDialog(raw);
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Selected != null)
                    Done(BuildFromMatch(dlg.Selected, raw));
                return;
            }
            if (res.Count == 1)
            {
                Done(BuildFromMatch(res[0], raw));
                return;
            }
            SelectTzDialog pick = new SelectTzDialog(raw, res);
            if (pick.ShowDialog(this) == DialogResult.OK && pick.Selected != null)
                Done(BuildFromMatch(pick.Selected, raw));
        }

        static City FindBuiltin(string raw)
        {
            CityDb.Build();
            string q = raw.ToLower();
            string qs = ZhTxt.ToSimp(raw).ToLower();
            string qt = ZhTxt.ToTrad(raw).ToLower();
            foreach (City c in CityDb.Legacy)
            {
                string zh = c.Zh.ToLower();
                string en = c.En != null ? c.En.ToLower() : "";
                if (zh == q || zh == qs || zh == qt) return c;
                if (en.Length > 0 && en == q) return c;
                if (en.Length > 2 && q.Length >= 2 && en.StartsWith(q)) return c;
                if (qt.Length >= 2 && zh.StartsWith(qt)) return c;
            }
            return null;
        }

        static bool IsValidTz(string id)
        {
            if (id == null || id.Length == 0) return false;
            try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
            catch (Exception) { return false; }
        }

        static City BuildFromMatch(MatchResult mr, string raw)
        {
            string zh = (mr.Cn != null && mr.Cn.Length > 0) ? mr.Cn : raw;
            string en = (mr.En != null && mr.En.Length > 0) ? mr.En : raw;
            string country = CityDb.CountryForTz(mr.TzId);
            City c = new City(zh, en, mr.TzId, country);
            CityDb.AttachGeo(c);          // 补国家码/州省码，英文版滚屏显示 City·WA·USA
            return c;
        }

        void Done(City c)
        {
            if (c == null) return;
            Result = c;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Warn(string s)
        {
            MessageBox.Show(s, "About Time", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ============================================================ 匹配结果点选
    sealed class SelectTzDialog : Form
    {
        public MatchResult Selected;

        public SelectTzDialog(string city, List<MatchResult> results)
        {
            Text = L.B("匹配结果：", "Matches: ") + city;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Dpi.Sz(460, 300);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label tip = new Label();
            tip.AutoSize = false;
            Dpi.Set(tip, 14, 12, 432, 22);
            tip.Text = L.B("找到 ", "Found ") + results.Count + L.B(" 个匹配结果，请点选一项后点“确定”：", " matches. Select one, then click OK:");
            Controls.Add(tip);

            ListBox lb = new ListBox();
            Dpi.Set(lb, 14, 42, 432, 200);
            lb.IntegralHeight = false;
            lb.Font = new Font("Microsoft YaHei UI", 9.5f);
            foreach (MatchResult r in results) lb.Items.Add(r);
            lb.SelectedIndex = 0;
            lb.DoubleClick += new EventHandler(delegate
            {
                if (lb.SelectedItem != null) Selected = (MatchResult)lb.SelectedItem;
                DialogResult = DialogResult.OK;
                Close();
            });
            Controls.Add(lb);

            Button bOk = new Button();
            bOk.Text = L.B("确定", "OK");
            Dpi.Set(bOk, 180, 254, 110, 30);
            bOk.Click += new EventHandler(delegate
            {
                if (lb.SelectedItem != null) Selected = (MatchResult)lb.SelectedItem;
                DialogResult = DialogResult.OK;
                Close();
            });
            Controls.Add(bOk);
            AcceptButton = bOk;

            Button bNo = new Button();
            bNo.Text = "取消";
            Dpi.Set(bNo, 302, 254, 110, 30);
            bNo.DialogResult = DialogResult.Cancel;
            Controls.Add(bNo);
            CancelButton = bNo;
        }
    }

    // ============================================================ 手动指定时区（匹配失败兜底）
    sealed class ManualTzDialog : Form
    {
        public MatchResult Selected;
        List<TimeZoneInfo> _all = new List<TimeZoneInfo>();
        TextBox _filter;
        ListBox _list;
        Button _ok;
        string _name;
        string PH = L.B("输入关键字过滤…", "Filter time zones…");
        bool _phOn = true;

        public ManualTzDialog(string name)
        {
            _name = name;
            Text = L.B("未找到匹配 · 请手动指定时区", "No match · set the time zone manually");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Dpi.Sz(480, 372);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label tip = new Label();
            tip.AutoSize = false;
            Dpi.Set(tip, 14, 12, 452, 22);
            tip.Text = L.B("“" + name + "”未在时区库中匹配到，请在下方时区列表中手动选择：",
                            "“" + name + "” was not found. Pick a time zone from the list below:");
            Controls.Add(tip);

            _filter = new TextBox();
            Dpi.Set(_filter, 14, 42, 452, 26);
            _filter.Text = PH;
            _filter.ForeColor = SystemColors.GrayText;
            _filter.Enter += new EventHandler(delegate
            {
                if (_phOn) { _filter.Text = ""; _filter.ForeColor = SystemColors.WindowText; _phOn = false; }
            });
            _filter.TextChanged += new EventHandler(delegate { Reload(); });
            Controls.Add(_filter);

            _list = new ListBox();
            Dpi.Set(_list, 14, 76, 452, 238);
            _list.IntegralHeight = false;
            _list.Font = new Font("Microsoft YaHei UI", 9f);
            _list.SelectedIndexChanged += new EventHandler(delegate { _ok.Enabled = _list.SelectedItem != null; });
            _list.DoubleClick += new EventHandler(delegate { if (_list.SelectedItem != null) _ok.PerformClick(); });
            Controls.Add(_list);

            _ok = new Button();
            _ok.Text = "确定";
            Dpi.Set(_ok, 170, 328, 130, 32);
            _ok.Enabled = false;
            _ok.Click += new EventHandler(delegate
            {
                TimeZoneInfo tz = _list.SelectedItem as TimeZoneInfo;
                if (tz == null) return;
                TimeSpan off = tz.GetUtcOffset(DateTime.UtcNow);
                int total = (int)Math.Round(off.TotalMinutes);
                string sign = total >= 0 ? "+" : "-";
                int abs = Math.Abs(total);
                string offStr = sign + (abs / 60).ToString("00") + ":" + (abs % 60).ToString("00");
                bool cjk = TimeZoneMatcher.HasCjk(_name);
                string enName = cjk ? TimeZoneMatcher.PinyinTitle(_name) : _name;
                MatchResult mr = new MatchResult();
                mr.Display = _name + "/" + enName + " · (UTC" + offStr + ") " + tz.DisplayName;
                mr.TzId = tz.Id;
                mr.Cn = _name;
                mr.En = enName;
                Selected = mr;
                DialogResult = DialogResult.OK;
                Close();
            });
            Controls.Add(_ok);
            AcceptButton = _ok;

            Button bNo = new Button();
            bNo.Text = L.B("取消", "Cancel");
            Dpi.Set(bNo, 312, 328, 130, 32);
            bNo.DialogResult = DialogResult.Cancel;
            Controls.Add(bNo);
            CancelButton = bNo;

            Load += new EventHandler(delegate { InitList(); });
        }

        void InitList()
        {
            _all.Clear();
            foreach (TimeZoneInfo tz in TimeZoneInfo.GetSystemTimeZones()) _all.Add(tz);
            _all.Sort(delegate(TimeZoneInfo a, TimeZoneInfo b)
            {
                int c = a.GetUtcOffset(DateTime.UtcNow).CompareTo(b.GetUtcOffset(DateTime.UtcNow));
                if (c != 0) return c;
                return string.Compare(a.StandardName, b.StandardName, StringComparison.OrdinalIgnoreCase);
            });
            Reload();
        }

        void Reload()
        {
            if (_list == null) return;
            _list.Items.Clear();
            string kw = _phOn ? "" : _filter.Text.Trim().ToLower();
            foreach (TimeZoneInfo tz in _all)
            {
                if (kw.Length == 0) { _list.Items.Add(tz); continue; }
                string dn = tz.DisplayName != null ? tz.DisplayName.ToLower() : "";
                string sn = tz.StandardName != null ? tz.StandardName.ToLower() : "";
                if (dn.IndexOf(kw) >= 0 || sn.IndexOf(kw) >= 0) _list.Items.Add(tz);
            }
            _ok.Enabled = false;
        }
    }

    // ============================================================ 时钟管理：主时钟 + 世界时钟的增删改
    // 规则：至少保留一个时钟模块；主时钟与其余模块一样可以上移下移。
    sealed class ClockManagerDialog : Form
    {
        public bool ShowMain;
        public bool TimerOn;
        public bool GzOn;
        public int MainSkin;              // 2.0：主时钟皮肤 0 原样 / 1 纯中文（与界面语言解耦）
        public City MainCity;
        public int MainPos;
        public int TimerPos;
        public int GzPos;
        public int CalPos;
        public List<City> Cities;

        // Kind: 0 主时钟 / 1 世界时钟 / 2 日历（只占位，不在列表里显示） / 3 倒计时·秒表 / 4 干支计时
        sealed class Item
        {
            public int Kind;
            public City C;
            public bool On;
        }

        List<Item> items = new List<Item>();
        List<int> shown = new List<int>();
        ListBox list;
        Button bAdd, bChange, bLocal, bDel, bUp, bDown, bTog, bSkin;
        bool calAlways;

        public ClockManagerDialog(bool showMain, bool timerOn, bool gzOn, City mainCity,
            int mainPos, int timerPos, int gzPos, int calMode, int calPos, List<City> cities)
            : this(showMain, timerOn, gzOn, mainCity, mainPos, timerPos, gzPos, calMode, calPos, cities, 0)
        {
        }

        public ClockManagerDialog(bool showMain, bool timerOn, bool gzOn, City mainCity,
            int mainPos, int timerPos, int gzPos, int calMode, int calPos, List<City> cities, int mainSkin)
        {
            ShowMain = showMain;
            TimerOn = timerOn;
            GzOn = gzOn;
            MainSkin = (mainSkin < 0 || mainSkin > 1) ? 0 : mainSkin;
            MainCity = mainCity;
            MainPos = mainPos;
            TimerPos = timerPos;
            GzPos = gzPos;
            CalPos = calPos;
            calAlways = (calMode == 1);
            Cities = new List<City>();
            if (cities != null) foreach (City c in cities) Cities.Add(c);

            // 位次与 Widget.BuildRows 完全同一套算法：城市先铺，再依次插入 主 / 历 / 计 / 干
            List<int> seq = new List<int>();
            for (int i = 0; i < Cities.Count; i++) seq.Add(i);
            seq.Insert(Pos(mainPos, seq.Count), -1);
            seq.Insert(Pos(calPos, seq.Count), -2);
            seq.Insert(Pos(timerPos, seq.Count), -3);
            seq.Insert(Pos(gzPos, seq.Count), -4);
            for (int i = 0; i < seq.Count; i++)
            {
                Item it = new Item();
                int tok = seq[i];
                if (tok == -1) { it.Kind = 0; it.On = ShowMain; }
                else if (tok == -2) { it.Kind = 2; it.On = calAlways; }
                else if (tok == -3) { it.Kind = 3; it.On = TimerOn; }
                else if (tok == -4) { it.Kind = 4; it.On = GzOn; }
                else { it.Kind = 1; it.C = Cities[tok]; it.On = true; }
                items.Add(it);
            }

            Text = Bi("时钟管理", "clocks configuration");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            int dy = (Settings.CurrentLang == "en") ? 38 : 0;   // 英文提示是 4 行（干支用全称后变长），整块往下让
            ClientSize = Dpi.Sz(520, 416 + dy);
            Font = UiFont();

            Label tip = new Label();
            tip.AutoSize = false;
            Dpi.Set(tip, 18, 10, 484, 56 + dy);
            tip.Text = Bi("主时钟在最上为默认，也可上下移动；每个模块都能改地点。\r\n"
                + "主时钟、干支计时、倒计时 / 秒表都可逐个「关闭 / 开启」，\r\n"
                + "关掉的模块不会从列表里消失，而是标为「已关闭」。",
                "The main clock sits on top by default; it and every other module can move.\r\n"
                + "Main clock, Ganzhi Stems-Branches time system and countdown / stopwatch\r\n"
                + "can each be switched off or on; a module switched off stays in the list,\r\n"
                + "marked as off.");
            Controls.Add(tip);

            list = new ListBox();
            Dpi.Set(list, 18, 70 + dy, 484, 194);
            list.IntegralHeight = false;
            list.Font = new Font(Font.FontFamily, 9.5f);
            list.SelectedIndexChanged += new EventHandler(delegate { RefreshButtons(); });
            Controls.Add(list);

            bAdd = Btn(Bi("添加…", "Add…"), 18, 272 + dy, 104, delegate { DoAdd(); });
            bChange = Btn(Bi("更改地点…", "Set Location"), 128, 272 + dy, 116, delegate { DoChange(); });
            bLocal = Btn(Bi("恢复本地时间", "Use local time"), 250, 272 + dy, 132, delegate { DoLocal(); });
            bDel = Btn(Bi("删除", "Remove"), 388, 272 + dy, 96, delegate { DoDel(); });

            bUp = Btn(Bi("上移", "Move up"), 18, 314 + dy, 96, delegate { DoMove(-1); });
            bDown = Btn(Bi("下移", "Move down"), 120, 314 + dy, 96, delegate { DoMove(1); });
            // 2.0：「关闭 / 开启」按示意图缩到 96 宽，右边腾出「主时钟切换中/英文」
            // v2.1：按钮文字直接反映当前状态（见 SkinLabel）
            bTog = Btn(Bi("关闭 / 开启", "Off / On"), 222, 314 + dy, 96, delegate { DoToggle(); });
            bSkin = Btn(Bi("主时钟切换中/英文", "Main clock: Chinese / English"), 326, 314 + dy, 172,
                delegate { MainSkin = (MainSkin == 1) ? 0 : 1; SkinLabel(); });

            Button ok = Btn(Bi("确定", "OK"), 258, 360 + dy, 118, delegate
            {
                if (!AnyVisible())
                {
                    MessageBox.Show(Bi("至少要保留一个可见模块：主时钟、干支计时、倒计时 / 秒表、日历，或至少一个世界时钟。",
                        "At least one visible module must remain: the main clock, the Ganzhi Stems-Branches time system, the countdown / stopwatch, the calendar, or one world clock."),
                        "About Time", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Commit();                     // 把列表顺序写回 Cities / 各模块位次
                DialogResult = DialogResult.OK;
                Close();
            });
            AcceptButton = ok;

            Button no = Btn(Bi("取消", "Cancel"), 386, 360 + dy, 116, delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            });
            CancelButton = no;

            Fill();
        }

        static int Pos(int v, int n) { if (v < 0) return 0; if (v > n) return n; return v; }

        static Font UiFont()
        {
            // 不绑死本机字体：中文优先雅黑 UI，其它系统退回系统对话框字体
            try
            {
                Font f = new Font("Microsoft YaHei UI", 9f);
                if (f.FontFamily.Name == "Microsoft YaHei UI") return f;
            }
            catch (Exception) { }
            return new Font(SystemFonts.MessageBoxFont.FontFamily, 9f);
        }

        static string Bi(string zh, string en) { return Settings.CurrentLang == "en" ? en : zh; }

        Button Btn(string text, int x, int y, int w, EventHandler h)
        {
            Button b = new Button();
            b.Text = text;
            Dpi.Set(b, x, y, w, 32);
            b.Click += h;
            Controls.Add(b);
            return b;
        }

        int CountCities()
        {
            int n = 0;
            foreach (Item it in items) if (it.Kind == 1) n++;
            return n;
        }

        int CountOn()
        {
            int n = 0;
            foreach (Item it in items) if (it.Kind != 1 && it.Kind != 2 && it.On) n++;
            return n;
        }

        bool AnyVisible()
        {
            if (CountCities() > 0 || calAlways) return true;
            return CountOn() > 0;
        }

        string Label(Item it)
        {
            if (it.Kind == 0)
            {
                string s = Bi("主时钟 · ", "Main clock · ");
                s += (MainCity == null)
                    ? Bi("本地时间（跟随系统）", "local time (follows system)")
                    : (((Settings.CurrentLang == "en" && MainCity.En != null && MainCity.En.Length > 0) ? MainCity.En : MainCity.ZhUI)
                        + "  " + MainCity.UtcText());
                if (MainCity != null && MainCity.ZoneFailed) s += Bi("  （时区无效）", "  (invalid time zone)");
                if (!it.On) s += Bi("   — 已关闭", "   — off");
                return s;
            }
            if (it.Kind == 3) return Bi("倒计时 / 秒表", "Countdown / Stopwatch") + (it.On ? "" : Bi("   — 已关闭", "   — off"));
            if (it.Kind == 4) return Bi("干支计时", "Ganzhi Stems-Branches time system") + (it.On ? "" : Bi("   — 已关闭", "   — off"));
            return "";
        }

        void Fill()
        {
            int sel = list.SelectedIndex;
            list.BeginUpdate();
            list.Items.Clear();
            shown.Clear();
            int n = 1;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it.Kind == 2) continue;                    // 日历不在此管理
                if (it.Kind == 1)
                {
                    shown.Add(i);
                    string cname = (Settings.CurrentLang == "en" && it.C.En != null && it.C.En.Length > 0)
                        ? it.C.En : it.C.ZhUI;
                    list.Items.Add(n + "  " + cname + "  "
                        + it.C.UtcText()
                        + (it.C.ZoneFailed ? Bi("  （时区无效）", "  (invalid time zone)") : ""));
                    n++;
                }
                else
                {
                    shown.Add(i);
                    list.Items.Add(Label(it));
                }
            }
            if (sel < 0 || sel >= list.Items.Count) sel = 0;
            list.SelectedIndex = sel;
            list.EndUpdate();
            RefreshButtons();
        }

        Item Sel()
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= shown.Count) return null;
            return items[shown[i]];
        }

        void RefreshButtons()
        {
            Item it = Sel();
            int i = list.SelectedIndex;
            bool isMain = (it != null && it.Kind == 0);
            bool isCity = (it != null && it.Kind == 1);
            bool isMod = (it != null && (it.Kind == 0 || it.Kind == 3 || it.Kind == 4));
            bChange.Enabled = (isMain || isCity);
            bLocal.Enabled = isMain;
            bDel.Enabled = (isCity && CanRemoveCity());
            bUp.Enabled = (it != null && i > 0);
            bDown.Enabled = (it != null && i >= 0 && i < shown.Count - 1);
            bTog.Enabled = isMod;
            SkinLabel();                       // v2.1 修（审阅 P2-4）：按钮文字跟着当前皮肤走
        }

        /// <summary>v2.1 修（审阅 P2-4）：原来的「主时钟切换中/英文」是个无状态按钮，
        /// 点完不知道现在是中文还是英文数字。改成按钮上直接显示当前状态。</summary>
        void SkinLabel()
        {
            if (bSkin == null) return;
            bSkin.Text = (MainSkin == 1)
                ? Bi("主时钟：中文", "Main: Chinese")
                : Bi("主时钟：英文数字", "Main: English");
        }

        bool CanRemoveCity()
        {
            // 删掉这个城市后仍要有可见模块
            if (CountCities() > 1) return true;
            if (CountCities() == 1) return (CountOn() > 0 || calAlways);
            return false;
        }

        void DoAdd()
        {
            if (CountCities() >= 6)                       // 1.8.5：上限 8 → 6（用户要求压缩）
            {
                MessageBox.Show(Bi("最多 6 个世界时钟。", "Up to 6 world clocks."), "About Time");
                return;
            }
            City c = CityDialog.Pick(this, null);
            if (c == null) return;
            if (IsDup(c, null))
            {
                MessageBox.Show(Bi("该地点已在列表中。", "That location is already in the list."), "About Time");
                return;
            }
            int mainIdx = items.FindIndex(delegate(Item x) { return x.Kind == 0; });
            Item w = new Item(); w.Kind = 1; w.C = c; w.On = true;
            items.Insert(mainIdx + 1, w);         // 新地点插在主时钟后一位
            Fill();
            list.SelectedIndex = Math.Min(mainIdx + 1, list.Items.Count - 1);
        }

        void DoChange()
        {
            Item it = Sel();
            if (it == null || (it.Kind != 0 && it.Kind != 1)) return;
            City c = CityDialog.Pick(this, it.Kind == 0 ? MainCity : it.C);
            if (c == null) return;
            if (IsDup(c, it))
            {
                MessageBox.Show(Bi("该地点已在列表中。", "That location is already in the list."), "About Time");
                return;
            }
            if (it.Kind == 0) MainCity = c;
            else it.C = c;
            Fill();
        }

        void DoLocal()
        {
            MainCity = null;
            Fill();
        }

        void DoDel()
        {
            Item it = Sel();
            if (it == null || it.Kind != 1) return;
            if (!CanRemoveCity())
            {
                MessageBox.Show(Bi("至少要保留一个时钟模块。", "At least one clock module must remain."), "About Time");
                return;
            }
            int i = items.IndexOf(it);
            items.Remove(it);
            Fill();
            if (list.Items.Count > 1) list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
        }

        void DoMove(int dir)
        {
            Item it = Sel();
            if (it == null) return;
            int i = items.IndexOf(it);
            int j = i + dir;
            while (j >= 0 && j < items.Count && items[j].Kind == 2) j += dir;   // 跳过日历占位
            if (j < 0 || j >= items.Count) return;
            Item t = items[i];
            items[i] = items[j];
            items[j] = t;
            int keep = list.SelectedIndex;
            Fill();
            // 选中项在显示列表里的新位置
            for (int k = 0; k < shown.Count; k++) if (items[shown[k]] == it) { list.SelectedIndex = k; break; }
            if (list.SelectedIndex != keep) RefreshButtons();
        }

        // ------- 自检钩子（仅自检/出图路径使用，不影响交互） -------
        internal int ItemCount() { return items.Count; }

        internal int ShownCount() { return shown.Count; }

        internal string Dump()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int k = 0; k < list.Items.Count; k++)
                sb.AppendLine("  [" + k + "] " + list.Items[k].ToString());
            return sb.ToString();
        }

        /// <summary>自检钩子：把指定 Kind 的模块直接开/关一次（与按钮同一条状态路径，跳过 MessageBox 兜底）</summary>
        internal string TestToggle(int kind)
        {
            for (int k = 0; k < shown.Count; k++)
            {
                Item it = items[shown[k]];
                if (it.Kind != kind) continue;
                bool before = it.On;
                it.On = !it.On;
                if (kind == 0) ShowMain = it.On;
                if (kind == 3) TimerOn = it.On;
                if (kind == 4) GzOn = it.On;
                int keep = k;
                Fill();
                if (keep < list.Items.Count) list.SelectedIndex = keep;
                Commit();
                return "toggle kind=" + kind + " " + (before ? "on->off" : "off->on") + "  文案=[" + Label(it) + "]";
            }
            return "toggle kind=" + kind + " 未找到该模块";
        }

        void DoToggle()
        {
            Item it = Sel();
            if (it == null || (it.Kind != 0 && it.Kind != 3 && it.Kind != 4)) return;
            if (it.On && !AnyOtherVisible(it))
            {
                MessageBox.Show(Bi("这是最后一个可见模块，不能关闭；请先开启别的模块或添加一个世界时钟。",
                    "This is the last visible module and cannot be switched off. Turn on another module or add a world clock first."),
                    "About Time", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            it.On = !it.On;
            if (it.Kind == 0) ShowMain = it.On;
            if (it.Kind == 3) TimerOn = it.On;
            if (it.Kind == 4) GzOn = it.On;
            int keep = list.SelectedIndex;
            Fill();
            list.SelectedIndex = keep;
        }

        bool AnyOtherVisible(Item skip)
        {
            if (CountCities() > 0) return true;
            if (calAlways) return true;
            foreach (Item it in items)
                if (it != skip && it.Kind != 1 && it.Kind != 2 && it.On) return true;
            return false;
        }

        void Commit()
        {
            // 位次按「城市 → 主 → 历 → 计 → 干」的插入顺序折算，与 BuildRows 一一对应
            Cities = new List<City>();
            int nCity = 0, nMain = 0, nCal = 0, nTimer = 0;
            MainPos = 0; CalPos = 0; TimerPos = 0; GzPos = 0;
            foreach (Item it in items)
            {
                if (it.Kind == 1) { Cities.Add(it.C); nCity++; }
                else if (it.Kind == 0) { MainPos = nCity; nMain = 1; ShowMain = it.On; }
                else if (it.Kind == 2) { CalPos = nCity + nMain; nCal = 1; }
                else if (it.Kind == 3) { TimerPos = nCity + nMain + nCal; nTimer = 1; TimerOn = it.On; }
                else if (it.Kind == 4) { GzPos = nCity + nMain + nCal + nTimer; GzOn = it.On; }
            }
        }

        bool IsDup(City c, Item skip)
        {
            if (c == null) return false;
            if (MainCity != null && MainCity.Key == c.Key && (skip == null || skip.Kind != 0)) return true;
            foreach (Item it in items)
            {
                if (it.Kind != 1 || it == skip || it.C == null) continue;
                if (it.C.Key == c.Key) return true;
            }
            return false;
        }
    }
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        static Mutex _mutex;

        /// <summary>亮度近似 (2B+3G+R)/6，与 WallpaperFrost 的口径一致；供磨砂自检指标用</summary>
        static double Luma(System.Drawing.Color c)
        { return (c.B * 2.0 + c.G * 3.0 + c.R) / 6.0; }

        /// <summary>把分层帧的一个像素按 alpha 叠到背景像素上，得到屏幕上看到的亮度</summary>
        static double Over(System.Drawing.Color fg, System.Drawing.Color bg)
        {
            double a = fg.A / 255.0;
            return Luma(fg) * a + Luma(bg) * (1.0 - a);
        }

        [STAThread]
        static void Main(string[] args)
        {
            // 出图 / 自检模式不受"单实例"约束：本地常有正式实例在跑，
            // 否则 --shot 被静默拦下，exit code 0 却什么也不产出（排查时极易误判为程序坏了）
            bool toolMode = false;
            foreach (string a in args)
                if (a.StartsWith("--shot") || a.StartsWith("--selftest")) { toolMode = true; break; }

            bool created = false;
            try
            {
                _mutex = new Mutex(true, "AboutTime_SingleInstance", out created);
            }
            catch (Exception) { created = true; }
            if (!created && !toolMode)
            {
                // 已有实例在跑：把它叫到前面，而不是静默退出让用户以为程序坏了
                try
                {
                    IntPtr w = FindWindow(null, "About Time");
                    if (w != IntPtr.Zero) SetForegroundWindow(w);
                }
                catch (Exception) { }
                return;
            }

            try { SetProcessDPIAware(); } catch (Exception) { }
            Dpi.Init();

            // UI 线程异常不要"无声消失"：弹一次提示，然后继续运行
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(
                delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    MessageBox.Show("界面出现异常，程序会继续运行：\r\n" + e.Exception.Message,
                        "About Time", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });

            try { Ui.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show("内置字库加载失败（可执行文件可能不完整）：\r\n" + ex.Message,
                    "About Time", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 首次运行默认写入开机自启
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Settings.RootPath, false))
                {
                    if (k == null) Settings.ApplyAutoStart(true);
                }
            }
            catch (Exception) { }

            // 自启项还指向旧副本时改写成当前 exe（出图/自检模式不动注册表）
            if (!toolMode) { try { Settings.HealAutoStartPath(); } catch (Exception) { } }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 自检：--selftest=cw;<前缀> —— 弹出日历跟随透明度/主题/缩放/拖动 的接线验证
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=cw")) continue;
                string stem = a + "_out";
                int semi = a.IndexOf(';');
                if (semi > 0) stem = a.Substring(semi + 1);
                string log = "";
                try
                {
                    WidgetForm wf0 = new WidgetForm();
                    wf0.SetPreviewMode(2);
                    wf0.SetPreviewTheme(false);          // 从雪白起步，自检里再切碳灰
                    log = wf0.CalWinSelfTest(stem);
                    wf0.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(stem + "_cw.txt", log, System.Text.Encoding.UTF8); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=mn;<日志路径> —— 菜单接线验证（磨砂档位闭包 BUG 的回归，
            // 1.8.5 修订）：离屏 BuildMenu 逐项 PerformClick，断言点哪档是哪档
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=mn")) continue;
                string stem = a + "_out";
                int semi = a.IndexOf(';');
                if (semi > 0) stem = a.Substring(semi + 1);
                string log = "";
                try
                {
                    WidgetForm wf0 = new WidgetForm();
                    wf0.SetPreviewMode(2);
                    log = wf0.MenuBlurSelfTest();
                    wf0.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(stem + "_mn.txt", log, System.Text.Encoding.UTF8); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=cfg;<日志路径> —— 只读打印配置键（不写注册表，避免覆盖用户设置）
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=cfg")) continue;
                string path = "cfg_selftest.txt";
                int semi = a.IndexOf(';');
                if (semi > 0) path = a.Substring(semi + 1);
                string log = "";
                try
                {
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Settings.RootPath))
                    {
                        if (k == null) log = "(no key)";
                        else
                        {
                            string[] keys = { "TimerOn", "TimerPos", "TimerAlarm", "Style", "Scale", "MainPos", "BlurLevel", "BlurScaleV" };
                            foreach (string kn in keys)
                                log += kn + " = " + Convert.ToString(k.GetValue(kn)) + "\r\n";
                        }
                    }
                    // 1.9：再打一遍 Load() 的结论（只读，不落盘），用来核对磨砂档迁移是否生效
                    try
                    {
                        Settings st = Settings.Load();
                        log += "Load() BlurLevel = " + st.BlurLevel + "（注册表原值见上）\r\n";
                    }
                    catch (Exception ex2) { log += "Load ERR " + ex2.Message + "\r\n"; }
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(path, log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=cm;<日志路径> —— 时钟管理里三模块开/关（行数不变、位次折算不变）
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=cm")) continue;
                string path = "cm_selftest.txt";
                int semi = a.IndexOf(';');
                if (semi > 0) path = a.Substring(semi + 1);
                string log = "";
                try
                {
                    WidgetForm wf = new WidgetForm();
                    log = wf.ClockManagerSelfTest();
                    wf.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(path, log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=gz;<日志路径> —— 干支四柱标定值回归（含 2026-11-10 = 丙午/己亥/戊子/属马）
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=gz")) continue;
                string path = "gz_selftest.txt";
                int semi = a.IndexOf(';');
                if (semi > 0) path = a.Substring(semi + 1);
                System.Text.StringBuilder lg = new System.Text.StringBuilder();
                lg.AppendLine("== v1.5 ganzhi selftest ==");
                try
                {
                    DateTime t1 = new DateTime(2026, 11, 10, 14, 30, 0);
                    string yn = GzCore.YearName(t1), mn = GzCore.MonthName(t1), dn = GzCore.DayName(t1), hn = GzCore.HourName(t1);
                    bool ok = (yn == "丙午" && mn == "己亥" && dn == "戊子");
                    lg.AppendLine("2026-11-10 14:30 -> " + yn + " " + mn + " " + dn + " " + hn
                                  + "  期望 丙午 己亥 戊子 -> " + (ok ? "PASS" : "FAIL"));
                    lg.AppendLine("  属相=" + GzCore.AnimalOf(t1) + (GzCore.AnimalOf(t1) == "马" ? " PASS" : " FAIL")
                                  + "  时辰=" + GzCore.ShiChen(t1) + GzCore.KeNameOf(t1)
                                  + "  百刻=" + GzCore.BaiKe(t1)
                                  + "  八字=" + GzCore.BaZi(t1));
                    // 子初换日：23:30 应算次日子柱
                    DateTime t2 = new DateTime(2026, 11, 10, 23, 30, 0);
                    lg.AppendLine("2026-11-10 23:30 日柱=" + GzCore.DayName(t2) + "（子初换日，应为次日）"
                                  + "  对照 00:30 日柱=" + GzCore.DayName(new DateTime(2026, 11, 11, 0, 30, 0)));
                    // 立春换年：2026-02-03 仍属乙巳，2026-02-05 才进丙午
                    DateTime t3 = new DateTime(2026, 2, 3, 12, 0, 0), t4 = new DateTime(2026, 2, 5, 12, 0, 0);
                    lg.AppendLine("2026-02-03 年柱=" + GzCore.YearName(t3) + "  2026-02-05 年柱=" + GzCore.YearName(t4)
                                  + "（立春 " + GzCore.LiChun(2026).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "）");
                    // 时辰/刻 覆盖 12 个时辰 × 4 刻
                    int bad = 0;
                    for (int h = 0; h < 24; h++)
                    {
                        DateTime tt = new DateTime(2026, 11, 10, h, 0, 0);
                        if (GzCore.ShiChen(tt).Length != 2) bad++;
                    }
                    // 1.5.1：刻制标定 —— 96 刻制，初/正各 4 刻、每刻 15 分钟
                    // 特别盯 0:15 / 0:45：原先「正时」被多算 60 分钟，0:15 显示贰刻、0:45 还停在叁刻
                    {
                        string[] want = { "子正初刻", "子正壹刻", "子正贰刻", "子正叁刻" };
                        int[] mins = { 0, 15, 30, 45 };
                        System.Text.StringBuilder ksb = new System.Text.StringBuilder();
                        bool kok = true;
                        for (int i = 0; i < mins.Length; i++)
                        {
                            DateTime tt = new DateTime(2026, 11, 10, 0, mins[i], 0);
                            string got = GzCore.ShiChen(tt) + GzCore.KeNameOf(tt);
                            if (got != want[i]) kok = false;
                            ksb.Append("0:" + mins[i].ToString("00") + "=" + got + " ");
                        }
                        DateTime t1623 = new DateTime(2026, 11, 10, 16, 23, 0);
                        string s1623 = GzCore.ShiChen(t1623) + GzCore.KeNameOf(t1623);
                        if (s1623 != "申正壹刻") kok = false;
                        lg.AppendLine("刻制标定 0:00/0:15/0:30/0:45 -> " + ksb.ToString()
                                      + " 16:23=" + s1623 + " -> " + (kok ? "PASS" : "FAIL"));
                    }
                    // 1.5.1：五更标定 —— 戌一更 亥二更 子三更 丑四更 寅五更，白天无更
                    {
                        string gengs = "";
                        int gbad = 0;
                        for (int h = 0; h < 24; h++)
                        {
                            DateTime tt = new DateTime(2026, 11, 10, h, 30, 0);
                            string w;
                            if (h == 19 || h == 20) w = "一更";
                            else if (h == 21 || h == 22) w = "二更";
                            else if (h == 23 || h == 0) w = "三更";
                            else if (h == 1 || h == 2) w = "四更";
                            else if (h == 3 || h == 4) w = "五更";
                            else w = "";
                            if (GzCore.GengOf(tt) != w) gbad++;
                            if (GzCore.GengOf(tt).Length > 0) gengs += h.ToString("00") + ":" + GzCore.GengOf(tt) + " ";
                        }
                        lg.AppendLine("五更 " + gengs + "-> " + (gbad == 0 ? "PASS" : ("FAIL " + gbad)));
                    }
                    // 再算一遍四柱标定
                    {
                        WidgetForm wfo = new WidgetForm();
                        lg.AppendLine(wfo.RowOrder());
                        lg.AppendLine(wfo.GzMetrics());
                        try
                        {
                            string sheet = System.IO.Path.ChangeExtension(path, null) + "_刻制与五更时刻表.png";
                            wfo.GzRowSheet(sheet);
                            lg.AppendLine("行表=" + sheet);
                        }
                        catch (Exception ex) { lg.AppendLine("行表 ERR " + ex.Message); }
                        wfo.Dispose();
                    }
                    lg.AppendLine("24 小时时辰字样 = " + (bad == 0 ? "PASS" : ("FAIL " + bad)));
                }
                catch (Exception ex) { lg.AppendLine("ERR " + ex); }
                try { System.IO.File.WriteAllText(path, lg.ToString(), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=tm;<日志路径> —— 计时器键盘输入 / 命中 / 起停 / 到点 全跑一遍
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=dbl")) continue;
                string path = "dbl_selftest.txt";
                int semi = a.IndexOf(';');
                if (semi > 0) path = a.Substring(semi + 1);
                string log;
                try
                {
                    WidgetForm wf2 = new WidgetForm();
                    log = "== v1.5.1 double-click selftest ==\r\n" + wf2.DblClickSelfTest();
                    wf2.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(path, log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=detect;<日志路径>[;zh-CN|en-US] —— 「无键启动」的自动判定
            // 配套把环境变量 ABOUTTIME_ROOT 指到一个不存在的子键上，即模拟「第一次运行、注册表里什么都没有」。
            // 只读，全程不写用户的真实配置。
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=detect")) continue;
                string[] dparts = a.Substring("--selftest=detect".Length + 1).Split(';');
                string dpath = (dparts.Length > 0 && dparts[0].Trim().Length > 0) ? dparts[0].Trim() : "detect.txt";
                string dcul = (dparts.Length > 1 && dparts[1].Trim().Length > 0) ? dparts[1].Trim() : "";
                System.Text.StringBuilder db = new System.Text.StringBuilder();
                db.AppendLine("== 模拟无键启动（注册表子键 = " + Settings.RootPath + "）==");
                db.AppendLine("系统 UI 语言 = " + System.Globalization.CultureInfo.CurrentUICulture.Name);
                System.Globalization.CultureInfo oldCul = System.Threading.Thread.CurrentThread.CurrentUICulture;
                try
                {
                    if (dcul.Length > 0)
                    {
                        System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(dcul);
                        db.AppendLine("本组把 UI 语言临时改成 = " + dcul);
                    }
                    Settings st = Settings.Load();
                    db.AppendLine("→ 界面语言 Lang = " + st.Lang);
                    db.AppendLine("→ 农历 CalLunar = " + (st.CalLunar ? "开" : "关"));
                    db.AppendLine("→ 节日国别 CalCC = " + st.CalCC);
                    db.AppendLine("→ 干支计时 GzOn = " + (st.GzOn ? "开" : "关"));
                    db.AppendLine("→ 主题 Style = " + st.Style + "，透明 " + st.Opacity + "%，总在最前 " + (st.Topmost ? "是" : "否")
                + "，磨砂 " + (st.Blur ? ("开（" + st.BlurLevel + " 档）") : "关"));
                    db.AppendLine("→ 系统时区是否中国 IsChinaZone = " + (Settings.IsChinaZone() ? "是" : "否"));
                }
                catch (Exception ex) { db.AppendLine("ERR " + ex); }
                finally { System.Threading.Thread.CurrentThread.CurrentUICulture = oldCul; }
                try { System.IO.File.WriteAllText(dpath, db.ToString(), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=menu;<日志路径> —— 右键菜单冷建耗时（子菜单首次悬停卡顿的量化）
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=menu")) continue;
                string path = "menu_selftest.txt";
                int semi = a.IndexOf(';');
                if (semi > 0) path = a.Substring(semi + 1);
                string log;
                try
                {
                    WidgetForm wfm = new WidgetForm();
                    wfm.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(400);
                    Application.DoEvents();
                    System.Diagnostics.Stopwatch pb = System.Diagnostics.Stopwatch.StartNew();
                    for (int k = 0; k < 30; k++) wfm.PaintFrameForTest();
                    pb.Stop();
                    double paintMs = pb.ElapsedMilliseconds / 30.0;
                    pb.Reset(); pb.Start();
                    for (int k = 0; k < 10; k++) wfm.RenderStaticForTest();
                    pb.Stop();
                    double staticMs = pb.ElapsedMilliseconds / 10.0;
                    wfm.Hide();
                    log = "== v1.6 menu bench ==" + Environment.NewLine
                        + "推一帧（全卡逐像素 blit）=" + paintMs.ToString("0.0") + "ms  → 16ms 定时器占空 " + (paintMs / 16.0 * 100).ToString("0") + "%" + Environment.NewLine
                        + "重做静态层 RenderStatic=" + staticMs.ToString("0.0") + "ms" + Environment.NewLine
                        + wfm.MenuBench()
                        + Environment.NewLine + "== 悬停（子菜单 Show）==" + Environment.NewLine + wfm.MenuHoverBench();
                    wfm.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(path, log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=lang;<前缀> —— 走真实的「切语言」路径，核对时钟管理弹窗是否跟着变英文
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=lang")) continue;
                string stem = "lang_selftest";
                string[] lparts = a.Substring("--selftest=lang".Length + 1).Split(';');
                if (lparts.Length > 0 && lparts[0].Trim().Length > 0) stem = lparts[0].Trim();
                string log = "";
                try
                {
                    WidgetForm wf = new WidgetForm();
                    log = wf.LangSwitchSelfTest(stem);
                    wf.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(stem + ".txt", log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=blur;<前缀> —— 磨砂实机验证：真显示窗口，抓桌面截图 + 存磨砂底图 + 抓图日志
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=blur")) continue;
                string[] bparts = a.Substring("--selftest=blur".Length + 1).Split(';');
                string stem = (bparts.Length > 0 && bparts[0].Trim().Length > 0) ? bparts[0].Trim() : "blur_selftest";
                string theme = "";
                int bop = 0;
                int blv = 0;                    // 1.8.5：;lv=N —— 磨砂强度档位 1~5（缺省 3 = 标准档）
                bool blurOff = false;           // 参照档：同一位置、同一壁纸，但磨砂关掉
                for (int bi = 1; bi < bparts.Length; bi++)
                {
                    string bt = bparts[bi].Trim();
                    if (bt == "dark" || bt == "white" || bt == "navy") theme = bt;
                    else if (bt == "off") blurOff = true;
                    else if (bt.StartsWith("op=")) { int.TryParse(bt.Substring(3), out bop); }
                    else if (bt.StartsWith("lv=")) { int.TryParse(bt.Substring(3), out blv); }
                }
                string lg = "";
                WidgetForm wfb = null;
                try
                {
                    wfb = new WidgetForm();
                    if (theme.Length > 0) wfb.SetPreviewStyle(theme == "white" ? 1 : (theme == "navy" ? 2 : 0));
                    if (bop > 0) wfb.SetPreviewOpacity(bop);
                    if (!blurOff)
                    {
                        wfb.PreviewBlurOn();
                        if (blv >= 1 && blv <= 5) wfb.SetBlurLevel(blv);   // 档位要在磨砂打开后设置
                    }
                    else
                    {
                        // 1.9：off 档必须真关。老版本只跳过 PreviewBlurOn，而 SetBlurLevel 早把 Blur=1
                        // 落进了测试键（只要本次运行里先跑过任一档）—— 于是「关闭」参照其实是开着磨砂的，
                        // 白化量 / 纹理保留率的对照全部失真。这里显式置 0，再走一次预览刷新。
                        wfb.SetBlurLevel(0);
                    }
                    wfb.ForceTopForTest();     // 仅为了抓图时不被别的窗口挡住，抓完就 Dispose，不影响产品行为
                    wfb.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(800);       // 等壁纸层抓取与首帧上屏
                    Application.DoEvents();
                    Rectangle bb = wfb.Bounds;
                    int pad = 26;
                    Rectangle cap = new Rectangle(bb.X - pad, bb.Y - pad, bb.Width + pad * 2, bb.Height + pad * 2);
                    // 抓法说明（12.13）：直接 CopyFromScreen 会被此刻的前台窗口挡住（抓到 IDE 或聊天窗），
                    // 所以改成「先把挂件藏起来抓它身下的真桌面，再把挂件自己合成的整帧按 alpha 叠上去」。
                    // 这和分层窗口 What you see 完全等价，且不受 z-order 影响。
                    Bitmap comp = wfb.CompositeBmpForTest();
                    // 1.7：磨砂底图必须在 Hide() 之前取 —— 1.6 在 Hide 后调 FrostBmpForTest，
                    // !Visible 直接返回 null，日志里永远「磨砂底图=null」，与 LastLog 自相矛盾。
                    Bitmap fb = wfb.FrostBmpForTest();
                    wfb.Hide();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(220);       // 等桌面重绘（壁纸层没被挂件压住）
                    Application.DoEvents();
                    using (Bitmap scr = new Bitmap(cap.Width, cap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Graphics gs = Graphics.FromImage(scr))
                            gs.CopyFromScreen(cap.Location, Point.Empty, cap.Size);
                        // 抓图原档留一份：和 _comp.png 同一区域逐像素对比，就是「壁纸到底有没有被糊」的硬证据
                        scr.Save(stem + "_bg.png", System.Drawing.Imaging.ImageFormat.Png);

                        // 1.7 两个自动验收指标（审阅报告 3.5）：必须在叠合成帧之前、在同一卡片条带上
                        //（取卡片左内边 x≈2.5%、竖向 25%~85% 的无文字区）对比「合成帧 vs 原壁纸」——
                        //   白化量 = 卡片均值 − 壁纸均值（磨砂开的白化程度；目标 ≤ 15 灰阶）
                        //   纹理保留率 = 卡片水平梯度 / 壁纸水平梯度（目标 20%~35%；
                        //               <10% 像纯色板，>50% 看不出糊）
                        if (comp != null)
                        {
                            int ox = pad + (bb.Width - comp.Width) / 2, oy = pad + (bb.Height - comp.Height) / 2;
                            int sx = ox + (int)Math.Round(comp.Width * 0.025);
                            int sw = 4;
                            int sy0 = oy + (int)Math.Round(comp.Height * 0.25);
                            int sy1 = oy + (int)Math.Round(comp.Height * 0.85);
                            if (sx + sw + 1 < scr.Width && sy0 + 2 < sy1 && sy1 < scr.Height)
                            {
                                double m1 = 0, m2 = 0, h1 = 0, h2 = 0;
                                int n = 0;
                                for (int y = sy0; y < sy1; y += 2)
                                    for (int x = sx; x < sx + sw; x++)
                                    {
                                        // 合成帧是预乘 alpha 的分层帧：必须按 alpha 叠到壁纸上才是屏幕上看到的颜色
                                        //（磨砂层 alpha=255，实心卡 alpha=op —— 1.7 之前直接把 RGB 当不透明读，
                                        // 磨砂关那一档会读到 20 而不是 70 左右的真实观感）
                                        double v1 = Over(comp.GetPixel(x - ox, y - oy), scr.GetPixel(x, y));
                                        double v2 = v1 - Over(comp.GetPixel(x - ox + 2, y - oy), scr.GetPixel(x + 2, y));
                                        double w1 = Luma(scr.GetPixel(x, y));
                                        double w2 = w1 - Luma(scr.GetPixel(x + 2, y));
                                        m1 += v1; h1 += Math.Abs(v2);
                                        m2 += w1; h2 += Math.Abs(w2);
                                        n++;
                                    }
                                if (n > 0)
                                {
                                    m1 /= n; m2 /= n; h1 /= n; h2 /= n;
                                    lg += "白化量(卡片-壁纸均值)=" + (m1 - m2).ToString("F1")
                                        + " 灰阶  纹理保留率=" + (h2 > 0.0001 ? (h1 / h2).ToString("P0") : "n/a")
                                        + "（卡片HF=" + h1.ToString("F3") + " 壁纸HF=" + h2.ToString("F3") + "）\r\n";
                                }
                            }
                        }

                        using (Graphics gs = Graphics.FromImage(scr))
                        {
                            if (comp != null)
                            {
                                gs.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                                gs.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                                gs.DrawImage(comp, new Rectangle(pad + (bb.Width - comp.Width) / 2,
                                                                 pad + (bb.Height - comp.Height) / 2,
                                                                 comp.Width, comp.Height));
                            }
                        }
                        scr.Save(stem + "_desk.png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    lg += "窗口 bounds=" + bb + "\r\n";
                    lg += "桌面截图区域=" + cap + "\r\n";
                    lg += "合成帧=" + (comp == null ? "null" : (comp.Width + "x" + comp.Height + "  " + comp.PixelFormat)) + "\r\n";
                    if (comp != null) { comp.Save(stem + "_comp.png", System.Drawing.Imaging.ImageFormat.Png); comp.Dispose(); }

                    lg += "磨砂底图=" + (fb == null ? "null（退回实心卡片）" : (fb.Width + "x" + fb.Height + "  " + fb.PixelFormat)) + "\r\n";
                    lg += "W.GetLastLog=" + WallpaperFrost.LastLog + "\r\n";
                    if (fb != null) fb.Save(stem + "_frost.png", System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception ex) { lg += "ERR " + ex + "\r\n"; }
                finally
                {
                    try { System.IO.File.WriteAllText(stem + ".txt", lg, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                    if (wfb != null) { try { wfb.Dispose(); } catch (Exception) { } }
                }
                return;
            }

            // 自检：--selftest=frost;<日志路径> —— 换壁纸后磨砂是否重抓（1.8.2 回归）。
            // 做法：构造挂件并开磨砂 → 记下整幅抓取次数 → 改动壁纸文件的修改时间（视觉不变，
            // 但指纹变）→ 抽消息泵最多 8 秒，看抓取次数是否增加 → 还原修改时间。
            // 刻意**不**改系统壁纸设置：那会打断用户的幻灯片轮换，且不便还原。
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=frost")) continue;
                string fpath = a.Substring("--selftest=frost".Length + 1).Trim();
                if (fpath.Length == 0) fpath = "frost_selftest.txt";
                System.Text.StringBuilder lgf = new System.Text.StringBuilder();
                WidgetForm wff = null;
                try
                {
                    wff = new WidgetForm();
                    wff.PreviewBlurOn();
                    wff.ForceTopForTest();
                    wff.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(1200);        // 等首帧整幅抓取
                    Application.DoEvents();
                    int c1 = WallpaperFrost.CaptureCount;
                    lgf.Append("S0 首帧后 抓取次数=").Append(c1).Append("\r\n");
                    string sig = WallpaperFrost.WallpaperSignature();
                    lgf.Append("S1 指纹=").Append(sig).Append("\r\n");
                    // 2.1.1：指纹改成「Themes 全部转码文件 + spi 段」，路径不再是第一段，
                    // 改从 spi= 之后取一个真实存在的文件来 touch
                    string wp = "";
                    int si = sig.IndexOf("spi=", StringComparison.Ordinal);
                    if (si >= 0)
                    {
                        wp = sig.Substring(si + 4);
                        int bar = wp.IndexOf('|'); if (bar >= 0) wp = wp.Substring(0, bar);
                        int semi = wp.IndexOf(';'); if (semi >= 0) wp = wp.Substring(0, semi);
                    }
                    if (sig == "?" || wp.Length == 0 || !System.IO.File.Exists(wp))
                    {
                        lgf.Append("FAIL 取不到壁纸文件路径，无法做指纹变更测试\r\n");
                    }
                    else
                    {
                        wff.CheckWallpaperNowForTest();                // 先登记基线（否则第一次检查会落在改动之后）
                        lgf.Append("S1b 基线已登记，检查次数=").Append(wff.WallChecks).Append("\r\n");
                        System.IO.FileInfo fi = new System.IO.FileInfo(wp);
                        DateTime old = fi.LastWriteTimeUtc;
                        fi.LastWriteTimeUtc = old.AddMinutes(5);       // 只说“文件被重写过”
                        lgf.Append("S2 已改动壁纸文件时间戳，新指纹=").Append(WallpaperFrost.WallpaperSignature()).Append("\r\n");
                        int c2 = c1;
                        for (int i = 0; i < 16; i++)
                        {
                            System.Threading.Thread.Sleep(300);
                            wff.CheckWallpaperNowForTest();           // 同步核对，不依赖定时器心跳
                            Application.DoEvents();
                            c2 = WallpaperFrost.CaptureCount;
                            if (c2 > c1) break;
                        }
                        fi.LastWriteTimeUtc = old;                     // 还原
                        lgf.Append("S3 重抓后 抓取次数=").Append(c2)
                           .Append(" 指纹检查=").Append(wff.WallChecks)
                           .Append(" 命中=").Append(wff.WallHits).Append("\r\n");
                        lgf.Append("S4 当前指纹=").Append(WallpaperFrost.WallpaperSignature()).Append("\r\n");
                        lgf.Append((c2 > c1 ? "PASS" : "FAIL") + " 换壁纸后磨砂重抓\r\n");
                    }
                    lgf.Append("抓取日志：" + WallpaperFrost.LastLog + "\r\n");
                }
                catch (Exception fex) { lgf.Append("ERR ").Append(fex).Append("\r\n"); }
                finally
                {
                    try { System.IO.File.WriteAllText(fpath, lgf.ToString(), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                    if (wff != null) { try { wff.Dispose(); } catch (Exception) { } }
                }
                return;
            }

            // 自检：--selftest=mem;<日志路径>[;steps=N] —— 内存构成探针（1.8.1）。
            // 按真实路径走（数据表加载 → 窗口 → 磨砂抓取 → N 轮 RenderStatic/PaintFrame），
            // 每阶段打印 私有字节 / 工作集 / 托管堆 / GC 代数 / GDI·USER 句柄数，
            // 用差分定位「实机 90~110MB」里每一层各占多少，避免再拿自检短时口径当稳态数。
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=mem")) continue;
                string[] mparts = a.Substring("--selftest=mem".Length + 1).Split(';');
                string mpath = (mparts.Length > 0 && mparts[0].Trim().Length > 0) ? mparts[0].Trim() : "mem_selftest.txt";
                int msteps = 60;
                if (mparts.Length > 1) { int.TryParse(mparts[1], out msteps); }
                if (msteps < 10) msteps = 10;
                int idleSec = 0;                        // ;idle=N —— 不跑合成循环，让挂件自己的 OnTick 真实跑 N 秒
                bool mworld = false;                    // ;world=1 —— 演示世界全表「加载→卸载」的内存进出（1.8.4）
                for (int mi = 1; mi < mparts.Length; mi++)
                {
                    string mt = mparts[mi].Trim();
                    if (mt.StartsWith("idle=")) { int.TryParse(mt.Substring(5), out idleSec); }
                    if (mt == "world=1") mworld = true;
                }
                System.Text.StringBuilder lgm = new System.Text.StringBuilder();
                WidgetForm wfm = null;
                try
                {
                    // 每阶段立即落盘：上一版探针卡死在哪一步无从知晓，改成边跑边写
                    Action<string> snap = delegate(string tag)
                    {
                        System.Diagnostics.Process cp = System.Diagnostics.Process.GetCurrentProcess();
                        cp.Refresh();
                        lgm.Append(tag)
                           .Append(": Private=").Append((cp.PrivateMemorySize64 / 1048576.0).ToString("F1"))
                           .Append("MB WS=").Append((cp.WorkingSet64 / 1048576.0).ToString("F1"))
                           .Append("MB 托管堆=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1"))
                           .Append("MB GC(0/1/2)=").Append(GC.CollectionCount(0)).Append('/')
                           .Append(GC.CollectionCount(1)).Append('/').Append(GC.CollectionCount(2))
                           .Append(" GDI对象=").Append(Dpi.MemProbe_GetGuiResources(Dpi.MemProbe_GetCurrentProcess(), 0))
                           .Append(" USER对象=").Append(Dpi.MemProbe_GetGuiResources(Dpi.MemProbe_GetCurrentProcess(), 1))
                           .Append("\r\n");
                        try { System.IO.File.WriteAllText(mpath, lgm.ToString(), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                    };
                    snap("S0 进程起步");
                    wfm = new WidgetForm();
                    snap("S1 数据表加载 + 窗口构造");
                    wfm.PreviewBlurOn();                     // 真实路径：磨砂开
                    wfm.ForceTopForTest();
                    wfm.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(1200);     // 等磨砂整幅抓取 + 1/4 缓存 + 首帧
                    Application.DoEvents();
                    snap("S2 首帧 + 磨砂整幅抓取后");
                    if (mworld)
                    {
                        TimeZoneMatcher.EnsureWorld();
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        System.Threading.Thread.Sleep(200); Application.DoEvents();
                        snap("S2b 世界全表加载后");
                        TimeZoneMatcher.TrimIfIdle(true);
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        System.Threading.Thread.Sleep(200); Application.DoEvents();
                        snap("S2c 闲置卸载后");
                    }
                    if (idleSec > 0)
                    {
                        // idle 模式：完全交给挂件自己的 OnTick（含 16ms 滚屏帧），只做旁观采样
                        for (int isec = 0; isec < idleSec; isec += 10)
                        {
                            System.Threading.Thread.Sleep(10000);
                            Application.DoEvents();
                            snap("T+" + (isec + 10) + "s（真实 OnTick 运行中）");
                        }
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        System.Threading.Thread.Sleep(400);
                        Application.DoEvents();
                        snap("S4 idle " + idleSec + "s + GC.Collect 后");
                    }
                    for (int i = 0; i < msteps; i++)
                    {
                        wfm.RenderStaticForTest();
                        wfm.PaintFrameForTest();
                        if ((i % 10) == 9)
                        {
                            Application.DoEvents();
                            if ((i % 30) == 29) snap("S3 进行中 i=" + (i + 1) + "/" + msteps);
                        }
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    System.Threading.Thread.Sleep(400);
                    Application.DoEvents();
                    snap("S4 " + msteps + " 轮 RenderStatic+PaintFrame + GC.Collect 后");
                    lgm.Append("窗口=").Append(wfm.Bounds).Append("\r\n");
                    lgm.Append("磨砂=").Append(wfm.FrostBmpForTest() == null ? "null" : "on").Append("\r\n");
                    lgm.Append("口径说明：Private 是任务管理器「内存」的近亲；托管堆是 GC 看到的；")
                       .Append("S4 与 S2 的差 = 渲染路径是否在留东西（正常应 ≈0）\r\n");
                }
                catch (Exception mex) { lgm.Append("ERR ").Append(mex).Append("\r\n"); }
                finally
                {
                    try { System.IO.File.WriteAllText(mpath, lgm.ToString(), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                    if (wfm != null) { try { wfm.Dispose(); } catch (Exception) { } }
                }
                return;
            }

            // 自检：--selftest=city;<日志路径> —— 1.9「匹配结束即回收」的内存进出（用户实测的
            // 「添加地点后内存居高不下」就是这条路径）。跑法：构造挂件 → 6 城补国名（模拟添加）
            // → 世界全表加载（模拟点匹配）→ ReleaseAfterMatch（关闭对话框）→ 再来一轮验证不累积。
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=city")) continue;
                string cpath = "city_selftest.txt";
                int semi2 = a.IndexOf(';');
                if (semi2 > 0) cpath = a.Substring(semi2 + 1);
                System.Text.StringBuilder lgc = new System.Text.StringBuilder();
                WidgetForm wfc = null;
                try
                {
                    Action<string> snapc = delegate(string tag)
                    {
                        System.Diagnostics.Process cp = System.Diagnostics.Process.GetCurrentProcess();
                        cp.Refresh();
                        lgc.Append(tag)
                            .Append(": Private=").Append((cp.PrivateMemorySize64 / 1048576.0).ToString("F1"))
                            .Append("MB WS=").Append((cp.WorkingSet64 / 1048576.0).ToString("F1"))
                            .Append("MB 托管堆=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1"))
                            .Append("MB\r\n");
                        try { System.IO.File.WriteAllText(cpath, lgc.ToString(), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                    };
                    snapc("C0 进程起步");
                    wfc = new WidgetForm();
                    snapc("C1 构造完成（Settings.Load 已批量补国名，只扫一趟）");

                    // 先校验：老路径（全量索引）能算对的国家码/州码，新路径必须一模一样。
                    // 注：worldcities3.txt 里纽约写作「New York City」，用户配置里的 En 是「New York」，
                    // 精确匹配找不到 —— 这是 1.8.5 起就有的老现象（不是 1.9 引入的），此处按表内真名校验。
                    string[][] seeded = new string[][]
                    {
                        new string[] { "東京", "Tokyo", "Tokyo Standard Time" },
                        new string[] { "倫敦", "London", "GMT Standard Time" },
                        new string[] { "紐約", "New York City", "Eastern Standard Time" },
                    };
                    CityDb.BeginGeoBatch();
                    System.Collections.Generic.List<City> seedc = new System.Collections.Generic.List<City>();
                    foreach (string[] sv in seeded)
                    {
                        City c0 = new City(sv[0], sv[1], sv[2], "");
                        seedc.Add(c0);
                        CityDb.AttachGeo(c0);                 // 批量模式：只登记
                    }
                    CityDb.CommitGeoBatch();                  // 一趟解析后回填
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();   // 把这趟的临时垃圾清掉，别污染后面的采样
                    foreach (City c0 in seedc)
                        lgc.Append("  校验 ").Append(c0.En).Append(" → CC=").Append(c0.CC)
                           .Append(" A1=").Append(c0.A1).Append(" CC3=").Append(c0.CC3).Append("\r\n");
                    lgc.Append("  期望：Tokyo=JP / London=GB·ENG / New York City=US·NY·USA\r\n");

                    wfc.PreviewBlurOn();
                    wfc.ForceTopForTest();
                    wfc.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(1200);
                    Application.DoEvents();
                    snapc("C2 首帧 + 磨砂整幅抓取后");

                    // 场景 A：一次给 6 个城市补码（批量模式，一趟解析）
                    string[] cnames = new string[] { "Springfield", "Portland", "Victoria", "Hamilton", "Newcastle", "Bristol" };
                    System.Collections.Generic.List<City> addc = new System.Collections.Generic.List<City>();
                    foreach (string n in cnames) addc.Add(new City(n, n, "", ""));
                    CityDb.BeginGeoBatch();
                    foreach (City c in addc) CityDb.AttachGeo(c);        // 只登记
                    System.Diagnostics.Stopwatch swc = System.Diagnostics.Stopwatch.StartNew();
                    CityDb.CommitGeoBatch();                            // 一趟解析补齐 6 个
                    swc.Stop();
                    lgc.Append("  → 6 城批量补码耗时 ").Append(swc.Elapsed.TotalMilliseconds.ToString("F1"))
                       .Append("ms；命中缓存条数=").Append(CityDb.GeoHitCount).Append("\r\n");
                    snapc("C3 6 城批量补码后");

                    // 场景 B：真实「添加一个地点」= 单城一趟（老版本同样是单城一趟，第 7 个才建全量索引）
                    City gva = new City("日內瓦", "Geneva", "W. Europe Standard Time", "");
                    System.Diagnostics.Stopwatch sw1 = System.Diagnostics.Stopwatch.StartNew();
                    CityDb.AttachGeo(gva);
                    sw1.Stop();
                    lgc.Append("  → 单城添加耗时 ").Append(sw1.Elapsed.TotalMilliseconds.ToString("F1"))
                       .Append("ms；结果 CC=").Append(gva.CC).Append(" A1=").Append(gva.A1).Append("\r\n");
                    snapc("C3b 单城添加后（一次真实点击的开销）");

                    TimeZoneMatcher.EnsureWorld();                  // 用户点「匹配」：世界全表必须加载
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    System.Threading.Thread.Sleep(200);
                    Application.DoEvents();
                    snapc("C4 匹配时（世界全表已加载，这是峰值）");

                    CityDb.ReleaseAfterMatch();                     // 关闭「添加地点」对话框的收尾
                    System.Threading.Thread.Sleep(200);
                    Application.DoEvents();
                    snapc("C5 关闭对话框后（应回落到 C3 附近）");

                    TimeZoneMatcher.EnsureWorld();                  // 第二轮：确认可重复回收、不累积
                    CityDb.ReleaseAfterMatch();
                    System.Threading.Thread.Sleep(200);
                    Application.DoEvents();
                    snapc("C6 第二轮 匹配→关闭（应与 C5 持平）");
                    lgc.Append("判定：C5−C4 为负且幅度 ≥ 世界全表体积，C6−C5 ≈ 0 → 匹配数据在匹配结束后确实归还。\r\n");
                }
                catch (Exception cex) { lgc.Append("ERR ").Append(cex).Append("\r\n"); }
                finally
                {
                    try { System.IO.File.WriteAllText(cpath, lgc.ToString(), new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                    if (wfc != null) { try { wfc.Dispose(); } catch (Exception) { } }
                }
                return;
            }

            // 自检：--selftest=calpos;<日志路径> —— 日历常挂四档锚点 × 主时钟开/关，核对最终行序
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=calpos")) continue;
                string path = "calpos_selftest.txt";
                string[] sa = a.Split(';');
                if (sa.Length > 1 && sa[1].Length > 0) path = sa[1];
                string log = "";
                try
                {
                    WidgetForm wf = new WidgetForm();
                    wf.SetPreviewMode(1);                     // 日历改常挂（占一行）
                    string[] an = { "全表最上", "主时钟上方", "主时钟下方", "全表最下" };
                    for (int main = 1; main >= 0; main--)
                    {
                        for (int x = 0; x <= 3; x++)
                        {
                            wf.SetPreviewCalAnchor(x, main == 1);
                            log += "主时钟" + (main == 1 ? "开" : "关") + " · 锚点" + x + "（" + an[x] + "）→ "
                                 + wf.RowOrder() + "\r\n";
                        }
                    }
                    log += "行序字母：L=主时钟 C=日历 W=世界城市 T=倒计时/秒表 G=干支\r\n";
                    log += "期望：锚点0 时 C 在首位；锚点1 时 C 紧邻 L 之前；锚点2 时 C 紧邻 L 之后；锚点3 时 C 在末位。\r\n";
                    log += "      主时钟关闭时：锚点1 等同锚点0（C 在首位）、锚点2 等同锚点3（C 在末位）。\r\n";
                    wf.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(path, log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=cn;<日志路径>[;<png前缀>] —— 2.0 中文皮肤：导出排版中间量 + 出图
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=cn")) continue;
                string[] sa = a.Split(';');
                string path = (sa.Length > 1 && sa[1].Length > 0) ? sa[1] : "cn_selftest.txt";
                string png = (sa.Length > 2 && sa[2].Length > 0) ? sa[2] : null;
                string log = "";
                try
                {
                    WidgetForm wf = new WidgetForm();
                    wf.SetPreviewSkin(1);
                    if (png != null) wf.SnapshotSave(png + "_dark.png", false);
                    log = WidgetForm.CnDebug + "\r\n";
                    wf.SetPreviewStyle(1);
                    wf.SetPreviewSkin(1);
                    if (png != null) wf.SnapshotSave(png + "_white.png", true);
                    log += WidgetForm.CnDebug;
                    wf.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(path, log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检：--selftest=tm;<日志路径> —— 计时器键盘输入 / 命中 / 起停 / 到点 全跑一遍
            foreach (string a in args)
            {
                if (!a.StartsWith("--selftest=tm")) continue;
                string path = "timer_selftest.txt";
                int semi = a.IndexOf(';');
                if (semi > 0) path = a.Substring(semi + 1);
                string log = "";
                try
                {
                    WidgetForm wf1 = new WidgetForm();
                    log = wf1.SelfTest();
                    wf1.Dispose();
                }
                catch (Exception ex) { log = "ERR " + ex; }
                try { System.IO.File.WriteAllText(path, log, new System.Text.UTF8Encoding(true)); } catch (Exception) { }
                return;
            }

            // 自检出图：--shot=<路径>[;white][;ym=YYYY-MM[-DD]][;mp][;pp][;sc=1.0][;mode=1|2]
            string shotPath = null;
            bool shotWhite = false, shotMonthPopup = false, shotPopWin = false, shotMenu = false;
            bool shotLunOff = false;   // 出图时关农历（看英文版默认不显示农历的样子）
            bool shotTop = false;           // 菜单出图时按「总在最前」状态渲染（看磨砂项是否置灰）
            int shotTheme = -1;   // -1 跟随配置 / 0 碳灰 / 1 雪白
            string shotLang = "";
            string shotCC = "";   // v2.1 出图用：强制国家码，验证四种提示口径
            double shotScale = 0.0;
            int shotMode = -1;
            int shotY = 0, shotM = 0, shotD = 0;
            DateTime? shotToday = null;   // 1.6：把「今天」定到某天，专看节日小字/日历高亮
            int shotAtH = -1, shotAtM = 0;   // v2.1 修（审阅 P2-5）：at= 的时分先存着，等 today= 解析完再一起定
            string shotTimer = "";
            bool shotTimerPopup = false;
            int shotGz = -1;                 // -1 跟随配置 / 0 关 / 1 开
            int shotTimerOn = -1;            // -1 跟随配置 / 0 关 / 1 开
            bool shotGzPopup = false;
            bool shotClockMgr = false;      // 额外把「时钟管理」对话框画一张
            int shotSkin = -1;              // 2.0：主时钟皮肤 -1 跟随配置 / 0 原样 / 1 中文
            int shotClockOff = 0;           // 位掩码：1 主时钟 / 2 倒计时 / 4 干支 视作关闭
            foreach (string a in args)
            {
                if (!a.StartsWith("--shot=")) continue;
                foreach (string opt in a.Substring(7).Split(';'))
                {
                    string o = opt.Trim();
                    if (o.Length == 0) continue;
                    if (o == "white") { shotWhite = true; shotTheme = 1; }
                    else if (o == "dark") { shotWhite = false; shotTheme = 0; }
                    else if (o == "navy") { shotWhite = false; shotTheme = 2; }
                    else if (o == "mp") shotMonthPopup = true;
                    else if (o == "pp") shotPopWin = true;
                    else if (o == "menu") shotMenu = true;
                    else if (o == "top") shotTop = true;
                    else if (o == "lunoff") shotLunOff = true;
                    else if (o.StartsWith("sc="))
                    {
                        double.TryParse(o.Substring(3), out shotScale);
                        if (shotScale < 0.6 || shotScale > 1.2) shotScale = 0;
                    }
                    else if (o.StartsWith("mode="))
                    {
                        int.TryParse(o.Substring(5), out shotMode);
                        if (shotMode < 0 || shotMode > 2) shotMode = -1;
                    }
                    else if (o.StartsWith("lang=")) shotLang = o.Substring(5).Trim();
                    else if (o.StartsWith("cc=")) shotCC = o.Substring(3).Trim();
                    else if (o.StartsWith("tm=")) shotTimer = o.Substring(3).Trim();
                    else if (o == "tp") shotTimerPopup = true;
                    else if (o == "gz") shotGz = 1;
                    else if (o == "gzoff") shotGz = 0;
                    else if (o == "tmoff") shotTimerOn = 0;
                    else if (o == "tmon") shotTimerOn = 1;
                    else if (o == "gzpop") shotGzPopup = true;
                    else if (o == "cm") shotClockMgr = true;
                    else if (o.StartsWith("skin=")) { int.TryParse(o.Substring(5), out shotSkin); if (shotSkin < 0 || shotSkin > 1) shotSkin = -1; }
                    else if (o.StartsWith("at="))
                    {
                        // 2.0：把主时钟时刻定死（HH:MM），专看出图时的大字排布
                        string[] hm = o.Substring(3).Split(':');
                        try
                        {
                            int h = int.Parse(hm[0]), mi = int.Parse(hm[1]);
                            if (h >= 0 && h < 24 && mi >= 0 && mi < 60) { shotAtH = h; shotAtM = mi; }
                        }
                        catch (Exception) { }
                    }
                    else if (o == "cmx") { shotClockMgr = true; shotClockOff = 6; }
                    else if (o.StartsWith("today="))
                    {
                        string[] td = o.Substring(6).Split('-');
                        try
                        {
                            int y = int.Parse(td[0]), mo = int.Parse(td[1]), dd = int.Parse(td[2]);
                            shotToday = new DateTime(y, mo, dd);
                            if (shotY == 0) { shotY = y; shotM = mo; shotD = dd; }
                        }
                        catch (Exception) { }
                    }
                    else if (o.StartsWith("ym="))
                    {
                        string[] ym = o.Substring(3).Split('-');
                        if (ym.Length == 2) { int.TryParse(ym[0], out shotY); int.TryParse(ym[1], out shotM); }
                        else if (ym.Length == 3)
                        {
                            int.TryParse(ym[0], out shotY); int.TryParse(ym[1], out shotM); int.TryParse(ym[2], out shotD);
                        }
                    }
                    else shotPath = o;
                }
            }
            if (shotPath != null)
            {
                try
                {
                    WidgetForm wf = new WidgetForm();
                    if (shotScale > 0) wf.SetPreviewScale(shotScale);
                    if (shotToday != null) WidgetForm.PreviewDay = shotToday;
                    // v2.1 修（审阅 P2-5）：at= 原来把日期写死成 2000-01-01，于是出图时
                    // 右侧日期块显示「2000 / 01 M / 01 D」，与 today= 指定的日子对不上，
                    // 星期的淡橙高亮也对不上真实日期。现在两参数合流：
                    // 日期取 today=（没给就用真实今天），时刻取 at=。
                    if (shotAtH >= 0)
                        WidgetForm.PreviewClock =
                            (shotToday ?? DateTime.Today).Date.AddHours(shotAtH).AddMinutes(shotAtM);
                    if (shotMode >= 0) wf.SetPreviewMode(shotMode);
                    if (shotTop) wf.SetPreviewTopmost(true);
                    if (shotLunOff) wf.SetPreviewLunar(false);
                    if (shotTheme == 1) wf.SetPreviewStyle(1);
                    else if (shotTheme == 2) wf.SetPreviewStyle(2);
                    else if (shotTheme == 0) wf.SetPreviewStyle(0);
                    if (shotLang.Length > 0) wf.SetPreviewLang(shotLang);
                    if (shotY > 0 && shotM > 0) wf.SetPreviewMonth(shotY, shotM);
                    if (shotTimer.Length > 0) wf.SetPreviewTimer(shotTimer);
                    if (shotGz >= 0) wf.SetPreviewGz(shotGz == 1);
                    if (shotSkin >= 0) wf.SetPreviewSkin(shotSkin);
                    if (shotCC.Length == 2) wf.SetPreviewCC(shotCC);
                    if (shotTimerOn >= 0) wf.SetPreviewTimerOn(shotTimerOn == 1);
                    if (shotGzPopup)
                        wf.GzPopupSnapshotSave(shotPath.Replace(".png", "") + "_gz.png");
                    if (shotTimerPopup)
                        wf.TimerPopupSnapshotSave(shotPath.Replace(".png", "") + "_timer.png", shotWhite);
                    wf.SnapshotSave(shotPath, shotWhite);
                    // 2.0：中文皮肤出图时顺手把排版量值存成旁边的 .cn.txt，省得再猜坐标
                    if (WidgetForm.CnDebug.Length > 0)
                    {
                        try { System.IO.File.WriteAllText(shotPath + ".cn.txt", WidgetForm.CnDebug, System.Text.Encoding.UTF8); }
                        catch (Exception) { }
                    }
                    if (shotPopWin)      // 额外把"弹出式日历"整卡离屏画一张
                    {
                        string pw = shotPath.Replace(".png", "") + "_calwin.png";
                        wf.PopupWindowSnapshotSave(pw, shotWhite);
                    }
                    if (shotD > 0)       // 额外把当日详情浮层离屏画一张
                    {
                        DateTime day = new DateTime(shotY, shotM, shotD);
                        string pp = shotPath.Replace(".png", "") + "_popup.png";
                        wf.PopupSnapshotSave(pp, day);
                    }
                    if (shotMonthPopup)  // 额外把世界行整月小日历画一张
                    {
                        string mp = shotPath.Replace(".png", "") + "_month.png";
                        wf.MonthPopupSnapshotSave(mp, shotY, shotM);
                    }
                    if (shotMenu)        // 额外把右键菜单离屏画出来
                        wf.MenuSnapshotSave(shotPath.Replace(".png", ""));
                    if (shotClockMgr)   // 额外把「时钟管理」对话框离屏画一张
                        wf.ClockManagerSnapshotSave(shotPath.Replace(".png", "") + "_cmgr.png", shotClockOff);
                    wf.Dispose();
                }
                catch (Exception ex)
                {
                    try
                    {
                        System.IO.File.WriteAllText(shotPath + ".err.txt", ex.ToString());
                    }
                    catch (Exception) { }
                }
                return;
            }

            // 1.8.1：地名库不再启动预热 —— worldcities3.txt（5.5 万条）解析后占 ~38MB 托管内存，
            // 而它只在用户打开「添加地点」做搜索时才用得上。现在改为 DoMatch 里后台懒加载
            //（见 Support.cs 上文 ⓪），滚屏 / 磨砂 / 日历用户从此不再背这块内存。
            // 首次打开添加地点会多等约 1~2 秒（仅第一次），换取常驻内存降 1/3。

            try
            {
                WidgetForm f = new WidgetForm();
                Application.Run(f);
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序异常退出：\r\n" + ex.Message, "About Time",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
