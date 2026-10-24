using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace vUSB_LAB
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private System.Threading.Mutex? _instance;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            UnhandledException += (_, args) => LogFailure(args.Exception);
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _instance = new System.Threading.Mutex(true, @"Local\vUSB-LAB", out bool firstInstance);
            if (!firstInstance)
            {
                System.Windows.Forms.MessageBox.Show("vUSB is already running in the notification area.", "vUSB LAB");
                _instance.Dispose();
                Exit();
                return;
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            try
            {
                _window = new MainWindow();
            }
            catch (Exception error)
            {
                string log = LogFailure(error);
                System.Windows.Forms.MessageBox.Show(
                    $"vUSB Lab could not start:\n\n{error.Message}\n\nDetails: {log}", "vUSB Lab startup error");
                Exit();
            }
        }

        private static string LogFailure(Exception error)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vUSB Lab", "Logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "startup.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{error}\nMessage: {error.Message}\n");
            return path;
        }
    }
}
