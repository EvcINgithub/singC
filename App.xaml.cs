using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using singC.Helpers;
using singC.Models;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Dispatching;
// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace singC
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static Window window { get; private set; } = null!;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            AppSettings.Init();
            ConnectionViewModel.Initialize(DispatcherQueue.GetForCurrentThread());
            window = new MainWindow();
            window.Closed += OnMainWindowClosed;
            window.Activate();
            window.DispatcherQueue.TryEnqueue(InitializeBackgroundOnUiThread);
            AppUpdateService.Instance.Start(window.DispatcherQueue);
            window.DispatcherQueue.TryEnqueue(CompleteUpdateStartup);
        }

        private static async void CompleteUpdateStartup()
        {
            var arguments = Environment.GetCommandLineArgs();
            int markerIndex = Array.IndexOf(arguments, "--update-ready");
            try
            {
                if (markerIndex >= 0 && markerIndex + 1 < arguments.Length)
                {
                    var marker = Path.GetFullPath(arguments[markerIndex + 1]);
                    var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "singC", "updates") + Path.DirectorySeparatorChar;
                    if (marker.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(marker) == "app-ready")
                        File.WriteAllText(marker, "ready");
                }
                if (arguments.Contains("--resume-proxy")) await SingBoxService.Instance.StartAsync();
            }
            catch (Exception ex) { AppUpdateService.Instance.ReportStartupFailure(ex.Message); }
        }

        private static async void InitializeBackgroundOnUiThread()
        {
            try
            {
                await BackgroundManager.InitializeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"背景初始化失败，将使用默认背景: {ex.Message}");
            }
        }

        private async void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            // 应用关闭时，停止 sing-box 进程
            if (SingBoxService.Instance.State != SingBoxRuntimeState.Stopped)
            {
                await SingBoxService.Instance.StopAsync();
            }
        }
    }
}
