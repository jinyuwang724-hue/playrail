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
        s=new FirmwareShortcutState();
        var mappedWin=K(91);mappedWin.Extra=mapped.Extra;
        s.Key(mappedWin,true,true,true);
        var blocked=s.Key(mapped,true,true,true);
        Check(blocked.Swallow&&!blocked.GameBar,"mapped Win+G is blocked without opening QAM in game mode");
        mapped.Flags=128;
        Check(s.Key(mapped,true,true,true).Swallow,"mapped Win+G release is balanced");
        s=new FirmwareShortcutState();
        s.Key(mappedWin,false,false,false);
        mapped.Flags=0;
        Check(!s.Key(mapped,false,false,false).Swallow,"mapped Win+G still works on desktop");
        var pair=new GameBarShortcutCorrelation();
        int epoch=pair.Generation;
        Check(!pair.ShouldReplay(1000,1160,epoch),"keyboard fallback waits for the delayed firmware event");
        pair.QuickSettings(1300);
        Check(!pair.ShouldReplay(1000,1700,epoch),"QS arriving 300 ms after Win+G cannot toggle Game Bar twice");
        pair=new GameBarShortcutCorrelation();epoch=pair.Generation;pair.QuickSettings(1600);
        Check(!pair.ShouldReplay(1000,1700,epoch),"slow 600 ms firmware notification cancels keyboard fallback");
        pair=new GameBarShortcutCorrelation();epoch=pair.Generation;pair.QuickSettings(900);
        Check(!pair.ShouldReplay(1000,1700,epoch),"firmware notification preceding the keyboard report is correlated");
        pair=new GameBarShortcutCorrelation();epoch=pair.Generation;
        Check(pair.ShouldReplay(1000,1700,epoch),"real keyboard Win+G still opens Game Bar without a QS notification");
        pair.QuickSettings(1000);
        Check(pair.ShouldReplay(1800,2500,epoch),"an earlier physical press does not swallow a later keyboard shortcut");
        pair.QuickSettings(2000);pair.QuickSettings(2400);
        Check(!pair.ShouldReplay(1700,2400,epoch) && !pair.ShouldReplay(2100,2800,epoch),"two physical presses cancel both firmware shortcut copies");
        pair.Reset();
        Check(!pair.ShouldReplay(3000,3700,epoch),"context changes cancel a delayed keyboard fallback");
        Check(pair.ShouldReplay(3000,3700,pair.Generation),"a new context can handle a fresh keyboard shortcut");
        return 0;
    }
}
