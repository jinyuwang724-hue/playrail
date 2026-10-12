using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

// An ordinary parent-owned stdio bridge. Only the fixed adjacent Magpie may elevate.
class MagpieLauncher {
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint process);
    public static int Main(string[] args) {
        Console.InputEncoding=new UTF8Encoding(false);
        Console.OutputEncoding=new UTF8Encoding(false);
        Process child=null;
        try {
            if(args.Length!=1 || (args[0]!="normal" && args[0]!="elevated")) throw new ArgumentException("启动参数错误");
            string name="claw-magpie-"+Guid.NewGuid().ToString("N");
            var acl=new PipeSecurity();
            acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
            using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,acl)) {
                var pending=pipe.BeginWaitForConnection(null,null);
                string exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Magpie.exe");
                child=Process.Start(new ProcessStartInfo(exe,"--claw-pipe "+name+" "+Process.GetCurrentProcess().Id) {
                    UseShellExecute=true,Verb=args[0]=="elevated"?"runas":"open",WindowStyle=ProcessWindowStyle.Hidden,
                    WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory
                });
                if(child==null) throw new IOException("Magpie 启动失败");
                if(!pending.AsyncWaitHandle.WaitOne(30000)) throw new TimeoutException("Magpie 连接超时");
                pipe.EndWaitForConnection(pending);
                uint client;
                if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out client) || client!=(uint)child.Id)
                    throw new IOException("Magpie 连接身份不匹配");
                using(var reader=new StreamReader(pipe,new UTF8Encoding(false),false,4096,true))
                using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true) { AutoFlush=true }) {
                    var input=Task.Run(async()=> { string line; while((line=await Console.In.ReadLineAsync())!=null) await writer.WriteLineAsync(line); });
                    var output=Task.Run(async()=> { string line; while((line=await reader.ReadLineAsync())!=null) { Console.WriteLine(line); Console.Out.Flush(); } });
                    Task.WaitAny(input,output);
                }
            }
            return 0;
        } catch(Exception error) {
            string message=error.Message.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ");
            Console.WriteLine("{\"id\":1,\"ok\":false,\"errorCode\":7,\"error\":\""+message+"\"}");
            Console.Out.Flush(); return 1;
        } finally {
            if(child!=null) { try { if(!child.WaitForExit(5000)) child.Kill(); } catch { } child.Dispose(); }
        }
    }
}
