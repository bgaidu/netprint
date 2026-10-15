using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PrintServer
{
    /// <summary>关于对话框：显示软件信息与作者。</summary>
    internal sealed class AboutDialog : Form
    {
        public AboutDialog()
        {
            // 窗口设置
            Text = "关于";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 300);
            BackColor = Color.White;

            // 图标（左上角）
            var iconBox = new PictureBox
            {
                Size = new Size(64, 64),
                Location = new Point(28, 28),
                SizeMode = PictureBoxSizeMode.Zoom,
            };
            try { iconBox.Image = Icon.ToBitmap(); } catch { }
            Controls.Add(iconBox);

            // 名称
            var lblName = new Label
            {
                Text = "内网打印及扫描服务",
                Font = new Font("微软雅黑", 14F, FontStyle.Bold),
                Location = new Point(110, 30),
                AutoSize = true,
                ForeColor = Color.FromArgb(0, 102, 204),
            };
            Controls.Add(lblName);

            // 版本
            var lblVersion = new Label
            {
                Text = "C# 原生版 v1.0  (.NET Framework 4.8)",
                Font = new Font("微软雅黑", 9F),
                Location = new Point(110, 62),
                AutoSize = true,
                ForeColor = Color.Gray,
            };
            Controls.Add(lblVersion);

            // 分隔线
            var line = new Label
            {
                Text = "—————————————————————————————",
                ForeColor = Color.LightGray,
                Location = new Point(24, 108),
                AutoSize = true,
            };
            Controls.Add(line);

            // 作者
            var lblAuthor = new Label
            {
                Text = "作者：Bgaidu & AI",
                Font = new Font("微软雅黑", 10.5F),
                Location = new Point(28, 132),
                AutoSize = true,
            };
            Controls.Add(lblAuthor);

            // 说明
            var lblDesc = new Label
            {
                Text = "局域网内共享打印/扫描服务，支持 Win7/10/11。\n" +
                       "返回目录下可自由使用、修改。",
                Font = new Font("微软雅黑", 9F),
                Location = new Point(28, 162),
                AutoSize = true,
                ForeColor = Color.DimGray,
            };
            Controls.Add(lblDesc);

            // GitHub 链接
            var link = new LinkLabel
            {
                Text = "GitHub: github.com/bgaidu/netprint",
                Location = new Point(28, 216),
                AutoSize = true,
                LinkColor = Color.FromArgb(0, 102, 204),
            };
            link.LinkClicked += (s, e) =>
            {
                try { Process.Start("https://github.com/bgaidu/netprint"); } catch { }
            };
            Controls.Add(link);

            // 确定按钮
            var btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Size = new Size(90, 30),
                Location = new Point(290, 246),
            };
            Controls.Add(btnOk);
            AcceptButton = btnOk;
        }
    }
}