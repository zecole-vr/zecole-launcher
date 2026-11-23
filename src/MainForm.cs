using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Zecole.Launcher
{
    /// <summary>테두리 없는 1180×820 창. 마우스 위치와 상관없이 항상 주 모니터 가운데에 열고, 크기도 주 모니터 기준으로 정한다. 화면은 전부 WebView2(ui/index.html)가 그림.</summary>
    internal sealed class MainForm : Form
    {
        public const int LogicalWidth = 1180, LogicalHeight = 820;
        private const string Host = "app.zecole.local";

        private readonly WebView2 _web = new WebView2();
        private Bridge _bridge;

        public MainForm()
        {
            Text = Program.AppTitle;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(14, 14, 17);
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            ClientSize = FitSize(DeviceDpi / 96f);
            var wa = MainArea();
            Location = new Point(wa.X + Math.Max(0, (wa.Width - Width) / 2), wa.Y + Math.Max(0, (wa.Height - Height) / 2));

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = BackColor;
            Controls.Add(_web);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW: 테두리 없는 창에도 그림자
                cp.Style |= 0x20000;       // WS_MINIMIZEBOX: 작업 표시줄 클릭으로 최소화/복원
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleWindow(Handle);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ClientSize = FitSize(e.DeviceDpiNew / 96f);
        }

        /// <summary>주 모니터의 작업 영역(작업 표시줄 제외).</summary>
        private static Rectangle MainArea()
        {
            try { return (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea; }
            catch { return new Rectangle(0, 0, 1920, 1040); }
        }

        /// <summary>기본 크기 × DPI 배율. 주 모니터 작업 영역보다 크면 그 안에 들어가게 줄인다.
        /// 어느 모니터로 옮겨도 같은 크기를 유지하도록 항상 주 모니터만 기준으로 삼는다.</summary>
        private static Size FitSize(float scale)
        {
            int w = (int)(LogicalWidth * scale), h = (int)(LogicalHeight * scale);
            var wa = MainArea();
            w = Math.Min(w, wa.Width - 24);
            h = Math.Min(h, wa.Height - 24);
            return new Size(Math.Max(w, 640), Math.Max(h, 480));
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { await InitWebAsync(); }
            catch (WebView2RuntimeNotFoundException) { await WebView2Missing(); }
            catch (Exception ex)
            {
                Log.Error("init", ex);
                MessageBox.Show(this, "화면을 여는 중 문제가 생겼습니다.\n\n" + ex.Message, Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private async Task InitWebAsync()
        {
            CoreWebView2Environment.SetLoaderDllFolderPath(Program.ExtractNativeLoader());
            CoreWebView2Environment.GetAvailableBrowserVersionString(); // 런타임 없으면 여기서 예외
            var uiDir = Program.ExtractUi();

            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDir, "webview"),
                new CoreWebView2EnvironmentOptions { Language = "ko-KR" });
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.AreDevToolsEnabled = Debugger.IsAttached || Environment.GetEnvironmentVariable("ZECOLE_DEVTOOLS") == "1";
            s.AreDefaultContextMenusEnabled = s.AreDevToolsEnabled;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = s.AreDevToolsEnabled;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;

            core.SetVirtualHostNameToFolderMapping(Host, uiDir, CoreWebView2HostResourceAccessKind.Deny);

            // 외부 링크는 런처 안에서 열지 않고 기본 브라우저로
            core.NewWindowRequested += (o, a) => { a.Handled = true; Shell.OpenUrl(a.Uri); };
            core.NavigationStarting += (o, a) =>
            {
                if (!a.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase))
                {
                    a.Cancel = true;
                    Shell.OpenUrl(a.Uri);
                }
            };

            _bridge = new Bridge(this, core);
            core.WebMessageReceived += (o, a) => _bridge.Handle(a.WebMessageAsJson);
            // 빌드마다 주소를 바꿔 이전 화면이 캐시에서 뜨지 않게 함
            var stamp = File.GetLastWriteTimeUtc(Application.ExecutablePath).Ticks.ToString("x");
            core.Navigate($"https://{Host}/index.html?b={stamp}");
        }

        private async Task WebView2Missing()
        {
            var r = MessageBox.Show(this,
                "이 런처는 Microsoft Edge WebView2 런타임이 필요합니다.\n(Windows 10/11 대부분에 이미 설치되어 있습니다.)\n\n지금 Microsoft에서 받아 설치할까요?",
                Program.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) { Close(); return; }
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
                await Net.DownloadFileAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703", path, null);
                var p = Process.Start(new ProcessStartInfo(path, "/install") { UseShellExecute = true });
                await Task.Run(() => p.WaitForExit());
                Application.Restart();
            }
            catch (Exception ex)
            {
                Log.Error("webview2-setup", ex);
                Shell.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
            }
            Close();
        }

        // ── 다른 인스턴스에서 넘어온 "이 게임 열어줘" 요청 ──
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_COPYDATA)
            {
                var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(m.LParam);
                if (cds.dwData == (IntPtr)0x5A43)
                {
                    var hint = Marshal.PtrToStringUni(cds.lpData);
                    _bridge?.Emit("open-hint", new { hint });
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Activate();
                }
            }
            base.WndProc(ref m);
        }

        public void BeginDrag() => Native.BeginDrag(Handle);
    }
}
