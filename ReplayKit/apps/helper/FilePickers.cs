using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReplayKitHelper
{
    // the windows file and folder dialogs the pages ask for: each runs on a one-shot sta thread of its own and is raised above obs, since the helper is another process
    internal static class FilePickers
    {
        private sealed class OwnerWindow : System.Windows.Forms.IWin32Window
        {
            public OwnerWindow(IntPtr h) { Handle = h; }
            public IntPtr Handle { get; }
        }

        internal static string FolderPickerTitle => Locales.Message("picker.recordingFolder");
        internal static string FilePickerTitle => Locales.Message("picker.iconImage");

        // blocking modal picker on a one-shot STA thread (winforms dialogs need STA + a pump). parents to the ReplayKit Settings popup if its open. "" on cancel/error.
        internal static string PickPath(bool folder, string filter, string initial)
        {
            string result = "";
            var t = new Thread(() =>
            {
                try
                {
                    string folderTitle = FolderPickerTitle;
                    string fileTitle = FilePickerTitle;
                    IntPtr ownerHwnd = Native.FindReplayKitSettingsWindow();
                    // show() below blocks this thread while the dialog is up, so bring it to the front from a watcher -- the helper is a separate process and the dialog otherwise opens behind OBS.
                    var raiser = new Thread(() => Native.RaiseOwnDialog(folder ? folderTitle : fileTitle))
                    { IsBackground = true, Name = "picker-raise" };
                    raiser.Start();

                    if (folder)
                    {
                        result = PickFolderModern(ownerHwnd, initial, folderTitle);
                    }
                    else
                    {
                        System.Windows.Forms.IWin32Window owner = ownerHwnd == IntPtr.Zero ? null : new OwnerWindow(ownerHwnd);
                        using (var d = new System.Windows.Forms.OpenFileDialog { Filter = filter, CheckFileExists = true, Title = fileTitle })
                        {
                            if (!string.IsNullOrEmpty(initial) && File.Exists(initial)) d.FileName = initial;
                            if (d.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) result = d.FileName;
                        }
                    }
                }
                catch (Exception ex) { Log.Write("PickPath: " + ex.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(180000);
            return result;
        }

        internal static string AudioPickerTitle => Locales.Message("picker.importAudio");
        // the containers of video are listed too, since the sound of a video file can be taken out and used
        internal static string AudioFilter => Locales.T("Audio files") + "|*.mp3;*.wav;*.ogg;*.oga;*.opus;*.flac;*.m4a;*.aac;*.wma;*.aif;*.aiff;*.mka;*.mp2;*.ac3;*.amr;*.au;*.caf;*.webm;*.mp4;*.mov;*.mkv|" + AllFiles;

        internal static string MediaPickerTitle => Locales.Message("picker.importMedia");
        internal static string MediaFilter => Locales.T("GIFs and images") + "|*.gif;*.png;*.jpg;*.jpeg|" + AllFiles;

        internal static string VideoPickerTitle => Locales.Message("picker.importVideo");
        internal static string ImagesFilter => Locales.T("Images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif|" + AllFiles;

        internal static string IconFilter => Locales.T("Images (*.ico;*.png;*.jpg;*.bmp)") + "|*.ico;*.png;*.jpg;*.jpeg;*.bmp|" + AllFiles;

        internal static string VideoFilter => Locales.T("Videos") + "|*.mp4;*.m4v;*.mov;*.mkv;*.webm|" + AllFiles;

        // the catch-all filter entry shared by every picker, pattern kept
        private static string AllFiles => Locales.T("All files (*.*)") + "|*.*";

        internal static string[] PickAudioFiles() => PickFiles(AudioPickerTitle, AudioFilter);

        // the picker of the editor: a modal dialog on a one-shot sta thread like PickPath, where several files can be chosen at once; an empty list when it is cancelled or fails
        internal static string[] PickFiles(string title, string filter)
        {
            string[] result = new string[0];
            var t = new Thread(() =>
            {
                try
                {
                    IntPtr ownerHwnd = Native.FindReplayKitSettingsWindow();
                    var raiser = new Thread(() => Native.RaiseOwnDialog(title)) { IsBackground = true, Name = "picker-raise" };
                    raiser.Start();
                    System.Windows.Forms.IWin32Window owner = ownerHwnd == IntPtr.Zero ? null : new OwnerWindow(ownerHwnd);
                    using (var d = new System.Windows.Forms.OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = true, Title = title })
                    {
                        if (d.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) result = d.FileNames;
                    }
                }
                catch (Exception ex) { Log.Write("PickFiles: " + ex.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(180000);
            return result;
        }

        // modern folder picker -- IFileOpenDialog with FOS_PICKFOLDERS, the same Explorer-style chrome as the icon file picker. the net48 WinForms FolderBrowserDialog is the legacy tree-view control. STA-only; the caller already runs this on a one-shot STA thread. "" on cancel or any failure.
        internal static string PickFolderModern(IntPtr owner, string initial, string title)
        {
            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)new FileOpenDialogClass();
                dlg.GetOptions(out uint opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial) &&
                    SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem seed) == 0 && seed != null)
                {
                    try { dlg.SetFolder(seed); } finally { Marshal.ReleaseComObject(seed); }
                }
                if (dlg.Show(owner) != 0) return ""; // ERROR_CANCELLED or any hr -- treat as cancel
                dlg.GetResult(out IShellItem item);
                try
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pth);
                    string s = Marshal.PtrToStringUni(pth);
                    Marshal.FreeCoTaskMem(pth);
                    return s ?? "";
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            catch (Exception ex) { Log.Write("PickFolderModern: " + ex.Message); return ""; }
            finally { if (dlg != null) Marshal.ReleaseComObject(dlg); }
        }

        internal const uint FOS_PICKFOLDERS = 0x20;
        internal const uint FOS_FORCEFILESYSTEM = 0x40;
        internal const uint FOS_NOCHANGEDIR = 0x8;
        internal const uint FOS_PATHMUSTEXIST = 0x800;
        internal const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogClass { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
