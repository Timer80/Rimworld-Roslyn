// Roslyn —— RimWorld 门面
//
// 本文件是本包与 Verse 的唯一接触面。刻意保持极薄：
//   - cctor 标 [StaticConstructorOnStartup]，由 CallAll() 触发；
//   - 只做「装日志回调 + 委托给纯 .NET 核心」；
//   - **不引用任何 Roslyn 类型**（见 RoslynPayloadLoader 的设计红线）。

using System;
using Verse;

namespace Roslyn
{
    /// <summary>
    /// Roslyn 载荷前置加载门面。
    ///
    /// <para><b>为什么需要一个前置包。</b>RimWorld 启动期，
    /// <c>StaticConstructorOnStartupUtility.CallAll()</c> 跑完所有模组的
    /// <see cref="StaticConstructorOnStartup"/> cctor 之后，会执行
    /// <c>ReportProbablyMissingAttributes()</c>：它对 <c>GenTypes.AllTypes</c>
    /// （= Assembly-CSharp + 每个 mod 的 <c>loadedAssemblies</c>）里的每个类型做
    /// <c>GetFields(Static|Public|NonPublic)</c>。若某类型字段引用了尚未加载的
    /// Roslyn 类型，Mono 抛 <c>TypeLoadException: Could not load type of field ...</c>，
    /// 在 <c>Parallel.ForEach</c> 内聚合成 <c>AggregateException</c>，该 mod 功能整体不可用。</para>
    ///
    /// <para><b>时机是否够早。</b>够。见 RESEARCH.md A.1：字段扫描发生在 <c>CallAll()</c>
    /// **之后**（<c>PlayDataLoader.cs</c> 的 <c>LongEventHandler.ExecuteWhenFinished</c> 块内
    /// <c>CallAll(); if (DevMode) ReportProbablyMissingAttributes();</c>）。
    /// 只要本类被 CallAll 命中，扫描时 Roslyn 类型必然已可解析。
    /// 另一处扫描在 <c>Root.CheckGlobalInit()</c> 里且同样在 <c>Prefs.DevMode</c> 门后，
    /// 位于 <c>CallAll()</c> **之前</c>——但那发生在 PlayDataLoader 之前、
    /// mod 程序集尚未 <c>Assembly.LoadFrom</c> 时，扫不到本包与其它 mod 的类型（见 RESEARCH.md A.1.3）。</para>
    ///
    /// <para><b>依赖方请勿编译期引用本程序集。</b>用反射查询（README 有示例），
    /// 否则又制造一个「字段依赖前置包」的同类脆弱点。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    public static class RoslynLoader
    {
        /// <summary>日志前缀。</summary>
        private const string LogTag = "[Roslyn]";

        /// <summary>RimWorld 侧的 cctor。异常不外泄（CallAll 会捕获），失败走 Verse.Log 非静默。</summary>
        static RoslynLoader()
        {
            RoslynPayloadLoader.SetLogger(msg => Log.Message(LogTag + " " + msg));
            RoslynPayloadLoader.TryEnsureLoaded(out _);
        }

        /// <summary>
        /// 幂等确保 8 个 Roslyn 程序集已按固定顺序加载。
        /// 一般无需主动调用——CallAll 已在本类 cctor 里做过一次。
        /// </summary>
        /// <param name="failure">失败原因；成功为 <c>null</c>。</param>
        /// <returns>8 个程序集是否全部就绪。</returns>
        public static bool TryEnsureLoaded(out string failure)
        {
            RoslynPayloadLoader.SetLogger(msg => Log.Message(LogTag + " " + msg));
            return RoslynPayloadLoader.TryEnsureLoaded(out failure);
        }

        /// <summary>8 个 Roslyn 程序集是否已就绪（不触发加载）。</summary>
        public static bool IsLoaded => RoslynPayloadLoader.IsAvailable;

        /// <summary>是否已尝试过（区分「未运行」与「运行失败」）。</summary>
        public static bool Attempted => RoslynPayloadLoader.Attempted;

        /// <summary>失败原因；无失败为 <c>null</c>。</summary>
        public static string FailureReason => RoslynPayloadLoader.FailureReason;

        /// <summary>已加载程序集数；成功为 8。</summary>
        public static int LoadedCount => RoslynPayloadLoader.LoadedCount;

        /// <summary>人类可读诊断报告。</summary>
        public static string GetDiagnostics() => RoslynPayloadLoader.GetDiagnostics();

        /// <summary>
        /// 供依赖方反射调用的稳定入口名。刻意以字符串常量暴露，
        /// 让依赖方无需在本程序集上留 typeref。
        /// </summary>
        public const string EntryTypeName = "Roslyn.RoslynLoader";
    }
}