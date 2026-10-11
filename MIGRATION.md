# MIGRATION.md — 接入指南：如何让你的模组依赖本前置包

前置包：本目录（`packageId = RoslynPreload.Roslyn`）。
机制依据见 [RESEARCH.md](RESEARCH.md)；机制摘要见 [README.md](README.md)。

本文的主视角是**依赖方**：怎样声明、怎样查询、以及为什么必须用反射。
末尾 §6 保留一个**匿名化的真实案例**，说明这套接入方式能省掉哪些坑。

> 本包是**通用基础设施**：它不认识任何具体模组，也不假设谁在消费它。
> 下面每一条约束对**任何**消费者都成立。

---

## 0. 三十秒速览

| 你要做的事 | 怎么做 |
|---|---|
| 装前置包 | 把 `Roslyn\` 整个目录拷进 `Mods\`，启用（排在前面是稳妥习惯，非硬保证） |
| 表达排序意图 | 你的 `About.xml` 里加 `<loadAfter><li>RoslynPreload.Roslyn</li></loadAfter>` |
| 运行时查询状态 | **反射**调用 `Roslyn.RoslynLoader`（§2），**不要**编译期引用 `Roslyn.dll` |
| 想根除这类故障 | 把 Roslyn 类型从**静态字段**移到实例字段/局部变量（§3 C.5） |

---

## 1. 根因：字段扫描为什么会被 Roslyn 打死

RimWorld 启动期，`Verse.StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes()`
对**所有已加载模组程序集**的每个类型做
`GetFields(Static | Public | NonPublic)`。

只要某个类型的**静态字段**类型来自尚未加载的 Roslyn 程序集，Mono 就抛：

```
TypeLoadException: Could not load type of field
  'SomeMod.SomeFeature:s_imageRefs' (1) due to:
  Could not resolve type with token 010000db from typeref
  (expected class 'Microsoft.CodeAnalysis.MetadataReference' in assembly
   'Microsoft.CodeAnalysis, Version=4.14.0.0, ...')
```

它在 `Parallel.ForEach` 内聚合成 `AggregateException`，导致**该模组功能整体不可用**。

两个关键性质（完整证据见 RESEARCH.md A.1）：

1. 扫描**只看 `BindingFlags.Static`** —— 实例字段不参与。
2. 扫描发生在 `CallAll()` **之后** —— 所有 mod 的 `[StaticConstructorOnStartup]`
   cctor 都已跑完，才开始扫描。

⇒ 只要前置包被启用，扫描时 Roslyn 类型必然已可解析。**与前置包在模组列表里排第几位无关。**

---

## 2. 依赖方必须做的事

### 2.1 在 `About.xml` 里声明意图

```xml
<loadAfter>
  <li>RoslynPreload.Roslyn</li>
</loadAfter>
```

**请如实理解这条声明**：它**不保证** cctor 顺序，只在玩家点「重置所有模组」或
「修复排序」时经 `ModsConfig.TrySortMods()` 拓扑排序生效（RESEARCH.md A.2）。
真正的保障来自 §1 的时序性质 + 前置包自身的 cctor，**不是**来自这条声明。

若你希望用户能一眼看出缺前置包，可另加 `<modDependencies>`——它的**唯一**作用
就是模组列表 UI 的黄标/图标，`IsSatisfied` 只判断对方是否 Active，不参与任何排序。

### 2.2 运行时用反射查询，**绝不**编译期引用本程序集

**不要**在 `.csproj` 里 `<Reference Include="Roslyn">`。那会给你新增一条
「字段/元数据依赖前置包」的边 —— 前置包一失效你就跟着倒，
正是本包要消灭的同一种病。

只用字符串 + 反射：

```csharp
using System;
using System.Reflection;

public static class MyRoslynGate
{
    private static bool _checked;
    private static bool _ok;

    private const string Entry = "Roslyn.RoslynLoader";

    public static bool EnsureRoslyn(out string failure)
    {
        failure = null;
        if (_checked) return _ok;
        _checked = true;

        Type t = FindEntryType();
        if (t == null)
        {
            failure = "前置包 Roslyn 未加载";
            return false;
        }

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

    private static Type FindEntryType()
    {
        // 全 AppDomain 按名字找，避免在本程序集上留 typeref。
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = a.GetType(Entry, false);
            if (t != null) return t;
        }
        return null;
    }
}
```

`TryEnsureLoaded(out string failure)` 是幂等的：`CallAll()` 已经调过一次，
你再调只是读缓存。`failure` 为 `null` 表示成功，否则是可读的失败原因
（缺哪个 dll、哪个加载异常），请把它写进自己的日志或提示给用户。

> 兜底加载（可选）：若前置包没被发现（例如用户没启用），可以按 §2.4 自己
> `Assembly.LoadFrom` 一遍再找入口。**要**做本地兜底加载时，把兜底写成
> 「前置包在 → 单一加载点；不在 → 自举」的顺序，不要让两处无条件各加载一次。

### 2.3 `Assemblies/Roslyn.dll` 路径注意

若你要写兜底 `LoadFrom`，路径是相对 `Mods\` 的：`Mods/Roslyn/Assemblies/Roslyn.dll`。
更稳妥的做法是遍历 `LoadedModManager` 或用上面的全 AppDomain 名字查找，
避免把本包目录名写死在你代码里。

### 2.4 不要在 `LoadOrder` 上做文章

前置包的 `LoadOrder` 是 `readonly` 数组，被依赖者在前。Mono 对解析失败的 typeref
有**进程级负缓存**：顺序错了、之后补齐也**无法恢复**（必须重启进程）。
所以前置包失败后不重试（`s_attempted` 一次性门闩）。这条约束对所有消费者一致，
不要试图在消费侧绕过它。

---

## 3. 你的代码要改什么

| # | 改动 | 建议 | 理由 |
|---|------|------|------|
| **C.1** | `About.xml`：`<modDependencies>` 指向本包 | 可选 | 仅 UI 提示。**不要**指望它保证顺序。 |
| **C.2** | `About.xml`：`<loadAfter>` 指向本包 `packageId` | ✅ 加 | 表达意图；「重置/修复排序」后经拓扑排序生效（RESEARCH.md A.2）。 |
| **C.3** | 你的 Roslyn 加载器（若有） | 🔄 改为「查前置包 + 可选兜底」的薄壳 | 见 §2.2；避免两处各加载一次。 |
| **C.4** | 你自己 `Init` 里的早加载调用 | 🗑 降级为兜底 | 前置包正常路径已覆盖；保留仅作前置包缺失时的退路。 |
| **C.5** | 代码中任何**直接引用** Roslyn 的**静态字段** | ⚠️ 尽量改实例字段/局部变量 | 字段扫描只扫 `BindingFlags.Static`。**把 Roslyn 引用移出静态字段，是根上的解法**（§4）。 |

C.3 的形状：

```csharp
// 伪代码：你的 RoslynAssemblyLoader.EnsureLoaded 内部
if (TryQueryPreloadPackage(out string failure))   // 反射 Roslyn.RoslynLoader
    return s_available;                            // 成功：前置包单飞

return EnsureLoadedFallbackLocalPayload();        // 兜底：走自己的载荷目录
```

这样：前置包在 → 单一加载点；前置包不在/失败 → 你仍能自举，行为退化为未装本包时的现状。

> ⚠️ 关于「两处都在场」：若你已兜底加载过、前置包 cctor 又跑一次，Mono 对同一
> dll 的重复 `LoadFrom` 是幂等的（返回同一 Assembly），不会出问题；但**先加载失败
> 的那次会命中 typeref 负缓存**，所以前置包的失败是终态，别指望兜底能救。

---

## 4. §4 「这套机制能否真正解决字段扫描问题」的诚实评估

**能，但有明确边界。**

* ✅ **能** —— 字段扫描发生在 `CallAll()` 之后。前置包被启用并成功加载时，
  扫描时 Roslyn 类型必然可解析，`TypeLoadException` 不再发生。
  自测 PHASE 1→3 复现了「加载前抛 / 加载后不抛」。
* ✅ **耦合面小** —— 本方案只依赖「前置包被 CallAll 命中」，不依赖任何具体消费方的初始化链。
* ⚠️ **不消除字段依赖本身** —— 只要你的静态字段类型仍来自 Roslyn，一旦前置包
  未启用/加载失败，扫描仍会抛。**根上的解法是 C.5。**
* ⚠️ **自测跑在 CoreCLR 而非 Mono** —— 详见 RESEARCH.md「自测的诚实边界」。
  部署后**务必**以真实 `Player.log` 复核。

---

## 5. 风险清单与降级路径（通用）

| # | 风险 | 表现 | 降级路径 |
|---|------|------|----------|
| **R.1** | 前置包被用户禁用 / 没部署 | 你的字段扫描照旧抛 `TypeLoadException: Could not load type of field '...'`；功能整体不可用 | 你的 C.3 兜底路径自行加载；或请用户装前置包 |
| **R.2** | 前置包加载了但载荷目录缺文件 | 前置包 cctor 写 `[Roslyn] Roslyn 载荷加载失败: 载荷缺失: ...` 到 `Player.log`；`IsLoaded=false` | 你通过 `TryEnsureLoaded(out failure)` 拿到 `failure`；走兜底本地加载 |
| **R.3** | 载荷版本与你期望不匹配 | 典型症状：编译时 `MissingMethodException` / `TypeLoadException`（非字段扫描阶段） | 同步前置包的 `Roslyn\` 至你要的版本，见 `Roslyn/VERSIONS.md` |
| **R.4** | 加载顺序被破坏（有人改 `LoadOrder`） | Mono typeref 负缓存 → 运行时随机失败，且**必须重启进程** | `LoadOrder` 为 `readonly` 数组 + 注释 + README 警告；构建护栏不覆盖此项，靠 code review 约束 |
| **R.5** | 前置包 cctor 抛未捕获异常 | `CallAll()` 会 catch 每个 cctor 异常并打日志，不影响其它 cctor；但本包 cctor 内部已自 catch | 已实现：门面只调 `TryEnsureLoaded`，失败走日志 |
| **R.6** | 前置包程序集自身含 Roslyn typeref（被误改） | 前置包自己成为受害者，字段扫描崩溃 | **构建护栏** `tests/assert-no-roslyn-typref.ps1` 在 build 后拦截；另有自测 PHASE 0 审计 |
| **R.7** | 前置包 dll 放错目录 | `ModAssemblyHandler` 打 `Exception loading ...` 并 `break`（该 mod 后续 dll 都不加载） | 标准模组目录结构，用户可自查；`Player.log` 有明确报错 |

---

## 6. 案例（匿名化）：一个消费方踩过的坑

> 下面是一个**真实但匿名**的消费方案例，用来说明这套接入方式为什么长这样。

某消费方在自己的初始化链里**早加载** Roslyn（此前为修这个 bug 的一次临时提交）。
经 `Player.log` 逐帧核对，**该早加载确实早于崩溃点**——它是一个**真实有效**的修复，
不是无效功。它的局限只有一条：**它是本消费方初始化链的一部分**，若该模组因任何
原因没走到那一步（被禁用、前序步骤抛异常），这个早加载就不发生。
即：这是「消费方未启用时」的**降级问题**，不是「启用时也不生效」的**正确性问题**。

由此得出的三条通用结论（与前置包并存，不互斥）：

1. 把早加载降级为兜底，正常路径交给前置包，避免两处各加载一次。
2. 保留一条「前置包不可用 → 消费方自举」的退路（§3 C.3/C.4）。
3. 根上的修复始终是 **C.5**：把 Roslyn 引用移出静态字段。

---

## 7. 建议的落地顺序（通用）

1. 先做 **C.5**（Roslyn 引用移出静态字段）—— 根上的修复，独立于本包。
2. 部署前置包到 `Mods\`，**先不改** 任何消费方代码 —— 因为前置包单独就能修好字段扫描。
   此时应能看到日志里 `（8/8）` 且功能不再崩。
3. 再做 **C.3/C.4**：把各加载点改为「查询前置包 + 兜底」。
4. 用真实 `Player.log` 复核一遍（RESEARCH.md A.1 的验证方法）。

> 这样每一步都可独立回滚，且第 2 步就能拿到「前置包单飞就够」的直接证据。