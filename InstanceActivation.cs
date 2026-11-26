using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;

namespace SwitchEnv
{
    // Only the UI responds to these events. The launcher never opens the
    // existing instance's data, and the request carries no arguments.
    sealed class InstanceActivation : IDisposable
    {
        readonly EventWaitHandle request;
        readonly EventWaitHandle displayed;
        readonly RegisteredWaitHandle subscription;
        bool disposed;

        public InstanceActivation(string name, string sid, Dispatcher dispatcher, Action showWindow)
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(sid), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            bool created;
            try
            {
                request = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-open", out created, security);
                displayed = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-shown", out created, security);
                subscription = ThreadPool.RegisterWaitForSingleObject(request, (state, timedOut) =>
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (disposed) return;
                        showWindow();
                        displayed.Set();
                    }));
                }, null, Timeout.Infinite, false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public static bool TryShow(string name)
        {
            // A second launch can reach the mutex before the first has built its UI.
            var elapsed = Stopwatch.StartNew();
            const int timeoutMs = 4000;
            while (elapsed.ElapsedMilliseconds < timeoutMs)
            {
                EventWaitHandle request;
                if (EventWaitHandle.TryOpenExisting(name + "-open", EventWaitHandleRights.Modify, out request))
                {
                    using (request)
                    {
                        EventWaitHandle displayed;
                        if (EventWaitHandle.TryOpenExisting(name + "-shown", EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out displayed))
                        {
                            using (displayed)
                            {
                                displayed.Reset();
                                request.Set();
                                return displayed.WaitOne(Math.Max(0, timeoutMs - (int)elapsed.ElapsedMilliseconds));
                            }
                        }
                    }
                }
                Thread.Sleep(60);
            }
            return false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (subscription != null) subscription.Unregister(null);
            if (displayed != null) displayed.Dispose();
            if (request != null) request.Dispose();
        }
    }
}
