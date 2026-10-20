using System;
using System.Text;
using System.Web.Script.Serialization;
class FanReadOnlyProbe {
    public static int Main() {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine(new JavaScriptSerializer().Serialize(MsiFan.Read()));
        return 0;
    }
}
