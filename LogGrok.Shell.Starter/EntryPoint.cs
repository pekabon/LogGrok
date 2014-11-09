using System;
using System.Deployment.Application;
using System.Linq;

namespace LogGrok.Shell.Starter
{
    static class EntryPoint
    {
	    [STAThread]
	    public static void Main(string[] args)
	    {
            SingleInstanceManager manager = new SingleInstanceManager();

            var arguments = ApplicationDeployment.IsNetworkDeployed ?
                GetClickOnceArgs() :
                args.ToArray();

            manager.Run(arguments);
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
    }
}
