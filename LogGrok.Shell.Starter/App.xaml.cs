using System;
using System.Deployment.Application;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Custom.Windows;
using LogGrok.Core;
using LogGrok.Diagnostics;
using Microsoft.Practices.Unity;

namespace LogGrok.Shell.Starter
{
	public partial class App
	{
		public App()
			: base(ApplicationInstanceAwareness.Host)

		{
			InitializeComponent();
		}

		private void ProcessArgs(string[] args)
		{
			if (args.Length > 1)
				MessageBox.Show(string.Format("Unsupported args count use {0} <filename>",
					Process.GetCurrentProcess().MainModule.FileName));
			if (args.Length == 1)
				_bootstrapper.Container.Resolve<DocumentManager>().LoadNew(args[0]);
		}

		protected override void OnStartup(StartupEventArgs e, bool? isFirstInstance)
		{

			if (!isFirstInstance.GetValueOrDefault(false))
			{
				Shutdown();
				return;
			}

			StartupNextInstance += (_, arguments)
				=>
			{
				ProcessArgs(arguments.Args.Skip(1).ToArray());
				arguments.BringToForeground = true;
			};

			ShutdownMode = ShutdownMode.OnMainWindowClose;

			_bootstrapper.Run();
			Current.MainWindow.Show();

			var args = ApplicationDeployment.IsNetworkDeployed
				? GetClickOnceArgs()
				: Environment.GetCommandLineArgs().Skip(1).ToArray();
			
			ProcessArgs(args);
		}

		protected override void OnExit(ExitEventArgs e, bool isFirstInstance)
		{
			base.OnExit(e, isFirstInstance);
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
