using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Per-pixel-alpha surface, owned by the agent. No focus, mouse input, taskbar entry or dragging.
sealed class PerformanceOverlay : Form {
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowDisplayAffinity(IntPtr window,uint affinity);
    [DllImport("user32.dll",SetLastError=true)] static extern bool GetWindowDisplayAffinity(IntPtr window,out uint affinity);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    delegate void WindowEvent(IntPtr hook,uint evt,IntPtr window,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min,uint max,IntPtr module,WindowEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(IntPtr attributes,string name,uint flags,uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr callback,IntPtr argument,bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window,IntPtr screen,ref Point dest,ref Size size,IntPtr source,ref Point origin,int key,ref Blend blend,int flags);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindowIndirect(IntPtr window,ref LayerUpdate update);
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend { public byte operation,flags,alpha,format; }
    [StructLayout(LayoutKind.Sequential)] struct LayerUpdate {
        public int size; public IntPtr screen,dest,extent,source,origin;
        public int key; public IntPtr blend; public int flags; public IntPtr dirty;
    }
    [StructLayout(LayoutKind.Sequential)] struct DirtyRect { public int left,top,right,bottom; }
    static Color Hex(string s){return ColorTranslator.FromHtml(s);}
    static readonly Color Cpu=Hex("#2E97CB"),Gpu=Hex("#2E9762"),Vram=Hex("#AD64C1"),Ram=Hex("#C26693"),Battery=Hex("#FF9078"),Engine=Hex("#EB5B5B"),White=Color.White,Background=Hex("#020202"),Curve=Color.Lime;
    static readonly Dictionary<string,PrivateFontCollection> Fonts=new Dictionary<string,PrivateFontCollection>();
    static readonly Dictionary<string,FontFamily> Families=new Dictionary<string,FontFamily>();
    static readonly Dictionary<string,Font> FontCache=new Dictionary<string,Font>();
    static readonly Dictionary<string,GraphicsPath> TextPaths=new Dictionary<string,GraphicsPath>();
    static readonly Queue<string> TextPathOrder=new Queue<string>();
    static readonly Dictionary<string,Bitmap> TextImages=new Dictionary<string,Bitmap>();
    static readonly Queue<string> TextImageOrder=new Queue<string>();
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly System.Windows.Forms.Timer repaint=new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer maintenance=new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer numberWake=new System.Windows.Forms.Timer();
    Dictionary<string,object> state=new Dictionary<string,object>();
    readonly NumberLatch numbers=new NumberLatch();
    readonly GraphPlayback playback=new GraphPlayback();
    long graphReceived,graphPaintAt,paintCount;double? graphTail,graphCursor;string graphEpoch="",clockText="";
    double maximumPaintGap,paintCost,renderCost,uploadCost;
    bool captureExcluded;int captureError;
    volatile bool closing,animate;int framePending;Thread pacer;
    readonly AutoResetEvent animationChanged=new AutoResetEvent(false);
    WindowEvent foregroundChanged;IntPtr foregroundHook,windowHandle;
    Bitmap uploaded;double[] numberFrameTimes;
    long partialUploads,zOrderChanges;bool diagnosticsDirty;
    readonly object incomingGate=new object();Dictionary<string,object> incoming;long packetAt;
    static Bitmap textLayer;static string textLayerKey="";
    static Scene cachedScene;static string sceneKey="";
    static string diagnosticPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClawToolbox","data","overlay-render.json");
    bool dirty,numberPending,displayEnabled,sourceStale;long received;string painted="";IntPtr foreground;Rectangle lastBounds;float lastScale;int layerError,zOrderError;
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void OnHandleCreated(EventArgs e){
        base.OnHandleCreated(e);
        // Keep LS desktop/window capture from baking this HUD into its output,
        // which would otherwise leave a second scaled copy under the real HUD.
        captureExcluded=SetWindowDisplayAffinity(Handle,0x11);captureError=captureExcluded?0:Marshal.GetLastWin32Error();
    }
    protected override CreateParams CreateParams { get { var p=base.CreateParams;p.ExStyle|=0x080800A0;return p; } }
    protected override void WndProc(ref Message m){
        if(m.Msg==0x8001){Interlocked.Exchange(ref framePending,0);if(!closing)RefreshFrame();return;}
        if(m.Msg==0x8002){if(displayEnabled)EnsureTopmost();return;}
        if(m.Msg==0x84){m.Result=new IntPtr(-1);return;}
        if(m.Msg==0x21){m.Result=new IntPtr(3);return;}
        base.WndProc(ref m);
        if((m.Msg==0x7e||m.Msg==0x02e0)&&!closing){dirty=true;RequestFrame();}
    }
    internal static Dictionary<string,object> Obj(Dictionary<string,object> o,string k){object v;return o.TryGetValue(k,out v)&&v is Dictionary<string,object>?(Dictionary<string,object>)v:new Dictionary<string,object>();}
    internal static double? Num(Dictionary<string,object> o,string k){object v;double n;return o.TryGetValue(k,out v)&&v!=null&&double.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&!double.IsNaN(n)&&!double.IsInfinity(n)?(double?)n:null;}
    static string Str(Dictionary<string,object> o,string k,string fallback){object v;return o.TryGetValue(k,out v)&&v!=null?v.ToString():fallback;}
    static string Val(Dictionary<string,object> d,string k,string format,string unit){var n=Num(d,k);return n.HasValue?n.Value.ToString(format,CultureInfo.InvariantCulture)+unit:"N/A";}
    static List<double?> Values(Dictionary<string,object> d,string key){object v;var list=new List<double?>();if(d.TryGetValue(key,out v)&&v is IEnumerable)foreach(object item in (IEnumerable)v){double n;list.Add(item!=null&&double.TryParse(Convert.ToString(item,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&!double.IsInfinity(n)&&!double.IsNaN(n)?(double?)n:null);}return list;}
    static FontFamily Family(string name){
        FontFamily family;if(Families.TryGetValue(name,out family))return family;
        PrivateFontCollection collection;
        if(!Fonts.TryGetValue(name,out collection)){
            collection=new PrivateFontCollection();var path=Path.Combine(Path.GetDirectoryName(typeof(PerformanceOverlay).Assembly.Location),"fonts",name+".ttf");
            if(File.Exists(path))try{collection.AddFontFile(path);}catch{}
            Fonts[name]=collection;
        }
        family=collection.Families.Length>0?collection.Families[0]:new FontFamily(name.StartsWith("Noto")?"Microsoft YaHei UI":"Consolas");
        Families[name]=family;return family;
    }
    static Font CachedFont(string name,float size){var key=name+size;Font font;if(!FontCache.TryGetValue(key,out font)){font=new Font(Family(name),size,FontStyle.Regular,GraphicsUnit.Pixel);FontCache[key]=font;}return font;}
    static float Advance(Graphics g,string text,float size) {
        using(var format=(StringFormat)StringFormat.GenericTypographic.Clone())return g.MeasureString(text,CachedFont("Unispace-HUD",size),10000,format).Width;
    }
    static void DrawText(Graphics g,string text,float x,float y,float size,Color color,bool strong=false){
        var font=CachedFont("Unispace-HUD",size);
        string key=size+":"+text;GraphicsPath path;
        if(!TextPaths.TryGetValue(key,out path)){
            path=new GraphicsPath();path.AddString(text,font.FontFamily,(int)FontStyle.Regular,size,new PointF(0,0),StringFormat.GenericTypographic);
            if(TextPaths.Count>=2048){string oldest=TextPathOrder.Dequeue();TextPaths[oldest].Dispose();TextPaths.Remove(oldest);}TextPaths[key]=path;TextPathOrder.Enqueue(key);
        }
        // Cache at the actual Windows DPI, including the subpixel origin. A
        // numeric update should blit glyphs, not rasterize every core/label again.
        var origin=new[]{new PointF(x,y)};g.TransformPoints(CoordinateSpace.Device,CoordinateSpace.World,origin);
        float sx,sy;using(var transform=g.Transform){sx=Math.Abs(transform.Elements[0]);sy=Math.Abs(transform.Elements[3]);}
        var bounds=path.GetBounds();int left=(int)Math.Floor(origin[0].X+(bounds.Left-2)*sx),top=(int)Math.Floor(origin[0].Y+(bounds.Top-2)*sy);
        int width=Math.Max(1,(int)Math.Ceiling(origin[0].X+(bounds.Right+2)*sx)-left),height=Math.Max(1,(int)Math.Ceiling(origin[0].Y+(bounds.Bottom+2)*sy)-top);
        string imageKey=key+":"+color.ToArgb()+":"+sx+":"+sy+":"+Math.Round(origin[0].X-left,3)+":"+Math.Round(origin[0].Y-top,3);
        Bitmap raster;
        if(!TextImages.TryGetValue(imageKey,out raster)){
            raster=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
            using(var ink=Graphics.FromImage(raster))using(var outline=new Pen(Color.Black,1.5f))using(var brush=new SolidBrush(color)){
                ink.SmoothingMode=SmoothingMode.AntiAlias;ink.TranslateTransform(origin[0].X-left,origin[0].Y-top);ink.ScaleTransform(sx,sy);
                outline.LineJoin=LineJoin.Round;ink.DrawPath(outline,path);ink.FillPath(brush,path);
            }
            if(TextImages.Count>=2048){string oldest=TextImageOrder.Dequeue();TextImages[oldest].Dispose();TextImages.Remove(oldest);}TextImages[imageKey]=raster;TextImageOrder.Enqueue(imageKey);
        }
        var saved=g.Save();g.ResetTransform();g.DrawImageUnscaled(raster,left,top);g.Restore(saved);
    }
    internal sealed class Cell {
        public string text,kind="text"; public float x,y,size=22;public Color color=White;
    }
    internal sealed class Scene {
        public int width,height,level;public List<Cell> cells=new List<Cell>();
        public void Text(string text,float x,float y,Color color,float size=22){cells.Add(new Cell{text=text,x=x,y=y,color=color,size=size});}
    }
    static double? Fps(Dictionary<string,object> d){string kind=Str(d,"fpsKind","");if(kind!="")return Num(d,kind=="application"?"fps":kind);foreach(string key in new[]{"displayed","presented","fps"}){var n=Num(d,key);if(n.HasValue&&n>0)return n;}return null;}
    static string FpsLabel(Dictionary<string,object> d){return Str(d,"fpsLabel","")=="LSFG"?"LSFG":"FPS";}
    static string UpscalerLabel(Dictionary<string,object> d){string name=Str(d,"upscalerName","SR");return name=="DLSS"||name=="FSR"||name=="XeSS"||name=="TSR"?name:"SR";}
    internal sealed class NumberLatch {
        public int revision;public double updatedAt;string epoch="";Dictionary<string,object> held;
        public Dictionary<string,object> Update(Dictionary<string,object> sample,double now,string stream,bool pending=true){
            if(held==null||stream!=epoch||(pending&&now-updatedAt>=500)){held=new Dictionary<string,object>(sample);updatedAt=now;epoch=stream;revision++;}
            return new Dictionary<string,object>(held);
        }
    }
    internal sealed class GraphPlayback {
        public double? cursor;public double delay=1.25,rate=1;public int underruns;
        string epoch="";double? tail;double received,painted;bool waiting;
        public double? Update(double? end,string stream,double now){
            if(stream!=epoch){epoch=stream;tail=null;cursor=null;waiting=false;delay=1.25;}
            if(!end.HasValue){tail=null;cursor=null;painted=now;return null;}
            if(!tail.HasValue||end.Value>tail.Value){
                if(tail.HasValue)delay=Math.Max(delay,Math.Min(2.5,(now-received)*1.15));
                tail=end;received=now;
            }
            if(!cursor.HasValue){cursor=end.Value-delay;painted=now;return cursor;}
            double dt=Math.Max(0,now-painted);painted=now;
            // Advance at a clock rate, including between packets. A small bounded
            // rate correction absorbs clock drift; measured Y values stay intact.
            double target=tail.Value+(now-received)-delay;
            rate=Math.Max(.9,Math.Min(1.1,1+(target-cursor.Value)*.08));
            double next=cursor.Value+dt*rate;
            if(next>=tail.Value){if(!waiting)underruns++;waiting=true;cursor=tail;}
            else{waiting=false;cursor=next;}
            return cursor;
        }
    }
    Dictionary<string,object> PaintState(long now){
        var latest=Obj(state,"sample");string epoch=Str(latest,"graphEpoch","");var end=Num(latest,"graphEnd");
        graphEpoch=epoch;
        if(end!=graphTail){graphTail=end;graphReceived=now;}
        graphCursor=playback.Update(end,epoch,now/(double)Stopwatch.Frequency);
        graphPaintAt=now;
        int previousRevision=numbers.revision;
        var sample=numbers.Update(latest,now*1000.0/Stopwatch.Frequency,epoch,numberPending);
        if(numbers.revision!=previousRevision)numberFrameTimes=FrameIntervals(sample).ToArray();
        if(numberFrameTimes!=null&&numberFrameTimes.Length>0)sample["numberFrameTimes"]=numberFrameTimes;
        foreach(string key in new[]{"frameTimes","framePoints","graphEnd","graphEpoch","frameTimeKind","displayHzHistory"}){object value;if(latest.TryGetValue(key,out value))sample[key]=value;else sample.Remove(key);}
        if(graphCursor.HasValue)sample["graphCursor"]=graphCursor;
        clockText=(Num(Obj(state,"settings"),"level")??0)==4?DateTime.Now.ToString("HH:mm:ss",CultureInfo.InvariantCulture):"";
        return new Dictionary<string,object>(state){{"visualRevision",numbers.revision},{"clockText",clockText},{"sampleForPaint",sample}};
    }
    static string BatteryTime(Dictionary<string,object> d){var n=Num(d,"minutes");if(!n.HasValue||n<0)return "N/A";int minutes=(int)n.Value;return minutes>=60?(minutes/60)+"h"+(minutes%60).ToString("00")+"m":minutes+"m";}
    static void BatteryPower(Scene scene,Graphics g,Dictionary<string,object> d,float right,float y){
        var n=Num(d,"batteryW");string text=n.HasValue&&n<0&&Str(d,"batteryWSource","")!="capacityDelta"?"-"+Math.Abs(n.Value).ToString("0.0",CultureInfo.InvariantCulture):"--";
        scene.Text(text,right-Advance(g,text,22),y,White);scene.Text("W",right+1,y+1,White,12);
    }
    static string N(double? n,string format="0"){return n.HasValue?n.Value.ToString(format,CultureInfo.InvariantCulture):"N/A";}
    static Color Mix(Color a,Color b,double t){t=Math.Max(0,Math.Min(1,t));return Color.FromArgb((int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t));}
    // MangoHud upstream's configurable 60/90 load ramp; not a claim about undocumented SteamOS thresholds.
    static Color LoadTone(double? value){if(!value.HasValue)return White;return value<60?Mix(Hex("#39F900"),Hex("#FDFD09"),value.Value/60):Mix(Hex("#FDFD09"),Hex("#B22222"),(value.Value-60)/30);}
    static Color FpsTone(double? value){if(!value.HasValue)return White;return value<30?Mix(Hex("#B22222"),Hex("#FDFD09"),value.Value/30):Mix(Hex("#FDFD09"),Hex("#39F900"),(value.Value-30)/30);}
    static void Metric(Scene scene,Graphics g,double? n,string unit,float right,float y,string format="0",bool normalUnit=false,Color? tone=null){
        string value=N(n,format);if(normalUnit&&n.HasValue)value+=unit;
        scene.Text(value,right-Advance(g,value,22),y,n.HasValue?tone??White:White);
        if(n.HasValue&&!normalUnit)scene.Text(unit,right+1,y+1,tone??White,12);
    }
    static List<double> Times(Dictionary<string,object> d,string key="frameTimes"){var list=new List<double>();foreach(var n in Values(d,key))if(n.HasValue&&n>0)list.Add(n.Value);return list;}
    static List<double> FrameIntervals(Dictionary<string,object> d){object raw;var result=new List<double>();if(d.TryGetValue("framePoints",out raw)&&raw is IEnumerable)foreach(object item in (IEnumerable)raw){var point=item as IList;if(point!=null&&point.Count>=2)result.Add(Convert.ToDouble(point[1],CultureInfo.InvariantCulture));}return result.Count>0?result:Times(d);}
    internal static Scene BuildScene(Dictionary<string,object> snapshot,Graphics g){
        var settings=Obj(snapshot,"settings");var d=Obj(snapshot,snapshot.ContainsKey("sampleForPaint")?"sampleForPaint":"sample");int level=(int)(Num(settings,"level")??1);
        var scene=new Scene{level=level,width=380,height=252};
        if(level==0){scene.width=1;scene.height=1;return scene;}
        if(level==1){scene.width=68;scene.height=32;scene.Text(N(Fps(d)),8,2,White);return scene;}
        if(level==2){
            scene.width=1200;scene.height=36;
            string label=FpsLabel(d);scene.Text(label,8,label=="LSFG"?4:2,Engine,label=="LSFG"?18:22);Metric(scene,g,Fps(d),"",104,2);scene.cells.Add(new Cell{kind="mini",x=116,y=10});
            scene.Text("CPU",278,2,Cpu);Metric(scene,g,Num(d,"cpuUsage"),"%",394,2,"0",true);Metric(scene,g,Num(d,"cpuPower"),"W",454,2,"0.0");
            scene.Text("GPU",488,2,Gpu);Metric(scene,g,Num(d,"gpuUsage"),"%",604,2,"0",true);Metric(scene,g,Num(d,"gpuPower"),"W",664,2,"0.0");
            scene.Text("RAM",698,2,Ram);Metric(scene,g,Num(d,"ramUsed"),"GiB",808,2,"0.0");
            scene.Text("VRAM",858,2,Vram);Metric(scene,g,Num(d,"gpuMem")/1024.0,"GiB",982,2,"0.0");
            scene.Text("BATT",1032,2,Battery);Metric(scene,g,Num(d,"battery"),"%",1156,2,"0",true);
            if(Str(d,"charger","False")=="True")scene.cells.Add(new Cell{kind="plug",x=1180,y=6});
            else{BatteryPower(scene,g,d,1248,2);scene.Text(BatteryTime(d),1280,2,White);scene.width=1360;}
            foreach(int x in new[]{262,472,682,842,1016})scene.cells.Add(new Cell{kind="separator",x=x,y=8,color=Vram});
            return scene;
        }
        float y=8;
        if(level==4){scene.Text("Time",8,y,White);scene.Text(Str(snapshot,"clockText",Str(d,"time",DateTime.Now.ToString("HH:mm:ss"))),140,y,White);y+=21;}
        scene.Text("GPU",8,y,Gpu);Metric(scene,g,Num(d,"gpuUsage"),"%",198,y,"0",true,level==4?LoadTone(Num(d,"gpuUsage")):White);Metric(scene,g,Num(d,"gpuTemp"),"°C",312,y);y+=21;
        Metric(scene,g,Num(d,"gpuMhz"),"MHz",198,y);Metric(scene,g,Num(d,"gpuPower"),"W",312,y,"0.0");y+=21;
        scene.Text("CPU",8,y,Cpu);Metric(scene,g,Num(d,"cpuUsage"),"%",198,y,"0",true,level==4?LoadTone(Num(d,"cpuUsage")):White);Metric(scene,g,Num(d,"cpuTemp"),"°C",312,y);y+=21;
        Metric(scene,g,Num(d,"cpuMhz"),"MHz",198,y);Metric(scene,g,Num(d,"cpuPower"),"W",312,y,"0.0");y+=21;
        if(level==4){
            var cores=Values(d,"cpuCores");var frequencies=Values(d,"cpuCoreMhz");int count=cores.Count>0?cores.Count:Environment.ProcessorCount;
            for(int i=0;i<count;i++){scene.Text("CPU",8,y,Cpu);scene.Text(i.ToString(),50,y+1,Cpu,12);Metric(scene,g,i<cores.Count?cores[i]:null,"%",198,y,"0",true,LoadTone(i<cores.Count?cores[i]:null));Metric(scene,g,i<frequencies.Count?frequencies[i]:null,"MHz",312,y);y+=21;}
        }
        scene.Text("VRAM",8,y,Vram);Metric(scene,g,Num(d,"gpuMem")/1024.0,"GiB",198,y,"0.0");Metric(scene,g,Num(d,"vramMhz"),"MHz",312,y);y+=21;
        scene.Text("RAM",8,y,Ram);Metric(scene,g,Num(d,"ramUsed"),"GiB",198,y,"0.0");y+=21;
        scene.Text("BATT",8,y,Battery);Metric(scene,g,Num(d,"battery"),"%",198,y,"0",true);if(Str(d,"charger","False")=="True")scene.cells.Add(new Cell{kind="plug",x=286,y=y+4});y+=21;
        if(Str(d,"charger","False")!="True"){scene.Text("LEFT",8,y,Battery);scene.Text(BatteryTime(d),140,y,White);BatteryPower(scene,g,d,312,y);y+=21;}
        if(level==4){
            scene.Text("FAN",8,y,Battery);Metric(scene,g,Num(d,"fanRpm"),"RPM",198,y);y+=21;
            scene.Text(UpscalerLabel(d),8,y,Engine);scene.Text(Str(d,"upscalerText","UNKNOWN"),164,y+2,White,16);y+=21;
        }
        scene.Text(FpsLabel(d),8,y,Engine);Metric(scene,g,Fps(d),"",198,y,"0",false,level==4?FpsTone(Fps(d)):White);Metric(scene,g,Num(d,"frameTime"),"ms",312,y,"0.0");y+=30;
        scene.cells.Add(new Cell{kind="graph",x=8,y=y});y+=124;
        if(level==4){scene.cells.Add(new Cell{kind="display",x=8,y=y});y+=62;}
        scene.height=(int)y+8;return scene;
    }
    static void DrawGraph(Graphics g,Dictionary<string,object> d,string key,float x,float y,float width,float height,bool header,bool decorations=true,bool plot=true){
        object raw=null;
        var hasTimedPoints=key=="frameTimes"&&d.TryGetValue("framePoints",out raw)&&raw is IEnumerable;
        var times=hasTimedPoints&&!decorations?null:Times(d,key);
        double min=double.MaxValue,max=0;
        if(times!=null)foreach(var n in times){min=Math.Min(min,n);max=Math.Max(max,n);}
        if(decorations&&key=="frameTimes"){min=double.MaxValue;max=0;foreach(var n in d.ContainsKey("numberFrameTimes")?Times(d,"numberFrameTimes"):FrameIntervals(d)){min=Math.Min(min,n);max=Math.Max(max,n);}}
        string unit=key=="frameCounts"?"":key=="displayHzHistory"?"Hz":"ms",format=key=="frameCounts"?"0":"0.0";
        if(header){if(decorations){DrawText(g,key=="frameCounts"?"Frame Count":key=="displayHzHistory"?"Display Hz":key=="appFrameTimes"?"App":"Frametime",x,y,12,Engine);if(times.Count>0&&min<=max)DrawText(g,"min: "+min.ToString(format)+unit+", max: "+max.ToString(format)+unit,key=="displayHzHistory"?154:190,y,key=="displayHzHistory"?11:12,White);}y+=20;}
        if(key=="frameTimes"){
            if(header&&decorations)for(int tick=0;tick<=50;tick+=10){float ty=y+height-tick/50f*height;using(var grid=new Pen(Color.FromArgb(65,White),.5f))g.DrawLine(grid,x,ty,x+width,ty);DrawText(g,tick+"",312,ty-6,11,White);}
            if(!plot)return;
            var points=new List<PointF>();
            if(hasTimedPoints){
                double cursor=Num(d,"graphCursor")??Num(d,"graphEnd")??0;
                PointF? previous=null;
                foreach(object item in (IEnumerable)raw){var values=item as IList;if(values==null||values.Count<2)continue;double at=Convert.ToDouble(values[0],CultureInfo.InvariantCulture),ms=Convert.ToDouble(values[1],CultureInfo.InvariantCulture);double position=(at-(cursor-5))/5;var point=new PointF(x+(float)position*width,y+height-(float)Math.Min(50,Math.Max(0,ms))/50*height);if(position<0){previous=point;continue;}if(points.Count==0&&previous.HasValue)points.Add(previous.Value);points.Add(point);if(position>1)break;previous=point;}
            }else for(int i=0;i<times.Count;i++)points.Add(new PointF(x+width*i/Math.Max(1,times.Count-1),y+height-(float)Math.Min(50,times[i])/50*height));
            if(points.Count>1){var saved=g.Save();g.SetClip(new RectangleF(x,y,width,height+1));using(var pen=new Pen(Curve,1.5f))g.DrawLines(pen,points.ToArray());g.Restore(saved);}
            return;
        }
        if(header&&decorations&&times.Count>0)DrawText(g,times[times.Count-1].ToString(format)+unit,312,y+height/2,12,White);
        if(!plot)return;
        if(times.Count>1){double ceiling=Math.Max(33.333,max*1.1);var points=new PointF[times.Count];for(int i=0;i<points.Length;i++)points[i]=new PointF(x+width*i/(points.Length-1),y+height-(float)(times[i]/ceiling*height));using(var pen=new Pen(Curve,1.5f))g.DrawLines(pen,points);}
        else DrawText(g,"N/A",header?312:x+48,header?y+height/2:y,12,White);
    }
    static Scene SceneFor(Dictionary<string,object> snapshot,Graphics g){
        string key=snapshot.ContainsKey("visualRevision")?Str(snapshot,"visualRevision","")+":"+Num(Obj(snapshot,"settings"),"level")+":"+Str(snapshot,"clockText",""):"";
        if(key!=""&&key==sceneKey&&cachedScene!=null)return cachedScene;
        var scene=BuildScene(snapshot,g);if(key!=""){sceneKey=key;cachedScene=scene;}return scene;
    }
    internal static Size LogicalSize(Dictionary<string,object> snapshot,int availableWidth){using(var image=new Bitmap(1,1))using(var g=Graphics.FromImage(image)){var scene=SceneFor(snapshot,g);return new Size(scene.width,scene.height);}}
    internal static Bitmap RenderSnapshot(Dictionary<string,object> snapshot,float scale,int availableWidth){
        using(var dummy=new Bitmap(1,1))using(var measure=Graphics.FromImage(dummy)){
            var scene=SceneFor(snapshot,measure);scale=Math.Min(scale,availableWidth/(float)scene.width);
            int surfaceWidth=scene.level==2?availableWidth:(int)Math.Ceiling(scene.width*scale);
            string cacheKey=snapshot.ContainsKey("visualRevision")?Str(snapshot,"visualRevision","")+":"+scene.level+":"+surfaceWidth+":"+scene.height+":"+scale+":"+Str(snapshot,"clockText",""):"";
            bool reuse=cacheKey!=""&&cacheKey==textLayerKey&&textLayer!=null;
            var bitmap=reuse?(Bitmap)textLayer.Clone():new Bitmap(Math.Max(1,surfaceWidth),Math.Max(1,(int)Math.Ceiling(scene.height*scale)),PixelFormat.Format32bppPArgb);
            using(var g=Graphics.FromImage(bitmap)){
                if(!reuse){g.Clear(Color.Transparent);if(scene.level>1){using(var background=new SolidBrush(Color.FromArgb(128,Background)))g.FillRectangle(background,0,0,bitmap.Width,bitmap.Height);}}
                g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                // The L2 surface spans the screen; zoom changes only the centered content and bar height.
                if(scene.level==2)g.TranslateTransform((bitmap.Width/scale-scene.width)/2,0);
                var d=Obj(snapshot,snapshot.ContainsKey("sampleForPaint")?"sampleForPaint":"sample");
                if(!reuse){
                foreach(var cell in scene.cells){
                    if(cell.kind=="text")DrawText(g,cell.text,cell.x,cell.y,cell.size,cell.color);
                    else if(cell.kind=="separator")using(var pen=new Pen(cell.color,1))g.DrawLine(pen,cell.x,cell.y,cell.x,cell.y+19);
                    else if(cell.kind=="plug"){
                        var saved=g.Save();g.TranslateTransform(cell.x,cell.y);g.RotateTransform(40);using(var brush=new SolidBrush(White)){g.FillRectangle(brush,0,5,11,10);g.FillRectangle(brush,1,0,3,6);g.FillRectangle(brush,7,0,3,6);g.FillRectangle(brush,4,14,3,7);}g.Restore(saved);
                    }
                    else if(cell.kind=="graph"||cell.kind=="display")DrawGraph(g,d,cell.kind=="display"?"displayHzHistory":"frameTimes",cell.x,cell.y,296,cell.kind=="graph"?100:24,true,true,false);
                }
                if(cacheKey!=""){if(textLayer!=null)textLayer.Dispose();textLayer=(Bitmap)bitmap.Clone();textLayerKey=cacheKey;}}
                foreach(var cell in scene.cells){if(cell.kind=="mini")DrawGraph(g,d,"frameTimes",cell.x,cell.y,140,18,false,false);else if(cell.kind=="graph"||cell.kind=="display")DrawGraph(g,d,cell.kind=="display"?"displayHzHistory":"frameTimes",cell.x,cell.y,296,cell.kind=="graph"?100:24,true,false);}
            }
            // L1 has no panel: trim invisible padding so its visible numeral also touches the selected edges.
            if(scene.level==1){int left=bitmap.Width,top=bitmap.Height,right=-1,bottom=-1;
                for(int py=0;py<bitmap.Height;py++)for(int px=0;px<bitmap.Width;px++)if(bitmap.GetPixel(px,py).A>0){left=Math.Min(left,px);top=Math.Min(top,py);right=Math.Max(right,px);bottom=Math.Max(bottom,py);}
                if(right>=left&&bottom>=top){var trimmed=bitmap.Clone(Rectangle.FromLTRB(left,top,right+1,bottom+1),PixelFormat.Format32bppPArgb);bitmap.Dispose();return trimmed;}
            }
            return bitmap;
        }
    }
    static string LayoutKey(Dictionary<string,object> snapshot){
        var s=Obj(snapshot,"settings");
        return Str(s,"enabled","False")+":"+Num(s,"level")+":"+Num(s,"position")+":"+Num(s,"scale")+":"+Str(s,"size","");
    }
    void RequestFrame(){
        if(windowHandle==IntPtr.Zero||Interlocked.Exchange(ref framePending,1)!=0)return;
        if(!PostMessage(windowHandle,0x8001,IntPtr.Zero,IntPtr.Zero))Interlocked.Exchange(ref framePending,0);
    }
    void SetAnimation(bool active){
        if(animate==active)return;
        animate=active;
        if(pacer==null){if(active)repaint.Start();else repaint.Stop();}
        else animationChanged.Set();
    }
    static object Value(Dictionary<string,object> sample,string key){
        object value;return sample.TryGetValue(key,out value)?value:null;
    }
    static bool NumbersChanged(Dictionary<string,object> previous,Dictionary<string,object> next,int level){
        foreach(string key in previous.Keys)
            if(!next.ContainsKey(key))return true;
        foreach(var item in next){
            if(item.Key=="graphEnd"||(level<3&&(item.Key=="framePoints"||item.Key=="frameTimes")))continue;
            if(!Equals(item.Value,Value(previous,item.Key)))return true;
        }
        return false;
    }
    void RefreshFrame(){
        long now=Stopwatch.GetTimestamp();
        Dictionary<string,object> next;
        lock(incomingGate){next=incoming;incoming=null;received=packetAt;}
        bool graphPacket=false;
        if(next!=null){
            dirty|=sourceStale||LayoutKey(next)!=LayoutKey(state);
            var before=Obj(state,"sample");var after=Obj(next,"sample");
            int nextLevel=(int)(Num(Obj(next,"settings"),"level")??0);
            numberPending|=NumbersChanged(before,after,nextLevel);
            graphPacket=!Equals(Value(before,"framePoints"),Value(after,"framePoints"))||
                !Equals(Value(before,"frameTimes"),Value(after,"frameTimes"));
            state=next;sourceStale=false;
        }
        if(received>0&&now-received>Stopwatch.Frequency*15&&!sourceStale){
            state["sample"]=new Dictionary<string,object>();
            sourceStale=true;dirty=true;numberPending=true;
        }
        var s=Obj(state,"settings");int level=(int)(Num(s,"level")??0);
        var latest=Obj(state,"sample");
        string epoch=Str(latest,"graphEpoch","");
        var end=level>1?Num(latest,"graphEnd"):null;
        bool graphChanged=level>1&&(graphPacket||end!=graphTail||epoch!=graphEpoch);
        bool graphMoving=end.HasValue&&graphCursor.HasValue&&graphCursor.Value<end.Value;
        bool numberDue=numberPending&&(numbers.revision==0||epoch!=graphEpoch||
            now*1000.0/Stopwatch.Frequency-numbers.updatedAt>=500);
        bool clockDue=level==4&&DateTime.Now.ToString("HH:mm:ss",CultureInfo.InvariantCulture)!=clockText;
        if(dirty||(displayEnabled&&(graphChanged||graphMoving||numberDue||clockDue))){
            var previousBounds=lastBounds;bool wasVisible=displayEnabled;
            double previousNumberTime=numbers.updatedAt;
            dirty=false;ApplyState();
            if(numberDue||numbers.updatedAt!=previousNumberTime)numberPending=false;
            if(displayEnabled&&(!wasVisible||lastBounds!=previousBounds))EnsureTopmost();
        }
        SetAnimation(displayEnabled&&end.HasValue&&graphCursor.HasValue&&graphCursor.Value<end.Value);
        if(displayEnabled&&numberPending&&!animate){
            numberWake.Interval=Math.Max(1,(int)Math.Ceiling(numbers.updatedAt+500-now*1000.0/Stopwatch.Frequency));
            numberWake.Start();
        }else numberWake.Stop();
        int maintenanceInterval=level==4?200:1000;
        if(maintenance.Interval!=maintenanceInterval)maintenance.Interval=maintenanceInterval;
    }
    void StartPacer(){
        IntPtr timer=CreateWaitableTimerEx(IntPtr.Zero,null,2,0x100002);
        if(timer==IntPtr.Zero)return;
        pacer=new Thread(delegate(){
            try{
                long next=Stopwatch.GetTimestamp();long interval=Stopwatch.Frequency/30; // 30 Hz: the scrolling frame-time graph cost ~14 % of a core at 60 Hz
                while(!closing){
                    if(!animate){
                        animationChanged.WaitOne();
                        next=Stopwatch.GetTimestamp();
                        continue;
                    }
                    next+=interval;long now=Stopwatch.GetTimestamp();if(next<now)next=now;
                    long due=-Math.Max(1,(next-now)*10000000/Stopwatch.Frequency);
                    if(!SetWaitableTimer(timer,ref due,0,IntPtr.Zero,IntPtr.Zero,false)||WaitForSingleObject(timer,1000)!=0)break;
                    if(!closing&&animate)RequestFrame();
                }
            }finally{CloseHandle(timer);}
        });pacer.IsBackground=true;pacer.Start();
    }
    public PerformanceOverlay(){
        Text="Claw 性能显示";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
        repaint.Interval=33;repaint.Tick+=delegate{RefreshFrame();};
        numberWake.Tick+=delegate{numberWake.Stop();RefreshFrame();};
        maintenance.Interval=1000;
        long diagnosticsAt=0;
        maintenance.Tick+=delegate{
            if(!animate)RefreshFrame();
            long now=Stopwatch.GetTimestamp();
            if(diagnosticsDirty&&now-diagnosticsAt>=Stopwatch.Frequency){diagnosticsAt=now;WriteDiagnostics();}
        };
        Shown+=delegate{
            windowHandle=Handle;
            SetWindowLongPtr(Handle,-8,IntPtr.Zero);
            foregroundChanged=delegate{PostMessage(windowHandle,0x8002,IntPtr.Zero,IntPtr.Zero);};
            foregroundHook=SetWinEventHook(3,3,IntPtr.Zero,foregroundChanged,0,0,2);
            StartPacer();maintenance.Start();
            var reader=new Thread(delegate(){
                var inputJson=new JavaScriptSerializer();
                var snapshot=new Dictionary<string,object>();
                try{
                    string line;
                    while((line=Console.ReadLine())!=null){
                        var next=inputJson.Deserialize<Dictionary<string,object>>(line);
                        bool heartbeat=next.ContainsKey("heartbeat");
                        if(!heartbeat){
                            if(next.ContainsKey("delta")){
                                var sample=new Dictionary<string,object>(Obj(snapshot,"sample"));
                                foreach(var pair in Obj(next,"delta"))sample[pair.Key]=pair.Value;
                                object removed;
                                if(next.TryGetValue("remove",out removed)&&removed is IEnumerable)
                                    foreach(var key in (IEnumerable)removed)if(key is string)sample.Remove((string)key);
                                snapshot=new Dictionary<string,object>(snapshot);snapshot["sample"]=sample;
                                if(next.ContainsKey("settings"))snapshot["settings"]=next["settings"];
                            }else snapshot=next;
                        }
                        lock(incomingGate){packetAt=Stopwatch.GetTimestamp();if(!heartbeat)incoming=snapshot;}
                        if(!heartbeat)RequestFrame();
                    }
                }catch{}
                try{BeginInvoke((Action)Close);}catch{}
            });
            reader.IsBackground=true;reader.Start();
        };
        FormClosed+=delegate{
            closing=true;
            if(foregroundHook!=IntPtr.Zero)UnhookWinEvent(foregroundHook);
            maintenance.Dispose();repaint.Dispose();numberWake.Dispose();animationChanged.Set();
            if(pacer==null||pacer.Join(200))animationChanged.Dispose();
            if(uploaded!=null)uploaded.Dispose();
        };
    }
    void ApplyState(){
        long began=Stopwatch.GetTimestamp();
        if(animate&&graphPaintAt>0)maximumPaintGap=Math.Max(maximumPaintGap,(began-graphPaintAt)*1000.0/Stopwatch.Frequency);
        var s=Obj(state,"settings");displayEnabled=Str(s,"enabled","False")=="True"&&(Num(s,"level")??0)>0;if(!displayEnabled){Hide();return;}
        var bounds=Screen.PrimaryScreen.Bounds;float scale=(float)(Num(s,"scale")??100)/100f;
        scale*=Str(s,"size","medium")=="small"?.875f:Str(s,"size","medium")=="large"?1.25f:1;
        try{scale*=GetDpiForWindow(Handle)/96f;}catch{}
        var paint=PaintState(Stopwatch.GetTimestamp());var logical=LogicalSize(paint,(int)(bounds.Width/scale));scale=Math.Min(scale,Math.Min(bounds.Width/(float)logical.Width,bounds.Height/(float)logical.Height));
        var bitmap=RenderSnapshot(paint,scale,bounds.Width);
        try{
            long rendered=Stopwatch.GetTimestamp();renderCost=(rendered-began)*1000.0/Stopwatch.Frequency;
            var size=bitmap.Size;int anchor=(int)(Num(s,"position")??0);var dest=AnchorBounds(bounds,size,anchor).Location;
            var placed=new Rectangle(dest,size);
            Rectangle? damage=uploaded!=null&&placed==lastBounds&&layerError==0?(Rectangle?)ChangedPixels(uploaded,bitmap):null;
            if(damage.HasValue&&damage.Value.IsEmpty)return;
            var screen=GetDC(IntPtr.Zero);var source=CreateCompatibleDC(screen);var image=bitmap.GetHbitmap(Color.FromArgb(0));var previous=SelectObject(source,image);
            try{
                bool ok=Upload(screen,source,dest,size,damage);
                layerError=ok?0:Marshal.GetLastWin32Error();
                if(ok){
                    if(uploaded!=null)uploaded.Dispose();
                    uploaded=bitmap;bitmap=null;lastBounds=placed;lastScale=scale;
                    paintCount++;
                }
                diagnosticsDirty=true;
            }finally{SelectObject(source,previous);DeleteObject(image);DeleteDC(source);ReleaseDC(IntPtr.Zero,screen);uploadCost=(Stopwatch.GetTimestamp()-rendered)*1000.0/Stopwatch.Frequency;}
        }finally{if(bitmap!=null)bitmap.Dispose();}
        paintCost=(Stopwatch.GetTimestamp()-began)*1000.0/Stopwatch.Frequency;
    }
    internal static unsafe Rectangle ChangedPixels(Bitmap previous,Bitmap next){
        var area=new Rectangle(Point.Empty,next.Size);
        var oldBits=previous.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
        try{
            var newBits=next.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try{
                int left=area.Width,top=area.Height,right=-1,bottom=-1;
                for(int y=0;y<area.Height;y++){
                    var oldRow=(uint*)((byte*)oldBits.Scan0+y*oldBits.Stride);
                    var newRow=(uint*)((byte*)newBits.Scan0+y*newBits.Stride);
                    for(int x=0;x<area.Width;x++){
                        if(oldRow[x]==newRow[x])continue;
                        left=Math.Min(left,x);right=Math.Max(right,x);
                        top=Math.Min(top,y);bottom=y;
                    }
                }
                return right<0?Rectangle.Empty:Rectangle.FromLTRB(left,top,right+1,bottom+1);
            }finally{next.UnlockBits(newBits);}
        }finally{previous.UnlockBits(oldBits);}
    }
    unsafe bool Upload(IntPtr screen,IntPtr source,Point dest,Size size,Rectangle? damage){
        var origin=new Point();var blend=new Blend{alpha=255,format=1};
        if(damage.HasValue){
            var d=damage.Value;
            var rect=new DirtyRect{left=d.Left,top=d.Top,right=d.Right,bottom=d.Bottom};
            var info=new LayerUpdate{size=sizeof(LayerUpdate),screen=screen,dest=(IntPtr)(&dest),extent=(IntPtr)(&size),
                source=source,origin=(IntPtr)(&origin),blend=(IntPtr)(&blend),flags=2|8,dirty=(IntPtr)(&rect)};
            if(UpdateLayeredWindowIndirect(Handle,ref info)){partialUploads++;return true;}
        }
        return UpdateLayeredWindow(Handle,screen,ref dest,ref size,source,ref origin,0,ref blend,2);
    }
    internal static Rectangle AnchorBounds(Rectangle screen,Size size,int anchor){
        return new Rectangle(new Point(anchor%3==0?screen.Left:anchor%3==1?screen.Left+(screen.Width-size.Width)/2:screen.Right-size.Width,anchor/3==0?screen.Top:anchor/3==1?screen.Top+(screen.Height-size.Height)/2:screen.Bottom-size.Height),size);
    }
    void EnsureTopmost(){
        // Foreground/placement events own Z-order, independently of painting.
        bool ok=SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x1|0x2|0x10|0x40|0x200);
        zOrderError=ok?0:Marshal.GetLastWin32Error();
        zOrderChanges++;diagnosticsDirty=true;
    }
    void WriteDiagnostics(){
        diagnosticsDirty=false;
        uint displayAffinity;GetWindowDisplayAffinity(Handle,out displayAffinity);
        var active=GetForegroundWindow();bool top=(GetWindowLongPtr(Handle,-20).ToInt64()&8)!=0,visible=IsWindowVisible(Handle);
        var s=Obj(state,"settings");string signature=Num(s,"level")+":"+lastBounds+":"+active+":"+visible+":"+top+":"+layerError+":"+sourceStale+":"+paintCount+":"+zOrderChanges;
        if(signature==painted)return;painted=signature;foreground=active;
        try{File.WriteAllText(diagnosticPath,json.Serialize(new{ok=layerError==0,error=layerError,zOrderOk=zOrderError==0,zOrderError=zOrderError,level=Num(s,"level"),x=lastBounds.X,y=lastBounds.Y,width=lastBounds.Width,height=lastBounds.Height,scale=lastScale,topmost=top,visible=visible,captureExcluded=captureExcluded,captureError=captureError,displayAffinity=displayAffinity,owner=GetWindow(Handle,4).ToInt64(),foreground=foreground.ToInt64(),font=Family("Unispace-HUD").Name,stale=sourceStale,numericRevision=numbers.revision,numericUpdatedAt=numbers.updatedAt,clockText=clockText,paintAt=graphPaintAt*1000.0/Stopwatch.Frequency,paintCount=paintCount,partialUploads=partialUploads,zOrderChanges=zOrderChanges,paintCost=paintCost,renderCost=renderCost,uploadCost=uploadCost,maximumPaintGap=maximumPaintGap,graphTail=graphTail,graphCursor=graphCursor,graphRate=playback.rate,graphDelay=playback.delay,graphUnderruns=playback.underruns}));}catch{}
    }
    [STAThread]static void Main(string[] args){foreach(var arg in args)if(arg.StartsWith("--diagnostics="))diagnosticPath=arg.Substring(14);try{SetProcessDpiAwarenessContext(new IntPtr(-4));}catch{}Application.EnableVisualStyles();Application.Run(new PerformanceOverlay());}
}
