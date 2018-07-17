using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RegistryUtils
{
    /// <summary>
    /// <b>RegistryMonitor</b> allows you to monitor specific registry key.
    /// </summary>
    /// <remarks>
    /// If a monitored registry key changes, an event is fired. You can subscribe to these
    /// events by adding a delegate to <see cref="RegChanged"/>.
    /// <para>The Windows API provides a function
    /// <a href="http://msdn.microsoft.com/library/en-us/sysinfo/base/regnotifychangekeyvalue.asp">
    /// RegNotifyChangeKeyValue</a>, which is not covered by the
    /// <see cref="Microsoft.Win32.RegistryKey"/> class. <see cref="RegistryMonitor"/> imports
    /// that function and encapsulates it in a convenient manner.
    /// </para>
    /// </remarks>
    /// <example>
    /// This sample shows how to monitor <c>HKEY_CURRENT_USER\Environment</c> for changes:
    /// <code>
    /// public class MonitorSample
    /// {
    ///     static void Main() 
    ///     {
    ///         RegistryMonitor monitor = new RegistryMonitor(RegistryHive.CurrentUser, "Environment");
    ///         monitor.RegChanged += new EventHandler(OnRegChanged);
    ///         monitor.Start();
    ///
    ///         while(true);
    /// 
    ///         monitor.Stop();
    ///     }
    ///
    ///     private void OnRegChanged(object sender, EventArgs e)
    ///     {
    ///         Console.WriteLine("registry key has changed");
    ///     }
    /// }
    /// </code>
    /// </example>
    public class RegistryMonitor : IDisposable
    {
        #region P/Invoke

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegOpenKeyEx(IntPtr hKey, string subKey, uint options, int samDesired,
                                               out IntPtr phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool bWatchSubtree,
                                                          RegChangeNotifyFilter dwNotifyFilter, IntPtr hEvent,
                                                          bool fAsynchronous);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegCloseKey(IntPtr hKey);

        const int Key_Query_Value = 0x0001;
        const int Key_Notify = 0x0010;
        const int Standard_Rights_Read = 0x00020000;

        static readonly IntPtr HkeyClassesRoot = new IntPtr(unchecked((int) 0x80000000));
        static readonly IntPtr HkeyCurrentUser = new IntPtr(unchecked((int) 0x80000001));
        static readonly IntPtr HkeyLocalMachine = new IntPtr(unchecked((int) 0x80000002));
        static readonly IntPtr HkeyUsers = new IntPtr(unchecked((int) 0x80000003));
        static readonly IntPtr HkeyPerformanceData = new IntPtr(unchecked((int) 0x80000004));
        static readonly IntPtr HkeyCurrentConfig = new IntPtr(unchecked((int) 0x80000005));
        static readonly IntPtr HkeyDynData = new IntPtr(unchecked((int) 0x80000006));

        #endregion

        #region Event handling

        /// <summary>
        /// Occurs when the specified registry key has changed.
        /// </summary>
        public event EventHandler RegChanged;
        
        /// <summary>
        /// Raises the <see cref="RegChanged"/> event.
        /// </summary>
        /// <remarks>
        /// <p>
        /// <b>OnRegChanged</b> is called when the specified registry key has changed.
        /// </p>
        /// <note type="inheritinfo">
        /// When overriding <see cref="OnRegChanged"/> in a derived class, be sure to call
        /// the base class's <see cref="OnRegChanged"/> method.
        /// </note>
        /// </remarks>
        protected virtual void OnRegChanged()
        {
            var handler = RegChanged;
            handler?.Invoke(this, null);
        }

        /// <summary>
        /// Occurs when the access to the registry fails.
        /// </summary>
        public event ErrorEventHandler Error;
        
        /// <summary>
        /// Raises the <see cref="Error"/> event.
        /// </summary>
        /// <param name="e">The <see cref="Exception"/> which occured while watching the registry.</param>
        /// <remarks>
        /// <p>
        /// <b>OnError</b> is called when an exception occurs while watching the registry.
        /// </p>
        /// <note type="inheritinfo">
        /// When overriding <see cref="OnError"/> in a derived class, be sure to call
        /// the base class's <see cref="OnError"/> method.
        /// </note>
        /// </remarks>
        protected virtual void OnError(Exception e)
        {
            var handler = Error;
            handler?.Invoke(this, new ErrorEventArgs(e));
        }

        #endregion

        #region Private member variables

        IntPtr registryHive;
        string registrySubName;
        readonly object threadLock = new object();
        Thread thread;
        bool disposed;
        readonly ManualResetEvent eventTerminate = new ManualResetEvent(false);

        RegChangeNotifyFilter regFilter = RegChangeNotifyFilter.Key | RegChangeNotifyFilter.Attribute |
                                                   RegChangeNotifyFilter.Value | RegChangeNotifyFilter.Security;

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="RegistryMonitor"/> class.
        /// </summary>
        /// <param name="registryKey">The registry key to monitor.</param>
        public RegistryMonitor(RegistryKey registryKey)
        {
            InitRegistryKey(registryKey.Name);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RegistryMonitor"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        public RegistryMonitor(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            InitRegistryKey(name);
        }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="RegistryMonitor"/> class.
        /// </summary>
        /// <param name="registryHive">The registry hive.</param>
        /// <param name="subKey">The sub key.</param>
        public RegistryMonitor(RegistryHive registryHive, string subKey)
        {
            InitRegistryKey(registryHive, subKey);
        }

        /// <summary>
        /// Disposes this object.
        /// </summary>
        public void Dispose()
        {
            Stop();
            disposed = true;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets or sets the <see cref="RegChangeNotifyFilter">RegChangeNotifyFilter</see>.
        /// </summary>
        public RegChangeNotifyFilter RegChangeNotifyFilter
        {
            get => regFilter;
            set
            {
                lock (threadLock)
                {
                    if (IsMonitoring)
                        throw new InvalidOperationException("Monitoring thread is already running");

                    regFilter = value;
                }
            }
        }
        
        #region Initialization

        void InitRegistryKey(RegistryHive hive, string name)
        {
            switch (hive)
            {
                case RegistryHive.ClassesRoot:
                    registryHive = HkeyClassesRoot;
                    break;

                case RegistryHive.CurrentConfig:
                    registryHive = HkeyCurrentConfig;
                    break;

                case RegistryHive.CurrentUser:
                    registryHive = HkeyCurrentUser;
                    break;

                case RegistryHive.DynData:
                    registryHive = HkeyDynData;
                    break;

                case RegistryHive.LocalMachine:
                    registryHive = HkeyLocalMachine;
                    break;

                case RegistryHive.PerformanceData:
                    registryHive = HkeyPerformanceData;
                    break;

                case RegistryHive.Users:
                    registryHive = HkeyUsers;
                    break;

                default:
                    throw new InvalidEnumArgumentException(nameof(hive), (int)hive, typeof (RegistryHive));
            }
            registrySubName = name;
        }

        void InitRegistryKey(string name)
        {
            var nameParts = name.Split('\\');

            switch (nameParts[0])
            {
                case "HKEY_CLASSES_ROOT":
                case "HKCR":
                    registryHive = HkeyClassesRoot;
                    break;

                case "HKEY_CURRENT_USER":
                case "HKCU":
                    registryHive = HkeyCurrentUser;
                    break;

                case "HKEY_LOCAL_MACHINE":
                case "HKLM":
                    registryHive = HkeyLocalMachine;
                    break;

                case "HKEY_USERS":
                    registryHive = HkeyUsers;
                    break;

                case "HKEY_CURRENT_CONFIG":
                    registryHive = HkeyCurrentConfig;
                    break;

                default:
                    registryHive = IntPtr.Zero;
                    throw new ArgumentException("The registry hive '" + nameParts[0] + "' is not supported", "value");
            }

            registrySubName = string.Join("\\", nameParts, 1, nameParts.Length - 1);
        }
        
        #endregion

        /// <summary>
        /// <b>true</b> if this <see cref="RegistryMonitor"/> object is currently monitoring;
        /// otherwise, <b>false</b>.
        /// </summary>
        public bool IsMonitoring => thread != null;

        /// <summary>
        /// Start monitoring.
        /// </summary>
        public void Start()
        {
            if (disposed)
                throw new ObjectDisposedException(null, "This instance is already disposed");
            
            lock (threadLock)
            {
                if (!IsMonitoring)
                {
                    eventTerminate.Reset();
                    thread = new Thread(MonitorThread) {IsBackground = true};
                    thread.Start();
                }
            }
        }

        /// <summary>
        /// Stops the monitoring thread.
        /// </summary>
        public void Stop()
        {
            if (disposed)
                throw new ObjectDisposedException(null, "This instance is already disposed");
            
            lock (threadLock)
            {
                var thread = this.thread;
                if (thread != null)
                {
                    eventTerminate.Set();
                    thread.Join();
                }
            }
        }

        void MonitorThread()
        {
            try
            {
                ThreadLoop();
            }
            catch (Exception e)
            {
                OnError(e);
            }
            thread = null;
        }

        void ThreadLoop()
        {
            var result = RegOpenKeyEx(registryHive, registrySubName, 0, Standard_Rights_Read | Key_Query_Value | Key_Notify,
                                      out var registryKey);
            if (result != 0)
                throw new Win32Exception(result);

            try
            {
                var eventNotify = new AutoResetEvent(false);
                var waitHandles = new WaitHandle[] {eventNotify, eventTerminate};
                while (!eventTerminate.WaitOne(0, true))
                {
                    result = RegNotifyChangeKeyValue(registryKey, true, regFilter, eventNotify.SafeWaitHandle.DangerousGetHandle(), true);
                    if (result != 0)
                        throw new Win32Exception(result);

                    if (WaitHandle.WaitAny(waitHandles) == 0)
                    {
                        OnRegChanged();
                    }
                }
            }
            finally
            {
                if (registryKey != IntPtr.Zero)
                {
                    RegCloseKey(registryKey);
                }
            }
        }
    }
    
    /// <summary>
    /// Filter for notifications reported by <see cref="RegistryMonitor"/>.
    /// </summary>
    [Flags]
    public enum RegChangeNotifyFilter
    {
        /// <summary>Notify the caller if a subkey is added or deleted.</summary>
        Key = 1,
        
        /// <summary>Notify the caller of changes to the attributes of the key,
        /// such as the security descriptor information.</summary>
        Attribute = 2,
        
        /// <summary>Notify the caller of changes to a value of the key. This can
        /// include adding or deleting a value, or changing an existing value.</summary>
        Value = 4,
        
        /// <summary>Notify the caller of changes to the security descriptor
        /// of the key.</summary>
        Security = 8,
    }
}