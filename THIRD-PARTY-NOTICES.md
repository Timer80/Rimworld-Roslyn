# 第三方组件声明（THIRD-PARTY-NOTICES）

> English summary: This mod redistributes Microsoft's Roslyn
> ([dotnet/roslyn](https://github.com/dotnet/roslyn)) and .NET support libraries
> from official NuGet packages under the MIT License, © Microsoft Corporation.
> **Please report bugs for this mod in THIS repository's Issues — do NOT file
> issues at dotnet/roslyn.** Upstream is Microsoft's compiler project and is
> not responsible for this RimWorld mod. Full details below.

## 致谢：Microsoft Roslyn

本模组的载荷来自 **[dotnet/roslyn](https://github.com/dotnet/roslyn)** ——
C# / VB.NET 编译器与代码分析平台。`Roslyn/` 目录下随包分发的
`Microsoft.CodeAnalysis.dll` 与 `Microsoft.CodeAnalysis.CSharp.dll`
取自官方 NuGet 包（nuget.org），以 **MIT License** 分发，
版权归 Microsoft Corporation。精确版本与 SHA256 见 [`Roslyn/VERSIONS.md`](Roslyn/VERSIONS.md)。

完整的 Roslyn MIT 许可证文本见
<https://github.com/dotnet/roslyn/blob/main/LICENSE.md>。

其余载荷件（`System.*`）同为微软 .NET 官方 NuGet 包（MIT 许可），来源清单同样见 `Roslyn/VERSIONS.md`。

## ⚠️ Bug 报告去向（重要）

**本模组的任何问题，请到本仓库的 Issues 反馈，不要去 dotnet/roslyn 报告。**

- dotnet/roslyn 是微软维护的上游编译器项目，它不认识 RimWorld，也不对本模组负责；
- 本模组的问题（加载失败、顺序问题、与其它模组的兼容性等）100% 属于本仓库的范畴；
- 向上游误报只会消耗微软维护者的时间，且不会得到任何对本模组有效的修复。

只有当你确信问题出在 Roslyn 编译器本身（与 RimWorld、与本模组均无关）时，
才应该去 <https://github.com/dotnet/roslyn> 报告。

## 许可

本仓库自身的源码以 MIT 许可发布，见 [LICENSE](LICENSE)。
