using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Bounded key/mouse taps for the vendor buttons (MSI_Event has press notifications,
// not a reliable held/released state). No process paths or shell strings accepted.
static class MappedInput {
    const ulong Tag = 0x434C4157;
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int X,Y; public uint Data,Flags,Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Key { public ushort Vk,Scan; public uint Flags,Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit,Size=40)] struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public Mouse Mouse; [FieldOffset(8)] public Key Key; }
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint key,uint type);
    static Input Stroke(ushort vk,bool ext,bool up) {
        uint scan = vk >= 0xA6 && vk <= 0xB7 ? 0 : MapVirtualKeyW(vk,4)&255;
        return new Input {Type=1,Key=new Key {Vk=vk,Scan=(ushort)scan,Flags=(up?2u:0)|(ext?1u:0)|(scan!=0?8u:0),Extra=(UIntPtr)Tag}};
    }
    static void Send(List<Input> inputs) {
        if(inputs.Count!=0 && SendInput((uint)inputs.Count,inputs.ToArray(),40)!=(uint)inputs.Count)
            throw new InvalidOperationException("映射指令未能发送到当前窗口");
    }
    public static Dictionary<string,object> Run(Dictionary<string,object> command) {
        string kind=command.ContainsKey("kind")?command["kind"] as string:null;
        var down=new List<Input>(); var up=new List<Input>();
        if(kind=="none")return new Dictionary<string,object>{{"sent",true}};
        if(kind=="keys") {
            var keys=command.ContainsKey("keys")?command["keys"] as IList:null;
            var extended=command.ContainsKey("extended")?command["extended"] as IList:null;
            if(keys==null || keys.Count<1 || keys.Count>4)throw new ArgumentException("组合键需要一到四个键");
            var seen=new HashSet<int>();
            for(int i=0;i<keys.Count;i++) {
                int vk=Convert.ToInt32(keys[i]);
                if(vk<1 || vk>254 || !seen.Add(vk))throw new ArgumentException("无效或重复的键码");
                bool ext=extended!=null && i<extended.Count && true.Equals(extended[i]);
                if((GetAsyncKeyState(vk)&0x8000)!=0)continue;
                down.Add(Stroke((ushort)vk,ext,false)); up.Insert(0,Stroke((ushort)vk,ext,true));
            }
        } else if(kind=="mouse" || kind=="wheel") {
            int value=Convert.ToInt32(command["value"]);
            if(kind=="wheel") {
                if(value<1 || value>4)throw new ArgumentException("滚轮方向无效");
                down.Add(new Input {Type=0,Mouse=new Mouse {Flags=value<=2?0x800u:0x1000u,Data=unchecked((uint)(value==1 || value==4?120:-120)),Extra=(UIntPtr)Tag}});
            } else {
                if(value<1 || value>5)throw new ArgumentException("鼠标键无效");
                uint[] d={0,2,8,32,128,128},u={0,4,16,64,256,256};
                uint data=value>=4?(uint)(value-3):0;
                down.Add(new Input {Mouse=new Mouse {Flags=d[value],Data=data,Extra=(UIntPtr)Tag}});
                up.Add(new Input {Mouse=new Mouse {Flags=u[value],Data=data,Extra=(UIntPtr)Tag}});
            }
        } else throw new ArgumentException("此专用键不支持这种输出");
        try { Send(down); Thread.Sleep(40); } finally { Send(up); }
        return new Dictionary<string,object>{{"sent",true}};
    }
}
