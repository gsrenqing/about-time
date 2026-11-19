// About Time 1.2 —— 日历模块：农历 / 节气 / 节日规则 / 月历模型 / 当日详情浮层
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AboutTime
{
    // ============================================================ 农历（1900-2099 打包表）
    sealed class LunarInfo
    {
        public int Year;              // 农历年
        public int Month;             // 1..12
        public int Day;               // 1..30
        public bool Leap;             // 闰月
        public string MonthName;      // 正月 / 二月 … 冬月 / 腊月（闰月前缀"闰"）
        public string DayName;        // 初一 … 三十（不用"廿"：内嵌字库缺廿）
        public string YearName;       // 丙午年

        public string DateText
        {
            get { return (Day == 1 ? MonthName + "月" : DayName); }
        }
    }

    static class LunarCal
    {
        const int BaseYear = 1900;
        static bool _ready;
        static int[] _pack = new int[0];          // 每年 16 位打包
        static DateTime[] _ny = new DateTime[0];  // 每年正月初一（公历）
        static readonly DateTime Epoch = new DateTime(1900, 1, 31);

        static readonly string[] Gan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        static readonly string[] Zhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
        // 第六稿：农历月一律用数字（十一月 / 十二月），不再写「冬月 / 腊月」——
        // 中文皮肤左二列要显示「十一月廿八日」这种数字月，与公历侧的写法一致
        static readonly string[] Mn = { "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "十一", "十二" };
        static readonly string[] D1 = { "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十" };
        static readonly string[] D2 = { "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九" };

        public static int LastYear { get { return BaseYear + _pack.Length - 1; } }

        public static void Load()
        {
            if (_ready) return;
            _ready = true;
            List<int> vals = new List<int>();
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("lunar.txt"))
                {
                    if (s == null) return;
                    using (StreamReader rd = new StreamReader(s, System.Text.Encoding.UTF8))
                    {
                        string line;
                        while ((line = rd.ReadLine()) != null)
                        {
                            line = line.Trim();
                            if (line.Length == 0 || line[0] == '#') continue;
                            int sp = line.IndexOf(' ');
                            if (sp <= 0) continue;
                            string hex = line.Substring(sp + 1).Trim();
                            if (hex.StartsWith("0x") || hex.StartsWith("0X")) hex = hex.Substring(2);
                            try { vals.Add(Convert.ToInt32(hex, 16)); }
                            catch (Exception) { vals.Add(0); }
                        }
                    }
                }
            }
            catch (Exception) { }
            if (vals.Count == 0) return;
            _pack = vals.ToArray();
            _ny = new DateTime[_pack.Length + 1];
            _ny[0] = Epoch;
            for (int i = 0; i < _pack.Length; i++) _ny[i + 1] = _ny[i].AddDays(YearDays(_pack[i]));
        }

        public static bool Ready { get { Load(); return _pack.Length > 0; } }

        static int YearDays(int p)
        {
            int n = 0;
            for (int m = 1; m <= 12; m++) n += MonthDays(p, m);
            if ((p & 0xF) != 0) n += LeapDays(p);
            return n;
        }

        static int MonthDays(int p, int m) { return ((p >> (16 - m)) & 1) != 0 ? 30 : 29; }
        static int LeapDays(int p) { return ((p >> 16) & 1) != 0 ? 30 : 29; }

        /// <summary>公历 → 农历；超出 1900-2099 返回 null</summary>
        public static LunarInfo FromSolar(DateTime d)
        {
            Load();
            if (_pack.Length == 0) return null;
            DateTime day = d.Date;
            if (day < _ny[0] || day >= _ny[_ny.Length - 1]) return null;
            int y = _pack.Length - 1;
            for (int i = 0; i < _pack.Length; i++)
            {
                if (day >= _ny[i] && day < _ny[i + 1]) { y = i; break; }
            }
            int p = _pack[y];
            int leap = p & 0xF;
            int rest = (int)(day - _ny[y]).TotalDays;
            int m = 1;
            bool isLeap = false;
            while (true)
            {
                int len = isLeap ? LeapDays(p) : MonthDays(p, m);
                if (rest < len) break;
                rest -= len;
                if (leap != 0 && m == leap && !isLeap) isLeap = true;
                else { isLeap = false; m++; }
                if (m > 12) { m = 1; break; }
            }
            LunarInfo li = new LunarInfo();
            li.Year = BaseYear + y;
            li.Month = m;
            li.Leap = isLeap;
            li.Day = rest + 1;
            li.MonthName = (isLeap ? "闰" : "") + Mn[m - 1];
            li.DayName = DayName(li.Day);
            int gy = li.Year - 4;
            li.YearName = Gan[((gy % 10) + 10) % 10] + Zhi[((gy % 12) + 12) % 12] + "年";
            return li;
        }

        static readonly string[] Num = { "", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

        public static string DayName(int d)
        {
            if (d >= 1 && d <= 10) return D1[d - 1];
            if (d >= 11 && d <= 19) return D2[d - 11];
            if (d == 20) return "二十";
            if (d == 30) return "三十";
            if (d >= 21 && d <= 29) return "廿" + Num[d - 20];    // 1.3：廿一…廿九，统一 2 字
            return "";
        }

        public static string GanZhi(int year)
        {
            int g = year - 4;
            return Gan[((g % 10) + 10) % 10] + Zhi[((g % 12) + 12) % 12];
        }
    }

    // ============================================================ 24 节气（寿星公式，1900-2100 足够）
    static class SolarTerms
    {
        static readonly string[] Names = {
            "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨", "立夏", "小满", "芒种", "夏至",
            "小暑", "大暑", "立秋", "处暑", "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至"
        };
        static readonly int[] Info = {
            0, 21208, 42467, 63836, 85337, 107014, 128867, 150921, 173149, 195551, 218072, 240693,
            263343, 285989, 308563, 331033, 353350, 375494, 397447, 419210, 440795, 462224, 483532, 504758
        };
        static readonly DateTime Base = new DateTime(1900, 1, 6, 2, 5, 0);
        // 1.5：删掉早已无用的 MonthTerms / _cache（1 月小寒大寒会被它整月丢掉，留着迟早误用）

        /// <summary>第 n 个节气（0=小寒 … 23=冬至）的精确时刻，供干支换年/换月用</summary>
        public static DateTime Moment(int year, int n)
        {
            if (n < 0) n = 0; if (n > 23) n = 23;
            double ms = 31556925974.7 * (year - 1900) + Info[n] * 60000.0;
            return Base.AddTicks((long)(ms * 10000.0));
        }

        /// <summary>当天是否节气，返回节气名或空串</summary>
        public static string Of(DateTime d)
        {
            // 逐日核对（节气一年只有 24 个，直接按 24 个精确日期比）
            for (int n = 0; n < 24; n++)
            {
                double ms = 31556925974.7 * (d.Year - 1900) + Info[n] * 60000.0;
                DateTime t = Base.AddTicks((long)(ms * 10000.0));
                if (t.Month == d.Month && t.Day == d.Day) return Names[n];
                if (n == 1 && t.Month == 1 && t.Day > 1) continue;
            }
            return "";
        }
    }

    // ============================================================ 节日规则
    sealed class FestRule
    {
        public string CC;       // 国家码：* 通用 / CN / US / 藏族 …
        public char Kind;       // M 公历 / W 第几个周几 / E 复活节偏移 / L 农历 / S 节气 / T 藏历
        public string Key;
        public string Zh, En;
        public int Prio;        // 1 通用世界 > 2 地点国/民族 > 3 农历·节气
        public string Keys;     // 民族节日：触发城市关键词（逗号分隔）
        public bool Ethnic;
        public bool Legal;      // v2.1：第 7 列带 F = 该国法定/公休假日
    }

    sealed class Fest
    {
        public int Prio;
        public string Zh, En;
        public char Kind;       // 2.0：沿用规则表类型 M 公历 / W 周几 / E 复活节 / L 农历 / S 节气 / T 藏历
        public string CC = "*"; // v2.1：这条节日属于哪个国家（* 通用）
        public bool Legal;      // v2.1：法定/公休假日
    }

    static class HolidayDb
    {
        static List<FestRule> _rules;
        static Dictionary<string, bool> _tib;      // "yyyy-MM-dd|KEY" → 命中表
        static bool _loaded;

        static string ReadRes(string name)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null) return "";
                using (StreamReader rd = new StreamReader(s, System.Text.Encoding.UTF8))
                    return rd.ReadToEnd();
            }
        }

        /// <summary>藏历节日日期表：每行 "YYYY-MM-DD|KEY"，由 caltib(Phugpa) 生成 1901-2099</summary>
        static void LoadTibetan()
        {
            _tib = new Dictionary<string, bool>(StringComparer.Ordinal);
            string text = ReadRes("tibetan.txt");
            if (text.Length == 0) return;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                _tib[line] = true;
            }
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _rules = new List<FestRule>();
            LoadTibetan();
            Parse(ReadRes("holidays.txt"), false);
            Parse(ReadRes("ethnic.txt"), true);
        }

        static void Parse(string text, bool ethnic)
        {
            if (text == null || text.Length == 0) return;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split('|');
                FestRule r = new FestRule();
                r.Ethnic = ethnic;
                if (ethnic)
                {
                    // 地区|类型|规则|中文|英文|触发城市关键词
                    if (p.Length < 5) continue;
                    r.CC = "*";                          // 民族节日不看主时钟国别，只看城市关键词
                    r.Kind = p[1].Trim().Length > 0 ? p[1].Trim()[0] : 'M';
                    r.Key = p[2].Trim();
                    r.Zh = p[3].Trim();
                    r.En = p[4].Trim();
                    r.Keys = p.Length >= 6 ? p[5].Trim() : "";
                    r.Prio = 2;
                    if (p.Length >= 7) { int q = PrioOf(p[6]); if (q > 0) r.Prio = q; }
                }
                else
                {
                    // 国家名|国家码|类型|规则|中文|英文
                    if (p.Length < 6) continue;
                    r.CC = p[1].Trim();
                    r.Kind = p[2].Trim().Length > 0 ? p[2].Trim()[0] : 'M';
                    r.Key = p[3].Trim();
                    r.Zh = p[4].Trim();
                    r.En = p[5].Trim();
                    r.Keys = "";
                    r.Prio = (r.CC == "*") ? 1 : (r.Kind == 'L' || r.Kind == 'S' || r.Kind == 'T' ? 3 : 2);
                    // v2.1：第 7 列可写 "4"（只进浮层）/ "F"（法定公休）/ "4F"（两者）
                    if (p.Length >= 7)
                    {
                        string opt = p[6].Trim().ToUpperInvariant();
                        int q = PrioOf(opt);
                        if (q > 0) r.Prio = q;
                        if (opt.IndexOf('F') >= 0) r.Legal = true;
                    }
                }
                _rules.Add(r);
            }
        }

        /// <summary>第 7 列优先级覆盖：1 通用世界 / 2 国别民族 / 3 农历节气 / 4 仅当日浮层（不进格）</summary>
        static int PrioOf(string s)
        {
            try
            {
                int v = int.Parse(s.Trim());
                if (v >= 1 && v <= 4) return v;
            }
            catch (Exception) { }
            return 0;
        }

        // ---------- 复活节（西方格里高利算法）
        static DateTime Easter(int y)
        {
            int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4;
            int f = (b + 8) / 25, g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30;
            int i = c / 4, k = c % 4;
            int l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31;
            int day = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateTime(y, month, day);
        }


        static bool Hit(FestRule r, DateTime d, LunarInfo li, string term, bool lunarOn, string cityZh, string cityEn)
        {
            switch (r.Kind)
            {
                case 'M':
                    return MonthDay(r.Key, d) == true;
                case 'W':
                    return WeekRule(r.Key, d);
                case 'E':
                    return EasterHit(r.Key, d);
                case 'L':
                {
                    if (!lunarOn || li == null || li.Leap) return false;
                    int mm, dd;
                    if (!SplitMD(r.Key, out mm, out dd)) return false;
                    return li.Month == mm && li.Day == dd;
                }
                case 'S':
                    return lunarOn && term.Length > 0 && term == r.Key;
                case 'T':
                    return _tib != null && _tib.ContainsKey(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + r.Key);
            }
            return false;
        }

        static bool MonthDay(string key, DateTime d)
        {
            int mm, dd;
            if (!SplitMD(key, out mm, out dd)) return false;
            return d.Month == mm && d.Day == dd;
        }

        static bool SplitMD(string key, out int mm, out int dd)
        {
            mm = 0; dd = 0;
            int p = key.IndexOf('-');
            if (p <= 0) return false;
            try
            {
                mm = int.Parse(key.Substring(0, p));
                dd = int.Parse(key.Substring(p + 1));
            }
            catch (Exception) { return false; }
            return true;
        }

        // key: "MM:3MON" = 该月第 3 个周一；"MM:LMON" = 该月最后一个周一（月份位可省，省了就不限月）
        static bool WeekRule(string key, DateTime d)
        {
            if (key.Length < 4) return false;
            int month = 0, start = 0;
            int c = key.IndexOf(':');
            if (c > 0)
            {
                try { month = int.Parse(key.Substring(0, c)); }
                catch (Exception) { return false; }
                if (month < 1 || month > 12) return false;
                start = c + 1;
            }
            if (month != 0 && d.Month != month) return false;
            if (key.Length - start < 4) return false;
            int nth = 0;
            bool last = false;
            int i = start;
            if (key[i] == 'L' || key[i] == 'l') { last = true; i = start + 1; }
            else
            {
                while (i < key.Length && char.IsDigit(key[i])) i++;
                if (i == start) return false;
                try { nth = int.Parse(key.Substring(start, i - start)); }
                catch (Exception) { return false; }
                if (nth < 1 || nth > 5) return false;
            }
            if (key.Length < i + 3) return false;
            DayOfWeek wd;
            string wname = key.Substring(i, 3).ToUpperInvariant();
            if (wname == "MON") wd = DayOfWeek.Monday;
            else if (wname == "TUE") wd = DayOfWeek.Tuesday;
            else if (wname == "WED") wd = DayOfWeek.Wednesday;
            else if (wname == "THU") wd = DayOfWeek.Thursday;
            else if (wname == "FRI") wd = DayOfWeek.Friday;
            else if (wname == "SAT") wd = DayOfWeek.Saturday;
            else if (wname == "SUN") wd = DayOfWeek.Sunday;
            else return false;
            if (d.DayOfWeek != wd) return false;
            DateTime first = new DateTime(d.Year, d.Month, 1);
            int shift = ((int)wd - (int)first.DayOfWeek + 7) % 7;
            if (last)
            {
                int days = DateTime.DaysInMonth(d.Year, d.Month);
                int lastDay = 1 + shift + ((days - 1 - shift) / 7) * 7;
                return d.Day == lastDay;
            }
            int want = 1 + shift + (nth - 1) * 7;
            return want <= DateTime.DaysInMonth(d.Year, d.Month) && d.Day == want;
        }

        static bool EasterHit(string key, DateTime d)
        {
            int off = 0;
            try { off = int.Parse(key); }
            catch (Exception) { return false; }
            return Easter(d.Year).AddDays(off).Date == d.Date;
        }

        /// <summary>取某天的节日列表（按优先级排序，同名去重）</summary>
        public static List<Fest> For(DateTime d, string cc, LunarInfo li, string term,
                                     bool lunarOn, string cityZh, string cityEn)
        {
            Load();
            List<Fest> list = new List<Fest>();
            if (_rules == null) return list;
            string zh = cityZh != null ? cityZh : "";
            string en = cityEn != null ? cityEn.ToLowerInvariant() : "";
            foreach (FestRule r in _rules)
            {
                if (r.CC != "*" && r.CC != "通用" && r.CC != cc) continue;
                if (r.Ethnic)
                {
                    if (!EthnicHit(r.Keys, zh, en)) continue;
                }
                if (!Hit(r, d, li, term, lunarOn, zh, en)) continue;
                bool dup = false;
                foreach (Fest f0 in list)
                    if (f0.Zh == r.Zh) { dup = true; break; }
                if (dup) continue;
                Fest f = new Fest();
                f.Prio = r.Prio;
                f.Zh = r.Zh;
                f.En = r.En;
                f.Kind = r.Kind;
                f.CC = r.CC;
                f.Legal = r.Legal;
                list.Add(f);
            }
            // 2.0：除夕 = 农历腊月的最后一天（次日为正月初一）。holidays.txt 里没有这条，按规则补上
            if (li != null)
            {
                LunarInfo nex = LunarCal.FromSolar(d.Date.AddDays(1));
                if (nex != null && nex.Month == 1 && nex.Day == 1 && !nex.Leap)
                {
                    bool has = false;
                    foreach (Fest f0 in list) if (f0.Zh == "除夕") { has = true; break; }
                    if (!has)
                    {
                        Fest fx = new Fest();
                        fx.Prio = 3;
                        fx.Zh = "除夕";
                        fx.En = "Lunar New Year's Eve";
                        fx.Kind = 'L';
                        fx.CC = "CN";      // v2.1：除夕属于中国的传统节日，且是法定公休
                        fx.Legal = true;
                        list.Add(fx);
                    }
                }
            }
            // 优先级 → 字数少的优先（格内更易排下）
            list.Sort(delegate(Fest a, Fest b)
            {
                if (a.Prio != b.Prio) return a.Prio - b.Prio;
                if (a.Zh.Length != b.Zh.Length) return a.Zh.Length - b.Zh.Length;
                return string.CompareOrdinal(a.Zh, b.Zh);
            });
            return list;
        }

        static bool EthnicHit(string keys, string zh, string en)
        {
            if (keys.Length == 0) return false;
            foreach (string k in keys.Split(','))
            {
                string t = k.Trim();
                if (t.Length == 0) continue;
                if (zh.IndexOf(t, StringComparison.Ordinal) >= 0) return true;
                if (en.Length > 0 && en.IndexOf(t.ToLowerInvariant(), StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }

    // ============================================================ 月历模型
    sealed class CalDay
    {
        public DateTime Date;
        public string LunarText = "";     // 初九 / 十月
        public bool IsToday;
        public bool IsAdjacent;           // 1.3：上/下月的填充日（整格标灰）
        public List<string> Fest = new List<string>();
        public int More;                  // 被截断的条数（右上 +N 角标）
        public bool TermDay;
    }

    sealed class CalMonthModel
    {
        public int Year, Month, Rows;
        public int FirstDayOfWeek = 0;    // 0 周日 / 1 周一
        public CalDay[] Cells;
        public string YearName = "";      // 丙午年
        public string LunarMon = "";      // 该月里第一个农历初一所落的农历月名（十一月 / 闰五月…）

        public static CalMonthModel Build(DateTime cur, DateTime today, string cc,
                                          bool lunarOn, string cityZh, string cityEn, int firstDow, string lang)
        {
            CalMonthModel m = new CalMonthModel();
            m.Year = cur.Year; m.Month = cur.Month;
            m.FirstDayOfWeek = firstDow;
            DateTime first = new DateTime(cur.Year, cur.Month, 1);
            int days = DateTime.DaysInMonth(cur.Year, cur.Month);
            int off = ((int)first.DayOfWeek - firstDow + 7) % 7;
            int need = off + days;
            // 1.3：固定 6 行 × 7 列。行数随月份变化会让格高（也就等于日号节奏）逐月跳，
            // 所以一律补满到 42 格，月外的空格用上/下月日期填灰。
            m.Rows = 6;
            if (need > 42) m.Rows = 6;
            m.Cells = new CalDay[m.Rows * 7];
            bool en = (lang == "en");
            DateTime prevMon = first.AddMonths(-1), nextMon = first.AddMonths(1);
            int prevDays = DateTime.DaysInMonth(prevMon.Year, prevMon.Month);
            for (int i = 0; i < m.Cells.Length; i++)
            {
                CalDay cd = new CalDay();
                int dn = i - off + 1;
                bool adj = false;
                DateTime date;
                if (dn < 1)                       // 月初空格：用上月末尾日期填（1.3）
                {
                    date = new DateTime(prevMon.Year, prevMon.Month, prevDays + dn);
                    adj = true;
                }
                else if (dn > days)               // 月末空格：用下月开头日期填（1.3）
                {
                    date = new DateTime(nextMon.Year, nextMon.Month, dn - days);
                    adj = true;
                }
                else date = new DateTime(cur.Year, cur.Month, dn);
                cd.Date = date;
                cd.IsAdjacent = adj;
                cd.IsToday = (!adj && cd.Date == today.Date);
                LunarInfo li = LunarCal.FromSolar(cd.Date);
                string term = SolarTerms.Of(cd.Date);
                cd.TermDay = term.Length > 0;
                if (lunarOn)
                {
                    if (li != null)
                    {
                        // 农历初一显示月名，其余显示日号。DateText 已处理「闰」前缀，
                        // 1.5：原先再拼一次 (li.Leap?"闰":"") + MonthName 会出「闰闰五月」
                        cd.LunarText = li.DateText;
                        if (!adj && m.YearName.Length == 0 && dn <= 7) m.YearName = li.YearName;
                        // 标题里的农历月：取该月第一个「初一」所在的农历月（该月没有初一则取月中那天）
                        if (!adj && m.LunarMon.Length == 0 && li.DayName == "初一")
                            m.LunarMon = (li.Leap ? "闰" : "") + li.MonthName + "月";
                    }
                }
                List<Fest> fs = HolidayDb.For(cd.Date, cc, li, term, lunarOn, cityZh, cityEn);
                int lines = 0;
                foreach (Fest f in fs)
                {
                    // 1.5：优先级 4 = 二三级小节庆（消防宣传 / 艾滋病日 / 交通安全宣传…），
                    // 只进当日浮层，不占日历格，也不参与 +N 计数
                    if (f.Prio >= 4) continue;
                    string name = en ? f.En : f.Zh;
                    if (name.Length == 0) continue;
                    int take = (name.Length <= 4) ? 1 : 2;
                    if (lines + take > 2) { if (!adj) cd.More++; continue; }
                    cd.Fest.Add(name);
                    lines += take;
                }
                m.Cells[i] = cd;
            }
            if (m.YearName.Length == 0)
            {
                LunarInfo li = LunarCal.FromSolar(first);
                if (li != null) m.YearName = li.YearName;
            }
            if (m.LunarMon.Length == 0)
            {
                // 这个公历月里没有农历初一（少见）：取月中那天的农历月
                DateTime mid = new DateTime(cur.Year, cur.Month, 15);
                LunarInfo li = LunarCal.FromSolar(mid);
                if (li != null) m.LunarMon = (li.Leap ? "闰" : "") + li.MonthName + "月";
            }
            return m;
        }
    }

    // ============================================================ 当日详情浮层
    sealed class CalPopup : Form
    {
        string _title = "";
        List<string> _lines = new List<string>();
        bool _dark = true;
        int _w = 260, _h = 120;
        int _lh = 20;

        public CalPopup(bool dark, string title, List<string> lines, Font baseFont)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            _dark = dark;
            _title = title;
            _lines = lines;
            Measure(baseFont);
            Size = new Size(_w, _h);
            // 圆角：用 Region 裁切
            using (GraphicsPath gp = new GraphicsPath())
            {
                int r = 12;
                gp.AddArc(0, 0, r, r, 180, 90);
                gp.AddArc(_w - r, 0, r, r, 270, 90);
                gp.AddArc(_w - r, _h - r, r, r, 0, 90);
                gp.AddArc(0, _h - r, r, r, 90, 90);
                gp.CloseFigure();
                Region = new Region(gp);
            }
        }

        void Measure(Font f)
        {
            using (Bitmap tmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(tmp))
            {
                int maxW = 120;
                foreach (string s in _lines)
                {
                    SizeF sz = g.MeasureString(s, f, int.MaxValue, StringFormat.GenericTypographic);
                    if (sz.Width > maxW) maxW = (int)Math.Ceiling(sz.Width);
                }
                maxW += 44;
                if (_title.Length > 0)
                {
                    SizeF tz = g.MeasureString(_title, f, int.MaxValue, StringFormat.GenericTypographic);
                    if (tz.Width + 44 > maxW) maxW = (int)Math.Ceiling(tz.Width) + 44;
                }
                _w = Math.Max(200, Math.Min(maxW, 460));
                // 行距按真实墨迹高度取（f.Size*1.55 在雅黑 10pt 下会压行、末行叠上一行）
                _lh = Math.Max((int)Math.Ceiling(f.Size * 1.55f), (int)Math.Ceiling(g.MeasureString("中国Ag", f, int.MaxValue, StringFormat.GenericTypographic).Height));
                _h = 18 + _lh + 6 + _lines.Count * _lh + 16;
            }
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            Color bg = _dark ? Color.FromArgb(238, 34, 34, 36) : Color.FromArgb(245, 246, 246, 250);
            Color n = _dark ? Color.FromArgb(246, 246, 249) : Color.FromArgb(24, 24, 28);
            Color acc = _dark ? Color.FromArgb(228, 140, 54) : Color.FromArgb(190, 98, 30);
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(_dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0), 1f))
                g.DrawRectangle(p, 0.5f, 0.5f, _w - 1f, _h - 1f);
            Font f = Font;
            int lh = _lh;
            int y = 18;
            using (SolidBrush b = new SolidBrush(acc)) g.DrawString(_title, f, b, 20, y);
            y += lh + 6;
            foreach (string s in _lines)
            {
                // 2.0：当日详情里「· 春节」这类行，除夕 / 春节 / 元宵 标红
                string body = s.StartsWith("· ") ? s.Substring(2).Trim() : s;
                Color lc = WidgetForm.IsRedFest(body) ? WidgetForm.FestRed : n;
                using (SolidBrush b = new SolidBrush(lc)) g.DrawString(s, f, b, 20, y);
                y += lh;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { Close(); }
        protected override void OnDeactivate(EventArgs e) { Close(); }
    }

    /// <summary>整月小日历浮层：双击世界行/主时钟的日期时弹出，按该城市当地时间标当月</summary>
    sealed class MonthPopup : Form
    {
        DateTime _mon;          // 该月 1 号
        DateTime _sel;          // 高亮那一天
        bool _dark = true, _zh = true;
        int _firstDow = 0;      // 每周首日：0 周日 / 1 周一（跟随主日历设置）
        int _w = 300, _h = 300;
        const int CW = 38, CH = 34;
        Font _fT, _fW, _fD;

        public MonthPopup(bool dark, DateTime selDate, bool zh, int firstDow)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            _dark = dark; _zh = zh;
            _firstDow = (firstDow == 1) ? 1 : 0;
            _sel = selDate;
            _mon = new DateTime(selDate.Year, selDate.Month, 1);
            // 字体提成字段：原先每帧新建 3 个 Font 且不释放，反复打开会累积 GDI 句柄
            _fT = new Font("Microsoft YaHei UI", 10.5f);
            _fW = new Font("Microsoft YaHei UI", 8.5f);
            _fD = new Font("Microsoft YaHei UI", 9.5f);
            int rows = RowsOf();
            _w = 7 * CW + 20;
            _h = 16 + 26 + 22 + rows * CH + 12;
            Size = new Size(_w, _h);
            using (GraphicsPath gp = new GraphicsPath())
            {
                int r = 12;
                gp.AddArc(0, 0, r, r, 180, 90);
                gp.AddArc(_w - r, 0, r, r, 270, 90);
                gp.AddArc(_w - r, _h - r, r, r, 0, 90);
                gp.AddArc(0, _h - r, r, r, 90, 90);
                gp.CloseFigure();
                Region = new Region(gp);
            }
        }

        int RowsOf()
        {
            int first = ((int)_mon.DayOfWeek - _firstDow + 7) % 7;      // 首列跟随设置
            int days = DateTime.DaysInMonth(_mon.Year, _mon.Month);
            return (first + days + 6) / 7;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_fT != null) _fT.Dispose();
            if (_fW != null) _fW.Dispose();
            if (_fD != null) _fD.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            Color bg = _dark ? Color.FromArgb(238, 34, 34, 36) : Color.FromArgb(245, 246, 246, 250);
            Color n = _dark ? Color.FromArgb(246, 246, 249) : Color.FromArgb(24, 24, 28);
            Color dim = _dark ? Color.FromArgb(150, 150, 158) : Color.FromArgb(110, 110, 118);
            Color acc = _dark ? Color.FromArgb(228, 140, 54) : Color.FromArgb(190, 98, 30);
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(_dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0), 1f))
                g.DrawRectangle(p, 0.5f, 0.5f, _w - 1f, _h - 1f);

            Font fT = _fT;
            Font fW = _fW;
            Font fD = _fD;

            string title = _zh ? (_mon.Year + "年" + _mon.Month + "月") : (_mon.Year + "/" + _mon.Month);
            using (SolidBrush b = new SolidBrush(acc))
                g.DrawString(title, fT, b, 12, 10);

            string[] wkzhMon = { "一", "二", "三", "四", "五", "六", "日" };
            string[] wkzhSun = { "日", "一", "二", "三", "四", "五", "六" };
            string[] wkenMon = { "M", "T", "W", "T", "F", "S", "S" };
            string[] wkenSun = { "S", "M", "T", "W", "T", "F", "S" };
            string[] wkzh = (_firstDow == 1) ? wkzhMon : wkzhSun;
            string[] wken = (_firstDow == 1) ? wkenMon : wkenSun;
            int y = 10 + 26;
            using (SolidBrush b = new SolidBrush(dim))
                for (int col = 0; col < 7; col++)
                {
                    string t = _zh ? wkzh[col] : wken[col];
                    SizeF sz = g.MeasureString(t, fW, int.MaxValue, StringFormat.GenericTypographic);
                    g.DrawString(t, fW, b, 10 + col * CW + (CW - sz.Width) / 2f, y, StringFormat.GenericTypographic);
                }

            int first = ((int)_mon.DayOfWeek - _firstDow + 7) % 7;
            int days = DateTime.DaysInMonth(_mon.Year, _mon.Month);
            int gy = y + 22;
            using (SolidBrush b = new SolidBrush(n))
            using (SolidBrush ab = new SolidBrush(acc))
            {
                for (int d = 1; d <= days; d++)
                {
                    int idx = first + d - 1;
                    float cx = 10 + (idx % 7) * CW, cy = gy + (idx / 7) * CH;
                    if (d == _sel.Day)
                    {
                        using (SolidBrush hl = new SolidBrush(_dark ? Color.FromArgb(90, acc) : Color.FromArgb(60, acc)))
                            g.FillRectangle(hl, cx + 2f, cy + 1f, CW - 4f, CH - 3f);
                    }
                    string ds = d.ToString(CultureInfo.InvariantCulture);
                    SizeF sz = g.MeasureString(ds, fD, int.MaxValue, StringFormat.GenericTypographic);
                    g.DrawString(ds, fD, d == _sel.Day ? ab : b, cx + (CW - sz.Width) / 2f, cy + 8f,
                                 StringFormat.GenericTypographic);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { Close(); }
        protected override void OnDeactivate(EventArgs e) { Close(); }
    }

    /// <summary>弹出式日历窗口：把完整日历模块画在自己的卡片上（1.3）</summary>
    sealed class CalWindow : Form
    {
        WidgetForm _owner;

        // 分层窗口：整卡（含卡片底与文字）由 owner 画进一张 32bpp 位图再推上来，
        // 这样透明度和主题跟主窗同一套 P，换样式/调透明度立刻反映到弹出窗
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TOOLWINDOW = 0x80;

        public CalWindow(WidgetForm owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // 1.9 修订：弹出日历的层级改为「与主窗同一层」，不再自己 TopMost。
            // 原来它固定 TopMost 而主窗不是，于是它浮在一切窗口（含任务栏、别的应用）之上，
            // 截图里就会比挂件更靠前、显得位置和透明度都错位。
            // 用 Owner 关系让它始终贴在主窗上方（主窗抬起来它也跟），同时跟随主窗的「总在最前」。
            TopMost = owner.TopMost;
            // 挂到主窗名下：主窗抬起时它跟着抬，但两者仍在同一层（都不是 TopMost）
            try { Owner = owner; } catch (Exception) { }
            KeyPreview = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;   // 不进 Alt+Tab，逐像素 alpha
                return cp;
            }
        }

        /// <summary>整卡重画 + 推到屏幕（透明度/主题/缩放都取 owner 当前设置）</summary>
        public void PaintLayer()
        {
            if (!IsHandleCreated || IsDisposed) return;
            Point sp = PointToScreen(Point.Empty);
            _owner.PaintCalWindowLayer(Handle, Width, Height, sp.X, sp.Y);
        }

        public void Redraw() { PaintLayer(); }

        protected override void OnPaintBackground(PaintEventArgs e) { }   // 分层窗口不靠 WM_PAINT
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            PaintLayer();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Close(); return; }
            _owner.PopupClick(e.Location);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // 1.5.1：双击某一天 → 弹当日详情（原先直接 Close，把「当日详情」这个功能废掉了）
            if (e.Button == MouseButtons.Left) { _owner.PopupDblClick(e.Location, PointToScreen(e.Location)); return; }
            base.OnMouseDoubleClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }
    }
}
