using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Deployment.Application;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LogGrok.Core;
using LogGrok.Diagnostics;
using LogGrok.Shell;
using MahApps.Metro;
using Microsoft.Practices.Unity;
using System.Windows.Threading;

namespace LogGrok.Shell.Starter
{
	public partial class App : Application
	{
		public App()
		{
			InitializeComponent();
		}

        public void NextInstanceStarted(IEnumerable<string> args)
        {
            HandleCommandLine(args.ToArray());
            Activate();
        }

        public void Run(string[] args)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => HandleCommandLine(args)));
            Run();
        }

		protected override void OnStartup(StartupEventArgs _)
		{
			ShutdownMode = ShutdownMode.OnMainWindowClose;

			_bootstrapper.Run();
            Current.MainWindow.Show();
		}
        
		protected override void OnExit(ExitEventArgs _) 
        {
            Logger.FlushAll();   
        }

        private void Activate()
        {
            var window = Current.MainWindow;

            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }

        private void HandleCommandLine(string[] args)
        {
            if (args.Length > 1)
                MessageBox.Show(string.Format("Unsupported args count use {0} <filename>", Process.GetCurrentProcess().MainModule.FileName));
            if (args.Length == 1)
                _bootstrapper.Container.Resolve<DocumentManager>().LoadNew(args[0]);
        }

        private readonly Bootstrapper _bootstrapper = new Bootstrapper();
	}
}
