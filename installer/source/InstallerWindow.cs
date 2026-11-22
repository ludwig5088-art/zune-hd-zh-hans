using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class InstallerProgram
{
    internal static string BackupDirectory(string ip)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZuneChinesePilot", "backups", ip);
    }

    internal static string Run(string ip, string operation, Action<string> output)
    {
        IPAddress parsed;
        if (!IPAddress.TryParse(ip, out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("请输入播放器的 IPv4 地址，例如 192.168.2.20。");
        ip = parsed.ToString();
        using (var mutex = new Mutex(false, "Global\\ZuneChinesePilot-" + ip))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("这台电脑已有一个安装器正在操作此播放器。");
            try
            {
                var transport = new LyraFiles(ip, 1337, 524288);
                var installer = new BundleInstaller(transport, Path.Combine(BackupDirectory(ip), "full-0.2.3"), output);
                if (operation == "install") return installer.Install();
                if (operation == "undo") return installer.Undo();
                if (operation == "check") return installer.Check();
                throw new ArgumentException("未知操作。");
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    [STAThread]
    internal static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerWindow());
            return 0;
        }
        var transcript = new StringBuilder();
        int code = 0;
        try
        {
            if (args.Length < 2 || args.Length > 3) throw new ArgumentException("参数格式为 check、install 或 undo，随后为 IP 地址和可选的日志文件路径。");
            transcript.AppendLine(Run(args[1], args[0], line => transcript.AppendLine(line)));
        }
        catch (Exception error)
        {
            transcript.AppendLine("操作未完成。" + error.Message);
            code = 1;
        }
        // A WinForms executable may have no console handle, including hidden CLI launches.
        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(transcript.ToString());
            using (Stream stdout = Console.OpenStandardOutput()) stdout.Write(bytes, 0, bytes.Length);
        }
        catch (IOException) { }
        if (args.Length == 3)
        {
            try { File.WriteAllText(args[2], transcript.ToString(), new UTF8Encoding(true)); }
            catch (Exception error)
            {
                try { Console.Error.WriteLine("日志保存失败：" + error.Message); } catch (IOException) { }
            }
        }
        return code;
    }
}

internal sealed class InstallerWindow : Form
{
    private readonly TextBox ip = new TextBox();
    private readonly Button check = new Button();
    private readonly Button install = new Button();
    private readonly Button undo = new Button();
    private readonly TextBox log = new TextBox();
    private readonly Label status = new Label();
    private bool running;
    private bool connected;

    internal InstallerWindow()
    {
        Text = "Zune HD · 中文菜单";
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(688, 500);
        MinimumSize = new Size(704, 539);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(249, 248, 246);

        var heading = new Label { Text = "Zune HD 中文菜单", Font = new Font(Font.FontFamily, 22F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) };
        var subtitle = new Label { Text = "0.2.3 播放器界面候选  ·  Zune HD 4.5 (114) / Lyra 1.4.0", AutoSize = true, Location = new Point(26, 69) };
        var ipLabel = new Label { Text = "播放器 IP", AutoSize = true, Location = new Point(25, 112) };
        ip.SetBounds(122, 106, 225, 30);
        ip.AccessibleName = "播放器 IP 地址";
        check.Text = "检查连接";
        check.SetBounds(365, 104, 126, 34);
        install.Text = "安装 0.2.3";
        install.SetBounds(25, 153, 142, 37);
        undo.Text = "撤销 0.2.3";
        undo.SetBounds(182, 153, 142, 37);
        install.Enabled = undo.Enabled = false;
        status.Text = "保持 English；连接 Wi-Fi 后停在网络页面。安装后先核验英文启动。";
        status.SetBounds(25, 205, 632, 53);
        status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        log.SetBounds(25, 265, 638, 210);
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Color.White;
        log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        log.AccessibleName = "安装进度";
        Controls.AddRange(new Control[] { heading, subtitle, ipLabel, ip, check, install, undo, status, log });
        check.Click += delegate { Begin("check"); };
        install.Click += delegate { Begin("install"); };
        undo.Click += delegate { Begin("undo"); };
        ip.TextChanged += delegate { connected = false; install.Enabled = undo.Enabled = false; };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (running) { e.Cancel = true; status.Text = "正在完成当前文件操作，请等待结果。"; }
        };
        AcceptButton = check;
    }

    private void AddLog(string line)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(AddLog), line); return; }
        log.AppendText(line + Environment.NewLine);
    }

    private async void Begin(string operation)
    {
        if (operation == "undo" && MessageBox.Show(this,
            "请先切回 English 再撤销。若启用后英文开机异常，保持当前英文也可撤销。继续吗？",
            "撤销前核对", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        string address = ip.Text.Trim();
        running = true;
        check.Enabled = install.Enabled = undo.Enabled = ip.Enabled = false;
        status.Text = operation == "check" ? "正在检查播放器和安装记录……" : "正在操作，请保持播放器联网。";
        try
        {
            string result = await Task.Run(() => InstallerProgram.Run(address, operation, AddLog));
            connected = true;
            AddLog(result);
            status.Text = operation == "install" ? "传输校验完成，下一步见下方记录。" : result;
        }
        catch (Exception error)
        {
            connected = false;
            status.Text = "操作未完成，请查看记录，再检查连接。";
            AddLog(error.Message);
        }
        finally
        {
            running = false;
            ip.Enabled = check.Enabled = true;
            install.Enabled = undo.Enabled = connected;
        }
    }
}
