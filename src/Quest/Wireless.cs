using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Zecole.Launcher.Quest
{
    /// <summary>
    /// Quest 무선 ADB 연결.
    /// 처음 한 번 USB 로 연결된 상태에서 무선 모드(adb tcpip 5555)를 켜고 Quest 의 Wi-Fi 주소로 붙음.
    /// 마지막 주소를 기억해 두었다가 다음부터는 케이블 없이 자동으로 다시 연결.
    /// (Quest 를 재부팅하면 무선 모드가 꺼지므로 그때는 USB 로 한 번 다시 켜야 함)
    /// </summary>
    internal static class QuestWireless
    {
        public const int Port = 5555;
        private static readonly string SavePath = Path.Combine(Program.DataDir, "quest-wireless.json");
        private static readonly object Gate = new object();
        private static DateTime _lastAuto = DateTime.MinValue;
        private static volatile bool _paused;
        public static bool Paused => _paused;   // 사용자가 끊으면 다음에 직접 연결할 때까지 자동 재연결 안 함 (런처 실행 중에만)

        internal sealed class Host { public string DeviceId, Address, Model, SavedAt; }

        // ───────────── 기억한 주소 ─────────────
        public static List<Host> Saved()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(SavePath)) return new List<Host>();
                    var j = Bridge.Json.DeserializeObject(File.ReadAllText(SavePath, Encoding.UTF8)) as Dictionary<string, object>;
                    return ((j?["hosts"] as object[]) ?? new object[0]).OfType<Dictionary<string, object>>()
                        .Select(d => new Host { DeviceId = d.TryGetValue("deviceId", out var a) ? a as string : null, Address = d.TryGetValue("address", out var b) ? b as string : null,
                                                Model = d.TryGetValue("model", out var c) ? c as string : null, SavedAt = d.TryGetValue("savedAt", out var e) ? e as string : null })
                        .Where(h => h.Address != null && ValidAddress(h.Address)).ToList();
                }
                catch { return new List<Host>(); }
            }
        }

        private static void Save(List<Host> hosts)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
                var data = new Dictionary<string, object>
                {
                    ["hosts"] = hosts.Take(4).Select(h => new Dictionary<string, object> { ["deviceId"] = h.DeviceId, ["address"] = h.Address, ["model"] = h.Model, ["savedAt"] = h.SavedAt }).ToArray(),
                };
                var tmp = SavePath + ".tmp";
                File.WriteAllText(tmp, Bridge.Json.Serialize(data), new UTF8Encoding(false));
                if (File.Exists(SavePath)) File.Replace(tmp, SavePath, null); else File.Move(tmp, SavePath);
            }
        }

        public static void Remember(string deviceId, string address, string model)
        {
            var list = Saved().Where(h => h.Address != address && (deviceId == null || h.DeviceId != deviceId)).ToList();
            list.Insert(0, new Host { DeviceId = deviceId, Address = address, Model = model, SavedAt = DateTime.Now.ToString("o") });
            Save(list);
        }

        public static void Forget(string address)
        {
            var list = Saved();
            Save(address == null ? new List<Host>() : list.Where(h => h.Address != address).ToList());
        }

        /// <summary>IPv4:포트 형식만 허용 (사용자가 입력한 주소)</summary>
        public static bool ValidAddress(string a)
        {
            var m = Regex.Match(a ?? "", @"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3}):(\d{1,5})$");
            if (!m.Success) return false;
            for (int i = 1; i <= 4; i++) if (int.Parse(m.Groups[i].Value) > 255) return false;
            var port = int.Parse(m.Groups[5].Value);
            return port >= 1024 && port <= 65535 && m.Groups[1].Value != "127" && m.Groups[1].Value != "0";
        }

        public static string Normalize(string input)
        {
            var s = (input ?? "").Trim();
            if (Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}$")) s += ":" + Port;
            return ValidAddress(s) ? s : null;
        }

        // ───────────── 연결 ─────────────
        private static bool TryConnect(string exe, string address, int timeoutSec)
        {
            try
            {
                var o = new Adb(exe).Run(new[] { "connect", address }, timeoutSec, false).output;
                if (!Regex.IsMatch(o, @"connected to", RegexOptions.IgnoreCase) || Regex.IsMatch(o, @"(failed|cannot|unable)", RegexOptions.IgnoreCase)) return false;
                for (int i = 0; i < 6; i++)
                {
                    var st = new Adb(exe, address).Run(new[] { "get-state" }, 10, false);
                    if (st.code == 0 && st.output.Trim() == "device") return true;
                    Thread.Sleep(500);
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>기억한 주소로 자동 재연결 (기기 목록이 비었을 때, 15초에 한 번만)</summary>
        public static bool AutoReconnect(string exe)
        {
            var hosts = Saved();
            if (_paused || (DateTime.UtcNow - _lastAuto).TotalSeconds < 15) return false;
            _lastAuto = DateTime.UtcNow;
            bool any = false;
            foreach (var h in hosts.Take(2))
            {
                if (h.DeviceId == null) continue;
                if (TryConnect(exe, h.Address, 6))
                {
                    if (VerifyOrDrop(exe, h.Address, h.DeviceId)) { any = true; continue; }
                }
                // 주소는 같은데 포트가 바뀐 경우 (안드로이드 무선 디버깅은 재부팅마다 포트가 바뀜)
                else if (FindNewPort(exe, h) != null) any = true;
            }
            // 기억한 주소가 없거나 모두 실패: 이미 무선 디버깅으로 페어링된 Quest 를 mDNS 로 찾음 (기존 설치 스크립트와 같은 방식)
            if (!any) any = MdnsConnect(exe);
            return any;
        }

        private static bool MdnsConnect(string exe)
        {
            string o;
            try { o = new Adb(exe).Run(new[] { "mdns", "services" }, 10, false).output; } catch { return false; }
            bool any = false;
            foreach (Match m in Regex.Matches(o, @"_adb-tls-connect\._tcp\.?\s+(\d{1,3}(?:\.\d{1,3}){3}:\d{1,5})"))
            {
                var addr = m.Groups[1].Value;
                if (ValidAddress(addr) && TryConnect(exe, addr, 8)) { Log.Info("[무선] 페어링된 Quest를 찾아 연결했습니다: " + addr); any = true; }
            }
            return any;
        }

        /// <summary>연결된 기기가 기억한 기기 번호와 같은지. 다르면 연결을 끊음 (공유기가 주소를 다른 기기에 준 경우)</summary>
        private static bool VerifyOrDrop(string exe, string address, string deviceId)
        {
            string id = null;
            try { id = new Adb(exe, address).Shell(15, false, "getprop", "ro.serialno").output.Trim(); } catch { }
            if (id == deviceId) return true;
            try { new Adb(exe).Run(new[] { "disconnect", address }, 10, false); } catch { }
            return false;
        }

        // ───────────── 바뀐 포트 찾기 ─────────────
        private static readonly Dictionary<string, DateTime> _lastScan = new Dictionary<string, DateTime>();

        /// <summary>
        /// 기억한 IP 에서 열린 포트를 찾아 같은 기기(기기 번호 일치)일 때만 연결하고 새 주소를 기억.
        /// Quest 가 응답하지 않으면 검색하지 않고, 같은 IP 는 1분에 한 번만 검색.
        /// </summary>
        private static string FindNewPort(string exe, Host h, bool force = false)
        {
            var ip = h.Address.Split(':')[0];
            int oldPort = int.Parse(h.Address.Split(':')[1]);
            lock (_lastScan)
            {
                if (!force && _lastScan.TryGetValue(ip, out var t) && (DateTime.UtcNow - t).TotalSeconds < 60) return null;
                _lastScan[ip] = DateTime.UtcNow;
            }
            if (!Alive(ip)) return null;
            Log.Info($"[무선] 기억한 포트({oldPort})로 연결되지 않아 {ip}의 열린 포트를 찾습니다.");
            var ports = OpenPorts(ip, new[] { Port }.Concat(Enumerable.Range(30000, 35536)).Where(p => p != oldPort));
            foreach (var p in ports.Take(6))
            {
                var addr = ip + ":" + p;
                if (!ValidAddress(addr) || !TryConnect(exe, addr, 8)) continue;
                if (!VerifyOrDrop(exe, addr, h.DeviceId)) continue;
                Remember(h.DeviceId, addr, h.Model);
                Log.Info($"[무선] 새 포트로 연결했습니다: {p}");
                return addr;
            }
            return null;
        }

        private static bool Alive(string ip)
        {
            try { using (var ping = new System.Net.NetworkInformation.Ping()) return ping.Send(ip, 1000)?.Status == System.Net.NetworkInformation.IPStatus.Success; }
            catch { return false; }
        }

        /// <summary>TCP 연결이 되는 포트 목록 (동시에 여러 개씩, 포트마다 최대 0.4초)</summary>
        private static List<int> OpenPorts(string ip, IEnumerable<int> candidates)
        {
            var open = new List<int>();
            var addr = System.Net.IPAddress.Parse(ip);
            foreach (var batch in candidates.Select((p, i) => (p, i)).GroupBy(x => x.i / 1024, x => x.p))
            {
                var tasks = batch.Select(async p =>
                {
                    using (var c = new System.Net.Sockets.TcpClient())
                    {
                        try
                        {
                            var t = c.ConnectAsync(addr, p);
                            if (await System.Threading.Tasks.Task.WhenAny(t, System.Threading.Tasks.Task.Delay(400)) == t && c.Connected) return p;
                        }
                        catch { }
                        return -1;
                    }
                }).ToArray();
                System.Threading.Tasks.Task.WaitAll(tasks);
                open.AddRange(tasks.Select(t => t.Result).Where(p => p > 0));
            }
            // 기본 무선 포트(5555)를 먼저 시도
            return open.OrderBy(p => p == Port ? 0 : 1).ToList();
        }

        /// <summary>USB 로 연결된 Quest 의 무선 모드를 켜고 Wi-Fi 주소로 연결</summary>
        public static Dictionary<string, object> Enable(string exe, string usbSerial)
        {
            if (usbSerial == null || usbSerial.Contains(':')) throw new PatchError("무선 연결을 켜려면 먼저 Quest를 USB로 한 번 연결해 주세요.");
            var usb = new Adb(exe, usbSerial);
            var deviceId = usb.Shell(20, false, "getprop", "ro.serialno").output.Trim();
            var model = usb.Shell(20, false, "getprop", "ro.product.model").output.Trim();
            var ipOut = usb.Shell(20, false, "ip", "-f", "inet", "addr", "show", "wlan0").output;
            var ip = Regex.Match(ipOut, @"inet (\d{1,3}(?:\.\d{1,3}){3})/").Groups[1].Value;
            if (ip.Length == 0) throw new PatchError("Quest가 Wi-Fi에 연결되어 있지 않습니다. 헤드셋에서 PC와 같은 Wi-Fi에 연결한 뒤 다시 시도해 주세요.");
            var address = ip + ":" + Port;
            if (!ValidAddress(address)) throw new PatchError("Quest의 Wi-Fi 주소를 확인하지 못했습니다.");
            var r = usb.Run(new[] { "tcpip", Port.ToString() }, 20, false);
            if (r.code != 0) throw new PatchError("Quest의 무선 모드를 켜지 못했습니다: " + r.output);
            // 무선 모드로 바뀌는 동안 ADB가 잠깐 다시 시작됨
            bool ok = false;
            for (int i = 0; i < 6 && !ok; i++) { Thread.Sleep(1500); ok = TryConnect(exe, address, 10); }
            if (!ok) throw new PatchError($"Quest({ip})에 무선으로 연결하지 못했습니다. PC와 Quest가 같은 공유기(Wi-Fi)에 연결되어 있는지 확인해 주세요.");
            var id = new Adb(exe, address).Shell(20, false, "getprop", "ro.serialno").output.Trim();
            if (deviceId.Length > 0 && id != deviceId) throw new PatchError("무선으로 연결된 기기가 USB 기기와 다릅니다. 연결을 중단합니다.");
            Remember(deviceId.Length > 0 ? deviceId : null, address, model);
            _paused = false;
            return new Dictionary<string, object> { ["ok"] = true, ["address"] = address, ["message"] = $"무선 연결 완료 ({ip}) · 이제 USB 케이블을 분리해도 됩니다." };
        }

        /// <summary>입력하거나 기억한 주소로 연결</summary>
        public static Dictionary<string, object> Connect(string exe, string input)
        {
            var address = input != null ? Normalize(input) : Saved().FirstOrDefault()?.Address;
            if (address == null) throw new PatchError(input != null ? "주소 형식이 올바르지 않습니다. 예) 192.168.0.23" : "기억한 무선 연결이 없습니다. 먼저 USB로 연결해 무선 연결을 켜 주세요.");
            if (!TryConnect(exe, address, 12))
            {
                // 기억한 기기라면 포트가 바뀌었을 수 있으니 같은 IP에서 찾아봄
                var known = Saved().FirstOrDefault(h => h.DeviceId != null && h.Address.Split(':')[0] == address.Split(':')[0]);
                var found = known != null ? FindNewPort(exe, known, true) : null;
                if (found == null)
                    throw new PatchError("Quest를 찾지 못했습니다. Quest가 켜져 있고 같은 Wi-Fi에 있는지 확인해 주세요. Quest를 재부팅했다면 USB로 한 번 연결해 무선 연결을 다시 켜야 합니다. (무선 디버깅 앱을 쓰는 경우 앱 화면의 IP:포트를 입력해 주세요.)");
                _paused = false;
                return new Dictionary<string, object> { ["ok"] = true, ["address"] = found, ["message"] = "무선 연결 완료 (바뀐 포트를 찾아 연결했습니다)" };
            }
            var a = new Adb(exe, address);
            _paused = false;
            Remember(a.Shell(20, false, "getprop", "ro.serialno").output.Trim() is string id && id.Length > 0 ? id : null, address, a.Shell(20, false, "getprop", "ro.product.model").output.Trim());
            return new Dictionary<string, object> { ["ok"] = true, ["address"] = address, ["message"] = "무선 연결 완료" };
        }

        public static Dictionary<string, object> Disconnect(string exe, string address, bool forget)
        {
            if (address != null && !ValidAddress(address)) throw new PatchError("잘못된 주소입니다.");
            _paused = true;
            try { new Adb(exe).Run(address != null ? new[] { "disconnect", address } : new[] { "disconnect" }, 15, false); } catch { }
            if (forget) Forget(address);
            return new Dictionary<string, object> { ["ok"] = true, ["message"] = forget ? "무선 연결을 끊고 기억한 주소를 지웠습니다." : "무선 연결을 끊었습니다." };
        }
    }
}
