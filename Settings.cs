using System;
using System.IO;

namespace PrintServer
{
    /// <summary>
    /// 应用设置：保存到 data/config.json。
    /// 目前支持: WebPassword（网页访问密码，空字符串 = 不使用密码）。
    /// </summary>
    internal static class Settings
    {
        public static string ConfigPath => Path.Combine(Program.DataDir, "config.json");

        private static string _webPassword = "";
        private static readonly object Sync = new object();

        public static string WebPassword
        {
            get { lock (Sync) return _webPassword; }
            set { lock (Sync) { _webPassword = value ?? ""; } }
        }

        /// <summary>密码是否启用（非空即启用）。</summary>
        public static bool PasswordEnabled => !string.IsNullOrEmpty(WebPassword);

        /// <summary>加载配置（启动时调用）。</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var text = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
                var json = SimpleJson.Parse(text);
                WebPassword = json.Get("webPassword") ?? "";
            }
            catch (Exception ex)
            {
                Log.Warn("加载配置失败: " + ex.Message);
            }
        }

        /// <summary>保存配置到磁盘。</summary>
        public static void Save()
        {
            lock (Sync)
            {
                try
                {
                    var safe = WebPassword ?? "";
                    var dir = Path.GetDirectoryName(ConfigPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var text = "{\"webPassword\":" + WebServer.JsonEncode(safe) + "}";
                    File.WriteAllText(ConfigPath, text, System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Log.Warn("保存配置失败: " + ex.Message);
                }
            }
        }
    }
}