using System;
using System.IO;

namespace PrintServer
{
    /// <summary>简单日志：追加写入文件，控制台同步输出。</summary>
    internal static class Log
    {
        private static readonly object Sync = new object();

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
                    using (var w = File.AppendText(Program.LogFile))
                        w.WriteLine(line);
                }
                catch { /* 日志失败不影响主流程 */ }
            }
        }

        /// <summary>
        /// 自动清理：删除 keepDays 天前的日志行（每天调用一次，防止日志文件过大）。
        /// 采用重写方式：读取所有行，仅保留最近 keepDays 天的记录。
        /// </summary>
        public static void CleanupOld(int keepDays)
        {
            lock (Sync)
            {
                try
                {
                    if (!File.Exists(Program.LogFile)) return;
                    var now = DateTime.Now;
                    var keep = now.AddDays(-keepDays);
                    var all = File.ReadAllLines(Program.LogFile);
                    var kept = new System.Collections.Generic.List<string>(all.Length);
                    foreach (var line in all)
                    {
                        // 行首格式: [yyyy-MM-dd HH:mm:ss]
                        if (line.Length >= 20 && line[0] == '[')
                        {
                            DateTime dt;
                            if (DateTime.TryParse(line.Substring(1, 19), out dt))
                            {
                                if (dt >= keep) kept.Add(line);
                                continue;
                            }
                        }
                        kept.Add(line); // 无法解析的保留
                    }
                    if (kept.Count < all.Length)
                    {
                        File.WriteAllLines(Program.LogFile, kept);
                        Log.Info($"日志自动清理: 移除 {all.Length - kept.Count} 条 {keepDays} 天前的记录");
                    }
                }
                catch { /* 清理失败不影响 */ }
            }
        }
    }
}