using System;
using System.ServiceProcess;
using Microsoft.Win32;
using RegistryUtils;

namespace revertLockscreenChangePolicy
{
    sealed class RevertLockscreenChangePolicyService : ServiceBase
    {
        const string SoftwarePoliciesMicrosoftWindowsPersonalization = "SOFTWARE\\Policies\\Microsoft\\Windows\\Personalization";

        readonly RegistryMonitor monitor;

        public RevertLockscreenChangePolicyService()
        {
            monitor = new RegistryMonitor(RegistryHive.LocalMachine, SoftwarePoliciesMicrosoftWindowsPersonalization);
            monitor.RegChanged += OnRegChanged;

            ServiceName = "RevertLockscreenPolicy";
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                OnRegChanged(this, null);
            }
            catch
            {
                //
            }

            monitor.Start();
        }

        protected override void OnStop()
        {
            monitor?.Stop();
            monitor?.Dispose();
        }

        static void OnRegChanged(object sender, EventArgs e)
        {
            var regKey = Registry.LocalMachine.OpenSubKey(SoftwarePoliciesMicrosoftWindowsPersonalization,
                RegistryKeyPermissionCheck.ReadWriteSubTree);

            if (regKey != null)
            {
                regKey.SetValue("NoChangingLockScreen", 0);
                regKey.Close();
            }
        }
    }
}
