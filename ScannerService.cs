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

        /// <summary>执行扫描：body 为 JSON，含 scannerId、format(png/jpg)、dpi。</summary>
        public static string ScanJson(string body)
        {
            LastError = "";
            try
            {
                // 解析简单 JSON: {"scannerId":"1","format":"png","dpi":300}
                var fields = SimpleJson.Parse(body);
                string id = fields.Get("scannerId");
                string format = fields.Get("format") ?? "png";
                int dpi = fields.GetInt("dpi", 300);

                if (string.IsNullOrEmpty(id))
                    return "{\"success\":false,\"error\":\"未指定扫描仪\"}";

                Type mgrType = Type.GetTypeFromProgID("WIA.DeviceManager");
                if (mgrType == null)
                    return "{\"success\":false,\"error\":\"WIA 不可用\"}";

                dynamic mgr = Activator.CreateInstance(mgrType);
                dynamic device = null;
                try
                {
                    // 按索引连接（1-based）
                    device = mgr.DeviceInfos[int.Parse(id)].Connect();

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
                    return "{\"success\":false,\"error\":\"无法连接扫描仪\"}";
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
                return "{\"success\":false,\"error\":" + WebServer.JsonEncode(ex.Message) + "}";
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