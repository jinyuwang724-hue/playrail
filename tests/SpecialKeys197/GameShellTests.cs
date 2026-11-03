using System;
using ClawShell;
class SurfaceSystem : FakeSystem, IGameSurface {
    public int Activations,BackgroundChecks;
    public bool Visible,Minimized=true,Recover=true;
    public bool EnsureLibrarySurface(bool activate) {
        if(activate)Activations++;else BackgroundChecks++;
        if(!Recover)return false;
        Visible=true;Minimized=false;return true;
    }
}
class GameShellTests {
    static int count;
    static void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    static void Main(){
        var s=new SurfaceSystem{Explorer=true,Library=true,Steam=true};var g=new ShellGuard(s);
        Check(g.ToGame()==SwitchResult.Ok&&s.Visible&&!s.Minimized,"already running hidden frontend becomes visible on entry");
        Check(s.Activations==2,"entry verifies the surface before and after Explorer closes");
        Check(s.Launches==0,"showing an existing window does not start a duplicate process");
        s.Minimized=true;s.Visible=false;int combo=0;g.Tick(10,ref combo);
        Check(s.Visible&&!s.Minimized&&s.BackgroundChecks==1,"watchdog restores hidden or minimized window");
        Check(s.Activations==2,"background repair never asks to take game focus");
        s.Explorer=true;int checks=s.BackgroundChecks;g.Tick(20,ref combo);
        Check(s.BackgroundChecks==checks,"desktop mode permits a hidden library");
        s=new SurfaceSystem{Explorer=false,Library=true,Steam=true};g=new ShellGuard(s);
        Check(g.ToGame()==SwitchResult.Ok&&s.Activations==1,"re-entering game mode still explicitly shows frontend");
        s=new SurfaceSystem{Explorer=true,Library=true,Steam=true,Recover=false};g=new ShellGuard(s);
        Check(g.ToGame()==SwitchResult.Refused&&s.Explorer&&s.ExplorerStops==0,"missing window cannot leave desktop without a surface");
        s=new SurfaceSystem{Explorer=false,Library=true,Recover=false};g=new ShellGuard(s);
        Check(g.ToGame()==SwitchResult.Failed&&s.Explorer,"failed repair of an empty game shell recovers desktop");
        s=new SurfaceSystem{Explorer=false,Library=true,Recover=false};g=new ShellGuard(s);combo=0;
        for(int i=0;i<3;i++)g.Tick(i*10,ref combo);
        Check(g.GaveUp&&s.Explorer,"repeated window failures have a bounded desktop fallback");
        s=new SurfaceSystem{Library=false};g=new ShellGuard(s);
        Check(g.Boot()&&s.Activations==1&&s.Visible,"boot validates a visible frontend as well as a process");
        Console.WriteLine("PASS "+count+" game-shell checks");
    }
}
