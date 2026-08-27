using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace ReplayKitHelper
{
    // runs a delegate on a short-lived STA thread and rethrows whatever it threw (stack preserved). needed for System.Windows.Forms.Clipboard and other OLE calls -- connections are handled on MTA thread-pool threads now (Program.cs Task.Run per connection) instead of the old dedicated STA connection thread, so an OLE call made straight from a request handler throws "current thread must be STA".
    internal static class StaRunner
    {
        public static void Run(Action action)
        {
            Exception captured = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (captured != null) ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}
