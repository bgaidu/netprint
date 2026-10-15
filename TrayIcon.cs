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

            // 网络配置子菜单（需要管理员权限执行 netsh）
            var netMenu = new ToolStripMenuItem("网络配置（需要管理员权限）");
            netMenu.DropDownItems.Add("设置当前IP为静态", null, OnSetStaticIp);
            netMenu.DropDownItems.Add("启用DHCP", null, OnEnableDhcp);
            m.Items.Add(netMenu);

            m.Items.Add("打开网页", null, OnOpenHome);
            m.Items.Add("查看日志", null, OnViewLog);
            m.Items.Add("清理日志", null, OnCleanLogs);
            m.Items.Add("重新启动服务", null, OnRestart);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("开机自启：" + (AutoStart.IsEnabled() ? "已开启" : "未开启"), null, OnToggleAutostart);
            m.Items.Add("更改端口...", null, OnChangePort);
            m.Items.Add("网页密码：" + (Settings.PasswordEnabled ? "已启用" : "未启用"), null, OnSetPassword);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("关于", null, OnAbout);
            m.Items.Add("退出", null, OnExit);
            return m;
        }

        /// <summary>设置/修改/关闭网页访问密码。</summary>
        private void OnSetPassword(object sender, EventArgs e)
        {
            try
            {
                using (var dlg = new PasswordDialog())
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        Settings.WebPassword = dlg.Password;
                        Settings.Save();
                        Refresh();
                        MessageBox.Show(dlg.Password.Length == 0
                            ? "已关闭网页密码"
                            : "网页密码已设置，下次访问网页需要输入", "打印服务",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置密码失败: " + ex.Message, "打印服务",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void OnSetStaticIp(object sender, EventArgs e)
        {
            try
            {
                var r = NetworkConfig.SetCurrentIpStatic();
                MessageBox.Show(r.Message, "网络配置",
                    MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("操作失败: " + ex.Message, "网络配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void OnEnableDhcp(object sender, EventArgs e)
        {
            try
            {
                var r = NetworkConfig.EnableDhcp();
                MessageBox.Show(r.Message, "网络配置",
                    MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("操作失败: " + ex.Message, "网络配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>重建菜单并刷新图标文字（开机自启状态、端口变化后调用）。</summary>
        private void Refresh()
        {
            _icon.Text = $"内网打印服务 :{Program.Port}";
            _icon.ContextMenuStrip = BuildMenu();
        }

        private Icon LoadIcon()
        {
            // 单文件部署: 优先从嵌入资源加载 logo.ico
            var bytes = ResourceManager.ReadBytes("logo.ico");
            if (bytes != null)
            {
                try
                {
                    using (var ms = new MemoryStream(bytes))
                        return new Icon(ms);
                }
                catch { }
            }
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

        private void OnAbout(object sender, EventArgs e)
        {
            try
            {
                using (var dlg = new AboutDialog())
                {
                    dlg.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开关于窗口失败: " + ex.Message, "打印服务",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void Dispose()
        {
            _icon?.Dispose();
            _menu?.Dispose();
        }
    }
}