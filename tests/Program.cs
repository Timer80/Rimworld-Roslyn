// 离线自测主程序：证明「前置包先加载 → 字段扫描不再抛 TypeLoadException」。
//
// 复刻 Verse.StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes 的扫描语义：
//   foreach (Type t in AllTypes)          // AllTypes = 执行程序集 + 各 mod 的 loadedAssemblies
//       if (!t.HasAttribute<StaticConstructorOnStartup>())
//           t.GetFields(Static|Public|NonPublic)     // ← 这里在 Roslyn 未加载时抛 TypeLoadException
//
// 但本自测跑在 net8.0/CoreCLR 而不是 RimWorld 的 Mono 上。CoreCLR 的行为与 Mono 不同：
// CoreCLR 在加载含未解析 typeref 的类型时通常先允许加载，GetFields 时同样会抛
// TypeLoadException/TypeInitializationException——语义一致。为了让「失败」可观测，
// 本自测对两种情况都断言，并分别打印。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Roslyn.Tests
{
    internal static class Program
    {
        private static int s_pass;
        private static int s_fail;

        private static int Main(string[] args)
        {
            // tests/bin/Release/net10.0 -> 包根。向上 3 层。
            string binDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            string packageRoot = Path.GetDirectoryName(binDir.TrimEnd(Path.DirectorySeparatorChar));
            string payloadDir = args.Length > 0 ? args[0] : Path.Combine(packageRoot, "Roslyn");
            string preloadDll = Path.Combine(packageRoot, "Assemblies", "Roslyn.dll");
            string victimDll = Path.Combine(AppContext.BaseDirectory, "VictimMod.dll");

            Console.WriteLine("================================================================");
            Console.WriteLine(" Roslyn 离线自测");
            Console.WriteLine("================================================================");
            Console.WriteLine("package root : " + packageRoot);
            Console.WriteLine("payload dir  : " + payloadDir);
            Console.WriteLine("runtime      : " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Console.WriteLine();

            RoslynPayloadLoader.SetLogger(m => Console.WriteLine("  [preload] " + m));
            RoslynPayloadLoader.SetPayloadDirectoryOverride(payloadDir);

            // -----------------------------------------------------------
            // 阶段 0：前置 typeref 审计（权威，证明前置包自身无 Roslyn 依赖）
            // -----------------------------------------------------------
            Section("PHASE 0  typeref audit of the preload package itself");
            AuditPreloadAssemblyTyperefs(preloadDll);

            // -----------------------------------------------------------
            // 阶段 1：未加载 Roslyn 前，扫描受害者类型 → 预期失败
            // -----------------------------------------------------------
            Section("PHASE 1  field scan BEFORE preload (expect TypeLoadException)");
            if (!File.Exists(victimDll))
            {
                Console.WriteLine("  FATAL: VictimMod.dll not found at " + victimDll);
                return Summary();
            }

            // 显式加载受害者程序集 —— 模拟 RimWorld 里 ModAssemblyHandler.ReloadAll() 的
            // Assembly.LoadFrom。此刻 Roslyn 尚未加载，正是出问题的状态。
            Assembly victimAsm;
            try
            {
                victimAsm = Assembly.LoadFrom(victimDll);
                Console.WriteLine("  VictimMod loaded from " + victimAsm.Location);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  VictimMod LoadFrom threw: " + ex.GetType().Name + ": " + First(ex.Message));
                return Summary();
            }
            Console.WriteLine();

            bool beforeThrew = ScanVictimAndReport("pre-preload");
            Console.WriteLine();
            Console.WriteLine("  => " + (beforeThrew
                ? "PRE-LOAD SCAN THREW, as expected. This is the bug being fixed."
                : "PRE-LOAD SCAN DID NOT THROW. On CoreCLR typeref resolution is lazy, so the\n" +
                  "     throw may only surface when a Roslyn member is actually touched;\n" +
                  "     PHASE 4b forces that. The authoritative failure reproduction remains\n" +
                  "     the real Mono Player.log (see RESEARCH.md A.1.4)."));
            Console.WriteLine();

            // -----------------------------------------------------------
            // 阶段 2：运行前置包加载（等价于 CallAll 命中 cctor）
            // -----------------------------------------------------------
            Section("PHASE 2  run the preload package (fixed order, idempotent)");
            bool loaded = RoslynPayloadLoader.TryEnsureLoaded(out string failure);
            Check("RoslynPayloadLoader.TryEnsureLoaded() == true", loaded);
            if (!loaded)
            {
                Check("failure reason recorded (non-empty)", !string.IsNullOrEmpty(failure));
                Console.WriteLine("  failure: " + failure);
                return Summary();
            }
            Check("loaded count == " + RoslynPayloadLoader.LoadOrder.Length,
                  RoslynPayloadLoader.LoadedCount == RoslynPayloadLoader.LoadOrder.Length);
            Console.WriteLine("  diagnostics:\n" + RoslynPayloadLoader.GetDiagnostics().Replace("\n", "\n  "));

            // -----------------------------------------------------------
            // 阶段 3：加载后再次扫描同一类型 → 预期不再抛
            // -----------------------------------------------------------
            Section("PHASE 3  field scan AFTER preload (must NOT throw)");
            bool afterThrew = ScanVictimAndReport("post-preload");
            Check("post-preload scan of VictimMod.ReplSession did NOT throw", !afterThrew);

            // -----------------------------------------------------------
            // 阶段 4：复刻「全程序集 Parallel.ForEach 扫描」
            // -----------------------------------------------------------
            Section("PHASE 4  full-assembly field scan (Parallel.ForEach equivalent)");
            ScanAllAssemblies();

            // -----------------------------------------------------------
            // 阶段 4b：强制触碰 Roslyn 字段的运行时类型（让 CoreCLR 的惰性 typeref 解析显形）
            // -----------------------------------------------------------
            Section("PHASE 4b  force runtime resolution of the victim Roslyn fields");
            bool forcedThrew = ForceFieldTypeResolution(victimAsm);
            Console.WriteLine("  => " + (forcedThrew
                ? "FORCED RESOLUTION THREW."
                : "Forced resolution OK: the victim's Roslyn field types are now resolvable."));
            Check("post-preload: victim's Roslyn field types resolvable at runtime", !forcedThrew);

            // -----------------------------------------------------------
            // 阶段 5：幂等 + 失败缓存
            // -----------------------------------------------------------
            Section("PHASE 5  idempotency + failure caching");
            Check("second TryEnsureLoaded() == true", RoslynPayloadLoader.TryEnsureLoaded(out _));
            Check("third TryEnsureLoaded() == true", RoslynPayloadLoader.TryEnsureLoaded(out _));
            Check("LoadedCount still == 8 (no double-load, no throw)",
                  RoslynPayloadLoader.LoadedCount == RoslynPayloadLoader.LoadOrder.Length);
            Console.WriteLine("  => repeated calls are idempotent; Mono's typeref negative-cache is not re-triggered.");

            // -----------------------------------------------------------
            // 阶段 6：类型解析正向验证
            // -----------------------------------------------------------
            Section("PHASE 6  type resolution after preload");
            foreach (string tn in new[]
                     {
                         "Microsoft.CodeAnalysis.MetadataReference",
                         "Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax"
                     })
            {
                Type t = RoslynPayloadLoader.ResolveTypeOrNull(tn);
                Check("resolvable: " + tn, t != null);
            }

            return Summary();
        }

        // ---------------------------------------------------------------

        private static void Section(string title)
        {
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(" " + title);
            Console.WriteLine("----------------------------------------------------------------");
        }

        private static void Check(string what, bool ok)
        {
            if (ok)
            {
                s_pass++;
                Console.WriteLine("  PASS  " + what);
            }
            else
            {
                s_fail++;
                Console.WriteLine("  FAIL  " + what);
            }
        }

        /// <summary>
        /// 强制运行时解析受害者字段的 Roslyn 类型：访问 FieldType 的成员（Name/FullName），
        /// 这会让 CoreCLR 真正去绑定字段的 typeref——正是 Mono 在字段扫描时立刻失败的同一步。
        /// </summary>
        private static bool ForceFieldTypeResolution(Assembly victimAsm)
        {
            Type repl;
            try
            {
                repl = victimAsm.GetType("VictimMod.ReplSession", throwOnError: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  GetType threw: " + ex.GetType().Name + ": " + First(ex.Message));
                return true;
            }

            BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                foreach (FieldInfo f in repl.GetFields(flags))
                {
                    // 触碰字段类型的运行时元数据 = 触发 typeref 绑定。
                    Console.WriteLine("  field " + f.Name + " -> " + f.FieldType.FullName +
                                      " (asm=" + SafeAssemblyName(f.FieldType) + ")");
                    if (f.FieldType.FullName != null && f.FieldType.FullName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
                    {
                        Console.WriteLine("      resolved OK, IsClass=" + f.FieldType.IsClass +
                                          ", BaseType=" + SafeName(f.FieldType.BaseType));
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  THREW: " + ex.GetType().Name + ": " + First(ex.Message));
                return true;
            }
        }

        private static string SafeAssemblyName(Type t)
        {
            try { return t.Assembly == null ? "<null>" : t.Assembly.GetName().Name; }
            catch (Exception e) { return "<err:" + e.GetType().Name + ">"; }
        }

        private static string SafeName(Type t)
        {
            try { return t == null ? "<null>" : t.FullName; }
            catch (Exception e) { return "<err:" + e.GetType().Name + ">"; }
        }

        /// <summary>
        /// 对受害者类型做 GetFields（等价于 ReportProbablyMissingAttributes 的核心动作：
        /// GetFields 之后必须逐个触碰 FieldType，那才是 Mono 解析字段 typeref 的时刻）。
        /// </summary>
        private static bool ScanVictimAndReport(string tag)
        {
            Assembly victim = FindLoaded("VictimMod");
            if (victim == null)
            {
                Check("VictimMod assembly loaded (" + tag + ")", false);
                return false;
            }

            Type repl;
            try
            {
                repl = victim.GetType("VictimMod.ReplSession", throwOnError: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [" + tag + "] GetType threw: " + ex.GetType().Name + ": " + First(ex.Message));
                return true;
            }

            BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo[] fields = null;
            try
            {
                fields = repl.GetFields(flags);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [" + tag + "] GetFields THREW: " + ex.GetType().Name);
                Console.WriteLine("      " + First(ex.Message));
                return true;
            }

            // 关键：GetFields 可能成功返回，但**逐个触碰 FieldType** 才是 Mono 真正解析
            // 字段 typeref 的那一步——消费方模组的崩溃就发生在这里
            // （Player.log 栈帧：System.RuntimeType.GetFields_native）。
            Console.WriteLine("  [" + tag + "] GetFields returned " + fields.Length + " field(s); resolving each field type:");
            foreach (FieldInfo f in fields)
            {
                try
                {
                    Console.WriteLine("      " + f.Name + " : " + f.FieldType.FullName +
                                      "  (asm=" + SafeAssemblyName(f.FieldType) + ")");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("      " + f.Name + " : <FieldType resolution THREW " +
                                      ex.GetType().Name + ">");
                    Console.WriteLine("          " + First(ex.Message));
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine("          inner: " + ex.InnerException.GetType().Name +
                                          ": " + First(ex.InnerException.Message));
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>对所有已加载程序集的每个类型做 GetFields——等价于 Parallel.ForEach(GenTypes.AllTypes)。</summary>
        private static void ScanAllAssemblies()
        {
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            int typeCount = 0;
            int fieldCount = 0;
            var failures = new List<string>();

            foreach (Assembly asm in asms)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    types = rtle.Types.Where(t => t != null).ToArray();
                }
                catch (Exception ex)
                {
                    failures.Add(asm.GetName().Name + " GetTypes: " + ex.GetType().Name);
                    continue;
                }

                foreach (Type t in types)
                {
                    if (t == null) continue;
                    typeCount++;
                    try
                    {
                        fieldCount += t.GetFields(flags).Length;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(t.FullName + " :: " + ex.GetType().Name + ": " + First(ex.Message));
                    }
                }
            }

            Console.WriteLine("  assemblies scanned : " + asms.Length);
            Console.WriteLine("  types scanned      : " + typeCount);
            Console.WriteLine("  fields reflected   : " + fieldCount);
            if (failures.Count == 0)
            {
                s_pass++;
                Console.WriteLine("  PASS  no type threw during GetFields across all loaded assemblies");
            }
            else
            {
                s_fail++;
                Console.WriteLine("  FAIL  " + failures.Count + " type(s) threw:");
                foreach (string f in failures.Take(20)) Console.WriteLine("      " + f);
            }
        }

        /// <summary>阶段 0：证明前置包程序集自身不含任何 Roslyn 元数据引用（AssemblyRef/TypeRef）。</summary>
        private static void AuditPreloadAssemblyTyperefs(string preloadDll)
        {
            if (!File.Exists(preloadDll))
            {
                Console.WriteLine("  (skipped: " + preloadDll + " not built yet — build Source/Roslyn.csproj first)");
                return;
            }

            // 直接用 Mono.Cecil 不方便；改为用 System.Reflection.Metadata（本测试运行在 net8.0）。
            var refs = new List<string>();
            var typerefs = new List<string>();
            try
            {
                using FileStream fs = File.OpenRead(preloadDll);
                using var pe = new PEReader(fs);
                var md = pe.GetMetadataReader();
                foreach (var h in md.AssemblyReferences)
                {
                    var ar = md.GetAssemblyReference(h);
                    refs.Add(md.GetString(ar.Name));
                }
                foreach (var h in md.TypeReferences)
                {
                    var tr = md.GetTypeReference(h);
                    typerefs.Add(md.GetString(tr.Name) + "  (ns=" + md.GetString(tr.Namespace) + ")");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  (metadata audit skipped: " + ex.GetType().Name + ": " + First(ex.Message) + ")");
                return;
            }

            Console.WriteLine("  preload dll        : " + preloadDll);
            Console.WriteLine("  AssemblyRefs (" + refs.Count + "): " + string.Join(", ", refs));
            Console.WriteLine("  TypeRefs (" + typerefs.Count + "):");
            foreach (string t in typerefs) Console.WriteLine("      " + t);

            bool badRef = refs.Any(r => r != null && r.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase));
            bool badTref = typerefs.Any(t => t.IndexOf("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase) >= 0);

            Check("preload has no Microsoft.CodeAnalysis AssemblyRef", !badRef);
            Check("preload has no Microsoft.CodeAnalysis TypeRef", !badTref);
        }

        private static Assembly FindLoaded(string simpleName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a != null && string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }

        private static string First(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int nl = s.IndexOf('\n');
            string line = nl >= 0 ? s.Substring(0, nl) : s;
            return line.Length > 220 ? line.Substring(0, 220) + "..." : line;
        }

        private static int Summary()
        {
            Console.WriteLine();
            Console.WriteLine("================================================================");
            Console.WriteLine(" RESULT: " + s_pass + " passed, " + s_fail + " failed");
            Console.WriteLine("================================================================");
            return s_fail == 0 ? 0 : 1;
        }
    }
}