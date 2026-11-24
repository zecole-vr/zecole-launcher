using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace Zecole.Launcher
{
    /// <summary>
    /// 화면(JS) ↔ 런처(C#) 통신.
    /// JS: chrome.webview.postMessage({id, cmd, args}) → C#: {id, ok, result | error}
    /// C# → JS 알림: {event, data}
    /// </summary>
    internal sealed class Bridge
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        private readonly MainForm _form;
        private readonly CoreWebView2 _core;
        private readonly Installer _installer;
        private readonly Quest.QuestService _quest;
        private readonly Pc.PcService _pc;

        public Bridge(MainForm form, CoreWebView2 core)
        {
            _form = form;
            _core = core;
            _installer = new Installer(Emit);
            _quest = new Quest.QuestService(Emit);
            _pc = new Pc.PcService(Emit);
        }

        public void Emit(string evt, object data)
        {
            var json = Json.Serialize(new Dictionary<string, object> { ["event"] = evt, ["data"] = data });
            if (_form.InvokeRequired) _form.BeginInvoke(new Action(() => Post(json)));
            else Post(json);
        }

        private volatile bool _closing;
        /// <summary>창 닫기가 시작됨. 이후 화면으로 보내지 못한 알림은 오류로 남기지 않음</summary>
        public void MarkClosing() => _closing = true;

        private void Post(string json)
        {
            try { _core.PostWebMessageAsJson(json); }
            // 창을 닫는 중(WebView2 정리 뒤)에 늦게 도착한 알림은 받을 화면이 없으므로 오류로 남기지 않음
            catch (InvalidOperationException) when (_closing) { }
            catch (Exception ex) { Log.Error("post", ex); }
        }

        public async void Handle(string raw)
        {
            object id = null;
            try
            {
                var msg = (Dictionary<string, object>)Json.DeserializeObject(raw);
                id = msg.TryGetValue("id", out var i) ? i : null;
                var cmd = (string)msg["cmd"];
                var args = msg.TryGetValue("args", out var a) ? a as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                var result = await Dispatch(cmd, args);
                if (id != null) Post(Json.Serialize(new Dictionary<string, object> { ["id"] = id, ["ok"] = true, ["result"] = result }));
            }
            catch (Exception ex)
            {
                Log.Error("bridge", ex);
                if (id != null) Post(Json.Serialize(new Dictionary<string, object> { ["id"] = id, ["ok"] = false, ["error"] = ex.Message }));
            }
        }

        private static string S(Dictionary<string, object> a, string k) => a.TryGetValue(k, out var v) ? v as string : null;

        private async Task<object> Dispatch(string cmd, Dictionary<string, object> a)
        {
            switch (cmd)
            {
                // ── 창 ──
                case "win.drag": _form.BeginDrag(); return null;
                case "win.minimize": _form.WindowState = System.Windows.Forms.FormWindowState.Minimized; return null;
                case "win.close":
                    if (_installer.Busy || _quest.Busy || _pc.Busy) return false; // 설치 중에는 닫지 않음 (화면에서 안내)
                    _form.Close(); return true;

                // ── 정보 ──
                case "app.info":
                    return new Dictionary<string, object>
                    {
                        ["version"] = Program.Version,
                        ["startHint"] = Program.StartHint,
                        ["exeName"] = System.IO.Path.GetFileName(System.Windows.Forms.Application.ExecutablePath),
                        ["dataDir"] = Program.DataDir,
                    };

                // ── 외부 열기 ──
                case "open.url": Shell.OpenUrl(S(a, "url")); return null;
                case "open.folder": Shell.OpenFolder(S(a, "path")); return null;

                // ── 네트워크 (허용된 주소만, ETag 캐시) ──
                case "net.get": return await Net.GetTextAsync(S(a, "url"), a.TryGetValue("maxAgeSec", out var m) ? Convert.ToInt32(m) : 0);
                case "youtube.feed": return await YouTube.LatestAsync(S(a, "channelId"));

                // ── 이 PC 상태 ──
                case "steam.apps": return await Task.Run(() => Steam.InstalledApps());
                case "steam.run":
                    var appid = S(a, "appid");
                    if (appid == null || !System.Text.RegularExpressions.Regex.IsMatch(appid, @"^\d{1,10}$")) throw new ArgumentException("appid");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://rungameid/" + appid) { UseShellExecute = true });
                    return null;
                case "status.scan": return await Task.Run(() => Status.Scan(a["items"] as object[]));

                // ── 설치 ──
                case "install.start": return await _installer.StartAsync(a);
                case "install.cancel": _installer.Cancel(); return null;
                case "install.openDownloads": Shell.OpenFolder(Installer.DownloadRoot); return null;

                // ── 런처 내장 Quest 설치 엔진 ──
                case "quest.state": return await _quest.StateAsync(a);
                case "quest.run": return await _quest.RunAsync(a);
                case "quest.wifi": return await _quest.WifiAsync(a);
                case "quest.fov.state": return await _quest.FovStateAsync(a);
                case "quest.fov.run": return await _quest.FovRunAsync(a);
                case "quest.openStorage":
                    var st = S(a, "storage");
                    if (st == null || !System.Text.RegularExpressions.Regex.IsMatch(st, @"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$")) throw new ArgumentException("storage");
                    var dir = System.IO.Path.Combine(Program.ZecoleRoot, st.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    System.IO.Directory.CreateDirectory(dir); Shell.OpenFolder(dir); return null;

                // ── 런처 내장 PC VR 설치 엔진 ──
                case "pc.state": return await _pc.StateAsync(a);
                case "pc.run": return await _pc.RunAsync(a);
                case "pc.launch":
                    // 타이탄폴 2 VR: VR 모드 실행기로 게임 시작 (관리자 권한 없이)
                    var tf = Pc.GameLocator.Find("pc-tf2vr", S(a, "appid") is string ap && System.Text.RegularExpressions.Regex.IsMatch(ap, @"^\d{1,10}$") ? ap : null);
                    var vr = tf == null ? null : System.IO.Path.Combine(tf, "Titanfall2VRLauncher.exe");
                    if (vr == null || !System.IO.File.Exists(vr)) throw new InvalidOperationException("VR 모드 실행기를 찾지 못했습니다.");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vr) { UseShellExecute = true, WorkingDirectory = tf });
                    return null;
                case "pc.openFolder":
                    var gp = S(a, "path");
                    if (gp == null || !System.IO.Directory.Exists(gp)) throw new ArgumentException("path");
                    Shell.OpenFolder(gp); return null;

                // ── 런처 자체 업데이트 ──
                case "update.apply": return await SelfUpdate.ApplyAsync(S(a, "url"), S(a, "digest"), _form, Emit);

                default: throw new InvalidOperationException("알 수 없는 명령: " + cmd);
            }
        }
    }
}
