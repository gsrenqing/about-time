# About Time

A full-featured Windows desktop clock widget: world clocks, calendar, lunar calendar and Ganzhi (sexagenary cycle), holiday highlighting, alarm, frosted background, multi-monitor support, with automatic Chinese/English switching.

- Version: **v2.1**
- Runtime: Windows 10 / 11 (.NET Framework 4.x)
- Stack: C# / WinForms

## Repository Layout

```
├─ 源码/            # C# sources, fonts, city/timezone/lunar/holiday data, alarm sounds
├─ 成品/            # Compiled AboutTime.exe
├─ LICENSE          # GPL-3.0
└─ README.md
```

## Main Modules

| Source file | Responsibility |
|---|---|
| `Main.cs` | Entry point and main flow |
| `Widget.cs` | Main widget UI and rendering |
| `Calendar.cs` | Calendar module |
| `Timer.cs` | Timing and refresh |
| `Ganzhi.cs` | Ganzhi / lunar calculation |
| `TimeZoneMatcher.cs` | City timezone matching |
| `WallpaperFrost.cs` | Frosted background effect |
| `Support.cs` | Helpers and shared logic |

## Build from Source

Use the .NET Framework built-in `csc.exe` (no Visual Studio required):

```powershell
cd 源码
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe @compile.rsp
```

## Run

Double-click `成品\AboutTime.exe`. Before removing the program, use the widget's right-click menu to choose "Close all windows and clear all configuration" so no system residue remains.

## Data Sources

- City/timezone data: GeoNames geographic database under CC-BY 4.0 (geonames.org)
- Chinese administrative divisions: codes from the Ministry of Civil Affairs of the PRC
- Artwork inspired by the bedside clock 2 widget in Coolfish Desktop / XWidgets; thanks to the original author Jimking

## License

This project is open-sourced under **GPL-3.0**; see [LICENSE](LICENSE). For non-commercial study and testing only. The author is not liable for any issues arising from its use and does not commit to future upgrades.

---

# 中文说明

一款功能完整的 Windows 桌面时钟挂件：世界时钟、日历、农历与干支、节日标红、闹钟、磨砂背景、多显示器适配，支持中英文自动切换。

## 目录结构

```
├─ 源码/            # C# 源码、字体、城市/时区/农历/节日数据、提示音
├─ 成品/            # 已编译的 AboutTime.exe
├─ LICENSE          # GPL-3.0
└─ README.md
```

## 主要模块

| 源码文件 | 职责 |
|---|---|
| `Main.cs` | 程序入口与主流程 |
| `Widget.cs` | 挂件主界面与绘制 |
| `Calendar.cs` | 日历模块 |
| `Timer.cs` | 计时与刷新 |
| `Ganzhi.cs` | 干支 / 农历计算 |
| `TimeZoneMatcher.cs` | 城市时区匹配 |
| `WallpaperFrost.cs` | 磨砂背景效果 |
| `Support.cs` | 辅助与公共逻辑 |

## 从源码编译

使用 .NET Framework 自带的 `csc.exe`（无需 Visual Studio）：

```powershell
cd 源码
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe @compile.rsp
```

## 运行

直接双击 `成品\AboutTime.exe`。清除程序前，请先在挂件右键菜单中选择「关闭全部窗口并清除所有配置」，以彻底移除系统残留。

## 数据来源

- 城市时区数据依据 CC-BY 4.0 许可使用 [GeoNames](https://www.geonames.org/) 全球地理数据库
- 中国行政区划数据源自民政部《中华人民共和国行政区划代码》
- 主题美工借鉴酷鱼桌面 / XWidgets 中的 bedside clock 2，鸣谢原作者 Jimking

## 许可

本项目以 **GPL-3.0** 协议开源，详见 [LICENSE](LICENSE)。仅限非商业用途的学习、测试；作者不对使用过程中出现的问题负责，也不承诺后续版本升级。
