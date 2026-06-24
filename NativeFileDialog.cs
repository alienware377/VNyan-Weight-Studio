using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace VNyanWeightStudio
{
    /// <summary>
    /// Thin wrapper around the Win32 common "Save As" dialog (comdlg32!GetSaveFileNameW).
    /// VNyan ships as a Windows standalone, so this is available at runtime. On any
    /// non-Windows / unavailable platform it simply returns null and the caller falls
    /// back to a default path.
    /// </summary>
    internal static class NativeFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr lpstrFilter;        // double-null-terminated; manual buffer
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;          // in/out buffer, manual
            public int nMaxFile;
            public IntPtr lpstrFileTitle;     // out buffer, manual
            public int nMaxFileTitle;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        const int OFN_OVERWRITEPROMPT = 0x00000002;
        const int OFN_NOCHANGEDIR     = 0x00000008;
        const int OFN_FILEMUSTEXIST   = 0x00001000;
        const int OFN_PATHMUSTEXIST   = 0x00000800;
        const int OFN_EXPLORER        = 0x00080000;

        /// <summary>
        /// Show a Save As dialog. Returns the chosen full path, or null on cancel /
        /// unavailable platform. <paramref name="filterPattern"/> is e.g. "*.vrm".
        /// Must be called on the main (UI) thread.
        /// </summary>
        public static string SaveFile(string title, string initialDir, string defaultFileName,
                                      string filterLabel, string filterPattern, string defExt)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
                return null;

            const int bufChars = 4096;
            IntPtr fileBuf   = Marshal.AllocHGlobal(bufChars * 2);
            IntPtr titleBuf  = Marshal.AllocHGlobal(512 * 2);
            // filter is "Label\0pattern\0\0" — embedded nulls, so build it by hand.
            IntPtr filterBuf = Marshal.StringToHGlobalUni((filterLabel ?? "Files") + "\0" + (filterPattern ?? "*.*") + "\0");
            try
            {
                // seed the file buffer with the suggested name
                byte[] seed = Encoding.Unicode.GetBytes((defaultFileName ?? "") + "\0");
                if (seed.Length > bufChars * 2) Array.Resize(ref seed, bufChars * 2);
                Marshal.Copy(seed, 0, fileBuf, seed.Length);
                Marshal.WriteInt16(titleBuf, 0, 0);

                var ofn = new OpenFileName();
                ofn.lStructSize    = Marshal.SizeOf(typeof(OpenFileName));
                ofn.hwndOwner      = GetActiveWindow();
                ofn.lpstrFilter    = filterBuf;
                ofn.nFilterIndex   = 1;
                ofn.lpstrFile      = fileBuf;
                ofn.nMaxFile       = bufChars;
                ofn.lpstrFileTitle = titleBuf;
                ofn.nMaxFileTitle  = 512;
                ofn.lpstrInitialDir = initialDir;
                ofn.lpstrTitle     = title;
                ofn.lpstrDefExt    = defExt;
                ofn.Flags          = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER;

                bool ok = GetSaveFileNameW(ref ofn);
                if (!ok) return null; // cancelled (or error)
                return Marshal.PtrToStringUni(fileBuf);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WeightStudio] Save dialog unavailable, using default path: " + e.Message);
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuf);
                Marshal.FreeHGlobal(titleBuf);
                Marshal.FreeHGlobal(filterBuf);
            }
        }

        /// <summary>
        /// Show an Open dialog. Returns the chosen full path, or null on cancel /
        /// unavailable platform. <paramref name="filterPattern"/> is e.g. "*.vbx".
        /// Must be called on the main (UI) thread.
        /// </summary>
        public static string OpenFile(string title, string initialDir,
                                      string filterLabel, string filterPattern)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
                return null;

            const int bufChars = 4096;
            IntPtr fileBuf   = Marshal.AllocHGlobal(bufChars * 2);
            IntPtr titleBuf  = Marshal.AllocHGlobal(512 * 2);
            IntPtr filterBuf = Marshal.StringToHGlobalUni((filterLabel ?? "Files") + "\0" + (filterPattern ?? "*.*") + "\0");
            try
            {
                Marshal.WriteInt16(fileBuf, 0, 0);   // empty initial file name
                Marshal.WriteInt16(titleBuf, 0, 0);

                var ofn = new OpenFileName();
                ofn.lStructSize    = Marshal.SizeOf(typeof(OpenFileName));
                ofn.hwndOwner      = GetActiveWindow();
                ofn.lpstrFilter    = filterBuf;
                ofn.nFilterIndex   = 1;
                ofn.lpstrFile      = fileBuf;
                ofn.nMaxFile       = bufChars;
                ofn.lpstrFileTitle = titleBuf;
                ofn.nMaxFileTitle  = 512;
                ofn.lpstrInitialDir = initialDir;
                ofn.lpstrTitle     = title;
                ofn.Flags          = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER;

                bool ok = GetOpenFileNameW(ref ofn);
                if (!ok) return null; // cancelled (or error)
                return Marshal.PtrToStringUni(fileBuf);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WeightStudio] Open dialog unavailable: " + e.Message);
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuf);
                Marshal.FreeHGlobal(titleBuf);
                Marshal.FreeHGlobal(filterBuf);
            }
        }
    }
}
