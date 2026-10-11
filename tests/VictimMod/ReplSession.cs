// VictimMod —— 「受害者」模组模拟件。
//
// 复刻真实故障中消费方类型（某个「Roslyn 桥接 / REPL 会话」类）的字段形状：
//   真实日志（%LOCALAPPDATA%\Ludeon Studios\RimWorld by Ludeon Studios\Player.log:1614）报的是
//     TypeLoadException: Could not load type of field 'SomeMod.SomeFeature:s_imageRefs'
//       due to: Could not resolve type with token 010000db from typeref
//       (expected class 'Microsoft.CodeAnalysis.MetadataReference' in assembly 'Microsoft.CodeAnalysis, Version=4.14.0.0 ...')
//   另一个是同名显示类闭包字段 v : VariableDeclaratorSyntax。
//   （日志原文的消费方类型名已匿名化；版本/强命名 token 与 RESEARCH.md A.1.4 一致。）
//
// 关键点：Verse 的扫描用 BindingFlags.Static|Public|NonPublic，**只看静态字段**
//   （见 StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes）。消费方的
//   s_imageRefs 正是一个 static 字段。所以这里的字段也必须是 static，否则复现不忠实。
//
// 本文件**故意编译期引用 Roslyn**——这正是受害者的定义。
// 前置包则相反，绝不引用（见 Source/RoslynPayloadLoader.cs）。

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VictimMod
{
    /// <summary>
    /// 形状与真实故障中消费方「Roslyn 桥接 / REPL 会话」类型对齐的静态类型。
    /// 一旦对含 Roslyn 类型静态字段的类型做 GetFields，而 Roslyn 未加载，
    /// Mono 在解析字段 typeref 时抛 TypeLoadException。
    /// </summary>
    public static class ReplSession
    {
        // 对应真实故障的 s_imageRefs（MetadataReference，抽象基类）
        private static MetadataReference s_imageRefs;

        // 对应真实故障的显示类闭包字段 v（VariableDeclaratorSyntax）
        private static VariableDeclaratorSyntax v;

        /// <summary>报告两个 Roslyn 类型字段的当前绑定状态（触碰 typeref 会真正触发解析）。</summary>
        public static string Describe()
        {
            return "s_imageRefs=" + (s_imageRefs == null ? "null" : s_imageRefs.GetType().FullName) +
                   ", v=" + (v == null ? "null" : v.GetType().FullName);
        }
    }
}