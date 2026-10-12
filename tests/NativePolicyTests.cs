using System;
using ClawRouter;
class NativePolicyTests {
    static void Check(bool result,string name){if(!result)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static int Main(){
        Check(Policy.SteamMode(false,true,0)=="Unknown","missing Steam report cannot acquire ownership");
        Check(Policy.SteamMode(true,true,14999)=="BigPicture","live Big Picture report permits ownership");
        Check(Policy.SteamMode(true,false,0)=="SteamClosed","closed Big Picture releases ownership");
        Check(Policy.SteamMode(true,true,15001)=="Unknown","stale Steam renderer releases ownership");
        Check(Policy.SteamMode(true,true,-1)=="Unknown","invalid report age cannot acquire ownership");
        Check(Policy.CanRoute(true,true,"BigPicture",true),"route requires a released owner");
        Check(!Policy.CanRoute(true,true,"BigPicture",false),"no new action before ClawTweaks exits");
        Check(!Policy.CanRoute(true,true,"Unknown",true),"unknown Steam state stops routing");
        Check(!Policy.CanRoute(true,false,"BigPicture",true),"missing Steam bridge stops routing");
        Check(!Policy.CanRoute(false,true,"BigPicture",true),"manual disable overrides Big Picture");
        Check(!Policy.CanRoute(true,true,"SteamClosed",true),"closed Big Picture belongs to ClawTweaks");
        Check(!Policy.CanRoute(true,true,"Gaming",true),"Playhub Gaming alone cannot acquire ownership");
        Check(!Policy.CanRoute(true,true,"Desktop",true),"Playhub Desktop is no longer an ownership signal");
        Check(Policy.Action("shell")=="None","arbitrary actions cannot be executed");
        Check(GameBarGuard.IsTarget(@"C:\WindowsApps\GameBar.exe",GameBarGuard.PackageFamily,1,1),"exact Microsoft Game Bar in this session is eligible");
        Check(GameBarGuard.IsTarget(@"C:\WindowsApps\gamebarftserver.EXE",GameBarGuard.PackageFamily,1,1),"verified Game Bar full trust server is eligible");
        Check(!GameBarGuard.IsTarget(@"C:\Other\GameBar.exe","OtherPackage",1,1),"same executable name from another package is never closed");
        Check(!GameBarGuard.IsTarget(@"C:\WindowsApps\GameBar.exe",GameBarGuard.PackageFamily,2,1),"another Windows session is never closed");
        Check(!GameBarGuard.IsTarget(@"C:\WindowsApps\XboxGamingBarHelper.exe",GameBarGuard.PackageFamily,1,1),"hardware helper is outside the Game Bar guard");
        Check(!GameBarGuard.IsTarget(@"C:\Other\GameBar.exe",null,1,1),"unpackaged same-name process is never closed");return 0;
    }
}
