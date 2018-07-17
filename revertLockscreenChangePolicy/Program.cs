using System;
using System.Configuration.Install;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using Console = System.Console;

namespace revertLockscreenChangePolicy
{
    static class Program
    {
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;


            if (System.Environment.UserInteractive)
            {
                switch (string.Concat(args))
                {
                    case "--install":
                        ManagedInstallerClass.InstallHelper(new[] { Assembly.GetExecutingAssembly().Location });
                        break;
                    case "--uninstall":
                        ManagedInstallerClass.InstallHelper(new[] { "/u", Assembly.GetExecutingAssembly().Location });
                        break;

                    default:
                        Console.WriteLine("Use --install to install the service and --uninstall to uninstall it.");
                        break;
                }
            }
            else
            {
                var servicesToRun = new ServiceBase[]
                {
                    new RevertLockscreenChangePolicyService()
                };

                ServiceBase.Run(servicesToRun);
            }
        }

        static void CurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            File.AppendAllText(@"revertLockscreenChange.log", ((Exception)e.ExceptionObject).Message + ((Exception)e.ExceptionObject).GetBaseException().Message);
        }
    }
}
