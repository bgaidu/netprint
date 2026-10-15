using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrintServer
{
    internal static class Program
    {
        public static string BaseDir { get; private set; }
        public static string DataDir { get; private set; }
        public static string UploadDir { get; private set; }
        public static string ScannedDir { get; private set; }
        public static string LogDir { get; private set; }
        public static string LogFile { get; private set; }
        public static int Port { get; private set; } = 5000;

        public static WebServer Server { get; private set; }
        private static TrayIcon _tray;

        /// <summary>重启 Web 服务（托盘菜单用）。</summary>
        public static void RestartServer()
        {
            try { Server?.Dispose(); } catch { }
            Server = new WebServer(Port);
            Server.Start();
            Log.Info($"Web 服务已重启 http://0.0.0.0:{Port}");
        }

        /// <summary>更改服务端口并重启（托盘菜单用）。</summary>
        public static void ChangePort(int newPort)
        {
            if (newPort < 1024 || newPort > 65535)
                throw new ArgumentException("端口必须在 1024-65535 之间");
            Port = newPort;
            RestartServer();
            Log.Info($"端口已更改为 {newPort}");
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool elevated = false;
            foreach (var a in args)
                if (a == "--elevated") { elevated = true; break; }

            // 需要管理员: 非提升运行则请求提权
            if (!IsAdministrator() && !elevated)
            {
                var ok = TryElevate();
                if (ok) return; // 新提升的进程已启动，本进程退出
                // 用户拒绝 UAC: 降级运行（localhost 兜底）
            }

            // 路径初始化（exe 或 bin 目录）
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            DataDir = Path.Combine(BaseDir, "data");
            UploadDir = Path.Combine(DataDir, "uploads");
            ScannedDir = Path.Combine(DataDir, "scanned");
            LogDir = Path.Combine(DataDir, "logs");
            LogFile = Path.Combine(LogDir, "print_log.txt");

            Directory.CreateDirectory(UploadDir);
            Directory.CreateDirectory(ScannedDir);
            Directory.CreateDirectory(LogDir);

            // 解析命令行参数: --port=xxxx
            foreach (var a in args)
            {
                if (a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(a.Substring("--port=".Length), out int p);
                    if (p > 0 && p < 65536) Port = p;
                }
            }

            try
            {
                Server = new WebServer(Port);
                Server.Start();
                Log.Info($"Web 服务已启动 http://0.0.0.0:{Port} (管理员: {IsAdministrator()})");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Web 服务启动失败: " + ex.Message, "打印服务",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 托盘（主线程消息循环）
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 自动清理定时器: 每 5 分钟检查一次过期文件（上传10分钟/扫描30分钟）
            var cleanupTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            cleanupTimer.Tick += (s, e) => { try { FileStore.CleanupOldFiles(); } catch { } };
            cleanupTimer.Start();

            // 日志自动清理: 每 60 分钟检查一次，若超过当天 15:00 则清理 7 天前记录（每天最多清一次）
            var logCleanupTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
            bool logCleanedToday = false;
            logCleanupTimer.Tick += (s, e) =>
            {
                try
                {
                    if (!logCleanedToday && DateTime.Now.Hour >= 15)
                    {
                        Log.CleanupOld(7);
                        logCleanedToday = true;
                    }
                    else if (DateTime.Now.Hour < 15)
                    {
                        logCleanedToday = false;
                    }
                }
                catch { }
            };
            logCleanupTimer.Start();

            _tray = new TrayIcon();
            _tray.Show();
            Application.Run();
            _tray.Dispose();
        }

        private static bool IsAdministrator()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>以管理员权限重新启动自己；若用户拒绝 UAC 返回 false。</summary>
        private static bool TryElevate()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var sb = new System.Text.StringBuilder("--elevated");
                for (int i = 1; i < args.Length; i++)
                    sb.Append(' ').Append('"').Append(args[i]).Append('"');

                var psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = sb.ToString(),
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false; // 用户取消 UAC
            }
        }
    }
}