using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PrintServer
{
    /// <summary>
    /// 网络配置：查询本机 IP、设置静态 IP、启用 DHCP。
    /// 基于 netsh 命令（需要管理员权限），与 Python 原版功能对应。
    /// </summary>
    internal static class NetworkConfig
    {
        /// <summary>返回结果：success + message（错误时为原因）。</summary>
        public sealed class Result
        {
            public bool Success;
            public string Message;
        }

        /// <summary>获取主局域网 IPv4 地址（非回环）。</summary>
        public static string GetPrimaryIPv4()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.IsLoopback(addr.Address))
                            return addr.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        /// <summary>获取活动且有效的网络适配器名（用于 netsh 接口名）。</summary>
        private static string FindActiveAdapterName()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.IsLoopback(addr.Address))
                            return nic.Description; // netsh 用 Description 作接口名
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>将当前 IP 设置为静态（使用当前子网/网关）。</summary>
        public static Result SetCurrentIpStatic()
        {
            try
            {
                var adapter = FindActiveAdapterName();
                if (string.IsNullOrEmpty(adapter))
                    return new Result { Success = false, Message = "未找到活动网络适配器" };

                var ip = GetPrimaryIPv4();
                if (ip == "127.0.0.1")
                    return new Result { Success = false, Message = "当前无有效局域网 IP" };

                // 查询当前子网掩码和网关
                string mask = "255.255.255.0";
                string gateway = "";
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.Description != adapter) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork && addr.Address.ToString() == ip)
                        {
                            mask = addr.IPv4Mask?.ToString() ?? mask;
                        }
                    }
                    foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            gateway = gw.Address.ToString();
                            break;
                        }
                    }
                }

                // netsh interface ip set address name="接口" static ip mask gateway
                var args = $"interface ip set address name=\"{adapter}\" static {ip} {mask}"
                           + (string.IsNullOrEmpty(gateway) ? "" : $" {gateway}");

                var r = RunNetsh(args);
                if (r.Success)
                    return new Result { Success = true, Message = $"已将 {ip} 设置为静态 IP" };
                return r;
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = "设置静态IP失败: " + ex.Message };
            }
        }

        /// <summary>启用 DHCP（自动获取 IP）。</summary>
        public static Result EnableDhcp()
        {
            try
            {
                var adapter = FindActiveAdapterName();
                if (string.IsNullOrEmpty(adapter))
                    return new Result { Success = false, Message = "未找到活动网络适配器" };

                // 启用 DHCP
                var r1 = RunNetsh($"interface ip set address name=\"{adapter}\" source=dhcp");
                if (!r1.Success) return r1;

                // 同时启用 DNS 自动获取
                var r2 = RunNetsh($"interface ip set dns name=\"{adapter}\" source=dhcp");
                if (!r2.Success && !r2.Message.Contains("无法完成")) // DNS 失败不完全阻止
                    return r2;

                return new Result { Success = true, Message = "已启用 DHCP，正在获取新 IP..." };
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = "启用DHCP失败: " + ex.Message };
            }
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

                    if (p.ExitCode == 0)
                        return new Result { Success = true, Message = (stdout + stderr).Trim() };

                    // 常见错误: 拒绝访问(需要管理员)
                    var msg = (stdout + stderr).Trim();
                    if (msg.Contains("拒绝访问") || msg.Contains("Access is denied"))
                        return new Result { Success = false, Message = "需要管理员权限执行网络配置" };
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