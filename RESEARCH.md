# RESEARCH.md — Roslyn 前置加载机制调研

调研对象：RimWorld 1.6（`Assembly-CSharp.dll` 15777280 bytes, 2026-07-04）。
证据优先级：**实际出货程序集的反编译** > Ludeon 官方源码（rimworld-source-online MCP）> 社区资料。

> 两条独立证据链互相校验：官方源码用于说明意图与上下文，**随游戏出货的 `Assembly-CSharp.dll`
> 反编译用于确认实际行为**。两者在本报告中所有关键结论上完全一致。

---

## A.1 加载顺序保证（最关键的未知项）

### A.1.1 结论先行

> **mod 的 `[StaticConstructorOnStartup]` cctor 一定早于「会触发 TypeLoadException 的那次字段扫描」。**
>
> 前提：前置包的类被 `CallAll()` 命中（这要求它作为 mod 被启用并 `Assembly.LoadFrom` 成功）。
> **cctor 之间的相对顺序则没有任何保证**（见 A.1.4），但本方案**不需要**该保证。

### A.1.2 扫描函数本体

`Source/Verse/StaticConstructorOnStartupUtility.cs`

```csharp
public static void CallAll()                                     // :15
{
    foreach (Type item in GenTypes.AllTypesWithAttribute<StaticConstructorOnStartup>())
        RuntimeHelpers.RunClassConstructor(item.TypeHandle);       // :18-22（逐个 catch，不中断）
    coreStaticAssetsLoaded = true;
}

public static void ReportProbablyMissingAttributes()              // :32
{
    BindingFlags bindingFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    Parallel.ForEach(GenTypes.AllTypes, delegate(Type t)         // :34  ← 并行扫描
    {
        if (!t.HasAttribute<StaticConstructorOnStartup>())
            t.GetFields(bindingFlags) ...                         // :37  ← 抛点
    });
}
```

要点：

1. **只扫静态字段**（`BindingFlags.Static`）。实例字段不参与 —— 这直接决定了下文 A.4 的
   `ReplSession.s_imageRefs` 必须是 static 才相关。
2. 抛点在 `t.GetFields(...)` 及其后续对 `x.FieldType` 的访问 —— 即**解析字段 typeref 的那一刻**。
3. 在 `Parallel.ForEach` 内抛 → 聚合为 `AggregateException`。

### A.1.3 两个调用点，顺序**相反**

全仓库搜索 `ReportProbablyMissingAttributes` 只有两个调用点：

| # | 位置 | 相对 `CallAll()` | 门控 |
|---|------|------------------|------|
| **1** | `Source/Verse/PlayDataLoader.cs:349`（在 `LongEventHandler.ExecuteWhenFinished` 的 "Static constructor calls" 块内，`:346` 是 `CallAll()`） | **之后** | `Prefs.DevMode` |
| **2** | `Source/Verse/Root.cs:108`（`CheckGlobalInit()` 内），`:110` 是 `QueueLongEvent(CallAll)` | **之前** | `Prefs.DevMode` |

**出货程序集反编译确认二者一致**（`ilspycmd -t Verse.Root`）：

```csharp
if (Prefs.DevMode)
    StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes();   // Root.CheckGlobalInit
LongEventHandler.QueueLongEvent(StaticConstructorOnStartupUtility.CallAll, ...);
```

看起来站点 2 会推翻结论，但**它扫不到 mod 的类型**，这正是关键：

* 站点 2 在 `Root.Start()` → `CheckGlobalInit()` 中，此时 `PlayDataLoader.Loaded == false`，
  随后才 `QueueLongEvent(PlayDataLoader.LoadAllPlayData, doAsynchronously: true)`。
* 而 mod 程序集是在 `LoadedModManager` 的 `LoadModContent()` → `ModContentPack.ReloadContent()`
  → `ModAssemblyHandler.ReloadAll()` 里才 `Assembly.LoadFrom`（`ModAssemblyHandler.cs:37`）。
* `GenTypes.AllTypes` 的数据源 `AllActiveAssemblies`（`GenTypes.cs`）只枚举
  `Assembly.GetExecutingAssembly()` + `LoadedModManager.RunningMods[*].assemblies.loadedAssemblies`。
  **站点 2 执行时后者尚为空**，因此它扫不到任何 mod 的类型 → 抛不出 mod 的 TypeLoadException。

**站点 1 才是真凶**，且它在 `CallAll()` 之后 ⇒ **cctor 必然先于真正的崩溃点**。

### A.1.4 真实日志实证（决定性）

`%LOCALAPPDATA%\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`（2026-10-09）：

* `:1401`、`:1420` → `Verse.StaticConstructorOnStartupUtility:CallAll ()`，调用链为
  `PlayDataLoader/<>c:<DoPlayLoad>b__4_4 ()` ← `LongEventHandler.ExecuteToExecuteWhenFinished ()`
  ← `Root_Entry.Update ()`。
  —— 证实站点 1 的 `CallAll()` 确实在 PlayDataLoader 的长事件里跑，且早于下面的扫描。
* `:1614` → 崩溃：

```
Could not execute post-long-event action. Exception: System.AggregateException: One or more errors occurred.
 (Could not load type of field 'SomeMod.SomeFeature:s_imageRefs' (1) due to:
  Could not resolve type with token 010000db from typeref
  (expected class 'Microsoft.CodeAnalysis.MetadataReference' in assembly
   'Microsoft.CodeAnalysis, Version=4.14.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35')
  ...
  assembly:Microsoft.CodeAnalysis, Version=4.14.0.0 ... type:Microsoft.CodeAnalysis.MetadataReference member:(null))
```

> 注：日志为真实 `Player.log` 原文，仅将消费方类型名做了匿名化（`SomeMod.SomeFeature`）；
> 版本号、强命名 token、typeref token、调用栈等决定性证据均未改动。

* 栈帧（`:1595-1614`）：

```
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Log.Warning_Patch1 (string)
Verse.StaticConstructorOnStartupUtility/<>c__DisplayClass2_0:<ReportProbablyMissingAttributes>b__0 (System.Type)
System.Threading.Tasks.Parallel/<>c__DisplayClass33_0`2<System.Type, object>:<ForEachWorker>b__0 (int)
...
Could not execute post-long-event action. Exception: System.AggregateException
[Ref 6EFB1465]
(wrapper managed-to-native) System.RuntimeType.GetFields_native(System.RuntimeType,intptr,...)
```

**这份日志一次性证明了三点**：(a) 崩溃发生在站点 1 的 `ReportProbablyMissingAttributes` 内；
(b) 该站点在 `CallAll()` **之后**（`CallAll` 的日志行号 1401/1420 < 崩溃 1614，且栈帧同属 PlayDataLoader 长事件链）；
(c) 失败字段正是**消费方**的静态 Roslyn 字段。

### A.1.5 cctor 之间无顺序保证（诚实说明）

`CallAll()` 遍历 `GenTypes.AllTypesWithAttribute<StaticConstructorOnStartup>()`，其顺序来自
`GenTypes.AllTypes`（= `AllActiveAssemblies` 枚举序）→ 即 `LoadedModManager.RunningMods` 顺序
→ 即 `ModsConfig.data.activeMods` 列表顺序（玩家在模组列表里的上下顺序）。

**没有任何 `About.xml` 声明能强制某个 mod 排到最前**（见 A.2）。

**但本方案不依赖该顺序**：字段扫描只在 `CallAll()` **整体结束后**才发生，而 `CallAll` 会在
同一进程内把**所有**已加载 mod 的 cctor 都跑完（含前置包的）。因此只要前置包被加载成功，
无论它排第几位，扫描时 Roslyn 类型都已就绪。

### A.1.6 唯一残留的真实风险

若前置包**未启用**（被用户关掉 / 加载失败），则没有任何 cctor 帮忙加载 Roslyn，
站点 1 会在扫描到**依赖方**的类型时抛同样的 `TypeLoadException`。
此时受损的是那些把 Roslyn 类型放在静态字段里的模组（故障回归到引入本包前的现状），
但**前置包自身的失败必须被看见**，所以本实现强制写 `Verse.Log`（见 B.2）。

---

## A.2 `About.xml` 依赖声明的真实语义

### 结论

> `<loadAfter>` / `<loadBefore>` **不是** cctor 执行顺序的硬保证；`<modDependencies>` 更只是 UI 提示。
> 两者都**不**影响 `CallAll()` 里 cctor 的先后。

### 证据

全部来自 `Assembly-CSharp.dll` 反编译与 `Source/Verse/ModsConfig.cs`：

1. **存在拓扑排序，但它不常规运行。**
   `ModsConfig.TrySortMods()` 确实把 `LoadBefore.Concat(ForceLoadBefore)` 与
   `LoadAfter.Concat(ForceLoadAfter)` 作为有向图边，做 `DirectedAcyclicGraph.TopologicalSort()`。
   但全仓库中它只有两个调用点：
   * `ModsConfig.Reset()`（玩家点「重置所有模组」，`ModsConfig.cs:255`）
   * `Page_ModsConfig.cs:278`（「修复排序」按钮）
   **普通启动不执行。**

2. **勾选激活时只认 `forceLoad*`，不认 `loadAfter`。**
   `SetActive(...)` → `EnsureModAdheresToForcedLoadOrder(modIdentifier)`：
   ```csharp
   if (!modWithIdentifier.ForceLoadAfter.NullOrEmpty()) { num = Max(num, indexOf + 1); }
   if (!modWithIdentifier.ForceLoadBefore.NullOrEmpty()) { num = Min(num, indexOf); }
   ```
   —— 读的是 **Force** 版本。`loadAfter` 不在其中。

3. **`forceLoad*` 只在玩家勾选那一刻生效一次**，之后玩家仍可自由拖动；`ReorderConflict()`
   仅用 `forceLoadBefore/After` 阻止手动拖拽。

4. **`<modDependencies>` 是 UI 状态。**
   `Verse.ModDependency : ModRequirement` 只有 `IsSatisfied`（判断对方是否 Active）、
   `StatusIcon`、`Tooltip`、`OnClicked` —— 全是 UI。不参与任何加载或排序。

### 因此本包的做法

本包的 `About.xml` **不声明** `loadAfter` / `modDependencies`（它是被依赖方，声明语义正好相反），
并在文件内**如实写明**这类声明不是保证，只表达意图、且仅在「重置/修复排序」后经拓扑排序生效。
真正让方案成立的是 A.1 的时序性质，**不是**依赖声明。需要本包的模组在**自己的** `About.xml`
里声明 `loadAfter` 指向本包 `packageId`。

---

## A.3 社区先例

网络检索未找到「用普通 mod 机制做 Roslyn/前置依赖包」的成熟公开先例。已确认的邻近实践与本方案的关系：

| 实践 | 机制 | 可借鉴 / 不可借鉴 |
|------|------|-------------------|
| **Harmony（brrainz.harmony）** | 由 `UELoader` 用 doorstop（替换 `winhttp.dll`）在 CLR/Unity **之前**注入；且不引用任何游戏程序集 | ❌ 不可借鉴：普通 mod 做不到 doorstop。这正是本包必须走 mod 机制、且只能保证到「`CallAll` 时刻」的根本原因 |
| **`StaticConstructorOnStartup` 约定** | RimWorld 官方提供的模组级启动钩子 | ✅ 核心借鉴点。本包即用它 |
| **RimSearcher 等前置库** | 以普通 mod 形式存在，靠 `loadBefore` 表达意图 | ⚠️ 部分借鉴：它们的"前置"只作用于 UI 列表与 def 合并顺序，**不保证 cctor 顺序**（与 A.2 一致） |
| **ScriptCore / `Mono.Cecil` 等随包 DLL** | 把第三方 dll 放在自己 mod 目录里 | ✅ 借鉴包结构（本包 `Roslyn/` + `Assemblies/`），但它们同样不解决 typeref 负缓存问题 |

**为什么社区没有先例**：真正需要这种包的场景（本例）非常罕见 —— 它要求「某个 mod 的**静态字段**类型
来自第三方程序集，且该程序集不在 mono BCL 里」。Harmony 之所以能超前置，是因为它同时满足了
doorstop 注入 + 不引用游戏程序集两个条件，普通 mod 只能满足后者。

---

## A.4 类型解析 / `Assembly.LoadFrom` 的可见性

### 结论

> **可以。** `Assembly.LoadFrom` 把程序集加载进**默认 AppDomain**（`LoadFrom` 不创建独立加载上下文），
> 因此之后其它程序集对 Roslyn 类型的 typeref 解析能在全 AppDomain 范围找到它。

### 依据

1. **RimWorld 本身就依赖这个行为。**
   `ModAssemblyHandler.cs:37` 对每个 mod 的 dll 调用 `Assembly.LoadFrom(item.FullName)`，
   再 `AssemblyIsUsable(assembly)` → `asm.GetTypes()`。所有 mod 类型随后都能通过
   `GenTypes.AllTypes` 被枚举、被 Verse 反射实例化（如 `DefInjectionUtility`、`Mod` 类创建）。
   若 `LoadFrom` 的程序集对其它程序集不可见，RimWorld 的整个 mod 体系都不可能工作。

2. **`AppDomain.CurrentDomain.GetAssemblies()` 可枚举到。**
   本实现用它在 `FindLoadedAssembly` / `ResolveTypeOrNull` 里确认程序集确实进入了
   当前 AppDomain（自测 PHASE 2/6 已验证）。

3. **Mono 的 `LoadFrom` 走默认加载上下文。**
   `LoadFrom` 与 `Load` 都会在默认 ALC 中装载；`LoadFile`/`Assembly.Load(byte[])` 才可能产生
   隔离语境。这与 .NET Framework/CoreCLR 的语义一致，而 RimWorld 用的 MonoBleedingEdge 遵循之。
   真实日志里的 `PublicKeyToken=31bf3856ad364e35` 强命名标识与出货 dll 一致，说明走的是
   正常按标识的绑定路径。

4. **游戏已有一个全局 `AssemblyResolve` 兜底。**
   `ModAssemblyHandler.ReloadAll()` 首次执行时注册：
   ```csharp
   AppDomain.CurrentDomain.AssemblyResolve += (obj, args) => Assembly.GetExecutingAssembly();
   ```
   即解析失败时**返回 Assembly-CSharp**（必然不匹配，最终仍失败）。所以它**不能**用来补 Roslyn；
   本包**不**依赖它，而是主动 `LoadFrom` 到默认 ALC（依据 1~3 已足够）。

### ⚠️ 顺序不可调换 + 失败不可重试

这两条约束来自消费侧模组中长期实测并留档的注释（某个「Roslyn 桥接」类的
`RoslynAssemblyLoader.cs:9-11`），本包完整继承：

> ⚠️ 顺序不可调换：System.Memory 先于 System.Numerics.Vectors 加载时，Mono 会缓存
> typeref 解析失败，之后补齐也无法恢复（需重启进程）。
> ⚠️ 只尝试一次：失败后重试会命中同一个失败缓存，故用 s_attempted 锁死。

本包完整继承该约束：`LoadOrder` 固定 + `s_attempted` 一次性门闩 + 失败缓存 + 非静默日志。
（自测 PHASE 5 验证了幂等：连调三次均返回 true 且 `LoadedCount` 恒为 8。）

---

## B. 证据与产物索引

| 结论 | 证据位置 |
|------|----------|
| 扫描函数本体、BindingFlags、Parallel.ForEach | `Source/Verse/StaticConstructorOnStartupUtility.cs:15,32-52`；出货 dll 反编译一致 |
| 站点 1（扫描在 CallAll 之后） | `Source/Verse/PlayDataLoader.cs:346-349` |
| 站点 2（扫描在 CallAll 之前，但扫不到 mod） | `Source/Verse/Root.cs:106-110`；`GenTypes.cs` 的 `AllActiveAssemblies` |
| 真实崩溃栈帧 | `Player.log:1595-1614` |
| `CallAll` 先于崩溃 | `Player.log:1401,1420`（CallAll）< `:1614`（崩溃） |
| `TrySortMods` 只在 Reset/UI 调用 | `ModsConfig.cs:255`；`Page_ModsConfig.cs:278` |
| 激活时只认 forceLoad* | `ModsConfig.EnsureModAdheresToForcedLoadOrder` |
| modDependencies 是 UI | `Verse.ModDependency : ModRequirement`（仅 IsSatisfied/StatusIcon/Tooltip/OnClicked） |
| mod dll 由 `Assembly.LoadFrom` 装载 | `ModAssemblyHandler.cs:37` |
| Mono 顺序/失败不可重试 | 消费侧既有实测注释（`RoslynAssemblyLoader.cs:9-11`）；本包 PHASE 5 复验幂等 |

### 自测运行结果

```
================================================================
 Roslyn 离线自测
================================================================
package root : <包根>
payload dir  : <包根>\Roslyn
runtime      : .NET 10.0.12

PHASE 0  typeref audit of the preload package itself
  AssemblyRefs (3): mscorlib, Assembly-CSharp, System
  PASS  preload has no Microsoft.CodeAnalysis AssemblyRef
  PASS  preload has no Microsoft.CodeAnalysis TypeRef

PHASE 1  field scan BEFORE preload (expect TypeLoadException)
  [pre-preload] GetFields returned 2 field(s); resolving each field type:
      s_imageRefs : <FieldType resolution THREW FileNotFoundException>
          Could not load file or assembly 'Microsoft.CodeAnalysis, Version=4.14.0.0,
          Culture=neutral, PublicKeyToken=31bf3856ad364e35'.

PHASE 2  run the preload package (fixed order, idempotent)
  [preload] Roslyn 载荷加载完成（8/8）。
  PASS  RoslynPayloadLoader.TryEnsureLoaded() == true
  PASS  loaded count == 8

PHASE 3  field scan AFTER preload (must NOT throw)
  [post-preload] GetFields returned 2 field(s); resolving each field type:
      s_imageRefs : Microsoft.CodeAnalysis.MetadataReference  (asm=Microsoft.CodeAnalysis)
      v : Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax  (asm=Microsoft.CodeAnalysis.CSharp)
  PASS  post-preload scan of VictimMod.ReplSession did NOT throw

PHASE 4  full-assembly field scan (Parallel.ForEach equivalent)
  assemblies scanned : 19
  types scanned      : 9060
  fields reflected   : 16704
  PASS  no type threw during GetFields across all loaded assemblies

PHASE 4b  force runtime resolution of the victim Roslyn fields
  field s_imageRefs -> Microsoft.CodeAnalysis.MetadataReference (asm=Microsoft.CodeAnalysis)
      resolved OK, IsClass=True, BaseType=System.Object
  field v -> Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax (asm=Microsoft.CodeAnalysis.CSharp)
      resolved OK, IsClass=True, BaseType=Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode

PHASE 5  idempotency + failure caching
  PASS  second TryEnsureLoaded() == true
  PASS  third TryEnsureLoaded() == true
  PASS  LoadedCount still == 8

PHASE 6  type resolution after preload
  PASS  resolvable: Microsoft.CodeAnalysis.MetadataReference
  PASS  resolvable: Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax

================================================================
 RESULT: 12 passed, 0 failed
================================================================
```

PHASE 1 报的 `FileNotFoundException ... Microsoft.CodeAnalysis, Version=4.14.0.0,
PublicKeyToken=31bf3856ad364e35` 与真实 Mono 日志里的版本/强命名标识**完全一致**，
说明复现的失败与生产故障同源。

### 自测的诚实边界（务必阅读）

自测跑在 **.NET 10 / CoreCLR**，而生产是 **RimWorld 1.6 / MonoBleedingEdge**。差异：

1. **CoreCLR 的 typeref 解析是惰性的**。`GetFields()` 在 CoreCLR 上可能先成功返回，
   直到触碰 `FieldType` 才绑定 typeref；Mono 是在字段扫描过程中立刻抛
   `TypeLoadException`。自测因此在 PHASE 1 显式触碰 `FieldType`（`ScanVictimAndReport`）
   并在 PHASE 4b 再强制一次，才让失败显形。**生产的权威复现是 `Player.log:1614`。**
2. **BCL 版本的绑定不同**。自测里 `System.Memory` 等 6 个 BCL dll 的 `GetName().Version`
   显示为 `10.0.0.0`（宿主 .NET 10 自带同名程序集优先命中），而不是载荷里的 4.0.1.2 等。
   这**不影响** `Microsoft.CodeAnalysis(.CSharp) 4.14.0.0` —— 它们没有宿主同名版本，
   实打实从本包 `Roslyn/` 加载。
   ⇒ 在真实 RimWorld（Mono）下 8 个 dll 的绑定行为可能与自测不同，**部署后仍须以真实日志复核**
   （看 `[Roslyn] Roslyn 载荷加载完成（8/8）` 与 `GetDiagnostics()` 的版本列表）。
3. 自测的 `VictimMod` 是静态形状的等价物，不是任何真实消费方程序集。

### 构建护栏

`tests/assert-no-roslyn-typref.ps1` 在每次 `build` 后运行（`Source/Roslyn.csproj`
的 `AssertNoRoslynTypeRef` target），扫描产物的 **ASCII 投影**：ECMA-335 把类型/程序集名以
UTF-8 存在 `#Strings` 堆（每字符 1 字节），而字符串**字面量**在 `#US` 堆是 UTF-16
（含 NUL 交错），因此只有真正的 AssemblyRef/TypeRef 会连续命中 ASCII。
自测 PHASE 0 另用 `System.Reflection.Metadata` 做了权威表枚举（双重保险）。