# Roslyn — Roslyn Preload Package for RimWorld

[中文](README.md) | **English**

A **standalone preload package**: during RimWorld startup it loads the Roslyn
(`Microsoft.CodeAnalysis`) payload into the default AppDomain in a fixed order,
so that any mod depending on it no longer gets wiped out by the startup
field-scan.

Typical consumers: any RimWorld mod that needs **compile-time references to
Roslyn types** — C# consoles / REPLs, runtime expression evaluation, dynamic
mod scripting, etc.

---

## 1. What problem does it solve

During startup, RimWorld's
`Verse.StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes()`
runs `GetFields(BindingFlags.Static | Public | NonPublic)` on **every type of
every loaded mod assembly**.

If any type has a **static field** whose type comes from a not-yet-loaded
Roslyn assembly, Mono throws:

```
TypeLoadException: Could not load type of field
  'SomeMod.SomeFeature:s_imageRefs' (1) due to:
  Could not resolve type with token 010000db from typeref
  (expected class 'Microsoft.CodeAnalysis.MetadataReference' in assembly
   'Microsoft.CodeAnalysis, Version=4.14.0.0, ...')
```

Inside `Parallel.ForEach` these exceptions aggregate into an
`AggregateException`, and **the whole mod becomes non-functional**.

This package loads the 8 Roslyn dlls into the default AppDomain during the
`CallAll()` phase. The field scan happens **after** `CallAll()`, so by scan
time all types resolve and the problem disappears.

> For full source-level evidence, call-site ordering, and real log stack
> frames see **[RESEARCH.md](RESEARCH.md)**.

---

## 2. Mechanism

| Stage | Approach |
|------|------|
| Trigger | A class annotated `[StaticConstructorOnStartup]`, hit by `CallAll()` via `RuntimeHelpers.RunClassConstructor` |
| Load | `Assembly.LoadFrom` of the 8 dlls into the default AppDomain in a **fixed dependency order** |
| Idempotent | One-shot `s_attempted` latch + dictionary dedup; repeat calls return the cached result |
| Failure | Failure reason cached + written to `Verse.Log` (with `[Roslyn]` prefix). **Never silent** |
| Self-check | After loading, positively verifies that types like `Microsoft.CodeAnalysis.MetadataReference` resolve |
| Zero dependency | The package assembly contains **no** AssemblyRef/TypeRef to `Microsoft.CodeAnalysis`; everything goes through strings + reflection |

### Why the order must not be changed

Mono **negatively caches** failed typeref resolutions process-wide. If
`System.Memory` is loaded before `System.Numerics.Vectors`, the former's failed
typeref resolution to the latter gets cached, and back-filling later **cannot
recover** (the process must be restarted). Hence `LoadOrder` is fixed and
failed loads are never retried.

### Payload layout

```
Roslyn/
├─ About/About.xml
├─ Assemblies/Roslyn.dll   ← the only mod assembly
├─ Roslyn/                 ← 8 payload dlls (+ VERSIONS.md)
├─ Source/                 ← source code
└─ tests/                  ← offline self-tests
```

Resolution order (`ResolvePayloadDirectory`): `SetPayloadDirectoryOverride` →
environment variable `ROSLYN_PAYLOAD_DIR` (optional override) →
`<mod root>\Roslyn` → `<assembly dir>\Roslyn`.

---

## 3. Deployment

1. Copy the whole `Roslyn\` directory into `Mods\`.
2. Enable it and **place it before** the mods that depend on it (top of the
   list is the conventional habit).
3. Verify all 8 dlls under `Roslyn\` are present.

> ⚠️ **About "placing it first"**: this is not a hard guarantee (see RESEARCH.md
> A.2). This package's correctness comes from the timing property that *the
> field scan runs after `CallAll()`* — not from load order. Placing it first is
> just a safe habit. Clicking "Reset all mods" or "Fix mod order" in the mod
> list will apply `loadAfter` via topological sort.

**Verification**: after launch check `Player.log`
(`%LOCALAPPDATA%\Ludeon Studios\RimWorld by Ludeon Studios\`). You should see
`[Roslyn] Roslyn 载荷加载完成（8/8）` ("payload loaded, 8/8"), and the
`Could not load type of field '...'` errors should be gone.

---

## 4. How other mods should depend on it

### Consumers must NOT reference this assembly at compile time

A compile-time reference to `Roslyn.dll` creates **the same fragility**: if the
preload package is missing or fails to load, the consumer becomes the next
victim of "static field depends on the preload package" — precisely the disease
this package exists to cure.

The correct approach: **query via reflection only**.

```csharp
using System;
using System.Reflection;

// Full type name as a string, to avoid any typeref.
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
            // Preload package not loaded — or CallAll simply hasn't run yet.
            // Fall back to actively triggering it once.
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

        if (t == null) { failure ??= "Roslyn preload package not loaded"; return false; }

        try
        {
            MethodInfo m = t.GetMethod("TryEnsureLoaded",
                BindingFlags.Public | BindingFlags.Static);
            if (m == null) { failure = "TryEnsureLoaded not found"; return false; }

            object[] args = { null };
            _ok = (bool)m.Invoke(null, args);
            failure = args[0] as string;
        }
        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }

        return _ok;
    }
}
```

### Declare the intent in your `About.xml`

```xml
<loadAfter>
  <li>RoslynPreload.Roslyn</li>
</loadAfter>
```

As described above, this does **not** guarantee cctor ordering; it only takes
effect through topological sorting after a "reset/fix mod order". The real
guarantee is the reflection query above plus this package's own cctor.

---

## 5. Public API

```csharp
namespace Roslyn
{
    [StaticConstructorOnStartup]
    public static class RoslynLoader
    {
        public static bool   TryEnsureLoaded(out string failure);  // idempotent
        public static bool   IsLoaded;      // 8/8 ready (does not trigger loading)
        public static bool   Attempted;     // distinguishes "never ran" from "ran and failed"
        public static string FailureReason; // null when no failure
        public static int    LoadedCount;   // 8 on success
        public static string GetDiagnostics();
        public const string EntryTypeName = "Roslyn.Loader";
    }
}
```

Sample `GetDiagnostics()` output:

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

## 6. Building and self-testing

```powershell
# Build the mod assembly (net472, outputs to Assemblies/, auto-runs the typeref guard)
dotnet build Source\Roslyn.csproj -c Release

# Build and run the offline self-tests (never installed into Mods/, no game launch needed)
dotnet build tests\Roslyn.Tests.csproj -c Release
dotnet tests\bin\Release\net10.0\Roslyn.Tests.dll
```

Override the game path with `-p:RimWorldGameDir=<game root>` or the
`RIMWORLD_GAME_DIR` environment variable.

### Invariant guard

Every `Source` build automatically runs `tests/assert-no-roslyn-typref.ps1`:
if the output assembly contains any `Microsoft.CodeAnalysis`
**AssemblyRef/TypeRef metadata** (as contiguous ASCII bytes), **the build
fails**. This is the package's core invariant — the preload package itself must
never become the next victim.

---

## 7. License and attribution

The source code of this package is released under the **MIT License**, see
[LICENSE](LICENSE).

The 8 dlls under `Roslyn/` are taken from the official NuGet packages; exact
versions and SHA256 hashes are listed in that directory's `VERSIONS.md`.
They are © Microsoft, also distributed under the MIT License — our thanks to
the [dotnet/roslyn](https://github.com/dotnet/roslyn) team for their excellent
work. Full notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

> ⚠️ **Please report bugs for THIS MOD in this repository's Issues — NOT to
> dotnet/roslyn.** Upstream is Microsoft's compiler project; it does not know
> RimWorld and is not responsible for this mod.

---

## 8. Reporting issues

For load failures, ordering anomalies, or compatibility problems with other
mods, please open an issue in this repository, ideally attaching:

1. The relevant `[Roslyn]`-prefixed lines from `Player.log`;
2. The full exception stack (`TypeLoadException` / `AggregateException` etc.);
3. Your RimWorld version and mod list (screenshot or export).
