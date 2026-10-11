using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace ReplayKitHelper
{
    // runs a delegate on a short-lived STA thread and rethrows whatever it threw (stack preserved); connections run on MTA pool threads, and Clipboard and other OLE calls throw there
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
