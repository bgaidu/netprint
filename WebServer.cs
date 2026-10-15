using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PrintServer
{
    /// <summary>
    /// 自托管 Web 服务器，基于 HttpListener。
    /// 路由: /api/... (JSON API)，/static/... (静态资源)，/ (首页)
    /// </summary>
    internal sealed class WebServer : IDisposable
    {
        private HttpListener _listener = new HttpListener();
        private readonly int _port;
        private Thread _thread;
        private volatile bool _running;

        public const int MaxBodyBytes = 100 * 1024 * 1024; // 100MB 上传上限
        private static readonly HashSet<string> StaticExtMap = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".css", ".js", ".ico", ".png", ".jpg", ".gif", ".html", ".svg", ".woff", ".woff2"
        };

        public WebServer(int port)
        {
            _port = port;
        }

        public int Port => _port;

        /// <summary>健康检查：listener 是否仍在监听 + 请求处理线程是否存活。</summary>
        public bool IsHealthy
        {
            get
            {
                try
                {
                    if (_listener == null || !_listener.IsListening) return false;
                    if (_thread == null || !_thread.IsAlive) return false;
                    return true;
                }
                catch { return false; }
            }
        }

        private int _requestCount;
        /// <summary>自启动以来的请求计数（健康指标）。</summary>
        public int RequestCount => _requestCount;

        public void Start()
        {
            // 1) 优先通配符 +:port（需要管理员，通吃所有 Host）
            try
            {
                _listener.Prefixes.Add($"http://+:{_port}/");
                _listener.Start();
            }
            catch (Exception ex1)
            {
                // 非管理员或 URL ACL 未授权时通配符绑定被拒。
                // 具体 IP 前缀同样受 URL ACL 限制，唯一免授权的是 localhost。
                // 兜底绑定 localhost（仅本机可访问），并提示需要管理员权限。
                Log.Warn("通配符绑定失败(需要管理员权限)，仅绑定 localhost: " + ex1.Message);
                var second = new HttpListener();
                second.Prefixes.Add($"http://localhost:{_port}/");
                try
                {
                    second.Start();
                    _listener = second;
                }
                catch (Exception ex2)
                {
                    try { second.Close(); } catch { }
                    throw new InvalidOperationException("无法绑定任何监听地址(请以管理员身份运行): " + ex2.Message);
                }
            }

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "HttpLoop" };
            _thread.Start();
            Log.Info($"监听端口 {_port}");
        }

        /// <summary>获取本机局域网 IPv4 地址列表（用于无管理员权限时的回退绑定）。</summary>
        private static List<string> GetLocalIPs()
        {
            var ips = new List<string>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.IsLoopback(addr.Address))
                            ips.Add(addr.Address.ToString());
                    }
                }
            }
            catch { }
            return ips;
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    Interlocked.Increment(ref _requestCount);
                    ThreadPool.QueueUserWorkItem(Handle, ctx);
                }
                catch (ObjectDisposedException)
                {
                    // 服务被主动关闭
                    break;
                }
                catch (HttpListenerException ex)
                {
                    // 监听被拒等异常：等待 2 秒重试，避免死循环刷日志
                    if (!_running) break;
                    if (ex.ErrorCode == 995) // 操作已取消（关闭时）
                        break;
                    Log.Error("监听异常: " + ex.Message + "，2秒后重试");
                    try { Thread.Sleep(2000); } catch { }
                }
                catch (Exception ex)
                {
                    if (_running) Log.Error("监听异常: " + ex.Message);
                    try { Thread.Sleep(2000); } catch { }
                }
            }
        }

        private void Handle(object state)
        {
            var ctx = (HttpListenerContext)state;
            try
            {
                var path = ctx.Request.Url.AbsolutePath;
                var ip = GetClientIp(ctx);

                // 密码校验（静态资源/健康检查除外；启用密码时未带正确密码的 API 请求拒绝）
                if (Settings.PasswordEnabled && !IsPublicResource(path))
                {
                    var pwd = ctx.Request.Headers["X-Auth-Password"] ?? "";
                    if (pwd != Settings.WebPassword)
                    {
                        Log.Warn($"访问被拒绝(密码错误): IP={ip} {ctx.Request.HttpMethod} {path}");
                        WebServer.WriteError(ctx, 401, "需要有效密码");
                        return;
                    }
                }

                // 访问日志: 记录 IP + 操作内容（仅记录有意义的事件，避免静态资源刷屏）
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                    path == "/" || path == "" || path == "/preview/" || path.StartsWith("/uploads/"))
                {
                    Log.Info($"访问: IP={ip} {ctx.Request.HttpMethod} {path}");
                }

                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/preview/", StringComparison.OrdinalIgnoreCase))
                {
                    ApiRouter.Handle(ctx);
                    return;
                }

                if (path.StartsWith("/static/", StringComparison.OrdinalIgnoreCase))
                {
                    ServeStatic(ctx, path.Substring("/static/".Length));
                    return;
                }

                // 其余全部返回首页（SPA 风格单页）
                // 单文件部署: 从嵌入资源读取
                if (TryServeEmbedded(ctx, "wwwroot/index.html", "text/html; charset=utf-8"))
                    return;
                // 磁盘模式（开发）: 读文件
                ServeFile(ctx, GoHome());
            }
            catch (Exception ex)
            {
                Log.Error("请求处理异常: " + ex.Message);
                try { WriteError(ctx, 500, ex.Message); } catch { }
            }
        }

        /// <summary>获取客户端 IP（HttpListener 的 RemoteEndPoint）。</summary>
        private static string GetClientIp(HttpListenerContext ctx)
        {
            try
            {
                var ep = ctx.Request.RemoteEndPoint;
                return ep?.Address?.ToString() ?? "?";
            }
            catch { return "?"; }
        }

        /// <summary>无需密码即可访问的资源（登录页/静态资源/健康检查）。</summary>
        private static bool IsPublicResource(string path)
        {
            if (path == "/" || string.IsNullOrEmpty(path)) return true;      // 首页登录页
            if (path.StartsWith("/static/", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Equals("/api/auth/check", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string GoHome()
        {
            // 优先从嵌入资源读取首页（单文件部署），回退磁盘 wwwroot（开发调试）
            if (ResourceManager.Exists("wwwroot/index.html"))
                return null; // 由 Handle 分支走 ServeEmbedded
            var www = Path.Combine(Program.BaseDir, "wwwroot");
            var candidates = new[]
            {
                Path.Combine(www, "index.html"),
                Path.Combine(Program.BaseDir, "index.html"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>从嵌入资源响应（单文件部署时前端不走磁盘）。返回 true 表示已响应。</summary>
        internal static bool TryServeEmbedded(HttpListenerContext ctx, string resourceKey, string contentType)
        {
            var data = ResourceManager.ReadBytes(resourceKey);
            if (data == null) return false;
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
            return true;
        }

        internal static void ServeFile(HttpListenerContext ctx, string filePath, string contentType = null)
        {
            if (filePath == null || !File.Exists(filePath))
            {
                WriteError(ctx, 404, "Not Found");
                return;
            }
            var data = File.ReadAllBytes(filePath);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType ?? MimeFor(filePath);
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
        }

        private void ServeStatic(HttpListenerContext ctx, string relative)
        {
            // 防路径穿越
            if (relative.Contains("..") || relative.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                WriteError(ctx, 403, "Forbidden");
                return;
            }

            // 单文件部署: 从嵌入资源读取 (wwwroot/<relative>)
            if (TryServeEmbedded(ctx, "wwwroot/" + relative, MimeFor(relative)))
                return;

            // 磁盘模式（开发）: wwwroot 目录
            var www = Path.Combine(Program.BaseDir, "wwwroot");
            var full = Path.GetFullPath(Path.Combine(www, relative));
            if (!full.StartsWith(Path.GetFullPath(www) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                WriteError(ctx, 403, "Forbidden");
                return;
            }
            ServeFile(ctx, full);
        }

        internal static void WriteJson(HttpListenerContext ctx, int code, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        internal static void WriteText(HttpListenerContext ctx, int code, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        internal static void WriteError(HttpListenerContext ctx, int code, string message)
        {
            WriteJson(ctx, code, "{\"error\":" + JsonEncode(message) + "}");
        }

        internal static string JsonEncode(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"'); // 字符串值必须用双引号包裹
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        internal static string MimeFor(string path)
        {
            var ext = Path.GetExtension(path);
            switch (ext.ToLowerInvariant())
            {
                case ".css": return "text/css";
                case ".js": return "application/javascript";
                case ".json": return "application/json";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".html": return "text/html; charset=utf-8";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}