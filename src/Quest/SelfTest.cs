using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Zecole.Launcher
{
    /// <summary>
    /// 유지보수용 검증: 기기 없이 "원본 → 한글판" 생성이 검증본과 같은지 확인.
    /// ZECOLE_Launcher.exe --quest-selftest &lt;설치기.exe&gt; &lt;원본.hdog&gt; &lt;작업폴더&gt; &lt;결과.json&gt;
    /// </summary>
    internal static class SelfTest
    {
        public static void QuestBuild(string[] args)
        {
            var result = new Dictionary<string, object>();
            var sw = Stopwatch.StartNew();
            var log = new StringBuilder();
            try
            {
                string exe = args[1], source = args[2], work = args[3];
                Directory.CreateDirectory(Path.Combine(work, "pkg", "data"));
                if (exe.EndsWith(".zpkg", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    Quest.QuestPackage.ExtractZip(exe, Path.Combine(work, "pkg"));
                else Quest.PyInstallerArchive.Extract(exe, new Dictionary<string, string>
                {
                    ["bundle/manifest.json"] = Path.Combine(work, "pkg", "manifest.json"),
                    ["bundle/data/recipe.json"] = Path.Combine(work, "pkg", "data", "recipe.json"),
                    ["bundle/data/payload.bin"] = Path.Combine(work, "pkg", "data", "payload.bin"),
                });
                result["extractSeconds"] = sw.Elapsed.TotalSeconds;
                var pkg = Quest.QuestPackage.Load(Path.Combine(work, "pkg"));
                result["ops"] = pkg.Ops.Count;
                var output = Path.Combine(work, "out", pkg.S("main_name"));
                if (File.Exists(output)) File.Delete(output);
                Quest.QuestEngine.Build(pkg, source, output, (m, p) => log.AppendLine(m));
                result["outputSha256"] = Quest.Hash.File(output);
                result["expectedSha256"] = pkg.S("target_sha256");
                result["match"] = (string)result["outputSha256"] == pkg.S("target_sha256");
                result["ok"] = true;
            }
            catch (Exception ex) { result["ok"] = false; result["error"] = ex.ToString(); }
            result["seconds"] = sw.Elapsed.TotalSeconds;
            result["log"] = log.ToString();
            File.WriteAllText(args[4], Bridge.Json.Serialize(result), Encoding.UTF8);
        }

        /// <summary>
        /// PC 엔진 사본 시험: 실제 게임 폴더 대신 복사본 폴더에서 확인 → 복구 → 설치 → 재설치를 차례로 실행.
        /// ZECOLE_Launcher.exe --pc-selftest &lt;엔진&gt; &lt;릴리스 파일&gt; &lt;사본 게임 폴더&gt; &lt;결과.json&gt; [동작 순서, 기본 restore,install,install]
        /// </summary>
        public static void PcEngine(string[] args)
        {
            var result = new Dictionary<string, object>();
            var steps = new List<object>();
            var log = new StringBuilder();
            var sw = Stopwatch.StartNew();
            try
            {
                string engine = args[1], file = args[2], root = Path.GetFullPath(args[3]);
                // 입력부터 모두 확인한 뒤에 폴더를 만들거나 지움
                if (!File.Exists(file)) throw new FileNotFoundException("패치 파일이 없습니다: " + file);
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("사본 게임 폴더가 없습니다: " + root);
                if (engine == "pc-convrgence" && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZECOLE_SELFTEST_STORAGE")))
                    throw new InvalidOperationException("CONVRGENCE 자체 시험은 ZECOLE_SELFTEST_STORAGE(시험용 기록 폴더)가 필요합니다.");
                var stage = root + ".pkg";
                const string Mark = ".zecole-selftest";   // 자체 시험이 만든 폴더만 지움
                if (Directory.Exists(stage))
                {
                    if (!File.Exists(Path.Combine(stage, Mark))) throw new IOException("자체 시험이 만들지 않은 폴더가 이미 있어 멈춥니다: " + stage);
                    Directory.Delete(stage, true);
                }
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, Mark), "ZECOLE 런처 자체 시험용 임시 폴더");
                var pkg = Path.Combine(stage, Pc.PcService.ExtractPackage(engine, file, stage));
                result["extractSeconds"] = sw.Elapsed.TotalSeconds;
                var eng = Pc.PcPatch.Create(engine, root, pkg);
                // CONVRGENCE: 기록·백업 위치를 시험 폴더로 (실제 %LOCALAPPDATA%\ZECOLE\CONVRGENCE 를 건드리지 않음)
                var conv = eng as Pc.ConvrgenceSteam;
                if (conv != null)
                {
                    var st = Environment.GetEnvironmentVariable("ZECOLE_SELFTEST_STORAGE");
                    if (string.IsNullOrEmpty(st)) throw new InvalidOperationException("CONVRGENCE 자체 시험은 ZECOLE_SELFTEST_STORAGE(시험용 기록 폴더)가 필요합니다.");
                    conv.Storage = Path.GetFullPath(st);
                }
                result["packageVersion"] = eng.PackageVersion;
                void Inspect(string label)
                {
                    var t = Stopwatch.StartNew(); var i = eng.Inspect();
                    steps.Add(new Dictionary<string, object> { ["step"] = "inspect:" + label, ["state"] = i["state"], ["version"] = i["version"], ["canRestore"] = i["canRestore"], ["message"] = i["message"], ["sec"] = t.Elapsed.TotalSeconds });
                }
                Inspect("start");
                foreach (var step in (args.Length > 5 ? args[5] : "restore,install,install").Split(','))
                {
                    // 동작@failN = N번째 파일 적용 뒤 실패, 동작@killN = N번째 파일 적용 뒤 강제 종료(86) — CONVRGENCE 시험용
                    var action = step.Split('@')[0];
                    if (conv != null)
                    {
                        var mk = System.Text.RegularExpressions.Regex.Match(step, "@(fail|kill)([0-9]+)$");
                        conv.FailAfter = mk.Success && mk.Groups[1].Value == "fail" ? int.Parse(mk.Groups[2].Value) : 0;
                        conv.InterruptAfter = mk.Success && mk.Groups[1].Value == "kill" ? int.Parse(mk.Groups[2].Value) : 0;
                        if (action == "install") conv.Prefetch((m, p) => log.AppendLine("prefetch · " + m));
                    }
                    var t = Stopwatch.StartNew();
                    try { var m = eng.Apply(action, (msg, p) => log.AppendLine(action + " · " + msg)); steps.Add(new Dictionary<string, object> { ["step"] = action, ["ok"] = true, ["message"] = m, ["sec"] = t.Elapsed.TotalSeconds }); }
                    catch (Exception ex) { steps.Add(new Dictionary<string, object> { ["step"] = action, ["ok"] = false, ["message"] = ex.Message, ["sec"] = t.Elapsed.TotalSeconds }); }
                    Inspect("after " + action);
                }
                result["ok"] = true;
            }
            catch (Exception ex) { result["ok"] = false; result["error"] = ex.ToString(); }
            result["steps"] = steps; result["seconds"] = sw.Elapsed.TotalSeconds; result["log"] = log.ToString();
            File.WriteAllText(args[4], Bridge.Json.Serialize(result), new UTF8Encoding(false));
        }
    }
}
