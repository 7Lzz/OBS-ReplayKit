using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ReplayKitSetup
{
    // used to register the OBSReplayKit-Elevate task so the in-obs lua could relaunch obs elevated without a uac popup on every launch -- removed becuase a hidden, highest-privilege, unconditionally-installed scheduled task is exactly the shape av heuristics flag; only the removal of a task an older release left behind remains
    public static class ScheduledTask
    {
        // public so the uninstaller and DeleteElevationTask share the exact name of whatever older releases may have registered.
        public const string TASK_NAME = "OBSReplayKit-Elevate";

        // what the task scheduler answers for a task that does not exist
        private const int TaskNotFound = unchecked((int)0x80070002);

        // delete the ReplayKit scheduled task through the task scheduler itself. safe when the task is not present. Schedule.Service is late-bound com, so the trimmer warnings are moot
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "com idispatch dispatch, no trimmable managed members")]
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "com idispatch dispatch, no trimmable managed members")]
        public static bool DeleteElevationTask(Action<string> log = null)
        {
            object service = null;
            object folder = null;
            try
            {
                Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(serviceType);
                serviceType.InvokeMember("Connect", BindingFlags.InvokeMethod, null, service, new object[0]);
                folder = serviceType.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, service, new object[] { "\\" });
                folder.GetType().InvokeMember("DeleteTask", BindingFlags.InvokeMethod, null, folder, new object[] { TASK_NAME, 0 });
                log?.Invoke($"removed scheduled task '{TASK_NAME}'");
                return true;
            }
            catch (Exception exc) when (exc is COMException || exc is TargetInvocationException || exc is MissingMemberException || exc is TypeLoadException)
            {
                // late-bound com throws the error of the call itself, or wrapped when it went through reflection
                Exception root = exc is TargetInvocationException && exc.InnerException != null ? exc.InnerException : exc;
                if (root.HResult == TaskNotFound) return true;
                log?.Invoke("warn: scheduled task removal failed: " + root.Message);
                return false;
            }
            finally
            {
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (service != null) Marshal.ReleaseComObject(service);
            }
        }
    }
}
