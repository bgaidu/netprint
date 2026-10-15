using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PrintServer
{
    /// <summary>上传文件与扫描文件管理：保存、列表、解析、删除、过期清理。</summary>
    internal static class FileStore
    {
        private static readonly HashSet<string> AllowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".txt",
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
        };

        /// <summary>净化文件名（保留中文，剔除路径与危险字符），并保证扩展名合法。</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = name.Replace('/', '_').Replace('\\', '_');
            var invalids = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalids, c) >= 0 ? '_' : c);

            var cleaned = sb.ToString().Trim();
            if (cleaned.Length == 0) return null;

            var ext = Path.GetExtension(cleaned);
            if (string.IsNullOrEmpty(ext) || !AllowedExt.Contains(ext))
                return null; // 不允许的类型

            // 防重名：加时间戳前缀
            var result = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + cleaned;
            return result.Length > 100 ? result.Substring(0, 100) : result;
        }

        /// <summary>保存上传文件到 uploads，返回完整路径。</summary>
        public static string SaveUpload(string originalName, byte[] data)
        {
            var safe = SanitizeFileName(originalName);
            if (safe == null) throw new InvalidOperationException("非法或不允许的文件类型: " + originalName);
            if (data == null || data.Length == 0) throw new InvalidOperationException("文件内容为空");

            var full = Path.Combine(Program.UploadDir, safe);
            File.WriteAllBytes(full, data);
            Log.Info($"文件已保存: {safe} ({data.Length} 字节)");
            return full;
        }

        /// <summary>解析 uploads 中的文件名（防路径穿越）。</summary>
        public static string ResolveUpload(string name)
        {
            return ResolveIn(Program.UploadDir, name);
        }

        /// <summary>解析 scanned 中的文件名。</summary>
        public static string ResolveScanned(string name)
        {
            return ResolveIn(Program.ScannedDir, name);
        }

        private static string ResolveIn(string dir, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
                return null;
            var full = Path.GetFullPath(Path.Combine(dir, name));
            if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(full) ? full : null;
        }

        /// <summary>上传文件列表（JSON）。</summary>
        public static string ListUploads()
        {
            return ListJson(Program.UploadDir);
        }

        /// <summary>扫描文件列表（JSON）。</summary>
        public static string ListScanned()
        {
            return ListJson(Program.ScannedDir);
        }

        private static string ListJson(string dir)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    var info = new FileInfo(f);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"name\":").Append(WebServer.JsonEncode(info.Name))
                      .Append(",\"size\":").Append(info.Length)
                      .Append(",\"modified\":").Append(WebServer.JsonEncode(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")))
                      .Append('}');
                }
            }
            catch { /* 目录不存在等 */ }
            sb.Append(']');
            return sb.ToString();
        }

        public static void DeleteUpload(string name)
        {
            var full = ResolveUpload(name);
            if (full != null) { try { File.Delete(full); Log.Info("删除上传文件: " + name); } catch { } }
        }

        public static void DeleteScanned(string name)
        {
            var full = ResolveScanned(name);
            if (full != null) { try { File.Delete(full); Log.Info("删除扫描文件: " + name); } catch { } }
        }

        public static void ClearUploads()
        {
            foreach (var f in Directory.GetFiles(Program.UploadDir))
                try { File.Delete(f); } catch { }
            Log.Info("已清空上传队列");
        }

        public static void ClearScanned()
        {
            foreach (var f in Directory.GetFiles(Program.ScannedDir))
                try { File.Delete(f); } catch { }
            Log.Info("已清空扫描队列");
        }

        /// <summary>过期清理：上传 10 分钟、扫描 30 分钟自动删除。</summary>
        public static void CleanupOldFiles()
        {
            Cleanup(Program.UploadDir, TimeSpan.FromMinutes(10));
            Cleanup(Program.ScannedDir, TimeSpan.FromMinutes(30));
        }

        private static void Cleanup(string dir, TimeSpan age)
        {
            try
            {
                var now = DateTime.Now;
                foreach (var f in Directory.GetFiles(dir))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        if (now - info.LastWriteTime > age)
                        {
                            File.Delete(f);
                            Log.Info("自动清理过期文件: " + info.Name);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}