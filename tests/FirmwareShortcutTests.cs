using System;
using ClawRouter;
class FirmwareShortcutTests {
    static KeyboardStroke K(uint key,uint time=10,bool up=false,uint flags=0){return new KeyboardStroke{Key=key,Time=time,Flags=flags|(up?128u:0u)};}
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static int Main() {
        var s=new FirmwareShortcutState();s.Key(K(91),false);
        Check(!s.Key(K(71),false).Swallow,"Game Bar shortcut works outside takeover");
        s=new FirmwareShortcutState();
        Check(!s.Key(K(71),true).Swallow && !s.Key(K(71,10,true),true).Swallow,"ordinary G typing passes");
        s.Key(K(91),true);
        Check(s.Key(K(71),true).Swallow,"Win+G is disabled during takeover without raw device messages");
        Check(s.Key(K(71,10,true),true).Swallow,"suppressed G release is balanced");
        Check(!s.Key(K(91,10,true),true).Swallow,"Windows release passes");
        s=new FirmwareShortcutState();s.Key(K(91,10),true);
        Check(s.Key(K(71,500),true).Swallow,"different timestamps cannot bypass Game Bar suppression");
        Check(!s.Key(K(69,501),true).Swallow,"other Windows shortcuts pass");
        s=new FirmwareShortcutState();s.Key(K(92),true);
        Check(s.Key(K(71),true).Swallow,"right Windows modifier also disables Game Bar");
        s=new FirmwareShortcutState();s.Key(K(91),true);s.Key(K(92),true);s.Key(K(91,10,true),true);
        Check(s.Key(K(71),true).Swallow,"releasing one Windows modifier preserves the other");
        s=new FirmwareShortcutState();s.Key(K(91,10,false,16),true);
        Check(s.Key(K(71,10,false,16),true).Swallow,"synthetic Win+G cannot open Game Bar during takeover");
        s=new FirmwareShortcutState();s.Key(K(91),true);s.Key(K(71),true);
        Check(s.Key(K(71,15),true).Swallow && !s.Key(K(71,16),true).GameBar,"repeat is suppressed without double counting");
        s.Disabled();
        Check(s.Key(K(71,17,true),false).Swallow,"handover preserves a previously suppressed release");
        s.Key(K(91),false);
        Check(!s.Key(K(71),false).Swallow,"disabled filter restores Win+G");
        s=new FirmwareShortcutState();s.Key(K(91),true);s.Key(K(71),true);s.Key(K(91,11,true),true);
        Check(!s.Key(K(71,100),true).Swallow,"lost G release cannot consume later typing");
        s=new FirmwareShortcutState();s.Key(K(91),true);s.Disabled();
        Check(!s.Key(K(71),true).Swallow,"context reset cannot leave a stale Windows modifier");
        s=new FirmwareShortcutState();s.Key(K(91),true);
        var mapped=K(71);mapped.Extra=0x434C4157;
        Check(!s.Key(mapped,true).Swallow,"an explicitly mapped Win+G bypasses the firmware filter");
        return 0;
    }
}
