using Files.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Foundation.Collections;
using Windows.System;

namespace FilesFullTrust
{
    internal partial class Win32API
    {
        public static Task<T> StartSTATask<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            Thread thread = new Thread(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetResult(default);
                    Program.Logger.Info(ex, ex.Message);
                    //tcs.SetException(e);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        public static async Task<string> GetFileAssociationAsync(string filename)
        {
            // Find UWP apps
            var uwpApps = await Launcher.FindFileHandlersAsync(Path.GetExtension(filename));
            if (uwpApps.Any())
            {
                return uwpApps.First().PackageFamilyName;
            }

            // Find desktop apps
            var lpResult = new StringBuilder(2048);
            var hResult = Shell32.FindExecutable(filename, null, lpResult);
            if (hResult.ToInt64() > 32)
            {
                return lpResult.ToString();
            }

            return null;
        }

        public static string ExtractStringFromDLL(string file, int number)
        {
            var lib = Kernel32.LoadLibrary(file);
            StringBuilder result = new StringBuilder(2048);
            User32.LoadString(lib, number, result, result.Capacity);
            Kernel32.FreeLibrary(lib);
            return result.ToString();
        }

        public static string[] CommandLineToArgs(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine))
            {
                return Array.Empty<string>();
            }

            var argv = Shell32.CommandLineToArgvW(commandLine, out int argc);
            if (argv == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

            try
            {
                var args = new string[argc];
                for (var i = 0; i < args.Length; i++)
                {
                    var p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                    args[i] = Marshal.PtrToStringUni(p);
                }

                return args;
            }
            finally
            {
                Marshal.FreeHGlobal(argv);
            }
        }

        public static (string icon, string overlay, bool isCustom) GetFileIconAndOverlay(string path, int thumbnailSize, bool getOverlay = true)
        {
            string iconStr = null, overlayStr = null;

            using var shellItem = new Vanara.Windows.Shell.ShellItem(path);
            if (shellItem.IShellItem is Shell32.IShellItemImageFactory fctry)
            {
                var flags = Shell32.SIIGBF.SIIGBF_BIGGERSIZEOK;
                if (thumbnailSize < 80) flags |= Shell32.SIIGBF.SIIGBF_ICONONLY;
                var hres = fctry.GetImage(new SIZE(thumbnailSize, thumbnailSize), flags, out var hbitmap);
                if (hres == HRESULT.S_OK)
                {
                    using var image = GetBitmapFromHBitmap(hbitmap);
                    if (image != null)
                    {
                        byte[] bitmapData = (byte[])new ImageConverter().ConvertTo(image, typeof(byte[]));
                        iconStr = Convert.ToBase64String(bitmapData, 0, bitmapData.Length);
                    }
                }
                //Marshal.ReleaseComObject(fctry);
            }

            if (getOverlay)
            {
                var shfi = new Shell32.SHFILEINFO();
                var ret = Shell32.SHGetFileInfo(
                    path,
                    0,
                    ref shfi,
                    Shell32.SHFILEINFO.Size,
                    Shell32.SHGFI.SHGFI_OVERLAYINDEX | Shell32.SHGFI.SHGFI_ICON | Shell32.SHGFI.SHGFI_SYSICONINDEX | Shell32.SHGFI.SHGFI_ICONLOCATION);
                if (ret == IntPtr.Zero)
                {
                    return (iconStr, null, false);
                }

                bool isCustom = true;
                User32.DestroyIcon(shfi.hIcon);
                Shell32.SHGetImageList(Shell32.SHIL.SHIL_LARGE, typeof(ComCtl32.IImageList).GUID, out var tmp);
                using var imageList = ComCtl32.SafeHIMAGELIST.FromIImageList(tmp);
                if (imageList.IsNull || imageList.IsInvalid)
                {
                    return (iconStr, null, isCustom);
                }

                var overlayIdx = shfi.iIcon >> 24;
                if (overlayIdx != 0)
                {
                    var overlayImage = imageList.Interface.GetOverlayImage(overlayIdx);
                    using var hOverlay = imageList.Interface.GetIcon(overlayImage, ComCtl32.IMAGELISTDRAWFLAGS.ILD_TRANSPARENT);
                    if (!hOverlay.IsNull && !hOverlay.IsInvalid)
                    {
                        using var image = hOverlay.ToIcon().ToBitmap();
                        byte[] bitmapData = (byte[])new ImageConverter().ConvertTo(image, typeof(byte[]));
                        overlayStr = Convert.ToBase64String(bitmapData, 0, bitmapData.Length);
                    }
                }

                return (iconStr, overlayStr, isCustom);
            }
            else
            {
                return (iconStr, null, false);
            }

        }

        private static void RunPowershellCommand(string command, bool runAsAdmin)
        {
            try
            {
                using Process process = new Process();
                if (runAsAdmin)
                {
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                }
                process.StartInfo.FileName = "powershell.exe";
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.Arguments = command;
                process.Start();
                process.WaitForExit(30 * 1000);
            }
            catch (Win32Exception)
            {
                // If user cancels UAC
            }
        }

        public static IList<IconFileInfo> ExtractSelectedIconsFromDLL(string file, IList<int> indexes, int iconSize = 48)
        {
            var iconsList = new List<IconFileInfo>();

            foreach (int index in indexes)
            {
                User32.SafeHICON icon;
                User32.SafeHICON hIcon2;    // This is merely to pass into the function and is unneeded otherwise
                if (Shell32.SHDefExtractIcon(file, -1 * index, 0, out icon, out hIcon2, Convert.ToUInt32(iconSize)) == HRESULT.S_OK)
                {
                    using var image = icon.ToBitmap();
                    byte[] bitmapData = (byte[])new ImageConverter().ConvertTo(image, typeof(byte[]));
                    var icoStr = Convert.ToBase64String(bitmapData, 0, bitmapData.Length);
                    iconsList.Add(new IconFileInfo(icoStr, index));
                    User32.DestroyIcon(icon);
                    User32.DestroyIcon(hIcon2);
                }
            }
            return iconsList;
        }

        public static void UnlockBitlockerDrive(string drive, string password)
        {
            RunPowershellCommand($"-command \"$SecureString = ConvertTo-SecureString '{password}' -AsPlainText -Force; Unlock-BitLocker -MountPoint '{drive}' -Password $SecureString\"", true);
        }

        public static void OpenFormatDriveDialog(string drive)
        {
            // format requires elevation
            int driveIndex = drive.ToUpperInvariant()[0] - 'A';
            RunPowershellCommand($"-command \"$Signature = '[DllImport(\\\"shell32.dll\\\", SetLastError = false)]public static extern uint SHFormatDrive(IntPtr hwnd, uint drive, uint fmtID, uint options);'; $SHFormatDrive = Add-Type -MemberDefinition $Signature -Name \"Win32SHFormatDrive\" -Namespace Win32Functions -PassThru; $SHFormatDrive::SHFormatDrive(0, {driveIndex}, 0xFFFF, 0x0001)\"", true);
        }

        public static void SetVolumeLabel(string driveName, string newLabel)
        {
            // rename requires elevation
            RunPowershellCommand($"-command \"$Signature = '[DllImport(\\\"kernel32.dll\\\", SetLastError = false)]public static extern bool SetVolumeLabel(string lpRootPathName, string lpVolumeName);'; $SetVolumeLabel = Add-Type -MemberDefinition $Signature -Name \"Win32SetVolumeLabel\" -Namespace Win32Functions -PassThru; $SetVolumeLabel::SetVolumeLabel('{driveName}', '{newLabel}')\"", true);
        }

        private static Bitmap GetBitmapFromHBitmap(HBITMAP hBitmap)
        {
            try
            {
                Bitmap bmp = Image.FromHbitmap((IntPtr)hBitmap);
                if (Image.GetPixelFormatSize(bmp.PixelFormat) < 32)
                {
                    return bmp;
                }
                if (IsAlphaBitmap(bmp, out var bmpData))
                {
                    return GetAlphaBitmapFromBitmapData(bmpData);
                }
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap GetAlphaBitmapFromBitmapData(BitmapData bmpData)
        {
            return new Bitmap(
                    bmpData.Width,
                    bmpData.Height,
                    bmpData.Stride,
                    PixelFormat.Format32bppArgb,
                    bmpData.Scan0);
        }

        private static bool IsAlphaBitmap(Bitmap bmp, out BitmapData bmpData)
        {
            Rectangle bmBounds = new Rectangle(0, 0, bmp.Width, bmp.Height);

            bmpData = bmp.LockBits(bmBounds, ImageLockMode.ReadOnly, bmp.PixelFormat);

            try
            {
                for (int y = 0; y <= bmpData.Height - 1; y++)
                {
                    for (int x = 0; x <= bmpData.Width - 1; x++)
                    {
                        Color pixelColor = Color.FromArgb(
                            Marshal.ReadInt32(bmpData.Scan0, (bmpData.Stride * y) + (4 * x)));

                        if (pixelColor.A > 0 & pixelColor.A < 255)
                        {
                            return true;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return false;
        }

        public static async Task SendMessageAsync(NamedPipeServerStream pipe, ValueSet valueSet, string requestID = null)
        {
            var message = new Dictionary<string, object>(valueSet);
            message.Add("RequestID", requestID);
            var serialized = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, new JsonSerializerOptions() { IncludeFields = true }));
            await pipe.WriteAsync(serialized, 0, serialized.Length);
        }

        // There is usually no need to define Win32 COM interfaces/P-Invoke methods here.
        // The Vanara library contains the definitions for all members of Shell32.dll, User32.dll and more
        // The ones below are due to bugs in the current version of the library and can be removed once fixed
        // Structure used by SHQueryRecycleBin.
        [StructLayout(LayoutKind.Sequential, Pack = 0)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        public static IEnumerable<HWND> GetDesktopWindows()
        {
            HWND prevHwnd = HWND.NULL;
            var windowsList = new List<HWND>();
            while (true)
            {
                prevHwnd = User32.FindWindowEx(HWND.NULL, prevHwnd, null, null);
                if (prevHwnd == null || prevHwnd == HWND.NULL)
                {
                    break;
                }
                windowsList.Add(prevHwnd);
            }
            return windowsList;
        }

        public static void BringToForeground(IEnumerable<HWND> currentWindows)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(5 * 1000);

            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(500);

                    var newWindows = GetDesktopWindows().Except(currentWindows).Where(x => User32.IsWindowVisible(x) && !User32.IsIconic(x));
                    if (newWindows.Any())
                    {
                        foreach (var newWindow in newWindows)
                        {
                            User32.SetWindowPos(newWindow, User32.SpecialWindowHandles.HWND_TOPMOST,
                                    0, 0, 0, 0, User32.SetWindowPosFlags.SWP_NOSIZE | User32.SetWindowPosFlags.SWP_NOMOVE);
                            User32.SetWindowPos(newWindow, User32.SpecialWindowHandles.HWND_NOTOPMOST,
                                0, 0, 0, 0, User32.SetWindowPosFlags.SWP_SHOWWINDOW | User32.SetWindowPosFlags.SWP_NOSIZE | User32.SetWindowPosFlags.SWP_NOMOVE);
                        }
                        break;
                    }
                }
            });
        }

        // Get information from recycle bin.
        [DllImport(Lib.Shell32, SetLastError = false, CharSet = CharSet.Auto)]
        public static extern int SHQueryRecycleBin(string pszRootPath,
            ref SHQUERYRBINFO pSHQueryRBInfo);
    }
}