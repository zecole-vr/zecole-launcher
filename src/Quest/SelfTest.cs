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
                var stage = root + ".pkg";
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                var pkg = Path.Combine(stage, Pc.PcService.ExtractPackage(engine, file, stage));
                result["extractSeconds"] = sw.Elapsed.TotalSeconds;
                var eng = Pc.PcPatch.Create(engine, root, pkg);
                result["packageVersion"] = eng.PackageVersion;
                void Inspect(string label)
                {
                    var t = Stopwatch.StartNew(); var i = eng.Inspect();
                    steps.Add(new Dictionary<string, object> { ["step"] = "inspect:" + label, ["state"] = i["state"], ["version"] = i["version"], ["canRestore"] = i["canRestore"], ["message"] = i["message"], ["sec"] = t.Elapsed.TotalSeconds });
                }
                Inspect("start");
                foreach (var action in (args.Length > 5 ? args[5] : "restore,install,install").Split(','))
                {
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
