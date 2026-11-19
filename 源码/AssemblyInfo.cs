using System.Reflection;
using System.Runtime.InteropServices;

// 1.6：成品属性详情按 VU Widgets 3.6.8 的字段口径补齐
//   FileDescription ← AssemblyTitle / Comments ← AssemblyDescription
//   CompanyName ← AssemblyCompany / ProductName ← AssemblyProduct / LegalCopyright ← AssemblyCopyright
[assembly: AssemblyTitle("About Time")]
[assembly: AssemblyDescription("About Time - Desktop World Clock Widget")]
[assembly: AssemblyProduct("About Time")]
[assembly: AssemblyCompany("༺格桑仁青༻ & Old Newbie")]
[assembly: AssemblyCopyright("© 2026 ༺格桑仁青༻ & Old Newbie")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
// 2.1.1：磨砂跟随壁纸修复（指纹改看每屏转码文件）+ 审阅报告核实属实项
[assembly: AssemblyVersion("2.1.1.0")]
[assembly: AssemblyFileVersion("2.1.1.0")]
[assembly: ComVisible(false)]
