using System;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using RegistryUtils;

namespace revertLockscreenChangePolicy
{
    sealed class RevertLockscreenChangePolicyService : ServiceBase
    {
        const string SoftwarePoliciesMicrosoftWindowsPersonalization = "SOFTWARE\\Policies\\Microsoft\\Windows\\Personalization";

        readonly RegistryMonitor monitor;
        Timer timer;

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
                timer = new Timer(_ => OnRegChanged(null, null), null, 0, 300000);
                //OnRegChanged(this, null);
            }
            catch
            {
                //
            }

            monitor.Start();
        }

        protected override void OnStop()
        {
            timer.Dispose();
            monitor?.Stop();
            monitor?.Dispose();
        }

        static void OnRegChanged(object sender, EventArgs e)
        {
            var regKey = Registry.LocalMachine.OpenSubKey(SoftwarePoliciesMicrosoftWindowsPersonalization,
                RegistryKeyPermissionCheck.ReadWriteSubTree);

            var value = regKey?.GetValue("NoChangingLockScreen", null);
            if (value == null || value is int i && i == 0) return;

            regKey.SetValue("NoChangingLockScreen", 0);
            regKey.DeleteValue("NoChangingLockScreen");
            regKey.Close();
        }
    }
}
