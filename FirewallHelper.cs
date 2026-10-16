using System;
using System.Diagnostics;

namespace PrintServer
{
    /// <summary>
    /// Windows 防火墙助手：为自身监听端口添加入站允许规则（TCP）。
    /// 基于 netsh advfirewall 命令（需要管理员权限），与 NetworkConfig 同一范式。
    /// </summary>
    internal static class FirewallHelper
    {
        /// <summary>返回结果：success + message（错误时为原因）。</summary>
        public sealed class Result
        {
            public bool Success;
            public string Message;
        }

        /// <summary>防火墙入站规则名（按端口区分，便于多个端口并存）。</summary>
        private static string RuleName(int port) => $"PrintServer_In_{port}";

        /// <summary>
        /// 为自身监听端口在 Windows 防火墙添加 TCP 入站允许规则。
        /// 幂等：同名规则已存在则跳过。需要管理员权限。
        /// </summary>
        public static Result EnsurePortOpen(int port)
        {
            try
            {
                if (!Program.IsAdministrator())
                    return new Result { Success = false, Message = "需要管理员权限才能配置防火墙（以管理员身份运行后自动开放）" };

                var ruleName = RuleName(port);

                // 已存在则跳过（避免重复添加 / 需要管理员时反复弹 UAC）
                if (RuleExists(ruleName))
                    return new Result { Success = true, Message = $"防火墙端口 {port} 已开放（规则已存在）" };

                // 添加入站规则: TCP / 本地端口 port / 所有网络配置文件 / 允许
                var args = $"advfirewall firewall add rule name=\"{ruleName}\" " +
                           $"dir=in action=allow protocol=TCP localport={port} profile=any";
                var r = RunNetsh(args);
                if (r.Success)
                    return new Result { Success = true, Message = $"已在防火墙开放端口 {port}（入站 TCP 允许）" };
                return r;
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = "开放防火墙端口失败: " + ex.Message };
            }
        }

        /// <summary>查询指定端口的防火墙入站规则是否存在。</summary>
        private static bool RuleExists(string ruleName)
        {
            var r = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
            // 规则存在时 netsh 成功列出规则；不存在时返回码非 0（中文/英文均如此）
            return r.Success && !ContainsNotFound(r.Message);
        }

        /// <summary>
        /// 不同 Windows 语言下 "规则不存在" 的表述不同，做兜底匹配。
        /// 命中否定词即视为不存在。
        /// </summary>
        private static bool ContainsNotFound(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.Contains("没有找到") || msg.Contains("找不到") ||
                   msg.Contains("No rules") || msg.Contains("does not exist") ||
                   msg.Contains("No matching");
        }

        private static Result RunNetsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return new Result { Success = false, Message = "netsh 启动失败" };
                    var stdout = p.StandardOutput.ReadToEnd();
                    var stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } }

                    var msg = (stdout + stderr).Trim();
                    if (p.ExitCode == 0)
                        return new Result { Success = true, Message = string.IsNullOrEmpty(msg) ? "(ok)" : msg };

                    // 常见错误: 拒绝访问(需要管理员)
                    if (msg.Contains("拒绝访问") || msg.Contains("Access is denied"))
                        return new Result { Success = false, Message = "需要管理员权限执行防火墙配置" };
                    return new Result { Success = false, Message = string.IsNullOrEmpty(msg) ? $"netsh 返回码 {p.ExitCode}" : msg };
                }
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = "netsh 执行失败: " + ex.Message };
            }
        }
    }
}
