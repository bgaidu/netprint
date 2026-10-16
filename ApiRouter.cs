using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace PrintServer
{
    /// <summary>API 路由分发：/api/xxx</summary>
    internal static class ApiRouter
    {
        public static void Handle(HttpListenerContext ctx)
        {
            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');

            try
            {
                // ===== 密码认证接口 =====
                if (path == "/api/auth/check" && method == "GET")
                {
                    // 前端登录时检查密码是否正确（payload: password）
                    var pwd = ctx.Request.Headers["X-Auth-Password"] ?? "";
                    var ok = Settings.CheckPassword(pwd);
                    WebServer.WriteJson(ctx, 200,
                        "{\"enabled\":" + (Settings.PasswordEnabled ? "true" : "false") + ",\"ok\":" + (ok ? "true" : "false") + "}");
                    return;
                }

                // ===== 健康检查 =====
                if (path == "/api/health")
                {
                    var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    // lan_ip: 服务端局域网 IP，供前端二维码/快捷方式使用（不依赖浏览器 location.origin）
                    var lanIp = NetworkConfig.GetPrimaryIPv4();
                    WebServer.WriteJson(ctx, 200,
                        "{\"service_running\":true,\"status\":\"ok\",\"port\":" + Program.Port +
                        ",\"version\":\"" + ver + "\",\"lan_ip\":\"" + WebServer.JsonEncode(lanIp) + "\"}");
                    return;
                }

                // ===== 打印机 =====
                if (path == "/api/printers" && method == "GET")
                {
                    var printers = PrinterService.EnumeratePrinters();
                    WebServer.WriteJson(ctx, 200, printers);
                    return;
                }

                // ===== 打印 =====
                if (path == "/api/print" && method == "POST")
                {
                    HandlePrint(ctx);
                    return;
                }

                // ===== 上传文件管理 =====
                if (path == "/api/files" && method == "GET")
                {
                    WebServer.WriteJson(ctx, 200, FileStore.ListUploads());
                    return;
                }
                if (path.StartsWith("/api/files/") && method == "DELETE")
                {
                    var name = path.Substring("/api/files/".Length);
                    FileStore.DeleteUpload(name);
                    WebServer.WriteJson(ctx, 200, "{\"success\":true}");
                    return;
                }
                if (path == "/api/files/clear" && method == "POST")
                {
                    FileStore.ClearUploads();
                    WebServer.WriteJson(ctx, 200, "{\"success\":true}");
                    return;
                }

                // ===== 下载/预览上传文件 =====
                if (path.StartsWith("/uploads/") && method == "GET")
                {
                    var name = path.Substring("/uploads/".Length);
                    var full = FileStore.ResolveUpload(name);
                    if (full == null) { WebServer.WriteError(ctx, 404, "文件不存在"); return; }
                    WebServer.ServeFile(ctx, full);
                    return;
                }
                if (path.StartsWith("/preview/") && method == "GET")
                {
                    var name = path.Substring("/preview/".Length);
                    var full = FileStore.ResolveUpload(name);
                    if (full == null) { WebServer.WriteError(ctx, 404, "文件不存在"); return; }
                    // 仅图片可预览，其它返回提示
                    var ext = Path.GetExtension(name)?.ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".gif" || ext == ".pdf")
                        WebServer.ServeFile(ctx, full);
                    else
                        WebServer.WriteError(ctx, 400, "该类型不支持预览");
                    return;
                }

                // ===== 扫描仪 =====
                if (path == "/api/scanners" && method == "GET")
                {
                    WebServer.WriteJson(ctx, 200, ScannerService.ListScannersJson());
                    return;
                }
                if (path == "/api/scan" && method == "POST")
                {
                    var body = ReadBody(ctx, 512 * 1024);
                    var result = ScannerService.ScanJson(body);
                    // 内容日志: IP + 扫描仪 + 格式 + 结果
                    var fields = SimpleJson.Parse(body);
                    var ok = result != null && result.Contains("\"success\":true");
                    Log.Info($"扫描: IP={GetClientIpSafe(ctx)} 扫描仪={fields.Get("scannerId")} " +
                             $"格式={fields.Get("format")} DPI={fields.GetInt("dpi", 300)} 结果={(ok ? "成功" : "失败")}");
                    WebServer.WriteJson(ctx, 200, result);
                    return;
                }
                if (path == "/api/scanned" && method == "GET")
                {
                    WebServer.WriteJson(ctx, 200, FileStore.ListScanned());
                    return;
                }
                // 扫描文件重新打印: /api/scanned/<name>/print
                if (path.EndsWith("/print") && method == "POST" && path.StartsWith("/api/scanned/"))
                {
                    var name = path.Substring("/api/scanned/".Length, path.Length - "/api/scanned/".Length - "/print".Length);
                    HandleReprintScanned(ctx, name);
                    return;
                }
                if (path == "/api/scanned/clear" && method == "POST")
                {
                    FileStore.ClearScanned();
                    WebServer.WriteJson(ctx, 200, "{\"success\":true}");
                    return;
                }
                if (path.StartsWith("/api/scanned/") && method == "DELETE")
                {
                    var name = path.Substring("/api/scanned/".Length);
                    FileStore.DeleteScanned(name);
                    WebServer.WriteJson(ctx, 200, "{\"success\":true}");
                    return;
                }
                if (path.StartsWith("/api/scanned/") && method == "GET")
                {
                    // 下载扫描文件: /api/scanned/<name>
                    var name = path.Substring("/api/scanned/".Length);
                    var full = FileStore.ResolveScanned(name);
                    if (full == null) { WebServer.WriteError(ctx, 404, "文件不存在"); return; }
                    WebServer.ServeFile(ctx, full);
                    return;
                }

                // ===== 打印队列管理 =====
                if (path == "/api/queue" && method == "GET")
                {
                    // 查询打印队列: ?printer=xxx
                    var qp = ctx.Request.QueryString["printer"];
                    if (string.IsNullOrEmpty(qp)) qp = PrinterService.GetDefaultPrinter();
                    WebServer.WriteJson(ctx, 200, PrintQueueService.GetQueueJson(qp));
                    return;
                }
                if (path == "/api/queue/cancel" && method == "POST")
                {
                    // 取消任务: {"printer":"xxx","jobId":123}
                    var body = ReadBody(ctx, 1024 * 1024);
                    var j = SimpleJson.Parse(body);
                    var printer = j.Get("printer");
                    int jobId = j.GetInt("jobId", 0);
                    var ok = PrintQueueService.CancelJob(printer, jobId);
                    WebServer.WriteJson(ctx, 200,
                        "{\"success\":" + (ok ? "true" : "false") + ",\"message\":\""
                        + (ok ? "任务已取消" : "取消失败") + "\"}");
                    Log.Info($"取消打印任务: 打印机={printer} jobId={jobId} 结果={(ok ? "成功" : "失败")}");
                    return;
                }
                if (path == "/api/queue/clear" && method == "POST")
                {
                    // 清空队列: {"printer":"xxx"}
                    var body = ReadBody(ctx, 1024 * 1024);
                    var j = SimpleJson.Parse(body);
                    var printer = j.Get("printer");
                    var count = PrintQueueService.ClearQueue(printer);
                    WebServer.WriteJson(ctx, 200,
                        "{\"success\":true,\"cleared\":" + count + ",\"message\":\"已清空 " + count + " 个任务\"}");
                    Log.Info($"清空打印队列: 打印机={printer} 清除={count}");
                    return;
                }

                // ===== 日志管理 =====
                if (path == "/api/logs" && method == "GET")
                {
                    // 日志文件列表: [{name, date}]
                    var files = Log.ListLogFiles();
                    var sb = new System.Text.StringBuilder("[");
                    for (int i = 0; i < files.Count; i++)
                    {
                        var fname = files[i];
                        var date = fname.Length >= 18 ? fname.Substring(10, 8) : "";
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"name\":").Append(WebServer.JsonEncode(fname))
                          .Append(",\"date\":").Append(WebServer.JsonEncode(date)).Append('}');
                    }
                    sb.Append(']');
                    WebServer.WriteJson(ctx, 200, sb.ToString());
                    return;
                }
                if (path.StartsWith("/api/logs/") && method == "GET")
                {
                    // 读取某天日志: /api/logs/20261015
                    var date = path.Substring("/api/logs/".Length).Trim();
                    var content = Log.ReadDay(date);
                    if (content == null) { WebServer.WriteError(ctx, 404, "日志不存在"); return; }
                    // 转成 HTML 安全文本 + 换行
                    var escaped = WebServer.JsonEncode(content);
                    WebServer.WriteJson(ctx, 200, "{\"date\":" + WebServer.JsonEncode(date) + ",\"content\":" + escaped + "}");
                    return;
                }

                WebServer.WriteError(ctx, 404, "接口不存在: " + path);
            }
            catch (Exception ex)
            {
                Log.Error("API 错误 [" + path + "]: " + ex.Message);
                try { WebServer.WriteError(ctx, 500, ex.Message); } catch { }
            }
        }

        private static void HandlePrint(HttpListenerContext ctx)
        {
            var form = ReadMultipartOrForm(ctx);
            var printer = GetField(form, "printer");
            if (string.IsNullOrEmpty(printer))
                throw new InvalidOperationException("未选择打印机");

            int copies = 1, duplex = 1;
            int.TryParse(GetField(form, "copies"), out copies);
            int.TryParse(GetField(form, "duplex"), out duplex);
            if (copies < 1) copies = 1;
            if (copies > 99) copies = 99;
            var papersize = GetField(form, "papersize");
            var quality = GetField(form, "quality");

            var files = GetFiles(form);
            if (files.Count == 0)
                throw new InvalidOperationException("未选择文件");

            var results = new StringBuilder("[");
            bool first = true;
            var clientIp = GetClientIpSafe(ctx);
            foreach (var f in files)
            {
                if (!first) results.Append(',');
                first = false;
                try
                {
                    var saved = FileStore.SaveUpload(f.Name, f.Data);
                    var ok = PrinterService.PrintFile(saved, printer, copies, duplex, papersize, quality);
                    // 内容日志: IP + 文件 + 打印机 + 参数 + 结果
                    Log.Info($"打印: IP={clientIp} 文件={f.Name} 打印机={printer} 份数={copies} " +
                             $"双面={(duplex == 1 ? "单面" : "双面")} 纸张={papersize} 结果={(ok ? "成功" : "失败:" + PrinterService.LastError)}");
                    results.Append("{\"file\":").Append(WebServer.JsonEncode(f.Name))
                           .Append(",\"success\":").Append(ok ? "true" : "false")
                           .Append(",\"message\":").Append(WebServer.JsonEncode(PrinterService.LastError)).Append("}");
                }
                catch (Exception ex)
                {
                    Log.Error($"打印异常: IP={clientIp} 文件={f.Name} 错误={ex.Message}");
                    results.Append("{\"file\":").Append(WebServer.JsonEncode(f.Name))
                           .Append(",\"success\":false,\"message\":").Append(WebServer.JsonEncode(ex.Message)).Append("}");
                }
            }
            results.Append(']');
            WebServer.WriteJson(ctx, 200, "{\"results\":" + results.ToString() + "}");
        }

        /// <summary>扫描文件重新打印：复用已保存的扫描文件直接打印到指定打印机。</summary>
        private static void HandleReprintScanned(HttpListenerContext ctx, string name)
        {
            var full = FileStore.ResolveScanned(name);
            if (full == null)
            {
                WebServer.WriteError(ctx, 404, "文件不存在");
                return;
            }
            var content = ReadBody(ctx, 1024 * 1024);
            var json = SimpleJson.Parse(content);
            var printer = json.Get("printer");
            if (string.IsNullOrEmpty(printer))
            {
                printer = PrinterService.GetDefaultPrinter();
                if (string.IsNullOrEmpty(printer))
                {
                    WebServer.WriteError(ctx, 400, "未选择打印机且无默认打印机");
                    return;
                }
            }
            int copies = json.GetInt("copies", 1);
            int duplex = json.GetInt("duplex", 1);
            if (copies < 1) copies = 1;
            if (copies > 99) copies = 99;

            // 纸张大小由前端传入（9=A4 8=A3 11=A5 1=Letter 5=Legal）；为空则用打印机默认纸张
            var papersize = json.Get("papersize");

            var ok = PrinterService.PrintFile(full, printer, copies, duplex, papersize, "300x300", "scanned:" + name);
            Log.Info($"扫描文件重新打印: {name} -> {printer} 结果:{ok}");
            WebServer.WriteJson(ctx, 200,
                "{\"success\":" + (ok ? "true" : "false") +
                ",\"message\":" + WebServer.JsonEncode(ok ? "打印任务已发送" : PrinterService.LastError) + "}");
        }

        // ===== 请求体解析 =====

        /// <summary>获取客户端 IP（日志用）。</summary>
        private static string GetClientIpSafe(HttpListenerContext ctx)
        {
            try
            {
                return ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "?";
            }
            catch { return "?"; }
        }

        internal static string ReadBody(HttpListenerContext ctx, int maxBytes = WebServer.MaxBodyBytes)
        {
            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192];
                int total = 0;
                int n;
                while ((n = ctx.Request.InputStream.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > maxBytes) throw new InvalidOperationException("请求体过大");
                    ms.Write(buf, 0, n);
                    if (ctx.Request.ContentLength64 >= 0 && total >= ctx.Request.ContentLength64)
                        break;
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>解析表单字段（application/x-www-form-urlencoded 或 JSON）。</summary>
        private static Dictionary<string, string> GetField(string body, string name)
        {
            // 简化: 支持 key=value&key2=value2
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(body)) return dict;
            foreach (var pair in body.Split('&'))
            {
                var idx = pair.IndexOf('=');
                if (idx < 0) continue;
                var k = Uri.UnescapeDataString(pair.Substring(0, idx));
                var v = Uri.UnescapeDataString(pair.Substring(idx + 1));
                dict[k] = v;
            }
            return dict;
        }

        internal static string GetField(IDictionary<string, string> dict, string name)
        {
            string v;
            return dict != null && dict.TryGetValue(name, out v) ? v : null;
        }

        /// <summary>解析 multipart/form-data（上传文件）。</summary>
        internal static List<UploadFile> GetFiles(IDictionary<string, string> dict)
        {
            var list = new List<UploadFile>();
            if (dict == null) return list;
            foreach (var kv in dict)
            {
                if (kv.Key.StartsWith("@file:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = kv.Value.Split(new[] { '\u0001' }, 2);
                    if (parts.Length == 2 && parts[1].Length > 0)
                        list.Add(new UploadFile { Name = parts[0], Data = System.Convert.FromBase64String(parts[1]) });
                }
            }
            return list;
        }

        private static Dictionary<string, string> ReadMultipartOrForm(HttpListenerContext ctx)
        {
            var ct = ctx.Request.ContentType ?? "";
            if (ct.IndexOf("multipart/form-data", StringComparison.OrdinalIgnoreCase) >= 0)
                return MultipartParser.Parse(ctx);
            // fallback: 直接读文本（前端可序列化为 base64 表单）
            var body = ReadBody(ctx);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in body.Split('&'))
            {
                var idx = pair.IndexOf('=');
                if (idx < 0) continue;
                dict[Uri.UnescapeDataString(pair.Substring(0, idx))] = Uri.UnescapeDataString(pair.Substring(idx + 1));
            }
            return dict;
        }
    }

    internal sealed class UploadFile
    {
        public string Name;
        public byte[] Data;
    }

    /// <summary>字节级 multipart/form-data 解析器（正确处理二进制文件内容）。</summary>
    internal static class MultipartParser
    {
        private static int IndexOf(byte[] haystack, int start, byte[] needle)
        {
            if (needle.Length == 0) return -1;
            int max = haystack.Length - needle.Length;
            for (int i = start; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        public static Dictionary<string, string> Parse(HttpListenerContext ctx)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var contentType = ctx.Request.ContentType ?? "";
            var boundaryMatch = System.Text.RegularExpressions.Regex.Match(contentType, "boundary=(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!boundaryMatch.Success) return dict;

            var boundary = Encoding.UTF8.GetBytes("--" + boundaryMatch.Groups[1].Value.Trim('"'));
            var body = new byte[0];
            using (var ms = new MemoryStream())
            {
                var buf = new byte[65536];
                int n;
                long total = 0;
                while ((n = ctx.Request.InputStream.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > WebServer.MaxBodyBytes) throw new InvalidOperationException("上传文件过大(>100MB)");
                    ms.Write(buf, 0, n);
                }
                body = ms.ToArray();
            }

            // 按字节找所有 boundary 位置
            var positions = new List<int>();
            int pos = 0;
            while (true)
            {
                pos = IndexOf(body, pos, boundary);
                if (pos < 0) break;
                positions.Add(pos);
                pos += boundary.Length;
            }

            var crlf = new byte[] { (byte)'\r', (byte)'\n' };
            for (int i = 0; i < positions.Count; i++)
            {
                int start = positions[i] + boundary.Length;
                // 跳过 boundary 后的 \r\n
                if (start + 1 < body.Length && body[start] == '\r' && body[start + 1] == '\n')
                    start += 2;
                else if (start < body.Length && body[start] == '\n')
                    start++;

                // 结束: 下一个 boundary 或 -- 结尾
                int end = (i + 1 < positions.Count) ? positions[i + 1] : body.Length;
                // 去掉末尾 \r\n（属于上一段）
                if (end - 2 >= start && body[end - 2] == '\r' && body[end - 1] == '\n')
                    end -= 2;

                if (end <= start) continue;

                // 找 header 与内容分隔 \r\n\r\n
                var sep = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
                int headerEnd = IndexOf(body, start, sep);
                if (headerEnd < 0 || headerEnd >= end) continue;

                var headerStr = Encoding.UTF8.GetString(body, start, headerEnd - start);
                var dataStart = headerEnd + 4;
                var dataLen = Math.Max(0, end - dataStart);

                var nameMatch = System.Text.RegularExpressions.Regex.Match(headerStr, "name=\"([^\"]*)\"");
                if (!nameMatch.Success) continue;
                var name = nameMatch.Groups[1].Value;
                var filenameMatch = System.Text.RegularExpressions.Regex.Match(headerStr, "filename=\"([^\"]*)\"");

                if (filenameMatch.Success)
                {
                    var filename = filenameMatch.Groups[1].Value;
                    var data = new byte[dataLen];
                    Array.Copy(body, dataStart, data, 0, dataLen);
                    dict["@file:" + name] = filename + "\u0001" + System.Convert.ToBase64String(data);
                }
                else
                {
                    var val = Encoding.UTF8.GetString(body, dataStart, dataLen);
                    dict[name] = val;
                }
            }
            return dict;
        }
    }
}