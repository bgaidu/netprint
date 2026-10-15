using System;
using System.Collections.Generic;
using System.IO;

namespace PrintServer
{
    /// <summary>
    /// 简单日志：按天生成日志文件（print_log_yyyyMMdd.txt），控制台同步输出。
    /// 记录所有访问/打印/扫描/设置事件。
    /// </summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _currentDate;
        private static string _currentFile;

        /// <summary>获取当前日志文件路径（按天切换）。</summary>
        public static string CurrentFile
        {
            get
            {
                lock (Sync)
                {
                    var date = DateTime.Now.ToString("yyyyMMdd");
                    if (date != _currentDate || _currentFile == null)
                    {
                        _currentDate = date;
                        _currentFile = Path.Combine(Program.LogDir, $"print_log_{date}.txt");
                    }
                    return _currentFile;
                }
            }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";
            Console.WriteLine(line);
            lock (Sync)
            {
                try
                {
                    using (var w = File.AppendText(CurrentFile))
                        w.WriteLine(line);
                }
                catch { /* 日志失败不影响主流程 */ }
            }
        }

        /// <summary>
        /// 清理 keepDays 天前的日志文件（按天文件，直接删除旧文件）。
        /// </summary>
        public static void CleanupOld(int keepDays)
        {
            lock (Sync)
            {
                try
                {
                    if (!Directory.Exists(Program.LogDir)) return;
                    var keep = DateTime.Now.AddDays(-keepDays);
                    foreach (var f in Directory.GetFiles(Program.LogDir, "print_log_*.txt"))
                    {
                        try
                        {
                            var name = Path.GetFileNameWithoutExtension(f); // print_log_20261015
                            var dateStr = name.Length >= 18 ? name.Substring(10, 8) : "";
                            DateTime dt;
                            if (dateStr.Length == 8 && DateTime.TryParseExact(dateStr, "yyyyMMdd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out dt))
                            {
                                if (dt < keep)
                                {
                                    File.Delete(f);
                                    Log.Info($"日志清理: 删除旧日志 {Path.GetFileName(f)}");
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { /* 清理失败不影响 */ }
            }
        }

        /// <summary>返回日志目录下所有日志文件名列表（用于托盘查看，最新在前）。</summary>
        public static List<string> ListLogFiles()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(Program.LogDir))
                {
                    foreach (var f in Directory.GetFiles(Program.LogDir, "print_log_*.txt"))
                        list.Add(Path.GetFileName(f));
                }
            }
            catch { }
            list.Sort((a, b) => string.Compare(b, a, StringComparison.Ordinal)); // 最新在前
            return list;
        }
    }
}