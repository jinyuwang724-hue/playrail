using System;
using ClawRouter;
class ShortcutTests {
    static KeyboardStroke K(uint key,bool up=false,ulong extra=0){return new KeyboardStroke{Key=key,Flags=up?128u:0u,Extra=extra};}
    static int count;
    static void Check(bool value){if(!value)throw new Exception("check "+count);count++;}
    static void Main(){
        foreach(bool game in new[]{false,true}){
            var s=new FirmwareShortcutState();s.Key(K(91),game,game);
            var g=s.Key(K(71),game,game);Check(g.GameBar==game&&g.Swallow==game&&g.ObservedGameBar);
            g=s.Key(K(71),game,game);Check(!g.GameBar&&!g.ObservedGameBar);
            Check(s.Key(K(71,true),game,game).Swallow==game);s.Key(K(91,true),game,game);
            Check(!s.Key(K(71),game,game).Swallow);
        }
        foreach(bool capture in new[]{false,true}){
            var s=new FirmwareShortcutState();s.Key(K(91),false,capture);
            var t=s.Key(K(9),false,capture);Check(t.Switcher==capture&&t.Swallow==capture);
            Check(!s.Key(K(9),false,capture).Switcher);
            Check(s.Key(K(9,true),false,false).Swallow==capture);
            s.Key(K(91,true),false,capture);Check(!s.Key(K(9),false,capture).Switcher);
        }
        var injected=new FirmwareShortcutState();injected.Key(K(91,false,0x434C4157),true,true);
        Check(!injected.Key(K(9,false,0x434C4157),true,true).Swallow);
        var reports=new SpecialShortcutCorrelation();reports.Keyboard(88,100);
        Check(reports.Consume(88,400));Check(!reports.Consume(88,600));
        reports.Keyboard(88,1000);reports.Keyboard(42,1100);reports.Keyboard(88,1200);
        Check(reports.Consume(88,1300));Check(reports.Consume(42,1400));Check(reports.Consume(88,1500));
        reports.Keyboard(88,2000);Check(!reports.Consume(88,3000));
        Console.WriteLine("PASS "+count+" special-key state checks");
    }
}
