using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfImage = System.Windows.Controls.Image;
using WpfPoint = System.Windows.Point;

namespace RoommatePet
{
    internal static class Program
    {
        private const string MutexName = @"Local\RoommatePet.SingleInstance.v1";
        private const string ExitEventName = @"Local\RoommatePet.Exit.v1";

        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--exit", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var exitEvent = System.Threading.EventWaitHandle.OpenExisting(ExitEventName))
                        exitEvent.Set();
                }
                catch { }
                return;
            }

            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    System.Windows.MessageBox.Show("桌宠已经在运行了。\n如果找不到它，请查看屏幕右下角的系统托盘。", "室友桌宠", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var app = new WpfApplication();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                using (var exitEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ExitEventName))
                {
                    PetController controller = null;
                    app.Startup += delegate
                    {
                        controller = new PetController(app, exitEvent);
                        controller.Start();
                    };
                    app.Run();
                }
            }
        }
    }

    internal sealed class PetController
    {
        private readonly WpfApplication app;
        private readonly System.Threading.EventWaitHandle exitEvent;
        private readonly DispatcherTimer ticker;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Random random = new Random();
        private readonly SoundPlayer voicePlayer = new SoundPlayer();
        private readonly string[] poseNames = new string[] { "stand", "sit", "crawl" };
        private readonly string[] poseMessages = new string[] { "我站好了。", "坐一会儿，陪你。", "开始爬了。" };

        private PetWindow petWindow;
        private WinForms.NotifyIcon tray;
        private WinForms.ToolStripMenuItem pauseMenuItem;
        private WinForms.ToolStripMenuItem showMenuItem;
        private WinForms.ToolStripMenuItem topmostMenuItem;
        private ImageSource trayIcon;
        private double targetX;
        private double targetY;
        private DateTime targetPickedAt = DateTime.MinValue;
        private double targetIdleSeconds = 10.0;
        private string currentPose = "stand";

        private DateTime lastTick = DateTime.MinValue;
        private int poseIndex;
        private bool paused;
        private bool hidden;
        private bool exiting;
        private System.Threading.RegisteredWaitHandle exitRegistration;

        public PetController(WpfApplication application, System.Threading.EventWaitHandle exitSignal)
        {
            app = application;
            exitEvent = exitSignal;
            ticker = new DispatcherTimer(DispatcherPriority.Normal);
            ticker.Interval = TimeSpan.FromMilliseconds(33);
            ticker.Tick += OnTick;
        }

        public bool Paused { get { return paused; } }
        public bool Exiting { get { return exiting; } }

        public void Start()
        {
            exitRegistration = System.Threading.ThreadPool.RegisterWaitForSingleObject(exitEvent, delegate { app.Dispatcher.BeginInvoke(new Action(Exit)); }, null, System.Threading.Timeout.Infinite, false);
            petWindow = new PetWindow(this);
            petWindow.SetPose("stand");
            petWindow.Show();
            petWindow.PositionNearBottomRight();
            CreateTrayIcon();
            PickNewTarget();
            lastTick = DateTime.UtcNow;
            ticker.Start();
            petWindow.ShowSpeech("我来啦，今天也一起干活。", 3200);
            PlayVoice("startup");
        }

        private string AssetPath(string name)
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", name);
        }

        private ImageSource LoadTrayImage()
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(AssetPath("pet.ico"), UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void CreateTrayIcon()
        {
            try { trayIcon = LoadTrayImage(); } catch { trayIcon = null; }

            tray = new WinForms.NotifyIcon();
            tray.Text = "室友桌宠";
            tray.Visible = true;
            try { tray.Icon = new Drawing.Icon(AssetPath("pet.ico")); }
            catch { tray.Icon = Drawing.SystemIcons.Application; }

            var menu = new WinForms.ContextMenuStrip();
            pauseMenuItem = new WinForms.ToolStripMenuItem("暂停");
            pauseMenuItem.Click += delegate { TogglePause(); };
            menu.Items.Add(pauseMenuItem);

            showMenuItem = new WinForms.ToolStripMenuItem("隐藏桌宠");
            showMenuItem.Click += delegate { ToggleHidden(); };
            menu.Items.Add(showMenuItem);

            topmostMenuItem = new WinForms.ToolStripMenuItem("总在最前");
            topmostMenuItem.Checked = true;
            topmostMenuItem.CheckOnClick = true;
            topmostMenuItem.Click += delegate { ApplyTopmost(topmostMenuItem.Checked); };
            menu.Items.Add(topmostMenuItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());

            var actionItem = new WinForms.ToolStripMenuItem("切换站/坐/爬");
            actionItem.Click += delegate { CyclePose(); };
            menu.Items.Add(actionItem);

            var kneelItem = new WinForms.ToolStripMenuItem("喊爸爸");
            kneelItem.Click += delegate { ShoutDad(); };
            menu.Items.Add(kneelItem);

            var exitItem = new WinForms.ToolStripMenuItem("退出");
            exitItem.Click += delegate { Exit(); };
            menu.Items.Add(exitItem);

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ToggleHidden(); };
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (petWindow == null || exiting || paused || hidden || petWindow.IsDragging)
                return;

            var now = DateTime.UtcNow;
            var dt = Math.Max(0.0, Math.Min(0.08, (now - lastTick).TotalSeconds));
            lastTick = now;

            if (string.Equals(currentPose, "sit", StringComparison.Ordinal))
            {
                petWindow.SetMovementVisual(false, 0.0, clock.Elapsed.TotalSeconds);
                return;
            }

            var dx = targetX - petWindow.Left;
            var dy = targetY - petWindow.Top;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var moving = distance > 6.0;
            var elapsedSinceTarget = (now - targetPickedAt).TotalSeconds;

            if ((!moving && elapsedSinceTarget >= targetIdleSeconds) || (moving && elapsedSinceTarget > 60.0))
            {
                PickNewTarget();
                dx = targetX - petWindow.Left;
                dy = targetY - petWindow.Top;
                distance = Math.Sqrt(dx * dx + dy * dy);
                moving = distance > 6.0;
            }

            double directionX = 0.0;
            if (moving && distance > 0.01)
            {
                directionX = dx;
                var speed = string.Equals(currentPose, "crawl", StringComparison.Ordinal) ? 20.0 : 14.0;
                var step = Math.Min(distance, speed * dt);
                var nx = petWindow.Left + dx / distance * step;
                var ny = petWindow.Top + dy / distance * step;

                var area = SystemParameters.WorkArea;
                var bottomMaxY = area.Bottom - petWindow.Height - 8;
                var bottomMinY = Math.Max(area.Top, area.Bottom - petWindow.Height - 80);
                nx = Math.Max(area.Left, Math.Min(area.Right - petWindow.Width, nx));
                ny = Math.Max(bottomMinY, Math.Min(bottomMaxY, ny));
                petWindow.Left = nx;
                petWindow.Top = ny;
            }


            petWindow.SetMovementVisual(moving, directionX, clock.Elapsed.TotalSeconds);
        }

        private void PickNewTarget()
        {
            var area = SystemParameters.WorkArea;
            var maxX = Math.Max(area.Left, area.Right - petWindow.Width);
            var bottomY = area.Bottom - petWindow.Height;
            double x;
            var tries = 0;
            do
            {
                x = area.Left + random.NextDouble() * Math.Max(1.0, maxX - area.Left);
                tries++;
            }
            while (Math.Abs(x - petWindow.Left) < 180 && tries < 10);
            targetX = x;
            targetY = Math.Max(area.Top, bottomY - (string.Equals(currentPose, "crawl", StringComparison.Ordinal) ? random.Next(1, 16) : random.Next(10, 70)));
            targetPickedAt = DateTime.UtcNow;
            targetIdleSeconds = 12.0 + random.NextDouble() * 10.0;
        }

        public void TogglePause()
        {
            paused = !paused;
            if (pauseMenuItem != null)
                pauseMenuItem.Text = paused ? "继续" : "暂停";
            if (petWindow != null)
            {
                petWindow.SetPausedVisual(paused);
                petWindow.ShowSpeech(paused ? "我先休息一下。" : "继续陪你。", 2200);
                PlayVoice(paused ? "pause" : "resume");
            }
        }

        public void ToggleHidden()
        {
            if (petWindow == null)
                return;
            hidden = !hidden;
            if (hidden)
            {
                petWindow.Hide();
                if (showMenuItem != null) showMenuItem.Text = "显示桌宠";
            }
            else
            {
                petWindow.Show();
                petWindow.Activate();
                if (showMenuItem != null) showMenuItem.Text = "隐藏桌宠";
                lastTick = DateTime.UtcNow;
                petWindow.ShowSpeech("我回来了。", 1800);
                PlayVoice("return");
            }
        }

        public void ShowFromTray()
        {
            if (hidden) ToggleHidden();
        }

        public void ApplyTopmost(bool value)
        {
            if (petWindow != null)
                petWindow.Topmost = value;
            if (topmostMenuItem != null)
                topmostMenuItem.Checked = value;
        }

        public void CyclePose()
        {
            if (petWindow == null) return;
            poseIndex = (poseIndex + 1) % poseNames.Length;
            SetPose(poseNames[poseIndex]);
        }

        public void ShoutDad()
        {
            if (petWindow == null) return;
            petWindow.ShowSpeech("爸爸！", 1800);
            PlayVoice("dad");
        }

        private void SetPose(string pose)
        {
            currentPose = pose;
            petWindow.SetPose(pose);
            if (string.Equals(pose, "sit", StringComparison.Ordinal))
            {
                petWindow.ShowSpeech("坐一会儿，陪你。", 2200);
            }
            else if (string.Equals(pose, "crawl", StringComparison.Ordinal))
            {
                PickNewTarget();
                petWindow.ShowSpeech("开始爬了。", 2000);
            }
            else
            {
                petWindow.ShowSpeech("我站好了。", 2000);
            }
            PlayVoice(pose);
        }

        private void PlayVoice(string name)
        {
            try
            {
                voicePlayer.Stop();
                voicePlayer.SoundLocation = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "audio", name + ".wav");
                voicePlayer.Load();
                voicePlayer.Play();
            }
            catch { }
        }

        public void Exit()
        {
            if (exiting) return;
            exiting = true;
            if (exitRegistration != null)
            {
                exitRegistration.Unregister(null);
                exitRegistration = null;
            }
            ticker.Stop();
            if (petWindow != null)
            {
                petWindow.AllowClose();
                petWindow.Close();
            }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            if (trayIcon is IDisposable)
                ((IDisposable)trayIcon).Dispose();
            app.Shutdown();
        }

        public void OnDragged()
        {
            if (petWindow != null)
                petWindow.ShowSpeech("轻点放我下来。", 1500);
            PickNewTarget();
        }
    }

    internal sealed class PetWindow : Window
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyPause = 0x5101;
        private const int HotkeyHide = 0x5102;
        private const int HotkeyExit = 0x5103;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;

        private readonly PetController controller;
        private readonly WpfImage sprite;
        private readonly Border bubble;
        private readonly TextBlock bubbleText;
        private readonly DispatcherTimer bubbleTimer;
        private readonly ScaleTransform spriteScale = new ScaleTransform(1.0, 1.0);
        private readonly RotateTransform spriteRotate = new RotateTransform(0.0);
        private readonly TranslateTransform spriteTranslate = new TranslateTransform(0.0, 0.0);
        private readonly Dictionary<string, ImageSource> images = new Dictionary<string, ImageSource>();
        private HwndSource source;
        private IntPtr handle;
        private bool allowClose;
        private bool dragging;
        private string currentPose = "stand";

        public PetWindow(PetController controller)
        {
            this.controller = controller;
            Width = 232;
            Height = 302;
            MinWidth = Width;
            MinHeight = Height;
            MaxWidth = Width;
            MaxHeight = Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            bubbleText = new TextBlock
            {
                Foreground = new SolidColorBrush(WpfColor.FromRgb(35, 41, 54)),
                FontSize = 12.0,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            bubble = new Border
            {
                Child = bubbleText,
                Background = new SolidColorBrush(WpfColor.FromArgb(238, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(190, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(10, 0, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = 214,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.28, Color = WpfColor.FromRgb(31, 41, 55) }
            };
            Grid.SetRow(bubble, 0);
            root.Children.Add(bubble);

            var stage = new Grid { Margin = new Thickness(5, 0, 5, 2) };
            var shadow = new Ellipse
            {
                Width = 116,
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 8),
                Fill = new SolidColorBrush(WpfColor.FromArgb(55, 0, 0, 0)),
                Effect = new BlurEffect { Radius = 9 }
            };
            stage.Children.Add(shadow);

            sprite = new WpfImage
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 7),
                RenderTransformOrigin = new WpfPoint(0.5, 0.88)
            };
            var transforms = new TransformGroup();
            transforms.Children.Add(spriteScale);
            transforms.Children.Add(spriteRotate);
            transforms.Children.Add(spriteTranslate);
            sprite.RenderTransform = transforms;
            RenderOptions.SetBitmapScalingMode(sprite, BitmapScalingMode.HighQuality);
            stage.Children.Add(sprite);
            Grid.SetRow(stage, 1);
            root.Children.Add(stage);
            Content = root;

            CreateContextMenu();
            bubbleTimer = new DispatcherTimer();
            bubbleTimer.Interval = TimeSpan.FromMilliseconds(2800);
            bubbleTimer.Tick += delegate
            {
                bubbleTimer.Stop();
                bubble.Visibility = Visibility.Collapsed;
            };

            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (ContextMenu != null)
                {
                    ContextMenu.PlacementTarget = this;
                    ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    ContextMenu.IsOpen = true;
                    e.Handled = true;
                }
            };
        }

        public bool IsDragging { get { return dragging; } }

        public void PositionNearBottomRight()
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - Width - 28);
            Top = Math.Max(area.Top, area.Bottom - Height - 20);
        }

        private void CreateContextMenu()
        {
            var menu = new ContextMenu();
            var pause = new MenuItem { Header = "暂停 / 继续" };
            pause.Click += delegate { controller.TogglePause(); };
            menu.Items.Add(pause);

            var action = new MenuItem { Header = "切换站/坐/爬" };
            action.Click += delegate { controller.CyclePose(); };
            menu.Items.Add(action);

            var kneelNow = new MenuItem { Header = "喊爸爸" };
            kneelNow.Click += delegate { controller.ShoutDad(); };
            menu.Items.Add(kneelNow);

            var hide = new MenuItem { Header = "隐藏到托盘" };
            hide.Click += delegate { controller.ToggleHidden(); };
            menu.Items.Add(hide);

            var top = new MenuItem { Header = "保持置顶", IsCheckable = true, IsChecked = true };
            top.Click += delegate { controller.ApplyTopmost(top.IsChecked); };
            menu.Items.Add(top);

            menu.Items.Add(new Separator());
            var exit = new MenuItem { Header = "退出桌宠" };
            exit.Click += delegate { controller.Exit(); };
            menu.Items.Add(exit);
            ContextMenu = menu;
        }

        public void SetPose(string pose)
        {
            if (string.IsNullOrWhiteSpace(pose))
                return;
            currentPose = pose;
            ImageSource image;
            if (!images.TryGetValue(pose, out image))
            {
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", pose + ".png");
                if (!File.Exists(path))
                    path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "stand.png");
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
                images[pose] = image;
            }
            sprite.Source = image;
        }

        public void SetMovementVisual(bool moving, double directionX, double elapsedSeconds)
        {
            var bob = moving ? Math.Sin(elapsedSeconds * 6.2) * 2.4 : Math.Sin(elapsedSeconds * 2.0) * 1.0;
            var tilt = moving ? Math.Sin(elapsedSeconds * 6.2 + 0.7) * 2.2 : Math.Sin(elapsedSeconds * 1.6) * 0.55;
            spriteScale.ScaleX = directionX < 0 ? -1.0 : 1.0;
            spriteScale.ScaleY = 1.0 + (moving ? Math.Sin(elapsedSeconds * 12.4) * 0.008 : Math.Sin(elapsedSeconds * 3.0) * 0.004);
            spriteRotate.Angle = tilt;
            spriteTranslate.Y = bob;
        }

        public void SetPausedVisual(bool paused)
        {
            sprite.Opacity = paused ? 0.72 : 1.0;
            spriteRotate.Angle = paused ? -3.0 : 0.0;
            spriteTranslate.Y = paused ? 2.0 : 0.0;
        }

        public void ShowSpeech(string text, int milliseconds)
        {
            bubbleText.Text = text;
            bubble.Visibility = Visibility.Visible;
            bubbleTimer.Stop();
            bubbleTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            bubbleTimer.Start();
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                controller.CyclePose();
                e.Handled = true;
                return;
            }

            dragging = true;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                dragging = false;
                controller.OnDragged();
            }
            e.Handled = true;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle);
            if (source != null)
                source.AddHook(WndProc);
            RegisterHotKey(handle, HotkeyPause, ModControl | ModAlt | ModNoRepeat, 0x50);
            RegisterHotKey(handle, HotkeyHide, ModControl | ModAlt | ModNoRepeat, 0x48);
            RegisterHotKey(handle, HotkeyExit, ModControl | ModAlt | ModNoRepeat, 0x51);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey)
            {
                var id = wParam.ToInt32();
                if (id == HotkeyPause) controller.TogglePause();
                else if (id == HotkeyHide) controller.ToggleHidden();
                else if (id == HotkeyExit) controller.Exit();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void AllowClose()
        {
            allowClose = true;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!allowClose && !controller.Exiting)
                e.Cancel = true;
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (source != null)
                source.RemoveHook(WndProc);
            if (handle != IntPtr.Zero)
            {
                UnregisterHotKey(handle, HotkeyPause);
                UnregisterHotKey(handle, HotkeyHide);
                UnregisterHotKey(handle, HotkeyExit);
            }
            base.OnClosed(e);
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}


















