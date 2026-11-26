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
    /// <summary>테두리 없는 창(왼쪽 목록 · 본문 · 오른쪽 혜택 칸). 화면은 전부 WebView2(ui/index.html)가 그림.
    /// 기본 배치는 1460×820(DPI 배율 적용)이고, 창을 키우면 배치는 그대로 두고 화면 전체를 같은 비율로 확대한다(4K 등 큰 화면 대응).
    /// 가장자리를 끌어 크기를 바꾸고, 제목줄 더블클릭·최대화 버튼으로 최대화한다. 마지막 위치·크기·최대화 상태를 기억한다.</summary>
    internal sealed class MainForm : Form
    {
        public const int LogicalWidth = 1460, LogicalHeight = 820;
        /// <summary>이보다 작게 줄이면 배치가 좁아지므로 막는다(DPI 배율 적용 전 값)</summary>
        private const int MinLogicalWidth = 1200, MinLogicalHeight = 700;
        private const string Host = "app.zecole.local";

        private readonly WebView2 _web = new WebView2();
        private Bridge _bridge;
        private readonly bool _startMaximized;

        public MainForm()
        {
            Text = Program.AppTitle;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(14, 14, 17);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 창이 열릴 모니터(기억한 위치가 있으면 그 모니터, 없으면 주 모니터)의 배율·작업 영역으로 크기를 정함.
            // 아직 창이 없어 DeviceDpi는 주 모니터 값이므로 그 모니터의 배율을 직접 읽는다(4K 200% 보조 모니터 등).
            var saved = LoadWindow();
            var screen = saved?.Location != null ? Screen.FromPoint(saved.Location.Value) : PrimaryScreen();
            var wa = screen.WorkingArea;
            float scale = Native.DpiAt(new Point(wa.X + wa.Width / 2, wa.Y + wa.Height / 2)) / 96f;
            MinimumSize = MinSize(scale, wa);
            var size = saved?.Size ?? FitSize(scale, wa);
            Size = new Size(Math.Max(size.Width, MinimumSize.Width), Math.Max(size.Height, MinimumSize.Height));
            Location = saved?.Location ?? CenterIn(wa, Size);
            _startMaximized = saved?.Max ?? false;

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
                cp.Style |= 0x10000;       // WS_MAXIMIZEBOX: 최대화·화면 위쪽에 끌어 붙이기
                cp.Style |= 0x40000;       // WS_THICKFRAME: 크기 조절 허용 (테두리는 WM_NCCALCSIZE에서 없앰)
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleWindow(Handle);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _normalBounds = Bounds;
            if (_startMaximized) Native.SysCommand(Handle, true);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            MinimumSize = MinSize(e.DeviceDpiNew / 96f, Screen.FromRectangle(e.SuggestedRectangle).WorkingArea);
            // 다른 배율의 모니터로 옮기면 Windows가 제안한 크기(같은 비율)를 쓰되 그 모니터 안에 들어가게 함
            if (WindowState == FormWindowState.Normal)
            {
                var r = e.SuggestedRectangle;
                var wa = Screen.FromRectangle(r).WorkingArea;
                Bounds = new Rectangle(r.X, r.Y, Math.Min(r.Width, wa.Width), Math.Min(r.Height, wa.Height));
            }
            ApplyZoom();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            TrackNormalBounds();
            ApplyZoom();
            if (_bridge != null && WindowState != FormWindowState.Minimized) _bridge.Emit("win", new { max = WindowState == FormWindowState.Maximized });
        }

        /// <summary>창이 기본 배치보다 크면 화면 전체를 같은 비율로 확대. 기본보다 작게는 줄이지 않음(작은 화면은 예전처럼 배치가 좁아짐).</summary>
        private void ApplyZoom()
        {
            if (WindowState == FormWindowState.Minimized || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            // 창이 처음부터 다른 배율 모니터에 만들어지면 DeviceDpi가 주 모니터 값으로 남으므로 창의 실제 배율을 읽음
            float scale = (IsHandleCreated ? Native.WindowDpi(Handle) : DeviceDpi) / 96f;
            double z = Math.Min(ClientSize.Width / scale / LogicalWidth, ClientSize.Height / scale / LogicalHeight);
            z = Math.Round(Math.Max(1.0, Math.Min(z, 4.0)), 3);
            try { if (Math.Abs(_web.ZoomFactor - z) > 0.002) _web.ZoomFactor = z; }
            catch (Exception ex) { Log.Info("화면 확대 실패: " + ex.Message); }
        }

        public void ToggleMaximize() => Native.SysCommand(Handle, WindowState != FormWindowState.Maximized);

        /// <summary>마지막 보통 상태 위치·크기 (최대화·최소화 중에 닫아도 이 값을 기억)</summary>
        private Rectangle _normalBounds;
        /// <summary>최대화(또는 최소화)에서 보통 상태로 돌아오는 중</summary>
        private bool _leavingMax, _clearPending;

        /// <summary>보통 상태의 위치·크기를 기록. 최대화된 동안은 기록하지 않고, 복원 직후의 크기 변경이 끝나면(BeginInvoke) 다시 기록한다.</summary>
        private void TrackNormalBounds()
        {
            if (!IsHandleCreated) return;
            if (WindowState != FormWindowState.Normal) { _leavingMax = true; return; }
            if (_leavingMax)
            {
                if (!_clearPending)
                {
                    _clearPending = true;
                    BeginInvoke(new Action(() => { _leavingMax = false; _clearPending = false; if (WindowState == FormWindowState.Normal) _normalBounds = Bounds; }));
                }
                return;
            }
            _normalBounds = Bounds;
        }

        /// <summary>복원할 때 WinForms(Form.RestoreWindowBoundsIfNecessary)가 자기가 기억한 크기에 테두리 두께를 더해 다시 SetBounds 함
        /// (WS_THICKFRAME + 테두리 없앰 조합). 그대로 두면 최대화·복원할 때마다 창이 14px씩 커지므로 그 순간에는 기억한 보통 크기를 씀.</summary>
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (_leavingMax && WindowState == FormWindowState.Normal && !_normalBounds.IsEmpty && (specified & BoundsSpecified.Size) != 0)
            {
                width = _normalBounds.Width;
                height = _normalBounds.Height;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            TrackNormalBounds();
        }

        public void BeginResize(string edge)
        {
            if (WindowState != FormWindowState.Normal) return;
            Native.BeginResize(Handle, edge);
        }

        /// <summary>테두리를 없앴으므로 창 크기 = 화면 크기. WinForms가 WS_THICKFRAME 테두리 두께를 더하지 않게 함
        /// (더하면 최대화에서 복원할 때마다 창이 테두리만큼 커짐).</summary>
        protected override Size SizeFromClientSize(Size clientSize) => clientSize;

        private const int WM_NCCALCSIZE = 0x0083;

        /// <summary>WS_THICKFRAME의 테두리를 없애 창 전체를 화면으로 씀. 최대화되면 창이 테두리만큼 모니터 밖으로 넘치므로 작업 영역에 맞춤.</summary>
        private static bool HandleNcCalcSize(ref Message m)
        {
            if (m.Msg != WM_NCCALCSIZE || m.WParam == IntPtr.Zero) return false;
            if (Native.IsZoomed(m.HWnd))
            {
                var r = Marshal.PtrToStructure<Native.RECT>(m.LParam);
                var wa = Screen.FromPoint(new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)).WorkingArea;
                // 복원 도중에도 IsZoomed가 잠깐 참이므로, 제안된 창이 작업 영역을 실제로 덮을 때(진짜 최대화)만 맞춤
                if (r.Right - r.Left >= wa.Width && r.Bottom - r.Top >= wa.Height)
                    Marshal.StructureToPtr(new Native.RECT { Left = wa.Left, Top = wa.Top, Right = wa.Right, Bottom = wa.Bottom }, m.LParam, false);
            }
            m.Result = IntPtr.Zero;
            return true;
        }

        private static readonly string WindowFile = Path.Combine(Program.DataDir, "window-position.txt");

        private static Point CenterIn(Rectangle wa, Size size)
        {
            return new Point(wa.X + Math.Max(0, (wa.Width - size.Width) / 2), wa.Y + Math.Max(0, (wa.Height - size.Height) / 2));
        }

        private sealed class SavedWindow { public Point? Location; public Size? Size; public bool Max; }

        /// <summary>지난번에 닫은 위치·크기·최대화 상태. 파일 형식은 "x,y"(예전) 또는 "x,y,너비,높이,최대화(0/1)".
        /// 위치는 창 윗부분(끌어서 옮기는 곳)이 지금 연결된 어느 모니터에든 충분히 보일 때만 쓰고, 크기는 그 모니터 작업 영역 안으로 줄인다.
        /// 모니터를 빼거나 배치를 바꿔 화면 밖이 되면 위치·크기를 버리고 주 모니터 가운데에 새 크기로 연다.</summary>
        private static SavedWindow LoadWindow()
        {
            try
            {
                if (!File.Exists(WindowFile)) return null;
                var v = File.ReadAllText(WindowFile).Trim().Split(',');
                if (v.Length < 2 || !int.TryParse(v[0], out var x) || !int.TryParse(v[1], out var y)) return null;
                Size? size = null;
                if (v.Length >= 4 && int.TryParse(v[2], out var w) && int.TryParse(v[3], out var h) && w > 0 && h > 0) size = new Size(w, h);
                var res = new SavedWindow { Max = v.Length >= 5 && v[4] == "1" };
                var top = new Rectangle(x, y, size?.Width ?? LogicalWidth, 48);
                foreach (var sc in Screen.AllScreens)
                {
                    var hit = Rectangle.Intersect(sc.WorkingArea, top);
                    if (hit.Width < 160 || hit.Height < 24) continue;
                    res.Location = new Point(x, y);
                    if (size != null) res.Size = new Size(Math.Min(size.Value.Width, sc.WorkingArea.Width), Math.Min(size.Value.Height, sc.WorkingArea.Height));
                    break;
                }
                return res;
            }
            catch (Exception ex) { Log.Info("창 위치 불러오기 실패: " + ex.Message); }
            return null;
        }

        /// <summary>창을 닫을 때 위치·크기·최대화 상태를 기억한다(최대화·최소화 상태면 그 전의 보통 크기).</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            _bridge?.MarkClosing();
            try
            {
                var b = WindowState == FormWindowState.Normal ? Bounds : _normalBounds;
                Directory.CreateDirectory(Program.DataDir);
                File.WriteAllText(WindowFile, $"{b.X},{b.Y},{b.Width},{b.Height},{(WindowState == FormWindowState.Maximized ? 1 : 0)}");
            }
            catch (Exception ex) { Log.Info("창 위치 저장 실패: " + ex.Message); }
        }

        private static Screen PrimaryScreen()
        {
            try { return Screen.PrimaryScreen ?? Screen.AllScreens[0]; }
            catch { return Screen.AllScreens[0]; }
        }

        /// <summary>최소 크기 (그 모니터 작업 영역보다 크지 않게)</summary>
        private static Size MinSize(float scale, Rectangle wa) =>
            new Size(Math.Max(640, Math.Min((int)(MinLogicalWidth * scale), wa.Width - 24)),
                     Math.Max(480, Math.Min((int)(MinLogicalHeight * scale), wa.Height - 24)));

        /// <summary>처음 크기(기억한 크기가 없을 때). 기본 크기 × 배율이 그 모니터에 비해 많이 작으면(4K를 배율 100%로 쓰는 경우 등)
        /// 화면 너비의 70% 정도로 키워서 연다(화면은 ApplyZoom이 같은 비율로 확대). 작업 영역보다 크면 그 안에 들어가게 줄인다.</summary>
        private static Size FitSize(float scale, Rectangle wa)
        {
            int w = (int)(LogicalWidth * scale), h = (int)(LogicalHeight * scale);
            if (w < wa.Width * 0.55)
            {
                w = (int)(wa.Width * 0.7);
                h = w * LogicalHeight / LogicalWidth;
                if (h > wa.Height * 0.8) { h = (int)(wa.Height * 0.8); w = h * LogicalWidth / LogicalHeight; }
            }
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
            ApplyZoom();

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
            // 화면이 열리면 최대화 상태를 알려 버튼 모양을 맞춤 (최대화로 시작한 경우)
            core.NavigationCompleted += (o, a) => { ApplyZoom(); _bridge.Emit("win", new { max = WindowState == FormWindowState.Maximized }); };
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
            if (HandleNcCalcSize(ref m)) return;
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
