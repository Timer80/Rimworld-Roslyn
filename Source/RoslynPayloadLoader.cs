// Roslyn —— Roslyn 载荷加载核心（Verse 无关）
//
// 设计红线：本文件（及整个程序集）**不得**出现任何 Microsoft.CodeAnalysis 的 typeref / typeref-token。
// 若本包自身编译期引用了 Roslyn 类型，它就会成为同一个 bug 的新受害者
// （字段 typeref 解析失败 → Type.GetFields 抛 TypeLoadException）。
//
// 本文件刻意不引用 Verse / UnityEngine：它是纯 .NET 核心，既能在 RimWorld 里跑，
// 也能被离线自测工程直接调用（tests/FieldScanRepro）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Roslyn
{
    /// <summary>
    /// 按固定顺序把 Roslyn 载荷装进默认 AppDomain。幂等、失败缓存、不静默。
    /// </summary>
    /// <remarks>
    /// <para><b>顺序不可调换。</b>Mono 对解析失败的 typeref 会做进程级负缓存：
    /// 若先加载 <c>System.Memory</c> 再加载 <c>System.Numerics.Vectors</c>，
    /// 前者对后者的 typeref 解析失败会被缓存，之后补齐也无法恢复（须重启进程）。</para>
    /// <para><b>本类必须保持 Verse 无关。</b>RimWorld 侧的门面 <see cref="RoslynLoader"/>
    /// 只做「cctor → 本类」的一行委托，使离线测试能跑真实的加载逻辑。</para>
    /// </remarks>
    public static class RoslynPayloadLoader
    {
        /// <summary>
        /// 可选的载荷目录覆盖环境变量名（中性命名前缀 <c>ROSLYN_</c>，不绑定任何消费方）。
        /// 仅供下游部署形态或离线测试把载荷放在非标准位置时使用；
        /// 未设置、设置为空或指向不存在的目录时一律忽略，回落到默认定位。
        /// </summary>
        public const string PayloadDirEnvVar = "ROSLYN_PAYLOAD_DIR";

        /// <summary>
        /// 载荷的固定加载顺序（被依赖者在前）。**不要修改顺序**。
        /// 该顺序取自 Roslyn 载荷自身的依赖拓扑（见 Roslyn/VERSIONS.md）。
        /// </summary>
        public static readonly string[] LoadOrder =
        {
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.Memory.dll",
            "System.Collections.Immutable.dll",
            "System.Reflection.Metadata.dll",
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
        };

        /// <summary>用于正向验证「字段扫描所需的类型现在能解析」的探针（字符串形式，无 typeref）。</summary>
        private static readonly string[] ProbeTypes =
        {
            "Microsoft.CodeAnalysis.MetadataReference",
            "Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax",
        };

        private static readonly Dictionary<string, Assembly> s_loaded =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>一次性门闩：true = 已尝试过（无论成败）。失败必须缓存，重试无意义。</summary>
        private static bool s_attempted;

        /// <summary>进程内测试/诊断用的载荷目录覆盖；null 表示走默认定位。</summary>
        private static string s_payloadDirOverride;

        /// <summary>日志回调。由 Verse 门面注入 Verse.Log；离线测试注入 Console。</summary>
        private static Action<string> s_logger;

        private static bool s_available;
        private static string s_failure;
        private static string s_failedFile;

        /// <summary>8 个程序集是否已全部就绪且类型可解析。</summary>
        public static bool IsAvailable => s_available;

        /// <summary>是否已尝试过（区分「还没跑」与「跑了但失败」）。</summary>
        public static bool Attempted => s_attempted;

        /// <summary>失败原因；无失败则为 null。</summary>
        public static string FailureReason => s_failure;

        /// <summary>出错/缺失的文件名（相对），无则 null。</summary>
        public static string FailedFile => s_failedFile;

        /// <summary>已加载程序集数；成功时为 8。</summary>
        public static int LoadedCount => s_loaded.Count;

        /// <summary>安装日志回调（Verse.Log 适配 / 离线测试用）。重复安装以最后一次为准。</summary>
        public static void SetLogger(Action<string> logger)
        {
            s_logger = logger;
        }

        /// <summary>强制指定载荷目录，跳过默认定位（离线测试用）。</summary>
        public static void SetPayloadDirectoryOverride(string dir)
        {
            s_payloadDirOverride = dir;
        }

        /// <summary>
        /// 幂等地确保 8 个 Roslyn 程序集按固定顺序加载。线程安全。
        /// </summary>
        /// <param name="failure">失败原因；成功为 null。</param>
        public static bool TryEnsureLoaded(out string failure)
        {
            lock (s_loaded)
            {
                failure = null;
                if (s_attempted)
                {
                    failure = s_failure;
                    return s_available;
                }
                s_attempted = true;

                string dir = ResolvePayloadDirectory();
                if (string.IsNullOrEmpty(dir))
                {
                    return Fail("<载荷目录未定位>", null);
                }

                foreach (string file in LoadOrder)
                {
                    if (s_loaded.ContainsKey(file))
                    {
                        continue; // 已就绪（例如被其它加载器抢先载入）
                    }

                    string full;
                    try
                    {
                        full = Path.Combine(dir, file);
                    }
                    catch (ArgumentException ex)
                    {
                        return Fail("路径非法: " + file + " (" + ex.GetType().Name + ")", file);
                    }

                    if (!File.Exists(full))
                    {
                        return Fail("载荷缺失: " + full, file);
                    }

                    try
                    {
                        Assembly.LoadFrom(full);
                        s_loaded[file] = FindLoadedAssembly(file);
                    }
                    catch (Exception ex)
                    {
                        return Fail(file + " 加载失败: " + ex.GetType().Name + ": " + ex.Message, file);
                    }
                }

                // 正向验证：别的程序集此刻能否解析到 Roslyn 类型（等价于字段扫描会做的事）。
                foreach (string probe in ProbeTypes)
                {
                    if (ResolveTypeOrNull(probe) == null)
                    {
                        return Fail("已 LoadFrom，但类型自检解析不到 " + probe, null);
                    }
                }

                s_available = true;
                s_failure = null;
                s_failedFile = null;
                Log("Roslyn 载荷加载完成（" + s_loaded.Count + "/" + LoadOrder.Length + "）。");
                return true;
            }
        }

        /// <summary>
        /// 定位载荷目录。优先级：
        /// 1) <see cref="SetPayloadDirectoryOverride"/> 覆盖（测试）
        /// 2) 环境变量 <c>ROSLYN_PAYLOAD_DIR</c>（可选覆盖，供下游/测试把载荷放在
        ///    非标准位置时使用；未设置或目录不存在则忽略）
        /// 3) 本程序集所在目录的同级 <c>Roslyn\</c>（即 &lt;modroot&gt;\Roslyn）
        /// 4) 本程序集所在目录的 <c>Roslyn\</c>
        /// </summary>
        public static string ResolvePayloadDirectory()
        {
            if (!string.IsNullOrEmpty(s_payloadDirOverride) && Directory.Exists(s_payloadDirOverride))
            {
                return s_payloadDirOverride;
            }

            try
            {
                string env = Environment.GetEnvironmentVariable(PayloadDirEnvVar);
                if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
                {
                    return env;
                }
            }
            catch (Exception)
            {
                // 环境读取失败不应致命，继续下一条路径。
            }

            string selfDir = GetSelfDirectory();
            if (!string.IsNullOrEmpty(selfDir))
            {
                try
                {
                    // 标准部署：<modroot>\Assemblies\Roslyn.dll + <modroot>\Roslyn\*.dll
                    string parent = Path.GetDirectoryName(selfDir);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        string c1 = Path.Combine(parent, "Roslyn");
                        if (IsPayloadDir(c1))
                        {
                            return c1;
                        }
                    }
                    string c2 = Path.Combine(selfDir, "Roslyn");
                    if (IsPayloadDir(c2))
                    {
                        return c2;
                    }
                }
                catch (Exception)
                {
                    // 路径异常，落到返回 null。
                }
            }
            return null;
        }

        private static bool IsPayloadDir(string dir)
        {
            return Directory.Exists(dir) && File.Exists(Path.Combine(dir, LoadOrder[0]));
        }

        private static string GetSelfDirectory()
        {
            try
            {
                Assembly self = typeof(RoslynPayloadLoader).Assembly;
                string loc = !string.IsNullOrEmpty(self.Location) ? self.Location : self.CodeBase;
                if (string.IsNullOrEmpty(loc))
                {
                    return null;
                }
                if (loc.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    loc = new Uri(loc).LocalPath;
                }
                return Path.GetDirectoryName(loc);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>按简单名在当前 AppDomain 中定位已加载程序集；找不到返回 null。</summary>
        private static Assembly FindLoadedAssembly(string fileName)
        {
            string simple = Path.GetFileNameWithoutExtension(fileName);
            try
            {
                Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < all.Length; i++)
                {
                    Assembly a = all[i];
                    if (a != null && string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase))
                    {
                        return a;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("枚举 AppDomain 失败(" + simple + "): " + ex.GetType().Name + ": " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// 按全名在全 AppDomain 解析类型。纯字符串，**不产生 typeref**。
        /// </summary>
        public static Type ResolveTypeOrNull(string fullTypeName)
        {
            try
            {
                Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < all.Length; i++)
                {
                    Assembly a = all[i];
                    if (a == null)
                    {
                        continue;
                    }
                    try
                    {
                        Type t = a.GetType(fullTypeName, false);
                        if (t != null)
                        {
                            return t;
                        }
                    }
                    catch (Exception)
                    {
                        // 单个程序集解析失败不终止扫描。
                    }
                }
            }
            catch (Exception)
            {
                // 整体枚举失败，视为解析不到。
            }
            return null;
        }

        private static bool Fail(string reason, string failedFile)
        {
            s_available = false;
            s_failure = reason;
            s_failedFile = failedFile;
            Log("Roslyn 载荷加载失败: " + reason +
                "（依赖 Roslyn 类型的其它模组其字段扫描会抛 TypeLoadException；请确认本包已启用且载荷目录完整。）");
            return false;
        }

        private static void Log(string message)
        {
            Action<string> logger = s_logger;
            if (logger != null)
            {
                try
                {
                    logger(message);
                }
                catch (Exception)
                {
                    // 日志本身失败不得影响加载流程。
                }
            }
        }

        /// <summary>人类可读诊断报告（纯字符串拼接，无副作用）。</summary>
        public static string GetDiagnostics()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[Roslyn] 诊断\n");
            sb.Append("  已尝试: ").Append(s_attempted ? "是" : "否").Append('\n');
            sb.Append("  可用  : ").Append(!s_attempted ? "未运行" : (s_available ? "是" : "否")).Append('\n');
            sb.Append("  程序集: ").Append(s_loaded.Count).Append('/').Append(LoadOrder.Length).Append('\n');
            sb.Append("  载荷目录: ").Append(ResolvePayloadDirectory() ?? "<未找到>").Append('\n');
            if (!string.IsNullOrEmpty(s_failedFile))
            {
                sb.Append("  出错文件: ").Append(s_failedFile).Append('\n');
            }
            if (!string.IsNullOrEmpty(s_failure))
            {
                sb.Append("  失败原因: ").Append(s_failure).Append('\n');
            }
            sb.Append("  版本:\n");
            foreach (string file in LoadOrder)
            {
                Assembly a;
                if (s_loaded.TryGetValue(file, out a) && a != null)
                {
                    sb.Append("    ").Append(Path.GetFileNameWithoutExtension(file)).Append(" = ")
                      .Append(a.GetName().Version).Append('\n');
                }
                else
                {
                    sb.Append("    ").Append(Path.GetFileNameWithoutExtension(file)).Append(" = <未加载>\n");
                }
            }
            return sb.ToString();
        }
    }
}