using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PrintServer
{
    /// <summary>
    /// 嵌入资源管理器：wwwroot / logo.ico / poppler_bin 全部编译进 exe，
    /// 运行时从此读取。poppler 需要落盘才能以进程方式执行，启动时解压到临时目录。
    /// </summary>
    internal static class ResourceManager
    {
        private static readonly Assembly Asm = typeof(ResourceManager).Assembly;

        // 资源命名约定 (csproj LogicalName): res.wwwroot.* / res.logo.ico / res.poppler_bin.*
        private const string Prefix = "res.";
        private static readonly Dictionary<string, string> NameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>读取嵌入资源字节；不存在返回 null。</summary>
        public static byte[] ReadBytes(string resourceKey)
        {
            var fullName = ResolveFullName(resourceKey);
            if (fullName == null) return null;
            using (var s = Asm.GetManifestResourceStream(fullName))
            {
                if (s == null) return null;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>读取嵌入资源文本；不存在返回 null。</summary>
        public static string ReadText(string resourceKey)
        {
            var bytes = ReadBytes(resourceKey);
            return bytes == null ? null : System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>判断嵌入资源是否存在。</summary>
        public static bool Exists(string resourceKey)
        {
            return ResolveFullName(resourceKey) != null;
        }

        /// <summary>提取资源到目标文件（覆盖写入）；返回是否成功。</summary>
        public static bool Extract(string resourceKey, string targetPath)
        {
            var bytes = ReadBytes(resourceKey);
            if (bytes == null) return false;
            try
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(targetPath, bytes);
                return true;
            }
            catch { return false; }
        }

        /// <summary>将资源 key 解析为完整资源名（大小写不敏感匹配）。</summary>
        private static string ResolveFullName(string resourceKey)
        {
            lock (NameCache)
            {
                if (NameCache.TryGetValue(resourceKey, out var cached)) return cached;

                // 尝试直接拼接
                var direct = Prefix + resourceKey.Replace('/', '.').Replace('\\', '.').TrimStart('.');
                if (Asm.GetManifestResourceStream(direct) != null)
                {
                    NameCache[resourceKey] = direct;
                    return direct;
                }

                // 遍历匹配（处理大小写差异）
                foreach (var name in Asm.GetManifestResourceNames())
                {
                    if (string.Equals(name, direct, StringComparison.OrdinalIgnoreCase))
                    {
                        NameCache[resourceKey] = name;
                        return name;
                    }
                }
                NameCache[resourceKey] = null;
                return null;
            }
        }
    }
}