using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Zecole.Launcher
{
    internal static class Program
    {
        public const string AppTitle = "ZECOLE 한글패치 런처";
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        /// <summary>%LOCALAPPDATA%\Zecole — 각 게임 설치기들이 공유하는 상위 폴더</summary>
        public static readonly string ZecoleRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zecole");
        /// <summary>%LOCALAPPDATA%\Zecole\Launcher — 런처 전용 데이터</summary>
        public static readonly string DataDir = Path.Combine(ZecoleRoot, "Launcher");

        /// <summary>파일 이름이나 인자로 지정된 시작 게임 (예: ZECOLE_H3VR2_한글패치.exe → "h3vr2")</summary>
        public static string StartHint;

        private static Mutex _mutex;

        [STAThread]
        private static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */;
            ServicePointManager.DefaultConnectionLimit = 16;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

            if (args.Length > 0 && args[0] == "--quest-selftest") { SelfTest.QuestBuild(args); return; }
            if (args.Length > 4 && args[0] == "--pc-selftest") { SelfTest.PcEngine(args); return; }
            // 관리자 권한으로 실행된 작업 하나 (PC 게임 폴더 쓰기) — 창 없이 처리하고 종료
            if (args.Length > 1 && args[0] == "--pc-task") { Environment.ExitCode = Pc.PcTask.Run(args[1]); return; }
            StartHint = ReadStartHint(args);
            Directory.CreateDirectory(DataDir);

            // 이미 열려 있으면 그 창으로 시작 게임만 전달하고 종료
            _mutex = new Mutex(true, @"Local\ZECOLE.Launcher.Single", out bool first);
            if (!first)
            {
                Native.SendToExisting(AppTitle, StartHint ?? "");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Error("ui", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("domain", e.ExceptionObject as Exception);

            Log.Info($"start v{Version} exe={Path.GetFileName(Application.ExecutablePath)} hint={StartHint}");
            SelfUpdate.CleanupOld();
            Run();
            GC.KeepAlive(_mutex);
        }

        /// <summary>자체 업데이트 후 새 EXE 가 바로 뜰 수 있게 단일 실행 잠금 해제</summary>
        public static void ReleaseSingleInstance()
        {
            try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); _mutex = null; } catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run() => Application.Run(new MainForm());

        /// <summary>
        /// 시작 게임 찾기: --game=xxx 인자 > EXE 파일 이름.
        /// 파일 이름은 "ZECOLE_Launcher.exe" 가 기본이고,
        /// 게임별 배포 시 "ZECOLE_H3VR2_한글패치.exe" 처럼 이름만 바꾸면 해당 게임 화면으로 열림.
        /// </summary>
        private static string ReadStartHint(string[] args)
        {
            foreach (var a in args)
            {
                if (a.StartsWith("--game=", StringComparison.OrdinalIgnoreCase)) return a.Substring(7).Trim();
                if (a.StartsWith("zecole://", StringComparison.OrdinalIgnoreCase)) return a.Substring(9).Trim('/').Split('/').Last();
            }
            var name = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            // "(1)" 같은 브라우저 중복 다운로드 표시 제거
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");
            var parts = name.Split(new[] { '_', '-', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !new[] { "zecole", "launcher", "setup", "한글패치", "런처", "korean", "patch", "koreanpatch" }.Contains(p.ToLowerInvariant()))
                .ToArray();
            return parts.Length > 0 ? string.Join(" ", parts) : null;
        }

        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            var shortName = new AssemblyName(e.Name).Name;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib/" + shortName + ".dll"))
            {
                if (s == null) return null;
                var buf = new byte[s.Length];
                s.Read(buf, 0, buf.Length);
                return Assembly.Load(buf);
            }
        }

        /// <summary>WebView2Loader.dll(네이티브)은 메모리 로드가 안 되므로 데이터 폴더에 풀어 둠</summary>
        public static string ExtractNativeLoader()
        {
            var dir = Path.Combine(DataDir, "runtime", Version);
            var path = Path.Combine(dir, "WebView2Loader.dll");
            if (File.Exists(path)) return dir;
            Directory.CreateDirectory(dir);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib/WebView2Loader.dll"))
            using (var f = File.Create(path + ".tmp")) s.CopyTo(f);
            File.Move(path + ".tmp", path);
            return dir;
        }

        /// <summary>화면 파일(ui/*)을 데이터 폴더에 풀어서 WebView2 가상 호스트로 연결</summary>
        public static string ExtractUi()
        {
            var dir = Path.Combine(DataDir, "ui");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("ui/")))
            {
                var target = Path.Combine(dir, res.Substring(3).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var s = asm.GetManifestResourceStream(res))
                using (var f = File.Create(target)) s.CopyTo(f);
            }
            return dir;
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string PathNow => Path.Combine(Program.DataDir, "logs", DateTime.Now.ToString("yyyyMMdd") + ".log");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string where, Exception ex) => Write("ERR ", where + ": " + ex);

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PathNow));
                    File.AppendAllText(PathNow, $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
                }
            }
            catch { /* 로그 실패는 무시 */ }
            Debug.WriteLine(msg);
        }
    }

    internal static class Native
    {
        public const int WM_COPYDATA = 0x004A;
        private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref COPYDATASTRUCT l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        public static void BeginDrag(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        /// <summary>Windows 11 둥근 모서리 + 어두운 테두리</summary>
        public static void StyleWindow(IntPtr handle)
        {
            try
            {
                int round = 2; DwmSetWindowAttribute(handle, 33, ref round, 4);       // DWMWA_WINDOW_CORNER_PREFERENCE
                int dark = 1; DwmSetWindowAttribute(handle, 20, ref dark, 4);         // DWMWA_USE_IMMERSIVE_DARK_MODE
                int border = 0x00141110; DwmSetWindowAttribute(handle, 34, ref border, 4); // DWMWA_BORDER_COLOR
            }
            catch { /* Windows 10 은 해당 속성 없음 */ }
        }

        public static void SendToExisting(string title, string text)
        {
            var h = FindWindow(null, title);
            if (h == IntPtr.Zero) return;
            var p = Marshal.StringToHGlobalUni(text);
            try
            {
                var cds = new COPYDATASTRUCT { dwData = (IntPtr)0x5A43, cbData = (text.Length + 1) * 2, lpData = p };
                SendMessage(h, WM_COPYDATA, IntPtr.Zero, ref cds);
                ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
            }
            finally { Marshal.FreeHGlobal(p); }
        }
    }
}
