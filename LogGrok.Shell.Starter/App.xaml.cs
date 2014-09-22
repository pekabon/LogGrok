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

namespace LogGrok.Shell.Starter
{
	public partial class App : Application
	{
		public App()
		{
			InitializeComponent();
		}

		protected override void OnStartup(StartupEventArgs _)
		{
			ShutdownMode = ShutdownMode.OnMainWindowClose;

			_bootstrapper.Run();
            Current.MainWindow.Show();

			var args = ApplicationDeployment.IsNetworkDeployed ?
				GetClickOnceArgs() :
				Environment.GetCommandLineArgs().Skip(1).ToArray();

			if (args.Length > 1)
				MessageBox.Show(string.Format("Unsupported args count use {0} <filename>", Process.GetCurrentProcess().MainModule.FileName));
			if (args.Length == 1)
				_bootstrapper.Container.Resolve<DocumentManager>().LoadNew(args[0]);
		}

		protected override void OnExit(ExitEventArgs _) 
        {
            Logger.FlushAll();   
        }

		private static string[] GetClickOnceArgs()
		{
			var activationData = AppDomain.CurrentDomain.SetupInformation.ActivationArguments.ActivationData;

			if (activationData == null)
				return new string[0];

			var argUri = new Uri(activationData[0]);

			if (ApplicationDeployment.CurrentDeployment.ActivationUri == argUri)
				return new string[0];

			return argUri.IsFile ? new[] { argUri.LocalPath } : new string[0];
		}

		private readonly Bootstrapper _bootstrapper = new Bootstrapper();

	}
}
