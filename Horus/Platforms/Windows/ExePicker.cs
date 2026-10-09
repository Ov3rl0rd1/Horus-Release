using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// "Choose a program" through the classic Win32 open dialog.
    ///
    /// <para>Not MAUI's <c>FilePicker</c>: it is the WinRT <c>FileOpenPicker</c> underneath,
    /// which fails in an elevated process, and Horus always runs elevated (creating the
    /// adapter needs it). <c>GetOpenFileNameW</c> has no such restriction. It runs on its own
    /// STA thread so the window keeps drawing while the dialog is open.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class ExePicker
    {
        private const int MaxPath = 1024;
        private const int OfnFileMustExist = 0x1000, OfnPathMustExist = 0x800, OfnNoChangeDir = 0x8, OfnExplorer = 0x80000;

        public static Task<string?> PickAsync(string title)
        {
            var owner = MainWindowHandle();
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { done.TrySetResult(Show(owner, title)); }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return done.Task;
        }

        private static string? Show(IntPtr owner, string title)
        {
            var buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = owner,
                    lpstrFilter = "Программы (*.exe)\0*.exe\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = MaxPath,
                    lpstrTitle = title,
                    lpstrInitialDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Flags = OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnExplorer,
                };
                return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static IntPtr MainWindowHandle()
        {
            try
            {
                var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                return window is null ? IntPtr.Zero : global::WinRT.Interop.WindowNative.GetWindowHandle(window);
            }
            catch { return IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string? lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);
    }
}
