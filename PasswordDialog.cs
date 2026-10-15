using System;
using System.Drawing;
using System.Windows.Forms;

namespace PrintServer
{
    /// <summary>网页访问密码设置对话框（可设置/修改/关闭）。</summary>
    internal sealed class PasswordDialog : Form
    {
        private readonly TextBox _txtNew = new TextBox();
        private readonly TextBox _txtConfirm = new TextBox();

        /// <summary>设置后的密码；空字符串 = 关闭密码。</summary>
        public string Password { get; private set; } = "";

        public PasswordDialog()
        {
            Text = "网页访问密码";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(360, 190);
            BackColor = Color.White;

            var lblInfo = new Label
            {
                Text = "设置网页访问密码（留空并确认则关闭密码）",
                Location = new Point(16, 12),
                AutoSize = true,
                Font = new Font("微软雅黑", 9F),
                ForeColor = Color.DimGray,
            };
            Controls.Add(lblInfo);

            var lblNew = new Label { Text = "新密码:", Location = new Point(16, 48), AutoSize = true };
            Controls.Add(lblNew);
            _txtNew.Location = new Point(80, 44);
            _txtNew.Width = 260;
            _txtNew.UseSystemPasswordChar = true;
            Controls.Add(_txtNew);

            var lblConfirm = new Label { Text = "确认:", Location = new Point(16, 82), AutoSize = true };
            Controls.Add(lblConfirm);
            _txtConfirm.Location = new Point(80, 78);
            _txtConfirm.Width = 260;
            _txtConfirm.UseSystemPasswordChar = true;
            Controls.Add(_txtConfirm);

            var btnOk = new Button { Text = "确定", Size = new Size(90, 30), Location = new Point(140, 140), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Size = new Size(90, 30), Location = new Point(248, 140), DialogResult = DialogResult.Cancel };
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // 显示当前状态
            lblInfo.Text = Settings.PasswordEnabled
                ? "当前已启用密码。输入新密码修改；留空并确定则关闭密码。"
                : "当前未启用密码。输入密码后保存即启用。";

            btnOk.Click += (s, e) =>
            {
                var pwd = _txtNew.Text ?? "";
                if (pwd != (_txtConfirm.Text ?? ""))
                {
                    MessageBox.Show("两次输入的密码不一致", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _txtNew.Focus();
                    return; // 不设 DialogResult，窗口不关闭
                }
                if (pwd.Length > 0 && pwd.Length < 4)
                {
                    MessageBox.Show("密码至少 4 位（留空则关闭密码）", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _txtNew.Focus();
                    return;
                }
                Password = pwd;
                DialogResult = DialogResult.OK; // 校验通过才关闭
            };
        }
    }
}