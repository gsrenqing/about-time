# About Time

一款功能完整的 Windows 桌面时钟挂件：世界时钟、日历、农历与干支、节日标红、闹钟、磨砂背景、多显示器适配，支持中英文自动切换。

- 当前版本：**v2.1**
- 运行环境：Windows 10 / 11（.NET Framework 4.x）
- 技术栈：C# / WinForms

## 目录结构

```
├─ 源码/            # C# 源码、字体、城市/时区/农历/节日数据、提示音、编译说明
├─ 成品/            # 已编译的 AboutTime.exe 与说明
├─ 审阅截图/        # 验收与审阅截图
├─ 审阅报告_v2.1_BUG与优化.md
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

详细编译口径见 `源码\编译说明.txt`。

## 运行

直接双击 `成品\AboutTime.exe`。清除程序前，请先在挂件右键菜单中选择「关闭全部窗口并清除所有配置」，以彻底移除系统残留。

## 数据来源

- 城市时区数据依据 CC-BY 4.0 许可使用 [GeoNames](https://www.geonames.org/) 全球地理数据库
- 中国行政区划数据源自民政部《中华人民共和国行政区划代码》
- 主题美工借鉴酷鱼桌面 / XWidgets 中的 bedside clock 2，鸣谢原作者 Jimking

## 许可

本项目以 **GPL-3.0** 协议开源，详见 [LICENSE](LICENSE)。仅限非商业用途的学习、测试；作者不对使用过程中出现的问题负责，也不承诺后续版本升级。
