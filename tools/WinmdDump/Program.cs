using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// 列出 winmd 的公共类型/方法/属性面（按名称过滤），用于确认 Starward.Win2D 投影的实际 API。
// 用法：dotnet run <winmd 路径> [过滤词]
if (args.Length < 1)
{
    Console.WriteLine("usage: WinmdDump <winmd> [filter]");
    return 1;
}

string winmdPath = args[0];
string filter = args.Length > 1 ? args[1] : null;

using var fs = File.OpenRead(winmdPath);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

foreach (var tdh in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(tdh);
    string ns = md.GetString(td.Namespace);
    string name = md.GetString(td.Name);
    if (name.Contains('<'))
    {
        continue;
    }
    if (
        filter is not null
        && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        && !ns.Contains(filter, StringComparison.OrdinalIgnoreCase)
    )
    {
        continue;
    }
    Console.WriteLine($"== {ns}.{name}");
    foreach (var mh in td.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        string mn = md.GetString(m.Name);
        if (mn.StartsWith("<", StringComparison.Ordinal) || mn is "Finalize" or "GetHashCode")
        {
            continue;
        }
        var ps = new List<string>();
        foreach (var ph in m.GetParameters())
        {
            var p = md.GetParameter(ph);
            ps.Add(md.GetString(p.Name));
        }
        Console.WriteLine($"  {mn}({string.Join(", ", ps)})");
    }
    foreach (var ph in td.GetProperties())
    {
        var p = md.GetPropertyDefinition(ph);
        string pn = md.GetString(p.Name);
        if (pn.StartsWith("<", StringComparison.Ordinal))
        {
            continue;
        }
        Console.WriteLine($"  prop {pn}");
    }
}
return 0;
