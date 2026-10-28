using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

class ApiDump
{
    static void Main(string[] args)
    {
        string path = args[0];
        string filter = args.Length > 1 ? args[1] : null;
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var sb = new StringBuilder();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            string name = md.GetString(td.Name);
            string ns = md.GetString(td.Namespace);
            if (filter != null && !(name.Contains(filter) || (ns + "." + name).Contains(filter)))
                continue;
            sb.AppendLine($"== {ns}.{name}");
            foreach (var mh in td.GetMethods())
            {
                var mdef = md.GetMethodDefinition(mh);
                string mname = md.GetString(mdef.Name);
                if (mname == ".ctor" || mname.StartsWith("get_") || mname.StartsWith("set_"))
                    continue;
                int pcount = mdef.GetParameters().Count();
                sb.AppendLine($"  {mname}({pcount} params)");
            }
            foreach (var ph in td.GetProperties())
            {
                var pd = md.GetPropertyDefinition(ph);
                string pname = md.GetString(pd.Name);
                if (pname is "IID" or "ThisPtr" or "NativeObject")
                    continue;
                if (pname.StartsWith("_objRef_global"))
                    continue;
                sb.AppendLine($"  prop {pname}");
            }
        }
        Console.WriteLine(sb);
    }
}
