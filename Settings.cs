using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PrintServer
{
    /// <summary>
    /// 应用设置：保存到 data/config.json。
    /// 目前支持: 网页访问密码（SHA256 哈希存储，空 = 不使用密码）。
    /// </summary>
    internal static class Settings
    {
        public static string ConfigPath => Path.Combine(Program.DataDir, "config.json");

        private static string _webPasswordHash = "";
        private static readonly object Sync = new object();

        /// <summary>密码哈希（内部使用；空字符串 = 未启用）。</summary>
        private static string WebPasswordHash
        {
            get { lock (Sync) return _webPasswordHash; }
            set { lock (Sync) { _webPasswordHash = value ?? ""; } }
        }

        /// <summary>密码是否启用（哈希非空即启用）。</summary>
        public static bool PasswordEnabled => !string.IsNullOrEmpty(WebPasswordHash);

        /// <summary>校验密码是否匹配。密码未启用时始终返回 true。</summary>
        public static bool CheckPassword(string plain)
        {
            if (!PasswordEnabled) return true;
            if (string.IsNullOrEmpty(plain)) return false;
            return string.Equals(Hash(plain), WebPasswordHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>设置密码（空字符串 = 关闭密码）。存 SHA256 哈希，不存明文。</summary>
        public static void SetPassword(string plain)
        {
            WebPasswordHash = string.IsNullOrEmpty(plain) ? "" : Hash(plain);
        }

        /// <summary>兼容旧配置：如果 config 里是明文，转为哈希保存。</summary>
        public static void SetPasswordLegacy(string plainTextOrHash)
        {
            SetPassword(plainTextOrHash);
        }

        private static string Hash(string plain)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(plain));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>加载配置（启动时调用）。兼容旧版明文 webPassword 字段。</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var json = SimpleJson.Parse(text);

                // 优先新字段: webPasswordHash (已是 SHA256 hex, 64 字符)
                var hash = json.Get("webPasswordHash");
                if (!string.IsNullOrEmpty(hash) && hash.Length == 64)
                {
                    WebPasswordHash = hash;
                    return;
                }

                // 兼容旧字段: webPassword (明文) -> 迁移为哈希并保存
                var legacy = json.Get("webPassword");
                if (!string.IsNullOrEmpty(legacy))
                {
                    SetPassword(legacy);
                    Save();
                    Log.Info("已迁移旧版明文密码为 SHA256 哈希存储");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("加载配置失败: " + ex.Message);
            }
        }

        /// <summary>保存配置到磁盘（仅存哈希，永不写明文）。</summary>
        public static void Save()
        {
            lock (Sync)
            {
                try
                {
                    var dir = Path.GetDirectoryName(ConfigPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var text = "{\"webPasswordHash\":" + WebServer.JsonEncode(WebPasswordHash) + "}";
                    File.WriteAllText(ConfigPath, text, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Log.Warn("保存配置失败: " + ex.Message);
                }
            }
        }
    }
}