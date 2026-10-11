# Roslyn 载荷版本清单（8 件套）

进程内 `Assembly.Load` 用的固定依赖集，随包分发。来源全部为官方 NuGet 包（nuget.org flat-container / 本机 `%USERPROFILE%\.nuget\packages` 缓存），**不得**使用 Visual Studio 安装目录副本（不可再分发）。

| 文件 | 版本 | 来源包（NuGet） | 程序集版本（实测 `AssemblyName.Version`） |
|---|---|---|---|
| Microsoft.CodeAnalysis.dll | 4.14.0 | microsoft.codeanalysis.common/4.14.0/lib/netstandard2.0 | 4.14.0.0 |
| Microsoft.CodeAnalysis.CSharp.dll | 4.14.0 | microsoft.codeanalysis.csharp/4.14.0/lib/netstandard2.0 | 4.14.0.0 |
| System.Collections.Immutable.dll | 9.0.0 | system.collections.immutable/9.0.0/lib/netstandard2.0 | 9.0.0.0 |
| System.Reflection.Metadata.dll | 9.0.0 | system.reflection.metadata/9.0.0/lib/netstandard2.0 | 9.0.0.0 |
| System.Memory.dll | 4.5.5 | system.memory/4.5.5/lib/netstandard2.0 | 4.0.1.2 |
| System.Runtime.CompilerServices.Unsafe.dll | 6.0.0 | system.runtime.compilerservices.unsafe/6.0.0/lib/netstandard2.0 | 6.0.0.0 |
| System.Threading.Tasks.Extensions.dll | 4.5.4 | system.threading.tasks.extensions/4.5.4/lib/netstandard2.0 | 4.2.0.1 |
| System.Numerics.Vectors.dll | 程序集 4.1.3.0（包 4.4.0 的 lib/net46） | system.numerics.vectors/4.4.0 | 4.1.3.0 |

> **2026-10-09 换版（任务 4/5 实验轮，人类裁定）**：CodeAnalysis 4.14.0 的 AssemblyRef 表实测引用 `System.Collections.Immutable 9.0.0.0`、`System.Reflection.Metadata 9.0.0.0`、`System.Memory 4.0.1.2`、`System.Threading.Tasks.Extensions 4.2.0.1`——旧版（8.0.0/4.5.3/4.5.2）在 net472 测试工程触发 CS1705（版本倒挂硬错误）与运行期绑定失败，故这 4 件按 AssemblyRef 实测版本对齐重抽。换版后四组测试（53 条断言）全绿、STANDALONE_Mono 构建 0 警告 0 错误。`System.Numerics.Vectors` 未在倒挂清单内，维持 4.1.3.0（见下方选包说明）。
>
> 换版文件 SHA256：
> - System.Collections.Immutable.dll 9.0.0.0：`4743E42B28F1CFBC9C37B5121BC6E55E630D36AFB4C2D0AD3C310DAF2C56AD37`
> - System.Reflection.Metadata.dll 9.0.0.0：`D5A84292A8EAC69D1289B525CAC29959E48C1E65E200585D6FB97B617F344FE8`
> - System.Memory.dll 4.0.1.2：`11590D8BB3B12F29F4202B3EF8593229A5CD6DEBB61E76CBA9AC5493A82EE382`
> - System.Threading.Tasks.Extensions.dll 4.2.0.1：`09901F63EBC4B8F0897B0128FB230C146529AEE5C5CC90D7A7872E66A976BB5F`

> `System.Numerics.Vectors` 选包说明：曾评估包 4.5.0（其 lib/net46 程序集实为 4.1.4.0），因与可行性报告 §4.2 PoC 实测版本 4.1.3.0 不符而弃用——所有顺序/绑定实测证据均在 4.1.3.0 上取得，改用 4.4.0 官方包以保持与实测一致。

换任何一件都必须整套重验（docs/csharp-console-compiler-upgrade-feasibility.md §4.2 的顺序/版本约束）。
