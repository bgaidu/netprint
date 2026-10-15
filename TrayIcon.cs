using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PrintServer
{
    /// <summary>系统托盘图标与右键菜单：打开网页、开机自启、更改端口、日志、重启、退出。</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;

        public void Show()
        {
            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = $"内网打印服务 :{Program.Port}",
                Visible = true,
            };

            _menu = BuildMenu();
            _icon.ContextMenuStrip = _menu;
            _icon.DoubleClick += (s, e) => OnOpenHome(s, e);
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add($"服务地址: {GetLocalIp()}:{Program.Port}", null, OnOpenHome);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("打开网页", null, OnOpenHome);
            m.Items.Add("查看日志", null, OnViewLog);
            m.Items.Add("清理日志", null, OnCleanLogs);
            m.Items.Add("重新启动服务", null, OnRestart);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("开机自启：" + (AutoStart.IsEnabled() ? "已开启" : "未开启"), null, OnToggleAutostart);
            m.Items.Add("更改端口...", null, OnChangePort);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("退出", null, OnExit);
            return m;
        }

        /// <summary>重建菜单并刷新图标文字（开机自启状态、端口变化后调用）。</summary>
        private void Refresh()
        {
            _icon.Text = $"内网打印服务 :{Program.Port}";
            _icon.ContextMenuStrip = BuildMenu();
        }

        private Icon LoadIcon()
        {
            try
            {
                return new Icon(Path.Combine(Program.BaseDir, "logo.ico"));
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private static string GetLocalIp()
        {
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.IsLoopback(addr.Address))
                        {
                            return addr.Address.ToString();
                        }
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private void OnOpenHome(object sender, EventArgs e)
        {
            try { Process.Start($"http://{GetLocalIp()}:{Program.Port}/"); } catch { }
        }

        private void OnViewLog(object sender, EventArgs e)
        {
            try { Process.Start("notepad.exe", Program.LogFile); } catch { }
        }

        private void OnCleanLogs(object sender, EventArgs e)
        {
            try
            {
                if (File.Exists(Program.LogFile))
                {
                    File.WriteAllText(Program.LogFile, "");
                    MessageBox.Show("日志已清空", "打印服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("清理日志失败: " + ex.Message, "打印服务", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnToggleAutostart(object sender, EventArgs e)
        {
            if (AutoStart.IsEnabled())
            {
                if (AutoStart.Disable())
                    MessageBox.Show("已关闭开机自启", "打印服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                if (AutoStart.Enable())
                    MessageBox.Show("已开启开机自启", "打印服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show("设置开机自启失败(可能无写入注册表权限)", "打印服务", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Refresh();
        }

        private void OnChangePort(object sender, EventArgs e)
        {
            var current = Program.Port;
            var input = Microsoft.VisualBasic.Interaction.InputBox(
                "请输入新端口(1024-65535):", "更改端口", current.ToString());
            if (string.IsNullOrWhiteSpace(input)) return; // 取消

            int newPort;
            if (!int.TryParse(input.Trim(), out newPort) || newPort < 1024 || newPort > 65535)
            {
                MessageBox.Show("端口无效，请输入 1024-65535 之间的数字", "打印服务",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (newPort == current) return;

            try
            {
                Program.ChangePort(newPort);
                MessageBox.Show($"端口已改为 {newPort}，服务已重启", "打印服务",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show("更改端口失败: " + ex.Message, "打印服务",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnRestart(object sender, EventArgs e)
        {
            try
            {
                Program.RestartServer();
                _icon.Text = $"内网打印服务 :{Program.Port}";
                MessageBox.Show("服务已重新启动", "打印服务", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("重启失败: " + ex.Message, "打印服务", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            Application.Exit();
        }

        public void Dispose()
        {
            _icon?.Dispose();
            _menu?.Dispose();
        }
    }
}