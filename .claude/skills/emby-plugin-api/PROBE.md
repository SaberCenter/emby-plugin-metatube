# 探测 Emby API

`strings` 能确认一个成员名**存不存在**，但给不出签名、基类、枚举值，而且成员名散落在整个字符串堆里，误报很多。要准确签名就用 `MetadataLoadContext` 反射引用程序集。

## 程序集在哪

分在**两个** NuGet 包里，只加载一个会导致类型查不到或抛 `FileNotFoundException`：

| 包 | 程序集 |
|---|---|
| `mediabrowser.server.core` | `MediaBrowser.Controller.dll`、`Emby.Naming.dll` |
| `mediabrowser.common` | `MediaBrowser.Model.dll`、`MediaBrowser.Common.dll`、`Emby.Media.Model.dll`、`Emby.Web.GenericEdit.dll` |

路径：`~/.nuget/packages/<包名>/4.9.1.80/lib/netstandard2.0/`

`ILibraryManager`、`BaseItem`、`Movie` 在 Controller；`IScheduledTask`、`TaskTriggerInfo`、`ExtraType`、`MediaType` 在 Model。

## 探测程序

在 scratchpad 里建一个临时控制台项目：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Reflection.MetadataLoadContext" Version="9.0.0" />
  </ItemGroup>
</Project>
```

```csharp
using System.Reflection;

var dirs = new[] { "mediabrowser.server.core", "mediabrowser.common" }
    .Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nuget/packages", n, "4.9.1.80/lib/netstandard2.0"))
    .Where(Directory.Exists).ToList();

var embyDlls = dirs.SelectMany(d => Directory.GetFiles(d, "*.dll")).ToList();
var paths = embyDlls
    .Concat(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"))
    .ToList();

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
var asms = embyDlls.Select(p => { try { return mlc.LoadFromAssemblyPath(p); } catch { return null; } })
    .Where(a => a != null).ToList();

Type Find(string name) => asms
    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
    .FirstOrDefault(t => t.FullName == name || t.Name == name);

void Dump(string name, string filter = null)
{
    var t = Find(name);
    Console.WriteLine($"=== {name} => {t?.FullName ?? "NOT FOUND"}  base={t?.BaseType?.FullName}");
    if (t == null) return;
    if (t.IsEnum)
    {
        Console.WriteLine("  Flags? " + t.CustomAttributes.Any(a => a.AttributeType.Name == "FlagsAttribute"));
        foreach (var f in t.GetFields().Where(f => f.IsStatic))
            Console.WriteLine($"  {f.Name} = {f.GetRawConstantValue()}");
        return;
    }
    foreach (var e in t.GetEvents())
        Console.WriteLine($"  event {e.EventHandlerType?.Name} {e.Name}");
    foreach (var p in t.GetProperties().Where(p => filter == null || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
    foreach (var m in t.GetMethods().Where(m => !m.IsSpecialName && (filter == null || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))))
        Console.WriteLine($"  method {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name))})");
    Console.WriteLine("  interfaces: " + string.Join(", ", t.GetInterfaces().Select(i => i.FullName)));
}

// 想查什么就在这里加，filter 传成员名子串可以只看相关的那几个
Dump("ILibraryManager");
Dump("ItemUpdateType");
Dump("MediaBrowser.Controller.Entities.BaseItem", "Trailer");
```

`dotnet run` 即可。

## 两个坑

**`GetProperties()` 会连带解析属性类型**，如果那个类型所在程序集没加载就抛 `FileNotFoundException`——两个包都加载能避开绝大多数。

**`MetadataLoadContext` 是 reflection-only**，`Enum.GetNames(t)` / `Enum.GetValues(t)` 会抛异常。取枚举值只能用 `t.GetFields().Where(f => f.IsStatic)` 配 `f.GetRawConstantValue()`（上面 `Dump` 里已经这么写了）。

## 反射答不了的

参数化的**运行时行为**——事件在什么时机派发、哪些操作会触发、DI 会不会自动发现某个接口的实现——签名里看不出来，只能装到真实 Emby 上看日志。`SKILL.md` 里标了"运行时观察/验证过"的条目都属于这一类。
