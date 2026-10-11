# Roslyn — Roslyn 前置依赖包

**中文** | [English](README.en.md)

一个**独立的前置依赖包**：在 RimWorld 启动期把 Roslyn（`Microsoft.CodeAnalysis`）载荷
按固定顺序前置加载，让任何依赖它的模组都不会因为「字段扫描」而整体失效。

典型消费者：任何需要**编译期引用 Roslyn 类型**的 RimWorld 模组 —— C# 控制台 / REPL、
运行时表达式求值、动态 mod 脚本等。

---

## 1. 它解决什么问题

RimWorld 启动期，`Verse.StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes()`
会对**所有已加载模组程序集**的每个类型做
`GetFields(BindingFlags.Static | Public | NonPublic)`。

只要某个类型的**静态字段**引用了尚未加载的 Roslyn 类型，Mono 就会抛：

```
TypeLoadException: Could not load type of field
  'SomeMod.SomeFeature:s_imageRefs' (1) due to:
  Could not resolve type with token 010000db from typeref
  (expected class 'Microsoft.CodeAnalysis.MetadataReference' in assembly
   'Microsoft.CodeAnalysis, Version=4.14.0.0, ...')
```

该异常在 `Parallel.ForEach` 内聚合成 `AggregateException`，导致**该模组功能整体不可用**。

本包把 8 个 Roslyn dll 在 `CallAll()` 阶段装入默认 AppDomain。字段扫描发生在
`CallAll()` **之后**，所以扫描时类型已可解析，问题消失。

> 详细的源码定位、调用点顺序、真实日志栈帧见 **[RESEARCH.md](RESEARCH.md)**。

---

## 2. 机制

| 环节 | 做法 |
|------|------|
| 触发 | 一个标注 `[StaticConstructorOnStartup]` 的类，由 `CallAll()` 通过 `RuntimeHelpers.RunClassConstructor` 命中 |
| 加载 | 按**固定依赖顺序** `Assembly.LoadFrom` 8 个 dll 到默认 AppDomain |
| 幂等 | `s_attempted` 一次性门闩 + 字典去重，重复调用直接返回缓存结果 |
| 失败 | 缓存失败原因 + 写 `Verse.Log`（`[Roslyn]` 前缀）。**绝不静默** |
| 自检 | 加载后正向验证 `Microsoft.CodeAnalysis.MetadataReference` 等类型确可解析 |
| 零依赖 | 本包程序集内**无任何** `Microsoft.CodeAnalysis` 的 AssemblyRef/TypeRef，全部走字符串 + 反射 |

### 为什么顺序不可调换

Mono 对解析失败的 typeref 会做**进程级负缓存**。若先加载 `System.Memory` 再加载
`System.Numerics.Vectors`，前者对后者的 typeref 解析失败会被缓存，之后补齐也**无法恢复**
（必须重启进程）。所以 `LoadOrder` 是固定的，且失败后不重试。

### 载荷布局

```
Roslyn/
├─ About/About.xml
├─ Assemblies/Roslyn.dll   ← 唯一的模组程序集
├─ Roslyn/                             ← 8 个载荷 dll（+ VERSIONS.md）
├─ Source/                             ← 源码
└─ tests/                              ← 离线自测
```

定位规则（`ResolvePayloadDirectory`）：`SetPayloadDirectoryOverride` → 环境变量
`ROSLYN_PAYLOAD_DIR`（可选覆盖） → `<模组根>\Roslyn` → `<程序集目录>\Roslyn`。

---

## 3. 部署

1. 把整个 `Roslyn\` 目录放进 `Mods\`。
2. 启用它，并**排在**依赖它的模组**之前**（推荐排在整个列表最前）。
3. 确认 `Roslyn\` 下 8 个 dll 齐全。

> ⚠️ **关于「排在前面」**：这不是硬保证（见 RESEARCH.md A.2）。本包的正确性来自
> 「字段扫描在 `CallAll()` 之后」这一时序性质，而非排序。把本包排前面只是稳妥习惯。
> 若你点了模组列表的「重置所有模组」或「修复排序」，`loadAfter` 会经拓扑排序生效。

**验证**：启动后看 `Player.log`（`%LOCALAPPDATA%\Ludeon Studios\RimWorld by Ludeon Studios\`），
应出现 `[Roslyn] Roslyn 载荷加载完成（8/8）`，且不再出现
`Could not load type of field 'SomeMod.SomeFeature:s_imageRefs'`。

---

## 4. 如何让别的模组依赖它

### 依赖方不要编译期引用本程序集

编译期引用 `Roslyn.dll` 会制造**同一种脆弱性**：一旦前置包缺失/加载失败，
依赖方就成了下一个「字段依赖前置包」的受害者 —— 正是本包要消灭的病。

正确做法：**只用反射查询**。

```csharp
using System;
using System.Reflection;

// 类型全名用字符串，避免任何 typeref。
const string Entry = "Roslyn.RoslynLoader";

public static class MyRoslynGate
{
    private static bool _checked;
    private static bool _ok;

    public static bool EnsureRoslyn(out string failure)
    {
        failure = null;
        if (_checked) return _ok;
        _checked = true;

        Type t = null;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = a.GetType(Entry, false);
            if (t != null) break;
        }
        if (t == null)
        {
            // 前置包没被加载：也可能只是「还没到 CallAll」，再兜底主动触发一次。
            try
            {
                Assembly.LoadFrom("Mods/Roslyn/Assemblies/Roslyn.dll");
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = a.GetType(Entry, false);
                    if (t != null) break;
                }
            }
            catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }
        }

        if (t == null) { failure ??= "前置包 Roslyn 未加载"; return false; }

        try
        {
            MethodInfo m = t.GetMethod("TryEnsureLoaded",
                BindingFlags.Public | BindingFlags.Static);
            if (m == null) { failure = "找不到 TryEnsureLoaded"; return false; }

            object[] args = { null };
            _ok = (bool)m.Invoke(null, args);
            failure = args[0] as string;
        }
        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }

        return _ok;
    }
}
```

### 在 `About.xml` 里声明意图

```xml
<loadAfter>
  <li>RoslynPreload.Roslyn</li>
</loadAfter>
```

如 README/RESEARCH 所述，这**不保证** cctor 顺序，只在「重置/修复排序」后经拓扑排序生效。
真正的保障是上面的反射查询 + 前置包自身的 cctor。

---

## 5. 对外 API

```csharp
namespace Roslyn
{
    [StaticConstructorOnStartup]
    public static class RoslynLoader
    {
        public static bool   TryEnsureLoaded(out string failure);  // 幂等
        public static bool   IsLoaded;      // 8/8 就绪（不触发加载）
        public static bool   Attempted;      // 区分「未运行」与「运行失败」
        public static string FailureReason;  // 无失败为 null
        public static int    LoadedCount;    // 成功为 8
        public static string GetDiagnostics();
        public const string EntryTypeName = "Roslyn.Loader";
    }
}
```

`GetDiagnostics()` 输出示例：

```
[Roslyn] 诊断
  已尝试: 是
  可用  : 是
  程序集: 8/8
  载荷目录: ...\Mods\Roslyn\Roslyn
  版本:
    System.Numerics.Vectors = 4.1.3.0
    ...
    Microsoft.CodeAnalysis = 4.14.0.0
    Microsoft.CodeAnalysis.CSharp = 4.14.0.0
```

---

## 6. 构建与自测

```powershell
# 构建模组程序集（net472，产出到 Assemblies/，并自动跑 typeref 护栏）
dotnet build Source\Roslyn.csproj -c Release

# 构建并运行离线自测（不进 Mods/，不启动游戏）
dotnet build tests\Roslyn.Tests.csproj -c Release
dotnet tests\bin\Release\net10.0\Roslyn.Tests.dll
```

游戏路径可用 `-p:RimWorldGameDir=<游戏根>` 或环境变量 `RIMWORLD_GAME_DIR` 覆盖。

### 不变量护栏

每次构建 `Source` 后自动执行 `tests/assert-no-roslyn-typref.ps1`：
若产物里出现 `Microsoft.CodeAnalysis` 的 **AssemblyRef/TypeRef 元数据**（ASCII 连续字节），
**构建直接失败**。这是本包的核心不变量 —— 前置包自己绝不能成为下一个受害者。

---

## 7. 许可与来源

本包源码以 **MIT** 许可发布，见 [LICENSE](LICENSE)。

`Roslyn/` 下的 8 个 dll 取自官方 NuGet 包，精确版本与 SHA256 见该目录 `VERSIONS.md`，
版权归 Microsoft，同样以 MIT 许可分发 —— 感谢 [dotnet/roslyn](https://github.com/dotnet/roslyn)
团队的杰出工作。完整声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

> ⚠️ **本模组的 bug 请在本仓库的 Issues 反馈，不要向 dotnet/roslyn 报告。**
> 上游是微软的编译器项目，不认识 RimWorld，也不对本模组负责。

---

## 8. 问题反馈

遇到加载失败、排序异常、与其它模组的兼容性问题，请到本仓库的
[Issues](../../issues) 提交，尽量附上：

1. `Player.log` 中 `[Roslyn]` 前缀的相关行；
2. 完整的异常栈（`TypeLoadException` / `AggregateException` 等）；
3. RimWorld 版本与模组列表（截图或导出）。