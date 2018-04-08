﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using YamlDotNet;
using YamlDotNet.Serialization;
using BitMiracle.LibTiff.Classic;
using System.Drawing;
using System.Drawing.Imaging;
using Amazon;
using Amazon.Runtime;
using YamlDotNet.RepresentationModel;
using Amazon.S3;
using Amazon.S3.IO;
using Amazon.S3.Model;
using System.IO.Pipes;
using System.Net;
using VAutodrive;
using System.Net.Sockets;
using System.Windows.Media.Imaging;
using GTAVisionUtils;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using GTA.Native;
using Color = System.Windows.Media.Color;
using System.Configuration;
using System.Threading;
using IniParser;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace GTAVisionExport {
    
    class VisionExport : Script
    {
#if DEBUG
        const string session_name = "NEW_DATA_CAPTURE_NATURAL_V4_3";
#else
        const string session_name = "NEW_DATA_CAPTURE_NATURAL_V4_3";
#endif
        //private readonly string dataPath =
        //    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Data");
        private readonly string dataPath = @"D:\archives\"; // location where zip files (that would contain all screenshots) will be saved. 
        //private readonly int[] times = { 13, 19, 6, 7, 22};
        //private readonly int[] times = { 13, 19};
        
            // wantedWeather contains the list for which weather conditions one would like to capture data.
        private readonly Weather[] wantedWeather = new Weather[] { Weather.Blizzard, Weather.Christmas, Weather.Clear, Weather.Clearing, Weather.Clouds, Weather.ExtraSunny, Weather.Foggy, Weather.Neutral, Weather.Overcast, Weather.Raining, Weather.Smog, Weather.Snowing, Weather.Snowlight, Weather.ThunderStorm};
        
            // timeOfDays is a dictionary which contains specific hours of day for which one would to capture data.
        private readonly IDictionary<string, int> timeOfDays = new Dictionary<string, int>();

        private Player player;
        private string outputPath;
        private GTARun run;
        private bool enabled = false;
        private Socket server;
        private Socket connection;
        private UTF8Encoding encoding = new UTF8Encoding(false);
        private KeyHandling kh = new KeyHandling();
        private ZipArchive archive;
        private Stream S3Stream;
        private AmazonS3Client client;
        private Task postgresTask;
        private Task runTask;
        private int curSessionId = -1;
        private speedAndTime lowSpeedTime = new speedAndTime();
               
        private void basicInit() {
            if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
            PostgresExport.InitSQLTypes();
            player = Game.Player;
            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, 5555));
            server.Listen(5);
            //server = new UdpClient(5555);
            var parser = new FileIniDataParser();
            var location = AppDomain.CurrentDomain.BaseDirectory;
            var data = parser.ReadFile(Path.Combine(location, "GTAVision.ini"));
            //var access_key = data["aws"]["access_key"];
            //var secret_key = data["aws"]["secret_key"];
            //client = new AmazonS3Client(new BasicAWSCredentials(access_key, secret_key), RegionEndpoint.USEast1);
            //outputPath = @"D:\Datasets\GTA\";
            //outputPath = Path.Combine(outputPath, "testData.yaml");
            //outStream = File.CreateText(outputPath);

            World.Weather = Weather.Clear;
            World.CurrentDayTime = new TimeSpan(14, 0, 0);

            timeOfDays.Add(new KeyValuePair<string, int>("Night1", 22)); // Night 1
            //timeOfDays.Add(new KeyValuePair<string, int>("Night2", 23)); // Night 2
            timeOfDays.Add(new KeyValuePair<string, int>("Morning1", 6)); // Morning 1
            timeOfDays.Add(new KeyValuePair<string, int>("Morning2", 7)); // Morning 2
            timeOfDays.Add(new KeyValuePair<string, int>("Afternoon", 13)); // Afternoon
            timeOfDays.Add(new KeyValuePair<string, int>("Evening", 19)); // Evening
            if (Game.Player.Character.IsInVehicle())
            {
                Vehicle v = Game.Player.Character.CurrentVehicle;
                v.Repair();
            }

            this.Tick += new EventHandler(this.OnTick);
            this.KeyDown += OnKeyDown;

            
            Interval = 3000; // this variable controls after how many milliseconds script will trigger onTick() method.
            if (enabled)
            {
                postgresTask?.Wait();
                postgresTask = StartSession();
                runTask?.Wait();
                runTask = StartRun();

            }

            

        }

        public VisionExport()
        {

            UI.Notify("Loaded VisionExport.cs");
            basicInit();

            //World.Weather = Weather.Snowing;
            //World.CurrentDayTime = new TimeSpan(6, 0, 0);


        }

        private void handlePipeInput()
        {
            //UI.Notify(server.Available.ToString());
            if (connection == null) return;
            
            byte[] inBuffer = new byte[1024];
            string str = "";
            int num = 0;
            try
            {
                num = connection.Receive(inBuffer);
                str = encoding.GetString(inBuffer, 0, num);
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.WouldBlock)
                {
                    return;
                }
                throw;
            }
            if (num == 0)
            {
                connection.Shutdown(SocketShutdown.Both);
                connection.Close();
                connection = null;
                return;
            }
            UI.Notify("I am here "+
                str.Length.ToString());
            switch (str)
            {
                case "START_SESSION":
                    postgresTask?.Wait();
                    postgresTask = StartSession();
                    runTask?.Wait();
                    runTask = StartRun();
                    break;
                case "STOP_SESSION":
                    StopRun();
                    StopSession();
                    break;
                case "TOGGLE_AUTODRIVE":
                    ToggleNavigation();
                    break;
                case "ENTER_VEHICLE":
                    UI.Notify("Trying to enter vehicle");
                    EnterVehicle();
                    break;
                case "AUTOSTART":
                    Autostart();
                    break;
                case "RELOADGAME":
                    ReloadGame();
                    break;
                case "RELOAD":
                    FieldInfo f = this.GetType().GetField("_scriptdomain", BindingFlags.NonPublic | BindingFlags.Instance);
                    object domain = f.GetValue(this);
                    MethodInfo m = domain.GetType()
                        .GetMethod("DoKeyboardMessage", BindingFlags.Instance | BindingFlags.Public);
                    m.Invoke(domain, new object[] {Keys.Insert, true, false, false, false});
                    break;
                case "GET_SCREEN":
                    var last = ImageUtils.getLastCapturedFrame();
                    Int64 size = last.Length;
                    size = IPAddress.HostToNetworkOrder(size);
                    connection.Send(BitConverter.GetBytes(size));
                    connection.Send(last);
                    break;

            }
        }

        private void UploadFile()
        {
            
            archive.Dispose();
            var oldOutput = outputPath;
            if (oldOutput != null)
            {
                new Thread(() =>
                {
                    File.Move(oldOutput, Path.Combine(dataPath, run.guid + ".zip"));
                }).Start();
            }
            
            outputPath = Path.GetTempFileName();
            S3Stream = File.Open(outputPath, FileMode.Truncate);
            archive = new ZipArchive(S3Stream, ZipArchiveMode.Update);
            //File.Delete(oldOutput);
            
            /*
            archive.Dispose();
            var req = new PutObjectRequest {
                BucketName = "gtadata",
                Key = "images/" + run.guid + ".zip",
                FilePath = outputPath
            };
            var resp = client.PutObjectAsync(req);
            outputPath = Path.GetTempFileName();
            S3Stream = File.Open(outputPath, FileMode.Truncate);
            archive = new ZipArchive(S3Stream, ZipArchiveMode.Update);
            
            await resp;
            File.Delete(req.FilePath);
            */
        }

        private void yieldScreen() {
            DateTime endtime;
            endtime = DateTime.UtcNow + new TimeSpan(0, 0, 0, 0, 500);

            while (endtime > DateTime.UtcNow)
            {
                Script.Yield();
            }
        }

        private List<byte[]> captureWeatherAndTime() {
            
            List<byte[]> colors = new List<byte[]>();
            for (int w = 0; w < wantedWeather.Length; w++)
            {
                World.Weather = wantedWeather[w];

                foreach (KeyValuePair<string, int> item in timeOfDays)
                {
                   
                    World.CurrentDayTime = new TimeSpan(item.Value, 0, 0);
                    yieldScreen();

                    colors.Add(VisionNative.GetColorBuffer());
                    
                }
            }
            Function.Call(GTA.Native.Hash._SET_RAIN_FX_INTENSITY, 0.0f);
            return colors;
        }

        public void OnTick(object o, EventArgs e)
        {
            if (server.Poll(10, SelectMode.SelectRead) && connection == null)
            {
                connection = server.Accept();
                UI.Notify("CONNECTED");
                connection.Blocking = false;
            }
            //handlePipeInput();
            if (!enabled) return;

            //Array values = Enum.GetValues(typeof(Weather));

            switch (checkStatus()) {
                case GameStatus.NeedReload:
                    //TODO: need to get a new session and run?
                    StopRun();
                    runTask?.Wait();
                    runTask = StartRun();
                    //StopSession();
                    //Autostart();
                    UI.Notify("need reload game");
                    Script.Wait(100);
                    ReloadGame();
                    break;
                case GameStatus.NeedStart:
                    //TODO do the autostart manually or automatically?
                    //Autostart();
                    // use reloading temporarily
                    StopRun();
                    
                    ReloadGame();
                    Script.Wait(100);
                    runTask?.Wait();
                    runTask = StartRun();
                    //Autostart();
                    break;
                case GameStatus.NoActionNeeded:
                    break;
            }
            if (!runTask.IsCompleted) return;
            if (!postgresTask.IsCompleted) return;
            
            List<byte[]> colors;
            Game.Pause(true);
            //UI.Notify("Pause Start "+ Game.GameTime.ToString());
            Script.Wait(500);
            
            //GTAData dat = GTAData.DumpData(Game.GameTime + ".tiff", new List<Weather>());
            GTAData dat = GTAData.DumpData(Game.GameTime + ".tiff", wantedWeather.ToList());
            //GTAData dat = null;
            if (dat == null) return;
            var thisframe = VisionNative.GetCurrentTime();
            var depth = VisionNative.GetDepthBuffer();
            var stencil = VisionNative.GetStencilBuffer();
            //colors.Add(VisionNative.GetColorBuffer());
            colors = captureWeatherAndTime();
            /*foreach (var wea in wantedWeather) {
                World.TransitionToWeather(wea, 0.0f);
                Script.Wait(1);
                colors.Add(VisionNative.GetColorBuffer());
            }*/
            Game.Pause(false);
            //UI.Notify("Pause Ended " + Game.GameTime.ToString());


            /*if (World.Weather != Weather.Snowing)
            {
                World.TransitionToWeather(Weather.Snowing, 1);
                
            }*/
            //var colorframe = VisionNative.GetLastColorTime();
            //var depthframe = VisionNative.GetLastConstantTime();
            //var constantframe = VisionNative.GetLastConstantTime();
            //UI.Notify("DIFF: " + (colorframe - depthframe) + " FRAMETIME: " + (1 / Game.FPS) * 1000);
            //UI.Notify(colors[0].Length.ToString());
            if (depth == null || stencil == null)
            {
                UI.Notify("No DEPTH");
                return;
            }

            /*
             * this code checks to see if there's drift
             * it's kinda pointless because we end up "straddling" a present call,
             * so the capture time difference can be ~1/4th of a frame but still the
             * depth/stencil and color buffers are one frame offset from each other
            if (Math.Abs(thisframe - colorframe) < 60 && Math.Abs(colorframe - depthframe) < 60 &&
                Math.Abs(colorframe - constantframe) < 60)
            {
                



                
                PostgresExport.SaveSnapshot(dat, run.guid);
            }
            */
            ImageUtils.WaitForProcessing();
            ImageUtils.StartUploadTask(archive, Game.GameTime.ToString(), Game.ScreenResolution.Width,
                Game.ScreenResolution.Height, colors, depth, stencil);
            
            PostgresExport.SaveSnapshot(dat, run.guid);
            S3Stream.Flush();
            if ((Int64)S3Stream.Length > (Int64)2048 * (Int64)1024 * (Int64)1024) { // if length exceeds 2GBs save file
                ImageUtils.WaitForProcessing();
                StopRun();
                runTask?.Wait();
                runTask = StartRun();
            }
        }

        /* -1 = need restart, 0 = normal, 1 = need to enter vehicle */
        public GameStatus checkStatus()
        {
            Ped player = Game.Player.Character;
            if (player.IsDead) return GameStatus.NeedReload;
            if (player.IsInVehicle())
            {
                Vehicle vehicle = player.CurrentVehicle;
                //UI.Notify("T:" + Game.GameTime.ToString() + " S: " + vehicle.Speed.ToString());
                if (vehicle.Speed < 1.0f) //speed is in mph
                {
                    if (lowSpeedTime.checkTrafficJam(Game.GameTime, vehicle.Speed))
                    {
                        UI.Notify("Traffic is jam");
                        return GameStatus.NeedReload;
                    }
                }
                else
                {
                    lowSpeedTime.clearTime();
                }
                return GameStatus.NoActionNeeded;
            }
            else
            {
                UI.Notify("player is not in vehicle");
                return GameStatus.NeedReload;
            }
        }

        public Bitmap CaptureScreen()
        {
            var cap = new Bitmap(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
            var gfx = Graphics.FromImage(cap);
            //var dat = GTAData.DumpData(Game.GameTime + ".jpg");
            gfx.CopyFromScreen(0, 0, 0, 0, cap.Size);
            /*
            foreach (var ped in dat.ClosestPeds) {
                var w = ped.ScreenBBMax.X - ped.ScreenBBMin.X;
                var h = ped.ScreenBBMax.Y - ped.ScreenBBMin.Y;
                var x = ped.ScreenBBMin.X;
                var y = ped.ScreenBBMin.Y;
                w *= cap.Size.Width;
                h *= cap.Size.Height;
                x *= cap.Size.Width;
                y *= cap.Size.Height;
                gfx.DrawRectangle(new Pen(Color.Lime), x, y, w, h);
            } */
            return cap;
            //cap.Save(GetFileName(".png"), ImageFormat.Png);

        }

        public void Autostart()
        {
            UI.Notify("Autostart()");
            EnterVehicle();
            Script.Wait(200);
            ToggleNavigation();
            Script.Wait(200);
            postgresTask?.Wait();
            postgresTask = StartSession();
        }

        public async Task StartSession(string name = session_name)
        {
            if (name == null) name = Guid.NewGuid().ToString();
            if (curSessionId != -1) StopSession(); // makes sure that there is only one session at a time.
            int id = await PostgresExport.StartSession(name);
            curSessionId = id;
        }

        public void StopSession()
        {
            if (curSessionId == -1) return;
            PostgresExport.StopSession(curSessionId);
            curSessionId = -1;
        }
        public async Task StartRun()
        {
            await postgresTask;
            if(run != null) PostgresExport.StopRun(run);
            var runid = await PostgresExport.StartRun(curSessionId);

            //var s3Info = new S3FileInfo(client, "gtadata", run.archiveKey);
            //S3Stream = s3Info.Create();
            
            outputPath = Path.GetTempFileName();
            S3Stream = File.Open(outputPath, FileMode.Truncate);
            archive = new ZipArchive(S3Stream, ZipArchiveMode.Create);
            
            //archive = new ZipArchive(, ZipArchiveMode.Create);
            
            //archive = ZipFile.Open(Path.Combine(dataPath, run.guid + ".zip"), ZipArchiveMode.Create);
            

            run = runid;
            enabled = true;
        }

        public void StopRun()
        {
            runTask?.Wait();
            ImageUtils.WaitForProcessing();
            if (S3Stream.CanWrite)
            {
                S3Stream.Flush();
            }
            enabled = false;
            PostgresExport.StopRun(run);
            UploadFile();
            run = null;
            
            Game.Player.LastVehicle.Alpha = int.MaxValue;
        }

        public void EnterVehicle()
        {
            /*
            var vehicle = World.GetClosestVehicle(player.Character.Position, 30f);
            player.Character.SetIntoVehicle(vehicle, VehicleSeat.Driver);
            */
            //UI.Notify("EnterVehicle");
            //Script.Wait(500);
            Model mod = new Model(GTA.Native.VehicleHash.Asea);
            var vehicle = GTA.World.CreateVehicle(mod, player.Character.Position);
            
            player.Character.SetIntoVehicle(vehicle, VehicleSeat.Driver);
           
            /*World.DestroyAllCameras();
            myCamera = World.CreateCamera(new Vector3(), new Vector3(), 50);
            myCamera.IsActive = true;
            //GTA.Native.Function.Call(Hash.RENDER_SCRIPT_CAMS, false, true, myCamera.Handle, true, true);
            GTA.Native.Function.Call(Hash.RENDER_SCRIPT_CAMS, false, true, 0, true, true);

            while (!player.Character.IsInVehicle())
                Script.Yield();
            //UI.Notify("Yielding done");
            if (player.Character.IsInVehicle())
            {
                //GTA.Native.Function.Call(Hash.RENDER_SCRIPT_CAMS, true, true, myCamera.Handle, true, true);
                GTA.Native.Function.Call(Hash.RENDER_SCRIPT_CAMS, true, true, 0, true, true);
            }*/

            //UI.Notify("After Wait");
            //vehicle.Alpha = 0; //transparent
            //player.Character.Alpha = 0;
        }

        public void ToggleNavigation()
        {
            //YOLO
            MethodInfo inf = kh.GetType().GetMethod("AtToggleAutopilot", BindingFlags.NonPublic | BindingFlags.Instance);
            inf.Invoke(kh, new object[] {new KeyEventArgs(Keys.J)});
        }

        public void ReloadGame()
        {
            /*
            Process p = Process.GetProcessesByName("Grand Theft Auto V").FirstOrDefault();
            if (p != null)
            {
                IntPtr h = p.MainWindowHandle;
                SetForegroundWindow(h);
                SendKeys.SendWait("{ESC}");
                //Script.Wait(200);
            }
            */
            // or use CLEAR_AREA_OF_VEHICLES
            Ped player = Game.Player.Character;
            //UI.Notify("x = " + player.Position.X + "y = " + player.Position.Y + "z = " + player.Position.Z);
            // no need to release the autodrive here
            // delete all surrounding vehicles & the driver's car
            Function.Call(GTA.Native.Hash.CLEAR_AREA_OF_VEHICLES, player.Position.X, player.Position.Y, player.Position.Z, 1000f, false, false, false, false);
            player.LastVehicle.Delete();
            // teleport to the spawning position, defined in GameUtils.cs, subject to changes
            player.Position = GTAConst.StartPos;
            Function.Call(GTA.Native.Hash.CLEAR_AREA_OF_VEHICLES, player.Position.X, player.Position.Y, player.Position.Z, 100f, false, false, false, false);
            // start a new run
            EnterVehicle();
            //Script.Wait(2000);
            ToggleNavigation();

            lowSpeedTime.clearTime();

        }

        public void TraverseWeather()
        {
            for (int i = 1; i < 14; i++)
            {
                //World.Weather = (Weather)i;
                World.TransitionToWeather((Weather)i, 0.0f);
                //Script.Wait(1000);
            }
        }

        public void OnKeyDown(object o, KeyEventArgs k)
        {
            if (k.KeyCode == Keys.PageUp)
            {
                postgresTask?.Wait();
                postgresTask = StartSession();

                
                runTask?.Wait();

                
                runTask = StartRun();
                UI.Notify("GTA Vision Enabled");
            }
            if (k.KeyCode == Keys.PageDown)
            {
                UI.Notify("Page down pressed");
                StopRun();
                StopSession();

                UI.Notify("GTA Vision Disabled");
            }
            if (k.KeyCode == Keys.H) // temp modification
            {
                EnterVehicle();
                UI.Notify("Trying to enter vehicle");
                ToggleNavigation();
            }
            if (k.KeyCode == Keys.Y) // temp modification
            {
                ReloadGame();
            }
            if (k.KeyCode == Keys.U) // temp modification
            {
                var settings = ScriptSettings.Load("GTAVisionExport.xml");
                var loc = AppDomain.CurrentDomain.BaseDirectory;

                //UI.Notify(ConfigurationManager.AppSettings["database_connection"]);
                var str = settings.GetValue("", "ConnectionString");
                UI.Notify(loc);

            }
            if (k.KeyCode == Keys.G) // temp modification
            {
                var data = GTAData.DumpData(Game.GameTime + ".tiff", new List<Weather>(wantedWeather));

                string path = @"C:\Users\NGV-02\Documents\Data\trymatrix.txt";
                // This text is added only once to the file.
                if (!File.Exists(path))
                {
                    // Create a file to write to.
                    using (StreamWriter file = File.CreateText(path))
                    {
                        
                        
                        file.WriteLine("cam direction file");
                        file.WriteLine("direction:");
                        file.WriteLine(GameplayCamera.Direction.X.ToString() + ' ' + GameplayCamera.Direction.Y.ToString() + ' ' + GameplayCamera.Direction.Z.ToString());
                        file.WriteLine("Dot Product:");
                        file.WriteLine(Vector3.Dot(GameplayCamera.Direction, GameplayCamera.Rotation));
                        file.WriteLine("position:");
                        file.WriteLine(GameplayCamera.Position.X.ToString() + ' ' + GameplayCamera.Position.Y.ToString() + ' ' + GameplayCamera.Position.Z.ToString());
                        file.WriteLine("rotation:");
                        file.WriteLine(GameplayCamera.Rotation.X.ToString() + ' ' + GameplayCamera.Rotation.Y.ToString() + ' ' + GameplayCamera.Rotation.Z.ToString());
                        file.WriteLine("relative heading:");
                        file.WriteLine(GameplayCamera.RelativeHeading.ToString());
                        file.WriteLine("relative pitch:");
                        file.WriteLine(GameplayCamera.RelativePitch.ToString());
                        file.WriteLine("fov:");
                        file.WriteLine(GameplayCamera.FieldOfView.ToString());
                    }
                }
            }

            if (k.KeyCode == Keys.T) // temp modification
            {
                //World.Weather = Weather.Raining;
                /* set it between 0 = stop, 1 = heavy rain. set it too high will lead to sloppy ground */
                
                //var test = Function.Call<float>(GTA.Native.Hash.GET_RAIN_LEVEL);
                //UI.Notify(World.Weather.ToString()+"  = " + test);
                //test += 0.1f;
                //Function.Call(GTA.Native.Hash._SET_RAIN_FX_INTENSITY, test);
                //World.CurrentDayTime = new TimeSpan(12, 0, 0);
                //Script.Wait(5000);
            }

            if (k.KeyCode == Keys.N)
            {
                /*
                //var color = VisionNative.GetColorBuffer();
                
                List<byte[]> colors = new List<byte[]>();
                Game.Pause(true);
                Script.Wait(1);
                var depth = VisionNative.GetDepthBuffer();
                var stencil = VisionNative.GetStencilBuffer();
                foreach (var wea in wantedWeather) {
                    World.TransitionToWeather(wea, 0.0f);
                    Script.Wait(1);
                    colors.Add(VisionNative.GetColorBuffer());
                }
                Game.Pause(false);
                if (depth != null)
                {
                    var res = Game.ScreenResolution;
                    var t = Tiff.Open(Path.Combine(dataPath, "test.tiff"), "w");
                    ImageUtils.WriteToTiff(t, res.Width, res.Height, colors, depth, stencil);
                    t.Close();
                    UI.Notify(GameplayCamera.FieldOfView.ToString());
                }
                else
                {
                    UI.Notify("No Depth Data quite yet");
                }
                //UI.Notify((connection != null && connection.Connected).ToString());
                */
                //var color = VisionNative.GetColorBuffer();
                for (int i = 0; i < 100; i++)
                {
                    List<byte[]> colors = new List<byte[]>();
                    Game.Pause(true);
                    var depth = VisionNative.GetDepthBuffer();
                    var stencil = VisionNative.GetStencilBuffer();
                    foreach (var wea in wantedWeather)
                    {
                        World.TransitionToWeather(wea, 0.0f);
                        Script.Wait(1);
                        colors.Add(VisionNative.GetColorBuffer());
                    }

                    Game.Pause(false);
                    var res = Game.ScreenResolution;
                    var t = Tiff.Open(Path.Combine(dataPath, "info" + i.ToString() + ".tiff"), "w");
                    ImageUtils.WriteToTiff(t, res.Width, res.Height, colors, depth, stencil);
                    t.Close();
                    UI.Notify(GameplayCamera.FieldOfView.ToString());
                    //UI.Notify((connection != null && connection.Connected).ToString());


                    var data = GTAData.DumpData(Game.GameTime + ".dat", new List<Weather>(wantedWeather));

                    string path = @"C:\Users\NGV-02\Documents\Data\info.txt";
                    // This text is added only once to the file.
                    if (!File.Exists(path))
                    {
                        // Create a file to write to.
                        using (StreamWriter file = File.CreateText(path))
                        {
                            file.WriteLine("cam direction & Ped pos file");
                        }
                    }

                    using (StreamWriter file = File.AppendText(path))
                    {
                        file.WriteLine("==============info" + i.ToString() + ".tiff 's metadata=======================");
                        file.WriteLine("cam pos");
                        file.WriteLine(GameplayCamera.Position.X.ToString());
                        file.WriteLine(GameplayCamera.Position.Y.ToString());
                        file.WriteLine(GameplayCamera.Position.Z.ToString());
                        file.WriteLine("cam direction");
                        file.WriteLine(GameplayCamera.Direction.X.ToString());
                        file.WriteLine(GameplayCamera.Direction.Y.ToString());
                        file.WriteLine(GameplayCamera.Direction.Z.ToString());
                        file.WriteLine("character");
                        file.WriteLine(data.Pos.X.ToString());
                        file.WriteLine(data.Pos.Y.ToString());
                        file.WriteLine(data.Pos.Z.ToString());
                        foreach (var detection in data.Detections)
                        {
                            file.WriteLine(detection.Type.ToString());
                            file.WriteLine(detection.Pos.X.ToString());
                            file.WriteLine(detection.Pos.Y.ToString());
                            file.WriteLine(detection.Pos.Z.ToString());
                        }
                    }

                    Script.Wait(200);
                }
        }
            if (k.KeyCode == Keys.I)
            {
                //UI.Notify(World.CurrentDate.ToString());
                //var test = Function.Call<float>(GTA.Native.Hash.GET_SNOW_LEVEL);
                //UI.Notify(World.Weather.ToString() + " snow = " + test);
                //UI.ShowSubtitle("Month = "+World.CurrentDate.Month.ToString()+ " Date = " + World.CurrentDate.Day.ToString()+" Time = "+World.CurrentDayTime.Hours.ToString());
                //UI.Notify();
              
                /*World.Weather = Weather.Snowlight;
                World.CurrentDayTime = new TimeSpan(23, 0, 0);
                /*World.CurrentDayTime = new TimeSpan(0, 0, 0);
                Script.Wait(500);
                World.TransitionToWeather(wantedWeather[1], 0.0f);
                Script.Wait(500);*/
            }
        }
    }
}
