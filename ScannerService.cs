using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PrintServer
{
    /// <summary>WIA 扫描服务：枚举扫描仪、执行扫描。</summary>
    internal static class ScannerService
    {
        public static string LastError = "";

        /// <summary>列出扫描仪（JSON 数组）。</summary>
        public static string ListScannersJson()
        {
            var sb = new System.Text.StringBuilder("[");
            bool first = true;
            try
            {
                Type mgrType = Type.GetTypeFromProgID("WIA.DeviceManager");
                if (mgrType == null)
                {
                    return "{\"error\":\"WIA 不可用\"}";
                }
                dynamic mgr = Activator.CreateInstance(mgrType);
                int count = mgr.DeviceInfos.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic info = mgr.DeviceInfos[i];
                    string name = "";
                    try { name = info.Properties["Name"].Value; } catch { }
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(i)
                      .Append(",\"name\":").Append(WebServer.JsonEncode(name)).Append('}');
                }
                Marshal.ReleaseComObject(mgr);
            }
            catch (Exception ex)
            {
                return "{\"error\":" + WebServer.JsonEncode(ex.Message) + "}";
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>执行扫描：body 为 JSON，含 scannerId、format(png/jpg)、dpi。
        /// 失败时自动释放扫描仪（重启 WIA 服务）并重试一次。</summary>
        public static string ScanJson(string body)
        {
            LastError = "";
            // 设备互斥：等待打印完成再扫描（防止一体机设备冲突）
            if (!DeviceMutex.TryEnterScan())
            {
                return "{\"success\":false,\"error\":\"设备忙：正在打印中，请稍后重试\"}";
            }
            try
            {
                // 解析简单 JSON: {"scannerId":"1","format":"png","dpi":300}
                var fields = SimpleJson.Parse(body);
                string id = fields.Get("scannerId");
                string format = fields.Get("format") ?? "png";
                int dpi = fields.GetInt("dpi", 300);

                if (string.IsNullOrEmpty(id))
                    return "{\"success\":false,\"error\":\"未指定扫描仪\"}";

                int deviceIndex;
                if (!int.TryParse(id, out deviceIndex) || deviceIndex < 1)
                    return "{\"success\":false,\"error\":\"扫描仪编号无效\"}";

                // 第一次尝试
                var result = DoScan(deviceIndex, format, dpi);
                if (result != null) return result;

                // 失败: 仅当错误属于"设备存在但被锁定/占用"时才释放扫描仪并重试
                // (WIA 不可用/无设备/索引越界 属于环境问题，无需重启服务)
                if (IsLockRelatedError(LastError))
                {
                    Log.Warn("扫描失败(疑似设备被锁定)，尝试释放扫描仪后重试...");
                    LastError = "";
                    ReleaseScanner();
                    var retry = DoScan(deviceIndex, format, dpi);
                    if (retry != null) return retry;
                }

                return "{\"success\":false,\"error\":" + WebServer.JsonEncode(LastError) + "}";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return "{\"success\":false,\"error\":" + WebServer.JsonEncode(ex.Message) + "}";
            }
            finally
            {
                DeviceMutex.ReleaseScan();
            }
        }

        /// <summary>实际执行扫描；成功返回 JSON，失败返回 null 并设置 LastError。</summary>
        private static string DoScan(int deviceIndex, string format, int dpi)
        {
            try
            {
                Type mgrType = Type.GetTypeFromProgID("WIA.DeviceManager");
                if (mgrType == null)
                {
                    LastError = "WIA 不可用";
                    return null;
                }

                dynamic mgr = Activator.CreateInstance(mgrType);
                dynamic device = null;
                try
                {
                    // 按索引连接（1-based）
                    device = mgr.DeviceInfos[deviceIndex].Connect();

                    // 扫描配置
                    if (device != null)
                    {
                        // 设置 DPI
                        SetProperty(device, "HorizontalResolution", dpi);
                        SetProperty(device, "VerticalResolution", dpi);
                        SetProperty(device, "CurrentIntent", 4 /* 彩色 */);

                        dynamic item = device.Items[1];
                        var dir = Program.ScannedDir;
                        Directory.CreateDirectory(dir);
                        string ext = format == "jpg" ? "jpg" : "png";
                        string filename = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_scan." + ext;
                        string full = Path.Combine(dir, filename);

                        // 使用 WIA 的 Transfer -> 保存
                        dynamic imgFile = item.Transfer();
                        try
                        {
                            imgFile.SaveFile(full);
                        }
                        finally
                        {
                            try { Marshal.ReleaseComObject(imgFile); } catch { }
                        }

                        Log.Info($"扫描完成: {filename} ({dpi}dpi, {format})");
                        return "{\"success\":true,\"file\":\"" + filename + "\",\"path\":\"scanned\"}";
                    }
                    LastError = "无法连接扫描仪";
                    return null;
                }
                finally
                {
                    try { if (device != null) Marshal.ReleaseComObject(device); } catch { }
                    try { Marshal.ReleaseComObject(mgr); } catch { }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        /// <summary>判断错误是否属于"设备被锁定/占用"（这类才需要释放扫描仪）。</summary>
        private static bool IsLockRelatedError(string error)
        {
            if (string.IsNullOrEmpty(error)) return false;
            var e = error.ToLowerInvariant();
            // 无设备/环境问题不释放
            if (e.Contains("wia 不可用") || e.Contains("索引超出范围") || e.Contains("index out of range")) return false;
            // 锁定/占用/总线错误 才释放
            return e.Contains("busy") || e.Contains("locked") || e.Contains("占用") ||
                   e.Contains("锁") || e.Contains("0x8000") || e.Contains("e_busy") ||
                   e.Contains("device") || e.Contains("无法连接") || e.Contains("error");
        }

        /// <summary>
        /// 释放被锁定的扫描仪：(1) 结束占用 WIA 的进程 (2) 重启 WIA 服务 (3) 内存回收。
        /// 与 Python 原版的"自动释放被锁定扫描仪"对应。
        /// </summary>
        public static void ReleaseScanner()
        {
            try
            {
                Log.Info("释放扫描仪: 清理 WIA 占用...");
                // 1. 结束可能的扫描残留进程（Windows 扫描/照片应用等）
                foreach (var procName in new[] { "WindowsCamera", "WindowsScan", "WiaScanner" })
                {
                    try
                    {
                        foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
                            try { p.Kill(); } catch { }
                    }
                    catch { }
                }

                // 2. 重启 WIA 服务 (stisvc)
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = "stop stisvc",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (var p = System.Diagnostics.Process.Start(psi))
                        if (p != null) p.WaitForExit(15000);
                }
                catch { }

                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "net",
                        Arguments = "start stisvc",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (var p = System.Diagnostics.Process.Start(psi))
                        if (p != null) p.WaitForExit(15000);
                }
                catch { }

                // 3. 强制内存回收
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Log.Info("扫描仪释放完成");
            }
            catch (Exception ex)
            {
                Log.Warn("释放扫描仪异常: " + ex.Message);
            }
        }

        private static void SetProperty(dynamic item, string propName, object value)
        {
            try
            {
                var props = item.Properties;
                for (int i = 1; i <= props.Count; i++)
                {
                    try
                    {
                        dynamic p = props[i];
                        if (p.Name == propName)
                        {
                            p.Value = value;
                            return;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    /// <summary>极简 JSON 对象解析（key/value）。</summary>
    internal static class SimpleJson
    {
        public static SimpleJsonObject Parse(string json)
        {
            var obj = new SimpleJsonObject();
            if (string.IsNullOrEmpty(json)) return obj;
            // {"a":"b","c":123} 极简解析
            int i = json.IndexOf('{');
            int j = json.LastIndexOf('}');
            if (i < 0 || j <= i) return obj;
            var inner = json.Substring(i + 1, j - i - 1);
            foreach (var pair in inner.Split(','))
            {
                var idx = pair.IndexOf(':');
                if (idx < 0) continue;
                var key = pair.Substring(0, idx).Trim().Trim('"');
                var val = pair.Substring(idx + 1).Trim();
                if (val.StartsWith("\"") && val.EndsWith("\""))
                    val = val.Substring(1, val.Length - 2);
                obj.Set(key, val);
            }
            return obj;
        }
    }

    internal sealed class SimpleJsonObject
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _map =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void Set(string key, string value)
        {
            if (!string.IsNullOrEmpty(key)) _map[key] = value;
        }

        public string Get(string key)
        {
            string v;
            return _map.TryGetValue(key, out v) ? v : null;
        }

        public int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key), out v) ? v : def;
        }
    }
}