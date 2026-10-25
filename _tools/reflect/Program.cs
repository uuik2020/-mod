using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static string GameDir = @"E:\SteamLibrary\steamapps\common\Stardew Valley";

    static void Main(string[] args)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"))
            byName[Path.GetFileName(f)] = f;
        foreach (var f in Directory.GetFiles(GameDir, "*.dll"))
            byName[Path.GetFileName(f)] = f;
        var paths = byName.Values.ToList();
        var resolver = new PathAssemblyResolver(paths);
        using var mlc = new MetadataLoadContext(resolver);
        var asm = mlc.LoadFromAssemblyPath(Path.Combine(GameDir, "Stardew Valley.dll"));

        string filter = args.Length > 0 ? args[0] : "SaveGame";

        foreach (var type in asm.GetTypes().Where(t => t.Name == filter || t.FullName == filter))
        {
            Console.WriteLine("=== " + type.FullName);
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (args.Length > 1 && !f.Name.ToLower().Contains(args[1].ToLower())) continue;
                Console.WriteLine($"  FIELD {(f.IsPublic ? "pub" : "pri")}{(f.IsStatic ? " static" : "")} {f.FieldType.Name} {f.Name}");
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (args.Length > 1 && !p.Name.ToLower().Contains(args[1].ToLower())) continue;
                Console.WriteLine($"  PROP  {(p.GetMethod?.IsPublic == true ? "pub" : "pri")}{(p.GetMethod?.IsStatic == true ? " static" : "")} {p.PropertyType.Name} {p.Name}");
            }
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (args.Length > 1 && !m.Name.ToLower().Contains(args[1].ToLower())) continue;
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Console.WriteLine($"  METHOD {(m.IsPublic ? "pub" : "pri")}{(m.IsStatic ? " static" : "")} {m.ReturnType.Name} {m.Name}({ps})");
            }
        }
    }
}
