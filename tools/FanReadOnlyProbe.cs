using System;
using System.Text;
using System.Web.Script.Serialization;
class FanReadOnlyProbe {
    public static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        try { Console.WriteLine(new JavaScriptSerializer().Serialize(args.Length == 1 && args[0] == "--probe" ? MsiFan.Probe() : MsiFan.Read())); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        return 0;
    }
}
