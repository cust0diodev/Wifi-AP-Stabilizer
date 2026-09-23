using System;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace WifiAP {
    public interface ILog {
        void Write(string name, string detail);
    }
    public sealed class NullLog : ILog { public void Write(string name,string detail) { } }
    public sealed class FileLog : ILog {
        readonly object sync = new object();
        readonly long maxBytes;
        public readonly string DirectoryPath;
        public string LastError { get; private set; }
        public FileLog(string directory, long limit) { DirectoryPath=directory; maxBytes=limit; }
        public void Write(string name,string detail) {
            lock(sync) {
                try {
                    Directory.CreateDirectory(DirectoryPath);
                    string path=Path.Combine(DirectoryPath,"wifi-ap.log");
                    string line=DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")+" ["+name+"] "+(detail??"").Replace("\r"," ").Replace("\n"," ")+Environment.NewLine;
                    if(File.Exists(path) && new FileInfo(path).Length+Encoding.UTF8.GetByteCount(line)>maxBytes) {
                        if(File.Exists(path+".4")) File.Delete(path+".4");
                        for(int i=3;i>=1;i--) if(File.Exists(path+"."+i)) File.Move(path+"."+i,path+"."+(i+1));
                        File.Move(path,path+".1");
                    }
                    File.AppendAllText(path,line,new UTF8Encoding(false)); LastError=null;
                } catch(Exception ex) { LastError=ex.Message; } // Logging must never interrupt Wi-Fi control.
            }
        }
    }
    public static class Settings {
        public static readonly string Profile = ConfigurationManager.AppSettings["ProfileName"] ?? "MinhaRede";
        public static readonly string Target = ConfigurationManager.AppSettings["TargetBssid"] ?? "02:00:00:00:00:01";
        public static Guid InterfaceId {
            get {
                Guid id;
                if (!Guid.TryParse(ConfigurationManager.AppSettings["InterfaceGuid"], out id))
                    throw new InvalidOperationException("Configure InterfaceGuid no arquivo WiFi-AP.exe.config.");
                return id;
            }
        }
    }
    public sealed class Connection {
        public bool Connected;
        public string Bssid = "";
        public string Profile = "";
    }
    public interface IWifi : IDisposable {
        Connection Read();
        void Request(string profile, string bssid);
    }
    public sealed class NativeWifi : IWifi {
        IntPtr handle;
        Guid id;
        void Open() {
            if (handle != IntPtr.Zero) return;
            if (Settings.InterfaceId == Guid.Empty) throw new InvalidOperationException("Configure perfil, BSSID e InterfaceGuid no arquivo WiFi-AP.exe.config e reabra o aplicativo.");
            uint version;
            Check(WlanOpenHandle(2, IntPtr.Zero, out version, out handle));
            IntPtr list = IntPtr.Zero;
            try {
                Check(WlanEnumInterfaces(handle, IntPtr.Zero, out list));
                int count = Marshal.ReadInt32(list);
                int size = Marshal.SizeOf(typeof(InterfaceInfo));
                bool found = false;
                for (int i = 0; i < count; i++) {
                    InterfaceInfo item = (InterfaceInfo)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * size), typeof(InterfaceInfo));
                    if (item.Id == Settings.InterfaceId) { id = item.Id; found = true; break; }
                }
                if (!found) throw new InvalidOperationException("O adaptador Wi-Fi deste notebook não foi encontrado. Confira se ele está habilitado.");
            } catch { Dispose(); throw; }
            finally { if (list != IntPtr.Zero) WlanFreeMemory(list); }
        }
        public Connection Read() {
            Open();
            IntPtr data = IntPtr.Zero;
            uint bytes; int kind;
            try {
                uint result = WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out bytes, out data, out kind);
                if (result == 5023) return new Connection(); // interface disconnected
                Check(result);
                if (bytes < Marshal.SizeOf(typeof(ConnectionAttributes))) throw new InvalidOperationException("O Windows retornou dados Wi-Fi incompletos.");
                ConnectionAttributes current = (ConnectionAttributes)Marshal.PtrToStructure(data, typeof(ConnectionAttributes));
                return new Connection { Connected = current.State == 1,
                    Profile = current.Profile, Bssid = BitConverter.ToString(current.Association.Bssid).Replace('-', ':') };
            } catch(Win32Exception ex) { ResetIfStale(ex); throw; }
            finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
        }
        public void Request(string profile, string bssid) {
            Open();
            IntPtr desired = IntPtr.Zero;
            try {
                if (bssid != null) {
                    BssidList list = MakeList(bssid);
                    desired = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(BssidList)));
                    Marshal.StructureToPtr(list, desired, false);
                }
                ConnectParameters args = new ConnectParameters { Mode = 0, Profile = profile, Desired = desired, BssType = 1 };
                Check(WlanConnect(handle, ref id, ref args, IntPtr.Zero));
            } catch(Win32Exception ex) { ResetIfStale(ex); throw; }
            finally { if (desired != IntPtr.Zero) Marshal.FreeHGlobal(desired); }
        }
        void ResetIfStale(Win32Exception ex) {
            if(ex.NativeErrorCode==6 || ex.NativeErrorCode==1062 || ex.NativeErrorCode==1168 || ex.NativeErrorCode==1722) Dispose();
        }
        internal static BssidList MakeList(string value) {
            if(value==null || !Regex.IsMatch(value,@"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\z")) throw new ArgumentException("BSSID inválido.");
            byte[] mac = value.Split(':').Select(x => Convert.ToByte(x, 16)).ToArray();
            if (mac.Length != 6) throw new ArgumentException("BSSID inválido.");
            return new BssidList { Type = 0x80, Revision = 1, Size = (ushort)Marshal.SizeOf(typeof(BssidList)), Count = 1, Total = 1, Mac = mac };
        }
        static void Check(uint result) {
            if (result == 0) return;
            if (result == 5) throw new InvalidOperationException("Acesso ao Wi-Fi negado. Confira Configurações > Privacidade e segurança > Localização e o acesso de aplicativos da área de trabalho. Código 5.");
            throw new Win32Exception((int)result, "Wi-Fi: " + new Win32Exception((int)result).Message + " (" + result + ").");
        }
        public void Dispose() { if (handle != IntPtr.Zero) { WlanCloseHandle(handle, IntPtr.Zero); handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct InterfaceInfo { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Description; public int State; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BssidList { public byte Type, Revision; public ushort Size; public uint Count, Total; [MarshalAs(UnmanagedType.ByValArray, SizeConst=6)] public byte[] Mac; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ConnectParameters { public int Mode; [MarshalAs(UnmanagedType.LPWStr)] public string Profile; public IntPtr Ssid, Desired; public int BssType; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Ssid { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] public byte[] Bytes; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Association { public Ssid Ssid; public int BssType; [MarshalAs(UnmanagedType.ByValArray, SizeConst=6)] public byte[] Bssid; public int Phy; public uint PhyIndex, Signal, Rx, Tx; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Security { public int Enabled, OneX, Auth, Cipher; }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        internal struct ConnectionAttributes { public int State, Mode; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Profile; public Association Association; public Security Security; }
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int type);
        [DllImport("wlanapi.dll", CharSet=CharSet.Unicode)] static extern uint WlanConnect(IntPtr handle, ref Guid id, ref ConnectParameters args, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr data);
    }

    // Only used by the serialized worker. The UI paints immutable snapshots.
    public sealed class Controller : IDisposable {
        readonly IWifi wifi;
        readonly ILog log;
        readonly Func<TimeSpan> clock;
        TimeSpan lastRequest;
        string lastObservation, lastPollError, commandError;
        bool retryPaused;
        public bool Active { get; private set; }
        public bool Confirmed { get; private set; }
        public string Current = "Consultando...";
        public string Message = "Ative para manter a conexão com o AP escolhido.";
        public bool Failed;
        public Controller(IWifi backend) : this(backend,new NullLog(),null) { }
        public Controller(IWifi backend, ILog logger, Func<TimeSpan> time) {
            wifi=backend; log=logger;
            Stopwatch watch=Stopwatch.StartNew(); clock=time??delegate { return watch.Elapsed; };
        }
        public void Toggle() {
            if (Active) Release();
            else {
                log.Write("ACTIVATE_REQUEST","profile="+Settings.Profile+"; target="+Settings.Target);
                wifi.Read(); // Check access before changing connectivity.
                wifi.Request(Settings.Profile, Settings.Target);
                lastRequest = clock(); retryPaused=false; commandError=null;
                Active = true; Confirmed = false; Failed = false;
                Message = "Conectando ao AP escolhido...";
                log.Write("ACTIVATE_ACCEPTED","Aguardando confirmação da associação.");
            }
        }
        public void Release() {
            if (!Active) return;
            retryPaused=true; // A failed release must not allow the monitor to reapply the restriction.
            log.Write("RELEASE_REQUEST","Reconexões automáticas suspensas.");
            // A new unrestricted connection request replaces the desired BSSID list.
            // Preserve the currently used profile if the user has moved networks.
            string profile = Settings.Profile;
            try { Connection c = wifi.Read(); if (c.Connected && !String.IsNullOrEmpty(c.Profile)) profile = c.Profile; }
            catch(Exception ex) { log.Write("RELEASE_READ_WARNING",ex.Message); }
            wifi.Request(profile, null);
            Active = false; Confirmed = false; Failed = false; commandError=null;
            Message = "Bloqueio desativado. Conexão normal solicitada.";
            log.Write("RELEASE_ACCEPTED","profile="+profile+"; desiredBssid=null");
        }
        public void Poll() {
            Connection c = wifi.Read();
            Current = c.Connected ? c.Bssid : "Desconectado";
            string observation=(c.Connected?"connected":"disconnected")+"; bssid="+c.Bssid+"; profile="+c.Profile;
            if(observation!=lastObservation) { log.Write("CONNECTION",observation); lastObservation=observation; }
            if(lastPollError!=null) { log.Write("MONITOR_RECOVERED","Consulta ao adaptador restabelecida."); lastPollError=null; }
            Confirmed = Active && !retryPaused && c.Connected && String.Equals(c.Bssid, Settings.Target, StringComparison.OrdinalIgnoreCase) && c.Profile == Settings.Profile;
            Failed=commandError!=null;
            if(Failed) { Message=commandError; return; }
            if(!Active) { Message="Bloqueio desativado. Conexão sob controle do Windows."; return; }
            if (Confirmed) { Message = "Conectado ao AP escolhido. Monitoramento ativo."; return; }
            if(retryPaused) return;
            Message = "Aguardando o AP escolhido. Nova tentativa a cada 30 s.";
            if ((clock() - lastRequest).TotalSeconds >= 30) {
                lastRequest = clock(); // Monotonic interval also throttles failed requests.
                log.Write("RECONNECT_REQUEST","current="+Current+"; target="+Settings.Target);
                wifi.Request(Settings.Profile, Settings.Target);
                log.Write("RECONNECT_ACCEPTED","Aguardando confirmação da associação.");
            }
        }
        public void Error(Exception error, bool command) {
            Failed=true; Confirmed=false; Message=error.Message;
            if(command) { commandError=Message; log.Write("COMMAND_ERROR",Message); }
            else {
                Current="Indisponível";
                if(lastPollError!=Message) log.Write("MONITOR_ERROR",Message);
                lastPollError=Message;
            }
        }
        public void Dispose() { wifi.Dispose(); }
    }

    internal sealed class ViewState {
        internal readonly bool Active,Confirmed,Failed;
        internal readonly string Current,Message;
        internal ViewState(Controller c) { Active=c.Active; Confirmed=c.Confirmed; Failed=c.Failed; Current=c.Current; Message=c.Message; }
        internal bool Same(ViewState other) { return other!=null && Active==other.Active && Confirmed==other.Confirmed && Failed==other.Failed && Current==other.Current && Message==other.Message; }
    }

    public sealed class ActionButton : Button {
        public bool Active;
        public ActionButton() { SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint,true); }
        internal void TestClick() { OnClick(EventArgs.Empty); }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Active ? Color.FromArgb(38, 51, 61) : Color.FromArgb(89, 229, 179);
            if (!Enabled) fill = Color.FromArgb(58, 80, 77);
            using (GraphicsPath path = MainForm.Round(new Rectangle(0,0,Width-1,Height-1),14))
            using (SolidBrush b = new SolidBrush(fill)) e.Graphics.FillPath(b,path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Active ? Color.White : Color.FromArgb(10,37,31), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(6,6,Width-12,Height-12));
        }
    }
    public sealed class MainForm : Form {
        readonly Controller controller;
        ViewState view;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        readonly ILog log;
        readonly FileLog fileLog;
        readonly float displayScale;
        readonly LinkLabel logLink=new LinkLabel();
        readonly ToolTip logTip=new ToolTip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        internal readonly ActionButton ToggleButton = new ActionButton();
        internal Task LastAction = Task.FromResult(0);
        bool commandPending, closing, canClose;
        readonly bool preview;
        readonly Font title = new Font("Segoe UI",25,FontStyle.Bold);
        readonly Font body = new Font("Segoe UI",11);
        readonly Font small = new Font("Segoe UI",9);
        readonly Font medium = new Font("Segoe UI",14,FontStyle.Bold);
        readonly Font mono = new Font("Consolas",12);
        static readonly Color Muted = Color.FromArgb(157,173,192);
        static readonly Color Green = Color.FromArgb(89,229,179);
        public MainForm(Controller state, bool renderOnly) : this(state,renderOnly,new NullLog()) { }
        public MainForm(Controller state, bool renderOnly, ILog logger) {
            controller = state; preview = renderOnly;
            view=new ViewState(state); log=logger; fileLog=logger as FileLog;
            using(Graphics screen=Graphics.FromHwnd(IntPtr.Zero)) displayScale=screen.DpiX/96f;
            Text = "WiFi AP 1.1"; ClientSize = Scaled(0,0,560,640).Size;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(14,22,33); ForeColor = Color.White;
            Font = body; DoubleBuffered = true; AutoScaleMode = AutoScaleMode.None;
            Icon = SystemIcons.Application;
            AccessibleDescription = "Ativar ou desativar a conexão ao ponto de acesso Wi-Fi escolhido.";
            ToggleButton.Bounds=Scaled(32,456,496,58); ToggleButton.Font = new Font("Segoe UI",12,FontStyle.Bold);
            ToggleButton.FlatStyle = FlatStyle.Flat; ToggleButton.FlatAppearance.BorderSize = 0;
            ToggleButton.Text = "Ativar bloqueio"; ToggleButton.Cursor = Cursors.Hand;
            ToggleButton.AccessibleName = "Ativar ou desativar bloqueio de AP";
            Controls.Add(ToggleButton);
            ToggleButton.Click += delegate { if(!closing && !commandPending) LastAction = Run(delegate { controller.Toggle(); },true); };
            logLink.Bounds=Scaled(413,590,115,25); logLink.Text="Abrir logs"; logLink.TextAlign=ContentAlignment.MiddleRight;
            logLink.LinkColor=Green; logLink.ActiveLinkColor=Color.White; logLink.VisitedLinkColor=Green;
            logLink.AccessibleName="Abrir pasta de logs"; logLink.Visible=fileLog!=null;
            logLink.LinkClicked += delegate {
                try { Directory.CreateDirectory(fileLog.DirectoryPath); Process.Start(new ProcessStartInfo(fileLog.DirectoryPath) { UseShellExecute=true }); }
                catch(Exception ex) { MessageBox.Show(this,ex.Message,"Logs",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            };
            Controls.Add(logLink);
            if(fileLog!=null) logTip.SetToolTip(logLink,fileLog.DirectoryPath);
            timer.Interval = 3000;
            timer.Tick += async delegate { await Run(delegate { controller.Poll(); },false); };
            Shown += async delegate { if (!preview) { await Run(delegate { controller.Poll(); },false); if(!closing && !IsDisposed) timer.Start(); } };
            FormClosing += OnClosing;
        }
        internal async Task Run(Action action,bool userAction) {
            // Timer ticks are skipped during another operation. User actions wait for
            // the read to finish instead of being silently discarded.
            if(!userAction && (closing || commandPending || !gate.Wait(0))) return;
            if(userAction) { commandPending=true; UpdateView(); await gate.WaitAsync(); }
            try {
                ViewState next=await Task.Run(delegate {
                    try { action(); } catch(Exception ex) { controller.Error(ex,userAction); }
                    return new ViewState(controller);
                });
                bool changed=!next.Same(view); view=next;
                if(changed && !IsDisposed) Invalidate(Scaled(32,134,496,302));
            } finally {
                gate.Release();
                if(userAction) commandPending=false;
                if(!IsDisposed) UpdateView();
            }
        }
        internal void UpdateView() {
            bool enabled=!commandPending && !closing;
            string text=commandPending ? "Aguarde..." : view.Active ? "Desativar bloqueio" : "Ativar bloqueio";
            bool changed=ToggleButton.Enabled!=enabled || ToggleButton.Active!=view.Active || ToggleButton.Text!=text;
            if(ToggleButton.Enabled!=enabled) ToggleButton.Enabled=enabled;
            ToggleButton.Active=view.Active;
            if(ToggleButton.Text!=text) ToggleButton.Text=text;
            if(changed) ToggleButton.Invalidate();
            if(fileLog!=null) {
                string label=fileLog.LastError==null?"Abrir logs":"Falha no log";
                if(logLink.Text!=label) { logLink.Text=label; logTip.SetToolTip(logLink,fileLog.LastError??fileLog.DirectoryPath); }
            }
        }
        async void OnClosing(object sender, FormClosingEventArgs e) {
            if (canClose || preview) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; timer.Stop(); UpdateView();
            log.Write("CLOSE_REQUEST","Liberação solicitada antes de encerrar.");
            await LastAction;
            await Run(delegate { controller.Release(); },true);
            if (view.Active) {
                closing = false; UpdateView(); timer.Start();
                MessageBox.Show(this,"Não foi possível liberar a conexão: " + view.Message + "\n\nTente desativar novamente. As reconexões automáticas estão suspensas.","WiFi AP",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            log.Write("CLOSE","Conexão liberada; aplicativo encerrando.");
            canClose = true; Close();
        }
        internal static GraphicsPath Round(Rectangle r, int radius) {
            int d = radius*2; GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90);
            p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
        }
        Rectangle Scaled(int x,int y,int w,int h) { return new Rectangle((int)Math.Round(x*displayScale),(int)Math.Round(y*displayScale),(int)Math.Round(w*displayScale),(int)Math.Round(h*displayScale)); }
        void DrawText(Graphics g,string text,Font font,Color color,int x,int y,int w,int h) {
            TextRenderer.DrawText(g,text,font,Scaled(x,y,w,h),color,TextFormatFlags.Left|TextFormatFlags.Top|TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); Graphics g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
            DrawText(g,"WiFi AP",title,Color.White,28,27,430,50);
            DrawText(g,"Seu notebook, no AP que você escolheu.",body,Muted,32,84,496,30);
            Color stateColor = view.Failed ? Color.FromArgb(255,178,111) : view.Confirmed ? Green : Muted;
            using(GraphicsPath p=Round(Scaled(32,134,496,126),(int)(18*displayScale)))
            using(SolidBrush b=new SolidBrush(Color.FromArgb(24,36,51))) g.FillPath(b,p);
            using(SolidBrush b=new SolidBrush(stateColor)) g.FillEllipse(b,Scaled(51,156,10,10));
            string stateText = view.Failed ? "Atenção" : view.Confirmed ? "AP confirmado" : view.Active ? "Buscando o AP" : "Bloqueio desativado";
            DrawText(g,stateText,medium,stateColor,72,147,424,32);
            DrawText(g,view.Message,small,Muted,51,190,450,62);
            DrawText(g,"REDE",small,Muted,32,286,100,20);
            DrawText(g,Settings.Profile,medium,Color.White,32,309,496,30);
            DrawText(g,"AP ESCOLHIDO",small,Muted,32,355,230,20);
            DrawText(g,"AP ATUAL",small,Muted,294,355,230,20);
            DrawText(g,Settings.Target,mono,Color.White,32,384,246,40);
            DrawText(g,view.Current,mono,view.Confirmed?Green:Color.White,294,384,240,44);
            DrawText(g,"Ao desativar ou fechar, o aplicativo solicita a conexão normal.\nUma breve reconexão pode ocorrer.",small,Muted,32,535,496,48);
            DrawText(g,"MONITOR DE AP  ·  v1.1",small,Muted,32,591,350,23);
        }
        protected override void Dispose(bool disposing) {
            if(disposing) { timer.Dispose(); logTip.Dispose(); controller.Dispose(); ToggleButton.Font.Dispose(); title.Dispose(); body.Dispose(); small.Dispose(); medium.Dispose(); mono.Dispose(); }
            base.Dispose(disposing);
        }
    }
    internal sealed class FakeWifi : IWifi {
        public Connection Current = new Connection { Connected=true,Profile=Settings.Profile,Bssid=Settings.Target };
        public int Requests, Attempts, ReadDelay; public string LastBssid, LastProfile; public bool Reject, RejectRead;
        public Connection Read() { if(ReadDelay>0) Thread.Sleep(ReadDelay); if(RejectRead) throw new Exception("Leitura indisponível"); return Current; }
        public void Request(string profile,string bssid) { Attempts++; if(Reject) throw new Exception("Falha simulada"); Requests++; LastBssid=bssid; LastProfile=profile; }
        public void Dispose() { }
    }
    internal sealed class MemoryLog : ILog {
        public readonly System.Collections.Generic.List<string> Lines=new System.Collections.Generic.List<string>();
        public void Write(string name,string detail) { Lines.Add(name+":"+detail); }
    }
    internal static class Program {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        static void Assert(bool condition,string name) { if(!condition) throw new Exception("FAIL: " + name); }
        static void Wait(Task task) {
            DateTime limit=DateTime.UtcNow.AddSeconds(10);
            while(!task.IsCompleted && DateTime.UtcNow<limit) { Application.DoEvents(); Thread.Sleep(5); }
            Assert(task.IsCompleted,"Async completion"); task.GetAwaiter().GetResult();
        }
        static void Tests(string report) {
            Assert(Marshal.SizeOf(typeof(NativeWifi.BssidList))==20,"BSSID native size");
            Assert(Marshal.OffsetOf(typeof(NativeWifi.BssidList),"Mac").ToInt32()==12,"MAC offset");
            Assert(Marshal.SizeOf(typeof(NativeWifi.ConnectionAttributes))==604,"Connection structure");
            IntPtr p=Marshal.AllocHGlobal(20);
            try { Marshal.StructureToPtr(NativeWifi.MakeList(Settings.Target),p,false); Assert(Marshal.ReadInt16(p,2)==20 && Marshal.ReadByte(p,17)==Convert.ToByte(Settings.Target.Split(':')[5],16),"Full six-byte MAC and header"); } finally { Marshal.FreeHGlobal(p); }
            bool invalid=false; try { NativeWifi.MakeList("1:2:3:4:5:6"); } catch(ArgumentException) { invalid=true; } Assert(invalid,"Malformed MAC rejected");
            TimeSpan now=TimeSpan.Zero; MemoryLog logger=new MemoryLog();
            FakeWifi wifi=new FakeWifi(); Controller c=new Controller(wifi,logger,delegate { return now; });
            c.Poll(); Assert(wifi.Requests==0,"Opening does not reconnect");
            c.Poll(); Assert(logger.Lines.Count==1,"No duplicate connection log");
            c.Toggle(); Assert(c.Active && !c.Confirmed && wifi.Requests==1,"Activation is pending");
            c.Poll(); Assert(c.Confirmed && wifi.Requests==1,"Confirmed target no repeat");
            wifi.Current.Bssid="00:11:22:33:44:55";
            c.Poll(); Assert(!c.Confirmed && wifi.Requests==1,"Grace period");
            now=TimeSpan.FromSeconds(31); c.Poll(); Assert(wifi.Requests==2 && wifi.LastBssid==Settings.Target,"Roam correction");
            wifi.Reject=true; now=TimeSpan.FromSeconds(62);
            try { c.Poll(); } catch(Exception ex) { c.Error(ex,false); }
            int attempts=wifi.Attempts; now=TimeSpan.FromSeconds(63); c.Poll(); Assert(wifi.Attempts==attempts,"Retry failures throttled");
            try { c.Release(); } catch(Exception ex) { c.Error(ex,true); } Assert(c.Active,"Failed release is not reported off");
            attempts=wifi.Attempts; now=TimeSpan.FromMinutes(5); c.Poll(); Assert(wifi.Attempts==attempts && c.Failed,"Failed release pauses retry and retains error");
            wifi.Reject=false; c.Release(); Assert(!c.Active && wifi.LastBssid==null,"Release clears desired BSSID");
            int count=wifi.Requests; c.Poll(); Assert(wifi.Requests==count,"Off never reconnects");
            c.Error(new Exception("Falha de consulta"),false); c.Error(new Exception("Falha de consulta"),false);
            Assert(logger.Lines.Count(x=>x=="MONITOR_ERROR:Falha de consulta")==1,"Duplicate monitor errors suppressed");
            c.Poll(); Assert(!c.Failed && c.Current!="Indisponível","Poll recovery clears old error");
            c.Toggle(); wifi.Current.Profile="Outra rede"; c.Release(); Assert(wifi.LastProfile=="Outra rede","Release preserves current network");
            wifi.Reject=true; try { c.Toggle(); } catch(Exception ex) { c.Error(ex,true); }
            c.Poll(); Assert(!c.Active && c.Failed,"Activation failure stays off and error persists");
            FakeWifi readWifi=new FakeWifi(); Controller readController=new Controller(readWifi);
            readWifi.RejectRead=true;
            try { readController.Poll(); } catch(Exception ex) { readController.Error(ex,false); }
            Assert(readController.Current=="Indisponível" && readController.Failed,"Read failure removes stale AP");
            readWifi.RejectRead=false; readController.Poll();
            Assert(!readController.Failed && readController.Message.Contains("desativado"),"Idle read recovery restores message");

            string logDirectory=Path.Combine(Path.GetDirectoryName(report),"log-test-"+Guid.NewGuid().ToString("N"));
            FileLog file=new FileLog(logDirectory,200);
            for(int i=0;i<30;i++) file.Write("TEST","Mensagem com acentuação "+i);
            Assert(file.LastError==null && Directory.GetFiles(logDirectory).Length==5,"Bounded rotation");
            Assert(File.ReadAllText(Path.Combine(logDirectory,"wifi-ap.log")).Contains("29"),"Latest event persisted");
            FileLog broken=new FileLog(Path.Combine(logDirectory,"wifi-ap.log","invalid"),200); broken.Write("TEST","Fail"); Assert(broken.LastError!=null,"Log failure does not throw");

            FakeWifi buttonWifi = new FakeWifi();
            Controller buttonController = new Controller(buttonWifi);
            using(MainForm f=new MainForm(buttonController,true)) {
                f.StartPosition=FormStartPosition.Manual; f.Location=new Point(-32000,-32000); f.ShowInTaskbar=false;
                f.Show(); Application.DoEvents();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                f.ToggleButton.TestClick(); Wait(f.LastAction);
                Assert(f.ToggleButton.Text=="Desativar bloqueio","Actual activate button: " + f.ToggleButton.Text + "; requests=" + buttonWifi.Requests + "; active=" + buttonController.Active + "; message=" + buttonController.Message);
                int textChanges=0,enabledChanges=0;
                f.ToggleButton.TextChanged+=delegate { textChanges++; };
                f.ToggleButton.EnabledChanged+=delegate { enabledChanges++; };
                for(int i=0;i<12;i++) Wait(f.Run(delegate { buttonController.Poll(); },false));
                Assert(textChanges==0 && enabledChanges==0,"Background polls never flicker button");
                buttonWifi.ReadDelay=200;
                Task poll=f.Run(delegate { buttonController.Poll(); },false);
                Assert(f.ToggleButton.Enabled && f.ToggleButton.Text=="Desativar bloqueio","Button remains available during polling");
                f.ToggleButton.TestClick(); Wait(f.LastAction); Wait(poll);
                Assert(f.ToggleButton.Text=="Ativar bloqueio","Actual deactivate button: " + f.ToggleButton.Text + "; requests=" + buttonWifi.Requests + "; active=" + buttonController.Active + "; message=" + buttonController.Message); f.Close();
            }
            FakeWifi closeWifi=new FakeWifi(); Controller closeController=new Controller(closeWifi); closeController.Toggle();
            using(MainForm f=new MainForm(closeController,false)) {
                f.StartPosition=FormStartPosition.Manual; f.Location=new Point(-32000,-32000); f.ShowInTaskbar=false;
                f.Show(); Application.DoEvents(); f.Close();
                DateTime limit=DateTime.UtcNow.AddSeconds(5);
                while(!f.IsDisposed && DateTime.UtcNow<limit) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(f.IsDisposed && !closeController.Active && closeWifi.LastBssid==null,"Window close releases target");
            }
            File.WriteAllText(report,"PASS: native layout; MAC validation; activation/confirmation; monotonic retry; throttled failure; failed release suspends retry; release preserves current profile; idle never reconnects; errors persist/recover correctly; log deduplication, rotation and IO failure; both UI actions; 12 polls cause zero button text/enabled changes; click during polling waits and executes; close releases target. No real Wi-Fi mutation.\r\n");
        }
        [STAThread] static int Main(string[] args) {
            SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try {
                if(args.Length==2 && args[0]=="--self-test") { Tests(args[1]); return 0; }
                if(args.Length==2 && (args[0]=="--preview" || args[0]=="--preview-active")) {
                    FileLog previewLog=new FileLog(Path.Combine(Path.GetDirectoryName(args[1]),"preview-logs"),1024*1024);
                    Controller c=new Controller(new FakeWifi()); c.Poll();
                    if(args[0]=="--preview-active") { c.Toggle(); c.Poll(); }
                    using(MainForm form=new MainForm(c,true,previewLog)) { form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-32000,-32000); form.ShowInTaskbar=false; form.Show(); form.UpdateView(); Application.DoEvents(); using(Bitmap b=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(b,new Rectangle(0,0,form.Width,form.Height)); b.Save(args[1]); } form.Close(); } return 0;
                }
                if(args.Length==2 && args[0]=="--diagnose") {
                    using(NativeWifi wifi=new NativeWifi()) { Connection c=wifi.Read(); File.WriteAllText(args[1],"Connected="+c.Connected+"; BSSID="+c.Bssid+"; Profile="+c.Profile); } return 0;
                }
                bool owner;
                using(Mutex mutex=new Mutex(true,"Local\\WiFiAP-SingleInstance",out owner)) {
                    if(!owner) { MessageBox.Show("O WiFi AP já está aberto.","WiFi AP"); return 0; }
                    FileLog logger=new FileLog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WiFiAP","Logs"),1024*1024);
                    logger.Write("START","version=1.1.0; interface="+Settings.InterfaceId+"; target="+Settings.Target+"; pollSeconds=3; retrySeconds=30");
                    try { using(MainForm form=new MainForm(new Controller(new NativeWifi(),logger,null),false,logger)) Application.Run(form); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            } catch(Exception ex) {
                if(args.Length==2) File.WriteAllText(args[1],ex.ToString());
                else MessageBox.Show(ex.Message,"WiFi AP",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
