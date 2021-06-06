using Files.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vanara.InteropServices;
using Vanara.PInvoke;
using Vanara.Windows.Shell;

namespace FilesFullTrust
{
    internal partial class Win32API
    {
        public class ThreadWithMessageQueue<T> : IDisposable
        {
            private BlockingCollection<Internal> messageQueue;
            private Thread thread;
            private DisposableDictionary state;

            public void Dispose()
            {
                messageQueue.CompleteAdding();
                thread.Join();
                state.Dispose();
            }

            public async Task<V> PostMessageAsync<V>(T payload)
            {
                var message = new Internal(payload);
                messageQueue.TryAdd(message);
                return (V)await message.tcs.Task;
            }

            public Task PostMessage(T payload)
            {
                var message = new Internal(payload);
                messageQueue.TryAdd(message);
                return message.tcs.Task;
            }

            public ThreadWithMessageQueue(Func<T, DisposableDictionary, object> handleMessage)
            {
                messageQueue = new BlockingCollection<Internal>(new ConcurrentQueue<Internal>());
                state = new DisposableDictionary();
                thread = new Thread(new ThreadStart(() =>
                {
                    foreach (var message in messageQueue.GetConsumingEnumerable())
                    {
                        var res = handleMessage(message.payload, state);
                        message.tcs.SetResult(res);
                    }
                }));
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            private class Internal
            {
                public T payload;
                public TaskCompletionSource<object> tcs;

                public Internal(T payload)
                {
                    this.payload = payload;
                    this.tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        public class DisposableDictionary : IDisposable
        {
            private ConcurrentDictionary<string, object> dict;

            public DisposableDictionary()
            {
                dict = new ConcurrentDictionary<string, object>();
            }

            public string AddValue(object obj)
            {
                string key = Guid.NewGuid().ToString();
                if (!dict.TryAdd(key, obj))
                {
                    throw new ArgumentException("Could not create handle: key exists");
                }

                return key;
            }

            public void SetValue(string key, object obj)
            {
                RemoveValue(key);
                if (!dict.TryAdd(key, obj))
                {
                    throw new ArgumentException("Could not create handle: key exists");
                }
            }

            public object GetValue(string key)
            {
                dict.TryGetValue(key, out var elem);
                return elem;
            }

            public T GetValue<T>(string key)
            {
                dict.TryGetValue(key, out var elem);
                return (T)elem;
            }

            public void RemoveValue(string key)
            {
                dict.TryRemove(key, out var elem);
                (elem as IDisposable)?.Dispose();
            }

            public void Dispose()
            {
                foreach (var elem in dict)
                {
                    dict.TryRemove(elem.Key, out _);
                    (elem.Value as IDisposable)?.Dispose();
                }
            }
        }

        public class ContextMenu : Win32ContextMenu, IDisposable
        {
            private Shell32.IContextMenu cMenu;
            private User32.SafeHMENU hMenu;
            public List<string> ItemsPath { get; }

            public ContextMenu(Shell32.IContextMenu cMenu, User32.SafeHMENU hMenu, IEnumerable<string> itemsPath)
            {
                this.cMenu = cMenu;
                this.hMenu = hMenu;
                this.ItemsPath = itemsPath.ToList();
                this.Items = new List<Win32ContextMenuItem>();
            }

            public void InvokeVerb(string verb)
            {
                if (string.IsNullOrEmpty(verb))
                {
                    return;
                }

                try
                {
                    var currentWindows = Win32API.GetDesktopWindows();
                    var pici = new Shell32.CMINVOKECOMMANDINFOEX();
                    pici.lpVerb = new SafeResourceId(verb, CharSet.Ansi);
                    pici.nShow = ShowWindowCommand.SW_SHOWNORMAL;
                    pici.cbSize = (uint)Marshal.SizeOf(pici);
                    cMenu.InvokeCommand(pici);
                    Win32API.BringToForeground(currentWindows);
                }
                catch (Exception ex) when (
                    ex is COMException
                    || ex is UnauthorizedAccessException)
                {
                    Debug.WriteLine(ex);
                }
            }

            public void InvokeItem(int itemID)
            {
                if (itemID < 0)
                {
                    return;
                }

                try
                {
                    var currentWindows = Win32API.GetDesktopWindows();
                    var pici = new Shell32.CMINVOKECOMMANDINFOEX();
                    pici.lpVerb = Macros.MAKEINTRESOURCE(itemID);
                    pici.nShow = ShowWindowCommand.SW_SHOWNORMAL;
                    pici.cbSize = (uint)Marshal.SizeOf(pici);
                    cMenu.InvokeCommand(pici);
                    Win32API.BringToForeground(currentWindows);
                }
                catch (Exception ex) when (
                    ex is COMException
                    || ex is UnauthorizedAccessException)
                {
                    Debug.WriteLine(ex);
                }
            }

            #region FactoryMethods

            public static ContextMenu GetContextMenuForFiles(string[] filePathList, Shell32.CMF flags, Func<string, bool> itemFilter = null)
            {
                List<ShellItem> shellItems = new List<ShellItem>();
                try
                {
                    foreach (var fp in filePathList.Where(x => !string.IsNullOrEmpty(x)))
                    {
                        shellItems.Add(new ShellItem(fp));
                    }

                    return GetContextMenuForFiles(shellItems.ToArray(), flags, itemFilter);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FileNotFoundException)
                {
                    // Return empty context menu
                    return null;
                }
                finally
                {
                    foreach (var si in shellItems)
                    {
                        si.Dispose();
                    }
                }
            }

            private static ContextMenu GetContextMenuForFiles(ShellItem[] shellItems, Shell32.CMF flags, Func<string, bool> itemFilter = null)
            {
                if (shellItems == null || !shellItems.Any())
                {
                    return null;
                }

                using var sf = shellItems.First().Parent; // HP: the items are all in the same folder
                Shell32.IContextMenu menu = sf.GetChildrenUIObjects<Shell32.IContextMenu>(null, shellItems);
                var hMenu = User32.CreatePopupMenu();
                menu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, flags);
                var contextMenu = new ContextMenu(menu, hMenu, shellItems.Select(x => x.ParsingName));
                ContextMenu.EnumMenuItems(menu, hMenu, contextMenu.Items, itemFilter);
                return contextMenu;
            }

            #endregion FactoryMethods

            private static void EnumMenuItems(
                Shell32.IContextMenu cMenu,
                HMENU hMenu,
                List<Win32ContextMenuItem> menuItemsResult,
                Func<string, bool> itemFilter = null)
            {
                var itemCount = User32.GetMenuItemCount(hMenu);
                var mii = new User32.MENUITEMINFO();
                mii.cbSize = (uint)Marshal.SizeOf(mii);
                mii.fMask = User32.MenuItemInfoMask.MIIM_BITMAP
                    | User32.MenuItemInfoMask.MIIM_FTYPE
                    | User32.MenuItemInfoMask.MIIM_STRING
                    | User32.MenuItemInfoMask.MIIM_ID
                    | User32.MenuItemInfoMask.MIIM_SUBMENU;
                for (uint ii = 0; ii < itemCount; ii++)
                {
                    var menuItem = new ContextMenuItem();
                    var container = new SafeCoTaskMemString(512);
                    mii.dwTypeData = (IntPtr)container;
                    mii.cch = (uint)container.Capacity - 1; // https://devblogs.microsoft.com/oldnewthing/20040928-00/?p=37723
                    var retval = User32.GetMenuItemInfo(hMenu, ii, true, ref mii);
                    if (!retval)
                    {
                        container.Dispose();
                        continue;
                    }
                    menuItem.Type = (MenuItemType)mii.fType;
                    menuItem.ID = (int)(mii.wID - 1); // wID - idCmdFirst
                    if (menuItem.Type == MenuItemType.MFT_STRING)
                    {
                        Debug.WriteLine("Item {0} ({1}): {2}", ii, mii.wID, mii.dwTypeData);
                        menuItem.Label = mii.dwTypeData;
                        menuItem.CommandString = GetCommandString(cMenu, mii.wID - 1);
                        if (itemFilter != null && (itemFilter(menuItem.CommandString) || itemFilter(menuItem.Label)))
                        {
                            // Skip items implemented in UWP
                            container.Dispose();
                            continue;
                        }
                        if (mii.hbmpItem != HBITMAP.NULL && !Enum.IsDefined(typeof(HBITMAP_HMENU), ((IntPtr)mii.hbmpItem).ToInt64()))
                        {
                            var bitmap = GetBitmapFromHBitmap(mii.hbmpItem);
                            if (bitmap != null)
                            {
                                menuItem.Icon = bitmap;
                            }
                        }
                        if (mii.hSubMenu != HMENU.NULL)
                        {
                            Debug.WriteLine("Item {0}: has submenu", ii);
                            var subItems = new List<Win32ContextMenuItem>();
                            try
                            {
                                (cMenu as Shell32.IContextMenu2)?.HandleMenuMsg((uint)User32.WindowMessage.WM_INITMENUPOPUP, (IntPtr)mii.hSubMenu, new IntPtr(ii));
                            }
                            catch (NotImplementedException)
                            {
                                // Only for dynamic/owner drawn? (open with, etc)
                            }
                            EnumMenuItems(cMenu, mii.hSubMenu, subItems, itemFilter);
                            menuItem.SubItems = subItems;
                            Debug.WriteLine("Item {0}: done submenu", ii);
                        }
                    }
                    else
                    {
                        Debug.WriteLine("Item {0}: {1}", ii, mii.fType.ToString());
                    }
                    container.Dispose();
                    menuItemsResult.Add(menuItem);
                }
            }

            private static string GetCommandString(Shell32.IContextMenu cMenu, uint offset, Shell32.GCS flags = Shell32.GCS.GCS_VERBW)
            {
                if (offset > 5000)
                {
                    // Hackish workaround to avoid an AccessViolationException on some items,
                    // notably the "Run with graphic processor" menu item of NVidia cards
                    return null;
                }
                SafeCoTaskMemString commandString = null;
                try
                {
                    commandString = new SafeCoTaskMemString(512);
                    cMenu.GetCommandString(new IntPtr(offset), flags, IntPtr.Zero, commandString, (uint)commandString.Capacity - 1);
                    Debug.WriteLine("Verb {0}: {1}", offset, commandString);
                    return commandString.ToString();
                }
                catch (Exception ex) when (ex is InvalidCastException || ex is ArgumentException)
                {
                    // TODO: investigate this..
                    Debug.WriteLine(ex);
                    return null;
                }
                catch (Exception ex) when (ex is COMException || ex is NotImplementedException)
                {
                    // Not every item has an associated verb
                    return null;
                }
                finally
                {
                    commandString?.Dispose();
                }
            }

            #region IDisposable Support

            private bool disposedValue = false; // To detect redundant calls

            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        // TODO: dispose managed state (managed objects).
                        if (Items != null)
                        {
                            foreach (var si in Items)
                            {
                                (si as IDisposable)?.Dispose();
                            }

                            Items = null;
                        }
                    }

                    // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                    if (hMenu != null)
                    {
                        User32.DestroyMenu(hMenu);
                        hMenu = null;
                    }
                    if (cMenu != null)
                    {
                        Marshal.ReleaseComObject(cMenu);
                        cMenu = null;
                    }

                    disposedValue = true;
                }
            }

            ~ContextMenu()
            {
                Dispose(false);
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            #endregion IDisposable Support
        }

        public class ContextMenuItem : Win32ContextMenuItem, IDisposable
        {
            private Bitmap icon;

            [JsonIgnore]
            public Bitmap Icon
            {
                get
                {
                    return icon;
                }
                set
                {
                    icon = value;
                    byte[] bitmapData = (byte[])new ImageConverter().ConvertTo(value, typeof(byte[]));
                    IconBase64 = Convert.ToBase64String(bitmapData, 0, bitmapData.Length);
                }
            }

            public ContextMenuItem()
            {
                this.SubItems = new List<Win32ContextMenuItem>();
            }

            public void Dispose()
            {
                Icon?.Dispose();
                if (SubItems != null)
                {
                    foreach (var si in SubItems)
                    {
                        (si as IDisposable)?.Dispose();
                    }

                    SubItems = null;
                }
            }
        }

        // There is usually no need to define Win32 COM interfaces/P-Invoke methods here.
        // The Vanara library contains the definitions for all members of Shell32.dll, User32.dll and more
        // The ones below are due to bugs in the current version of the library and can be removed once fixed
        // https://docs.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-menuiteminfoa
        private enum HBITMAP_HMENU : long
        {
            HBMMENU_CALLBACK = -1,
            HBMMENU_MBAR_CLOSE = 5,
            HBMMENU_MBAR_CLOSE_D = 6,
            HBMMENU_MBAR_MINIMIZE = 3,
            HBMMENU_MBAR_MINIMIZE_D = 7,
            HBMMENU_MBAR_RESTORE = 2,
            HBMMENU_POPUP_CLOSE = 8,
            HBMMENU_POPUP_MAXIMIZE = 10,
            HBMMENU_POPUP_MINIMIZE = 11,
            HBMMENU_POPUP_RESTORE = 9,
            HBMMENU_SYSTEM = 1
        }
    }
}