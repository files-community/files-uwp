using Files.Common;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace FilesFullTrust
{
    internal class Program
    {
        public static Logger Logger { get; private set; }
        private static readonly LogWriter logWriter = new LogWriter();
        private static JsonSerializerOptions includeFieldsOptions = new JsonSerializerOptions { IncludeFields = true };

        [STAThread]
        private static void Main(string[] args)
        {
            Logger = new Logger(logWriter);
            logWriter.InitializeAsync("debug_fulltrust.log").Wait();
            AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;

            if (HandleCommandLineArgs())
            {
                // Handles OpenShellCommandInExplorer
                return;
            }

            try
            {
                // Create handle table to store e.g. context menu references
                handleTable = new Win32API.DisposableDictionary();

                // Create shell COM object and get recycle bin folder
                using var recycler = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_RecycleBinFolder);
                ApplicationData.Current.LocalSettings.Values["RecycleBin_Title"] = recycler.Name;

                // Create filesystem watcher to monitor recycle bin folder(s)
                // SHChangeNotifyRegister only works if recycle bin is open in explorer :(
                binWatchers = new List<FileSystemWatcher>();
                var sid = WindowsIdentity.GetCurrent().User.ToString();
                foreach (var drive in DriveInfo.GetDrives())
                {
                    var recyclePath = Path.Combine(drive.Name, "$RECYCLE.BIN", sid);
                    if (drive.DriveType == DriveType.Network || !Directory.Exists(recyclePath))
                    {
                        continue;
                    }
                    var watcher = new FileSystemWatcher
                    {
                        Path = recyclePath,
                        Filter = "*.*",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
                    };
                    watcher.Created += RecycleBinWatcher_Changed;
                    watcher.Deleted += RecycleBinWatcher_Changed;
                    watcher.EnableRaisingEvents = true;
                    binWatchers.Add(watcher);
                }

                librariesWatcher = new FileSystemWatcher
                {
                    Path = librariesPath,
                    Filter = "*" + ShellLibraryItem.EXTENSION,
                    NotifyFilter = NotifyFilters.Attributes | NotifyFilters.LastWrite | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                };

                librariesWatcher.Created += (object _, FileSystemEventArgs e) => OnLibraryChanged(e.ChangeType, e.FullPath, e.FullPath);
                librariesWatcher.Changed += (object _, FileSystemEventArgs e) => OnLibraryChanged(e.ChangeType, e.FullPath, e.FullPath);
                librariesWatcher.Deleted += (object _, FileSystemEventArgs e) => OnLibraryChanged(e.ChangeType, e.FullPath, null);
                librariesWatcher.Renamed += (object _, RenamedEventArgs e) => OnLibraryChanged(e.ChangeType, e.OldFullPath, e.FullPath);
                librariesWatcher.EnableRaisingEvents = true;

                // Create cancellation token for drop window
                cancellation = new CancellationTokenSource();

                // Connect to app service and wait until the connection gets closed
                appServiceExit = new ManualResetEvent(false);
                InitializeAppServiceConnection();

                // Preload context menu for better performance
                // We query the context menu for the app's local folder
                var preloadPath = ApplicationData.Current.LocalFolder.Path;
                using var _ = Win32API.ContextMenu.GetContextMenuForFiles(new string[] { preloadPath }, Shell32.CMF.CMF_NORMAL | Shell32.CMF.CMF_SYNCCASCADEMENU, FilterMenuItems(false));

                // Initialize device watcher
                deviceWatcher = new DeviceWatcher(connection);
                deviceWatcher.Start();

                // Wait until the connection gets closed
                appServiceExit.WaitOne();
            }
            finally
            {
                connection?.Dispose();
                foreach (var watcher in binWatchers)
                {
                    watcher.Dispose();
                }
                handleTable?.Dispose();
                deviceWatcher?.Dispose();
                librariesWatcher?.Dispose();
                cancellation?.Cancel();
                cancellation?.Dispose();
                appServiceExit?.Dispose();
            }
        }

        private static void UnhandledExceptionTrapper(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            Logger.Error(exception, exception.Message);
        }

        private static async void RecycleBinWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            Debug.WriteLine($"Recycle bin event: {e.ChangeType}, {e.FullPath}");
            if (e.Name.StartsWith("$I"))
            {
                // Recycle bin also stores a file starting with $I for each item
                return;
            }
            if (connection?.IsConnected ?? false)
            {
                var response = new ValueSet()
                {
                    { "FileSystem", @"Shell:RecycleBinFolder" },
                    { "Path", e.FullPath },
                    { "Type", e.ChangeType.ToString() }
                };
                if (e.ChangeType == WatcherChangeTypes.Created)
                {
                    using var folderItem = new ShellItem(e.FullPath);
                    var shellFileItem = GetShellFileItem(folderItem);
                    response["Item"] = JsonSerializer.Serialize(shellFileItem, includeFieldsOptions);
                }
                // Send message to UWP app to refresh items
                await Win32API.SendMessageAsync(connection, response);
            }
        }

        private static NamedPipeServerStream connection;
        private static ManualResetEvent appServiceExit;
        private static CancellationTokenSource cancellation;
        private static Win32API.DisposableDictionary handleTable;
        private static IList<FileSystemWatcher> binWatchers;
        private static DeviceWatcher deviceWatcher;
        private static FileSystemWatcher librariesWatcher;
        private static readonly string librariesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Libraries");

        private static async void InitializeAppServiceConnection()
        {
            connection = NamedPipeServerStreamConstructors.New(
                $@"FilesInteropService_ServerPipe",
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous,
                2048,
                2048,
                null,
                HandleInheritability.None,
                PipeAccessRights.ChangePermissions);

            PipeSecurity Security = connection.GetAccessControl();
            PipeAccessRule ClientRule = new PipeAccessRule(new SecurityIdentifier("S-1-15-2-1"), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow);
            PipeAccessRule OwnerRule = new PipeAccessRule(WindowsIdentity.GetCurrent().Owner, PipeAccessRights.FullControl, AccessControlType.Allow);
            Security.AddAccessRule(ClientRule);
            Security.AddAccessRule(OwnerRule);
            if (IsAdministrator())
            {
                PipeAccessRule EveryoneRule = new PipeAccessRule(new SecurityIdentifier("S-1-1-0"), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow);
                Security.AddAccessRule(EveryoneRule); // TODO: find the minimum permission to allow connection when admin
            }
            connection.SetAccessControl(Security);

            try
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await connection.WaitForConnectionAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not initialize pipe!");
            }

            if (connection.IsConnected)
            {
                connection.ReadMode = PipeTransmissionMode.Message;
                var info = (Buffer: new byte[connection.InBufferSize], Message: new StringBuilder());
                BeginRead(info);
            }
            else
            {
                appServiceExit.Set();
            }
        }

        private static void BeginRead((byte[] Buffer, StringBuilder Message) info)
        {
            var isConnected = connection.IsConnected;
            if (isConnected)
            {
                try
                {
                    connection.BeginRead(info.Buffer, 0, info.Buffer.Length, EndReadCallBack, info);
                }
                catch
                {
                    isConnected = false;
                }
            }
            if (!isConnected)
            {
                appServiceExit.Set();
            }
        }

        private static void EndReadCallBack(IAsyncResult result)
        {
            var info = ((byte[] Buffer, StringBuilder Message))result.AsyncState;
            var readBytes = connection.EndRead(result);
            if (readBytes > 0)
            {
                // Get the read bytes and append them
                info.Message.Append(Encoding.UTF8.GetString(info.Buffer, 0, readBytes));

                if (connection.IsMessageComplete) // Message is completed
                {
                    var message = info.Message.ToString().TrimEnd('\0');
                    var options = new JsonSerializerOptions
                    {
                        Converters =
                        {
                            new DictionaryStringObjectJsonConverter()
                        },
                        IncludeFields = true
                    };
                    Connection_RequestReceived(connection, JsonSerializer.Deserialize<Dictionary<string, object>>(message, options));

                    // Begin a new reading operation
                    var nextInfo = (Buffer: new byte[connection.InBufferSize], Message: new StringBuilder());
                    BeginRead(nextInfo);

                    return;
                }
            }
            BeginRead(info);
        }

        private static async void Connection_RequestReceived(NamedPipeServerStream conn, Dictionary<string, object> message)
        {
            // Get a deferral because we use an awaitable API below to respond to the message
            // and we don't want this call to get cancelled while we are waiting.
            if (message == null)
            {
                return;
            }

            if (message.ContainsKey("Arguments"))
            {
                // This replaces launching the fulltrust process with arguments
                // Instead a single instance of the process is running
                // Requests from UWP app are sent via AppService connection
                var arguments = (string)message["Arguments"];
                var localSettings = ApplicationData.Current.LocalSettings;
                Logger.Info($"Argument: {arguments}");

                await ParseArgumentsAsync(message, arguments, localSettings);
            }
            else if (message.ContainsKey("Application"))
            {
                var application = (string)message["Application"];
                HandleApplicationLaunch(application, message);
            }
            else if (message.ContainsKey("ApplicationList"))
            {
                var applicationList = JsonSerializer.Deserialize<IEnumerable<string>>((string)message["ApplicationList"]);
                HandleApplicationsLaunch(applicationList, message);
            }
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static async Task ParseArgumentsAsync(Dictionary<string, object> message, string arguments, ApplicationDataContainer localSettings)
        {
            switch (arguments)
            {
                case "Terminate":
                    // Exit fulltrust process (UWP is closed or suspended)
                    appServiceExit.Set();
                    break;

                case "Elevate":
                    // Relaunch fulltrust process as admin
                    if (!IsAdministrator())
                    {
                        try
                        {
                            using (Process elevatedProcess = new Process())
                            {
                                elevatedProcess.StartInfo.Verb = "runas";
                                elevatedProcess.StartInfo.UseShellExecute = true;
                                elevatedProcess.StartInfo.FileName = Process.GetCurrentProcess().MainModule.FileName;
                                elevatedProcess.StartInfo.Arguments = "elevate";
                                elevatedProcess.Start();
                            }
                            await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", 0 } }, message.Get("RequestID", (string)null));
                            appServiceExit.Set();
                        }
                        catch (Win32Exception)
                        {
                            // If user cancels UAC
                            await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", 1 } }, message.Get("RequestID", (string)null));
                        }
                    }
                    else
                    {
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", -1 } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "RecycleBin":
                    var binAction = (string)message["action"];
                    await ParseRecycleBinActionAsync(message, binAction);
                    break;

                case "DetectQuickLook":
                    // Check QuickLook Availability
                    var available = QuickLook.CheckQuickLookAvailability();
                    await Win32API.SendMessageAsync(connection, new ValueSet() { { "IsAvailable", available } }, message.Get("RequestID", (string)null));
                    break;

                case "ToggleQuickLook":
                    var path = (string)message["path"];
                    QuickLook.ToggleQuickLook(path);
                    break;

                case "LoadContextMenu":
                    var contextMenuResponse = new ValueSet();
                    var loadThreadWithMessageQueue = new Win32API.ThreadWithMessageQueue<Dictionary<string, object>>(HandleMenuMessage);
                    var cMenuLoad = await loadThreadWithMessageQueue.PostMessageAsync<Win32API.ContextMenu>(message);
                    contextMenuResponse.Add("Handle", handleTable.AddValue(loadThreadWithMessageQueue));
                    contextMenuResponse.Add("ContextMenu", JsonSerializer.Serialize(cMenuLoad, includeFieldsOptions));
                    var serializedCm = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(contextMenuResponse, includeFieldsOptions));
                    await Win32API.SendMessageAsync(connection, contextMenuResponse, message.Get("RequestID", (string)null));
                    break;

                case "ExecAndCloseContextMenu":
                    var menuKey = (string)message["Handle"];
                    var execThreadWithMessageQueue = handleTable.GetValue<Win32API.ThreadWithMessageQueue<Dictionary<string, object>>>(menuKey);
                    if (execThreadWithMessageQueue != null)
                    {
                        await execThreadWithMessageQueue.PostMessage(message);
                    }
                    // The following line is needed to cleanup resources when menu is closed.
                    // Unfortunately if you uncomment it some menu items will randomly stop working.
                    // Resource cleanup is currently done on app closing,
                    // if we find a solution for the issue above, we should cleanup as soon as a menu is closed.
                    //handleTable.RemoveValue(menuKey);
                    break;

                case "InvokeVerb":
                    var filePath = (string)message["FilePath"];
                    var split = filePath.Split('|').Where(x => !string.IsNullOrWhiteSpace(x));
                    using (var cMenu = Win32API.ContextMenu.GetContextMenuForFiles(split.ToArray(), Shell32.CMF.CMF_DEFAULTONLY))
                    {
                        cMenu?.InvokeVerb((string)message["Verb"]);
                    }
                    break;

                case "Bitlocker":
                    var bitlockerAction = (string)message["action"];
                    if (bitlockerAction == "Unlock")
                    {
                        var drive = (string)message["drive"];
                        var password = (string)message["password"];
                        Win32API.UnlockBitlockerDrive(drive, password);
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Bitlocker", "Unlock" } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "SetVolumeLabel":
                    var driveName = (string)message["drivename"];
                    var newLabel = (string)message["newlabel"];
                    Win32API.SetVolumeLabel(driveName, newLabel);
                    await Win32API.SendMessageAsync(connection, new ValueSet() { { "SetVolumeLabel", driveName } }, message.Get("RequestID", (string)null));
                    break;

                case "FileOperation":
                    await ParseFileOperationAsync(message);
                    break;

                case "GetIconOverlay":
                    var fileIconPath = (string)message["filePath"];
                    var thumbnailSize = (int)(long)message["thumbnailSize"];
                    var iconOverlay = Win32API.StartSTATask(() => Win32API.GetFileIconAndOverlay(fileIconPath, thumbnailSize)).Result;
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "Icon", iconOverlay.icon },
                        { "Overlay", iconOverlay.overlay },
                        { "HasCustomIcon", iconOverlay.isCustom }
                    }, message.Get("RequestID", (string)null));
                    break;

                case "GetIconWithoutOverlay":
                    var fileIconPath2 = (string)message["filePath"];
                    var thumbnailSize2 = (int)(long)message["thumbnailSize"];
                    var icon2 = Win32API.StartSTATask(() => Win32API.GetFileIconAndOverlay(fileIconPath2, thumbnailSize2, false)).Result;
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "Icon", icon2.icon },
                    }, message.Get("RequestID", (string)null));
                    break;

                case "NetworkDriveOperation":
                    await ParseNetworkDriveOperationAsync(message);
                    break;

                case "GetOneDriveAccounts":
                    try
                    {
                        var oneDriveAccountsKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\OneDrive\Accounts", false);

                        if (oneDriveAccountsKey == null)
                        {
                            await Win32API.SendMessageAsync(connection, new ValueSet() { { "Count", 0 } }, message.Get("RequestID", (string)null));
                            return;
                        }

                        var oneDriveAccounts = new ValueSet();
                        foreach (var account in oneDriveAccountsKey.GetSubKeyNames())
                        {
                            var accountKeyName = @$"{oneDriveAccountsKey.Name}\{account}";
                            var displayName = (string)Registry.GetValue(accountKeyName, "DisplayName", null);
                            var userFolder = (string)Registry.GetValue(accountKeyName, "UserFolder", null);
                            var accountName = string.IsNullOrWhiteSpace(displayName) ? "OneDrive" : $"OneDrive - {displayName}";
                            if (!string.IsNullOrWhiteSpace(userFolder) && !oneDriveAccounts.ContainsKey(accountName))
                            {
                                oneDriveAccounts.Add(accountName, userFolder);
                            }
                        }
                        oneDriveAccounts.Add("Count", oneDriveAccounts.Count);
                        await Win32API.SendMessageAsync(connection, oneDriveAccounts, message.Get("RequestID", (string)null));
                    }
                    catch
                    {
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Count", 0 } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "GetSharePointSyncLocationsFromOneDrive":
                    try
                    {
                        using var oneDriveAccountsKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\OneDrive\Accounts", false);

                        if (oneDriveAccountsKey == null)
                        {
                            await Win32API.SendMessageAsync(connection, new ValueSet() { { "Count", 0 } }, message.Get("RequestID", (string)null));
                            return;
                        }

                        var sharepointAccounts = new ValueSet();

                        foreach (var account in oneDriveAccountsKey.GetSubKeyNames())
                        {
                            var accountKeyName = @$"{oneDriveAccountsKey.Name}\{account}";
                            var displayName = (string)Registry.GetValue(accountKeyName, "DisplayName", null);
                            var userFolderToExcludeFromResults = (string)Registry.GetValue(accountKeyName, "UserFolder", null);
                            var accountName = string.IsNullOrWhiteSpace(displayName) ? "SharePoint" : $"SharePoint - {displayName}";

                            var sharePointSyncFolders = new List<string>();
                            var mountPointKeyName = @$"SOFTWARE\Microsoft\OneDrive\Accounts\{account}\ScopeIdToMountPointPathCache";
                            using (var mountPointsKey = Registry.CurrentUser.OpenSubKey(mountPointKeyName))
                            {
                                if (mountPointsKey == null)
                                {
                                    continue;
                                }

                                var valueNames = mountPointsKey.GetValueNames();
                                foreach (var valueName in valueNames)
                                {
                                    var value = (string)Registry.GetValue(@$"HKEY_CURRENT_USER\{mountPointKeyName}", valueName, null);
                                    if (!string.Equals(value, userFolderToExcludeFromResults, StringComparison.OrdinalIgnoreCase))
                                    {
                                        sharePointSyncFolders.Add(value);
                                    }
                                }
                            }

                            foreach (var sharePointSyncFolder in sharePointSyncFolders.OrderBy(o => o))
                            {
                                var parentFolder = Directory.GetParent(sharePointSyncFolder)?.FullName ?? string.Empty;
                                if (!sharepointAccounts.Any(acc => string.Equals(acc.Key, accountName, StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(parentFolder))
                                {
                                    sharepointAccounts.Add(accountName, parentFolder);
                                }
                            }
                        }

                        sharepointAccounts.Add("Count", sharepointAccounts.Count);
                        await Win32API.SendMessageAsync(connection, sharepointAccounts, message.Get("RequestID", (string)null));
                    }
                    catch
                    {
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Count", 0 } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "ShellFolder":
                    // Enumerate shell folder contents and send response to UWP
                    var folderPath = (string)message["folder"];
                    var responseEnum = new ValueSet();
                    var folderContentsList = await Win32API.StartSTATask(() =>
                    {
                        var flc = new List<ShellFileItem>();
                        try
                        {
                            using (var shellFolder = new ShellFolder(folderPath))
                            {
                                foreach (var folderItem in shellFolder)
                                {
                                    try
                                    {
                                        var shellFileItem = GetShellFileItem(folderItem);
                                        flc.Add(shellFileItem);
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        // Happens if files are being deleted
                                    }
                                    finally
                                    {
                                        folderItem.Dispose();
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                        return flc;
                    });
                    responseEnum.Add("Enumerate", JsonSerializer.Serialize(folderContentsList, includeFieldsOptions));
                    await Win32API.SendMessageAsync(connection, responseEnum, message.Get("RequestID", (string)null));
                    break;

                case "ShellLibrary":
                    await HandleShellLibraryMessage(message);
                    break;

                case "GetSelectedIconsFromDLL":
                    var selectedIconInfos = Win32API.ExtractSelectedIconsFromDLL((string)message["iconFile"], JsonSerializer.Deserialize<List<int>>((string)message["iconIndexes"]), Convert.ToInt32(message["requestedIconSize"]));
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "IconInfos", JsonSerializer.Serialize(selectedIconInfos) },
                    }, message.Get("RequestID", (string)null));
                    break;

                default:
                    if (message.ContainsKey("Application"))
                    {
                        var application = (string)message["Application"];
                        HandleApplicationLaunch(application, message);
                    }
                    else if (message.ContainsKey("ApplicationList"))
                    {
                        var applicationList = JsonSerializer.Deserialize<IEnumerable<string>>((string)message["ApplicationList"]);
                        HandleApplicationsLaunch(applicationList, message);
                    }
                    break;
            }
        }

        private static async void OnLibraryChanged(WatcherChangeTypes changeType, string oldPath, string newPath)
        {
            if (newPath != null && (!newPath.ToLower().EndsWith(ShellLibraryItem.EXTENSION) || !File.Exists(newPath)))
            {
                Debug.WriteLine($"Ignored library event: {changeType}, {oldPath} -> {newPath}");
                return;
            }

            Debug.WriteLine($"Library event: {changeType}, {oldPath} -> {newPath}");

            if (connection?.IsConnected ?? false)
            {
                var response = new ValueSet { { "Library", newPath ?? oldPath } };
                switch (changeType)
                {
                    case WatcherChangeTypes.Deleted:
                    case WatcherChangeTypes.Renamed:
                        response["OldPath"] = oldPath;
                        break;

                    default:
                        break;
                }
                if (!changeType.HasFlag(WatcherChangeTypes.Deleted))
                {
                    var library = ShellItem.Open(newPath) as ShellLibrary;
                    if (library == null)
                    {
                        Logger.Error($"Failed to open library after {changeType}: {newPath}");
                        return;
                    }
                    response["Item"] = JsonSerializer.Serialize(GetShellLibraryItem(library, newPath), includeFieldsOptions);
                    library.Dispose();
                }
                // Send message to UWP app to refresh items
                await Win32API.SendMessageAsync(connection, response);
            }
        }

        private static async Task HandleShellLibraryMessage(Dictionary<string, object> message)
        {
            switch ((string)message["action"])
            {
                case "Enumerate":
                    // Read library information and send response to UWP
                    var enumerateResponse = await Win32API.StartSTATask(() =>
                    {
                        var response = new ValueSet();
                        try
                        {
                            var libraryItems = new List<ShellLibraryItem>();
                            // https://docs.microsoft.com/en-us/windows/win32/search/-search-win7-development-scenarios#library-descriptions
                            var libFiles = Directory.EnumerateFiles(librariesPath, "*" + ShellLibraryItem.EXTENSION);
                            foreach (var libFile in libFiles)
                            {
                                using var shellItem = ShellItem.Open(libFile);
                                if (shellItem is ShellLibrary library)
                                {
                                    libraryItems.Add(GetShellLibraryItem(library, libFile));
                                }
                            }
                            response.Add("Enumerate", JsonSerializer.Serialize(libraryItems, includeFieldsOptions));
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e);
                        }
                        return response;
                    });
                    await Win32API.SendMessageAsync(connection, enumerateResponse, message.Get("RequestID", (string)null));
                    break;

                case "Create":
                    // Try create new library with the specified name and send response to UWP
                    var createResponse = await Win32API.StartSTATask(() =>
                    {
                        var response = new ValueSet();
                        try
                        {
                            using var library = new ShellLibrary((string)message["library"], Shell32.KNOWNFOLDERID.FOLDERID_Libraries, false);
                            response.Add("Create", JsonSerializer.Serialize(GetShellLibraryItem(library, library.GetDisplayName(ShellItemDisplayString.DesktopAbsoluteParsing)), includeFieldsOptions));
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e);
                        }
                        return response;
                    });
                    await Win32API.SendMessageAsync(connection, createResponse, message.Get("RequestID", (string)null));
                    break;

                case "Update":
                    // Update details of the specified library and send response to UWP
                    var updateResponse = await Win32API.StartSTATask(() =>
                    {
                        var response = new ValueSet();
                        try
                        {
                            var folders = message.ContainsKey("folders") ? JsonSerializer.Deserialize<string[]>((string)message["folders"]) : null;
                            var defaultSaveFolder = message.Get("defaultSaveFolder", (string)null);
                            var isPinned = message.Get("isPinned", (bool?)null);

                            bool updated = false;
                            var libPath = (string)message["library"];
                            using var library = ShellItem.Open(libPath) as ShellLibrary;
                            if (folders != null)
                            {
                                if (folders.Length > 0)
                                {
                                    var foldersToRemove = library.Folders.Where(f => !folders.Any(folderPath => string.Equals(folderPath, f.FileSystemPath, StringComparison.OrdinalIgnoreCase)));
                                    foreach (var toRemove in foldersToRemove)
                                    {
                                        library.Folders.Remove(toRemove);
                                        updated = true;
                                    }
                                    var foldersToAdd = folders.Distinct(StringComparer.OrdinalIgnoreCase)
                                                              .Where(folderPath => !library.Folders.Any(f => string.Equals(folderPath, f.FileSystemPath, StringComparison.OrdinalIgnoreCase)))
                                                              .Select(ShellItem.Open);
                                    foreach (var toAdd in foldersToAdd)
                                    {
                                        library.Folders.Add(toAdd);
                                        updated = true;
                                    }
                                    foreach (var toAdd in foldersToAdd)
                                    {
                                        toAdd.Dispose();
                                    }
                                }
                            }
                            if (defaultSaveFolder != null)
                            {
                                library.DefaultSaveFolder = ShellItem.Open(defaultSaveFolder);
                                updated = true;
                            }
                            if (isPinned != null)
                            {
                                library.PinnedToNavigationPane = isPinned == true;
                                updated = true;
                            }
                            if (updated)
                            {
                                library.Commit();
                                response.Add("Update", JsonSerializer.Serialize(GetShellLibraryItem(library, libPath), includeFieldsOptions));
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e);
                        }
                        return response;
                    });
                    await Win32API.SendMessageAsync(connection, updateResponse, message.Get("RequestID", (string)null));
                    break;
            }
        }

        private static async Task ParseNetworkDriveOperationAsync(Dictionary<string, object> message)
        {
            switch (message.Get("netdriveop", ""))
            {
                case "GetNetworkLocations":
                    var networkLocations = await Win32API.StartSTATask(() =>
                    {
                        var netl = new ValueSet();
                        using (var nethood = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_NetHood))
                        {
                            foreach (var link in nethood)
                            {
                                var linkPath = (string)link.Properties["System.Link.TargetParsingPath"];
                                if (linkPath != null)
                                {
                                    netl.Add(link.Name, linkPath);
                                }
                            }
                        }
                        return netl;
                    });
                    networkLocations.Add("Count", networkLocations.Count);
                    await Win32API.SendMessageAsync(connection, networkLocations, message.Get("RequestID", (string)null));
                    break;

                case "OpenMapNetworkDriveDialog":
                    var hwnd = (long)message["HWND"];
                    NetworkDrivesAPI.OpenMapNetworkDriveDialog(hwnd);
                    break;

                case "DisconnectNetworkDrive":
                    var drivePath = (string)message["drive"];
                    NetworkDrivesAPI.DisconnectNetworkDrive(drivePath);
                    break;
            }
        }

        private static object HandleMenuMessage(Dictionary<string, object> message, Win32API.DisposableDictionary table)
        {
            switch (message.Get("Arguments", ""))
            {
                case "LoadContextMenu":
                    var contextMenuResponse = new ValueSet();
                    var filePath = (string)message["FilePath"];
                    var extendedMenu = (bool)message["ExtendedMenu"];
                    var showOpenMenu = (bool)message["ShowOpenMenu"];
                    var split = filePath.Split('|').Where(x => !string.IsNullOrWhiteSpace(x));
                    var cMenuLoad = Win32API.ContextMenu.GetContextMenuForFiles(split.ToArray(),
                        (extendedMenu ? Shell32.CMF.CMF_EXTENDEDVERBS : Shell32.CMF.CMF_NORMAL) | Shell32.CMF.CMF_SYNCCASCADEMENU, FilterMenuItems(showOpenMenu));
                    table.SetValue("MENU", cMenuLoad);
                    return cMenuLoad;

                case "ExecAndCloseContextMenu":
                    var cMenuExec = table.GetValue<Win32API.ContextMenu>("MENU");
                    if (message.TryGetValue("ItemID", out var menuId))
                    {
                        switch (message.Get("CommandString", (string)null))
                        {
                            case "format":
                                var drivePath = cMenuExec.ItemsPath.First();
                                Win32API.OpenFormatDriveDialog(drivePath);
                                break;

                            default:
                                cMenuExec?.InvokeItem((int)(long)menuId);
                                break;
                        }
                    }
                    // The following line is needed to cleanup resources when menu is closed.
                    // Unfortunately if you uncomment it some menu items will randomly stop working.
                    // Resource cleanup is currently done on app closing,
                    // if we find a solution for the issue above, we should cleanup as soon as a menu is closed.
                    //table.RemoveValue("MENU");
                    return null;

                default:
                    return null;
            }
        }

        private static Func<string, bool> FilterMenuItems(bool showOpenMenu)
        {
            var knownItems = new List<string>()
            {
                "opennew", "openas", "opencontaining", "opennewprocess",
                "runas", "runasuser", "pintohome", "PinToStartScreen",
                "cut", "copy", "paste", "delete", "properties", "link",
                "Windows.ModernShare", "Windows.Share", "setdesktopwallpaper",
                "eject", "rename", "explore", "openinfiles",
                Win32API.ExtractStringFromDLL("shell32.dll", 30312), // SendTo menu
                Win32API.ExtractStringFromDLL("shell32.dll", 34593), // Add to collection
            };

            bool filterMenuItemsImpl(string menuItem)
            {
                return string.IsNullOrEmpty(menuItem) ? false : knownItems.Contains(menuItem)
                    || (!showOpenMenu && menuItem.Equals("open", StringComparison.OrdinalIgnoreCase));
            }

            return filterMenuItemsImpl;
        }

        private static async Task ParseFileOperationAsync(Dictionary<string, object> message)
        {
            switch (message.Get("fileop", ""))
            {
                case "Clipboard":
                    await Win32API.StartSTATask(() =>
                    {
                        System.Windows.Forms.Clipboard.Clear();
                        var fileToCopy = (string)message["filepath"];
                        var operation = (DataPackageOperation)(long)message["operation"];
                        var fileList = new System.Collections.Specialized.StringCollection();
                        fileList.AddRange(fileToCopy.Split('|'));
                        if (operation == DataPackageOperation.Copy)
                        {
                            System.Windows.Forms.Clipboard.SetFileDropList(fileList);
                        }
                        else if (operation == DataPackageOperation.Move)
                        {
                            byte[] moveEffect = new byte[] { 2, 0, 0, 0 };
                            MemoryStream dropEffect = new MemoryStream();
                            dropEffect.Write(moveEffect, 0, moveEffect.Length);
                            var data = new System.Windows.Forms.DataObject();
                            data.SetFileDropList(fileList);
                            data.SetData("Preferred DropEffect", dropEffect);
                            System.Windows.Forms.Clipboard.SetDataObject(data, true);
                        }
                        return true;
                    });
                    break;

                case "DragDrop":
                    cancellation.Cancel();
                    cancellation.Dispose();
                    cancellation = new CancellationTokenSource();
                    var dropPath = (string)message["droppath"];
                    var dropText = (string)message["droptext"];
                    var drops = Win32API.StartSTATask<List<string>>(() =>
                    {
                        var form = new DragDropForm(dropPath, dropText, cancellation.Token);
                        System.Windows.Forms.Application.Run(form);
                        return form.DropTargets;
                    });
                    break;

                case "DeleteItem":
                    var fileToDeletePath = (string)message["filepath"];
                    var permanently = (bool)message["permanently"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        if (!permanently)
                        {
                            op.Options |= ShellFileOperations.OperationFlags.AllowUndo;
                        }
                        using var shi = new ShellItem(fileToDeletePath);
                        op.QueueDeleteOperation(shi);
                        var deleteTcs = new TaskCompletionSource<bool>();
                        op.PostDeleteItem += (s, e) => deleteTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await deleteTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "RenameItem":
                    var fileToRenamePath = (string)message["filepath"];
                    var newName = (string)message["newName"];
                    var overwriteOnRename = (bool)message["overwrite"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        op.Options |= !overwriteOnRename ? ShellFileOperations.OperationFlags.PreserveFileExtensions | ShellFileOperations.OperationFlags.RenameOnCollision : 0;
                        using var shi = new ShellItem(fileToRenamePath);
                        op.QueueRenameOperation(shi, newName);
                        var renameTcs = new TaskCompletionSource<bool>();
                        op.PostRenameItem += (s, e) => renameTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await renameTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "MoveItem":
                    var fileToMovePath = (string)message["filepath"];
                    var moveDestination = (string)message["destpath"];
                    var overwriteOnMove = (bool)message["overwrite"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        op.Options |= !overwriteOnMove ? ShellFileOperations.OperationFlags.PreserveFileExtensions | ShellFileOperations.OperationFlags.RenameOnCollision : 0;
                        using var shi = new ShellItem(fileToMovePath);
                        using var shd = new ShellFolder(Path.GetDirectoryName(moveDestination));
                        op.QueueMoveOperation(shi, shd, Path.GetFileName(moveDestination));
                        var moveTcs = new TaskCompletionSource<bool>();
                        op.PostMoveItem += (s, e) => moveTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await moveTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "CopyItem":
                    var fileToCopyPath = (string)message["filepath"];
                    var copyDestination = (string)message["destpath"];
                    var overwriteOnCopy = (bool)message["overwrite"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        op.Options |= !overwriteOnCopy ? ShellFileOperations.OperationFlags.PreserveFileExtensions | ShellFileOperations.OperationFlags.RenameOnCollision : 0;
                        using var shi = new ShellItem(fileToCopyPath);
                        using var shd = new ShellFolder(Path.GetDirectoryName(copyDestination));
                        op.QueueCopyOperation(shi, shd, Path.GetFileName(copyDestination));
                        var copyTcs = new TaskCompletionSource<bool>();
                        op.PostCopyItem += (s, e) => copyTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await copyTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "ParseLink":
                    var linkPath = (string)message["filepath"];
                    try
                    {
                        if (linkPath.EndsWith(".lnk"))
                        {
                            using var link = new ShellLink(linkPath, LinkResolution.NoUIWithMsgPump, null, TimeSpan.FromMilliseconds(100));
                            await Win32API.SendMessageAsync(connection, new ValueSet()
                            {
                                { "TargetPath", link.TargetPath },
                                { "Arguments", link.Arguments },
                                { "WorkingDirectory", link.WorkingDirectory },
                                { "RunAsAdmin", link.RunAsAdministrator },
                                { "IsFolder", !string.IsNullOrEmpty(link.TargetPath) && link.Target.IsFolder }
                            }, message.Get("RequestID", (string)null));
                        }
                        else if (linkPath.EndsWith(".url"))
                        {
                            var linkUrl = await Win32API.StartSTATask(() =>
                            {
                                var ipf = new Url.IUniformResourceLocator();
                                (ipf as System.Runtime.InteropServices.ComTypes.IPersistFile).Load(linkPath, 0);
                                ipf.GetUrl(out var retVal);
                                return retVal;
                            });
                            await Win32API.SendMessageAsync(connection, new ValueSet()
                            {
                                { "TargetPath", linkUrl },
                                { "Arguments", null },
                                { "WorkingDirectory", null },
                                { "RunAsAdmin", false },
                                { "IsFolder", false }
                            }, message.Get("RequestID", (string)null));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Could not parse shortcut
                        Logger.Warn(ex, ex.Message);
                        await Win32API.SendMessageAsync(connection, new ValueSet()
                            {
                                { "TargetPath", null },
                                { "Arguments", null },
                                { "WorkingDirectory", null },
                                { "RunAsAdmin", false },
                                { "IsFolder", false }
                            }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "CreateLink":
                case "UpdateLink":
                    var linkSavePath = (string)message["filepath"];
                    var targetPath = (string)message["targetpath"];
                    if (linkSavePath.EndsWith(".lnk"))
                    {
                        var arguments = (string)message["arguments"];
                        var workingDirectory = (string)message["workingdir"];
                        var runAsAdmin = (bool)message["runasadmin"];
                        using var newLink = new ShellLink(targetPath, arguments, workingDirectory);
                        newLink.RunAsAdministrator = runAsAdmin;
                        newLink.SaveAs(linkSavePath); // Overwrite if exists
                    }
                    else if (linkSavePath.EndsWith(".url"))
                    {
                        await Win32API.StartSTATask(() =>
                        {
                            var ipf = new Url.IUniformResourceLocator();
                            ipf.SetUrl(targetPath, Url.IURL_SETURL_FLAGS.IURL_SETURL_FL_GUESS_PROTOCOL);
                            (ipf as System.Runtime.InteropServices.ComTypes.IPersistFile).Save(linkSavePath, false); // Overwrite if exists
                            return true;
                        });
                    }
                    break;
            }
        }

        private static async Task ParseRecycleBinActionAsync(Dictionary<string, object> message, string action)
        {
            switch (action)
            {
                case "Empty":
                    // Shell function to empty recyclebin
                    Shell32.SHEmptyRecycleBin(IntPtr.Zero, null, Shell32.SHERB.SHERB_NOCONFIRMATION | Shell32.SHERB.SHERB_NOPROGRESSUI);
                    break;

                case "Query":
                    var responseQuery = new ValueSet();
                    Win32API.SHQUERYRBINFO queryBinInfo = new Win32API.SHQUERYRBINFO();
                    queryBinInfo.cbSize = Marshal.SizeOf(queryBinInfo);
                    var res = Win32API.SHQueryRecycleBin("", ref queryBinInfo);
                    if (res == HRESULT.S_OK)
                    {
                        var numItems = queryBinInfo.i64NumItems;
                        var binSize = queryBinInfo.i64Size;
                        responseQuery.Add("NumItems", numItems);
                        responseQuery.Add("BinSize", binSize);
                        await Win32API.SendMessageAsync(connection, responseQuery, message.Get("RequestID", (string)null));
                    }
                    break;

                default:
                    break;
            }
        }

        private static ShellLibraryItem GetShellLibraryItem(ShellLibrary library, string filePath)
        {
            var libraryItem = new ShellLibraryItem
            {
                FullPath = filePath,
                AbsolutePath = library.GetDisplayName(ShellItemDisplayString.DesktopAbsoluteParsing),
                RelativePath = library.GetDisplayName(ShellItemDisplayString.ParentRelativeParsing),
                DisplayName = library.GetDisplayName(ShellItemDisplayString.NormalDisplay),
                IsPinned = library.PinnedToNavigationPane,
            };
            var folders = library.Folders;
            if (folders.Count > 0)
            {
                libraryItem.DefaultSaveFolder = library.DefaultSaveFolder.FileSystemPath;
                libraryItem.Folders = folders.Select(f => f.FileSystemPath).ToArray();
            }
            return libraryItem;
        }

        private static ShellFileItem GetShellFileItem(ShellItem folderItem)
        {
            bool isFolder = folderItem.IsFolder && Path.GetExtension(folderItem.Name) != ".zip";
            if (folderItem.Properties == null)
            {
                return new ShellFileItem(isFolder, folderItem.FileSystemPath, Path.GetFileName(folderItem.Name), folderItem.Name, DateTime.Now, DateTime.Now, DateTime.Now, null, 0, null);
            }
            folderItem.Properties.TryGetValue<string>(
                Ole32.PROPERTYKEY.System.ParsingPath, out var parsingPath);
            parsingPath ??= folderItem.FileSystemPath; // True path on disk
            folderItem.Properties.TryGetValue<string>(
                Ole32.PROPERTYKEY.System.ItemNameDisplay, out var fileName);
            fileName ??= Path.GetFileName(folderItem.Name); // Original file name
            string filePath = folderItem.Name; // Original file path + name (recycle bin only)
            folderItem.Properties.TryGetValue<System.Runtime.InteropServices.ComTypes.FILETIME?>(
                Ole32.PROPERTYKEY.System.Recycle.DateDeleted, out var fileTime);
            var recycleDate = fileTime?.ToDateTime().ToLocalTime() ?? DateTime.Now; // This is LocalTime
            folderItem.Properties.TryGetValue<System.Runtime.InteropServices.ComTypes.FILETIME?>(
                Ole32.PROPERTYKEY.System.DateModified, out fileTime);
            var modifiedDate = fileTime?.ToDateTime().ToLocalTime() ?? DateTime.Now; // This is LocalTime
            folderItem.Properties.TryGetValue<System.Runtime.InteropServices.ComTypes.FILETIME?>(
                Ole32.PROPERTYKEY.System.DateCreated, out fileTime);
            var createdDate = fileTime?.ToDateTime().ToLocalTime() ?? DateTime.Now; // This is LocalTime
            string fileSize = folderItem.Properties.TryGetValue<ulong?>(
                Ole32.PROPERTYKEY.System.Size, out var fileSizeBytes) ?
                folderItem.Properties.GetPropertyString(Ole32.PROPERTYKEY.System.Size) : null;
            folderItem.Properties.TryGetValue<string>(
                Ole32.PROPERTYKEY.System.ItemTypeText, out var fileType);
            return new ShellFileItem(isFolder, parsingPath, fileName, filePath, recycleDate, modifiedDate, createdDate, fileSize, fileSizeBytes ?? 0, fileType);
        }

        private static void HandleApplicationsLaunch(IEnumerable<string> applications, Dictionary<string, object> message)
        {
            foreach (var application in applications)
            {
                HandleApplicationLaunch(application, message);
            }
        }

        private static async void HandleApplicationLaunch(string application, Dictionary<string, object> message)
        {
            var arguments = message.Get("Arguments", "");
            var workingDirectory = message.Get("WorkingDirectory", "");
            var currentWindows = Win32API.GetDesktopWindows();

            try
            {
                using Process process = new Process();
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.FileName = application;
                // Show window if workingDirectory (opening terminal)
                process.StartInfo.CreateNoWindow = string.IsNullOrEmpty(workingDirectory);
                if (arguments == "runas")
                {
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runas";
                    if (Path.GetExtension(application).ToLower() == ".msi")
                    {
                        process.StartInfo.FileName = "msiexec.exe";
                        process.StartInfo.Arguments = $"/a \"{application}\"";
                    }
                }
                else if (arguments == "runasuser")
                {
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.Verb = "runasuser";
                    if (Path.GetExtension(application).ToLower() == ".msi")
                    {
                        process.StartInfo.FileName = "msiexec.exe";
                        process.StartInfo.Arguments = $"/i \"{application}\"";
                    }
                }
                else
                {
                    process.StartInfo.Arguments = arguments;
                }
                process.StartInfo.WorkingDirectory = workingDirectory;
                process.Start();
                Win32API.BringToForeground(currentWindows);
            }
            catch (Win32Exception)
            {
                using Process process = new Process();
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.FileName = application;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.WorkingDirectory = workingDirectory;
                try
                {
                    process.Start();
                    Win32API.BringToForeground(currentWindows);
                }
                catch (Win32Exception)
                {
                    try
                    {
                        await Win32API.StartSTATask(() =>
                        {
                            var split = application.Split('|').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => GetMtpPath(x));
                            if (split.Count() == 1)
                            {
                                Process.Start(split.First());
                                Win32API.BringToForeground(currentWindows);
                            }
                            else
                            {
                                var groups = split.GroupBy(x => new
                                {
                                    Dir = Path.GetDirectoryName(x),
                                    Prog = Win32API.GetFileAssociationAsync(x).Result ?? Path.GetExtension(x)
                                });
                                foreach (var group in groups)
                                {
                                    if (!group.Any())
                                    {
                                        continue;
                                    }
                                    using var cMenu = Win32API.ContextMenu.GetContextMenuForFiles(group.ToArray(), Shell32.CMF.CMF_DEFAULTONLY);
                                    cMenu?.InvokeVerb(Shell32.CMDSTR_OPEN);
                                }
                            }
                            return true;
                        });
                    }
                    catch (Win32Exception)
                    {
                        // Cannot open file (e.g DLL)
                    }
                    catch (ArgumentException)
                    {
                        // Cannot open file (e.g DLL)
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Invalid file path
            }
        }

        private static bool HandleCommandLineArgs()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            var arguments = (string)localSettings.Values["Arguments"];
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                localSettings.Values.Remove("Arguments");

                if (arguments == "ShellCommand")
                {
                    // Kill the process. This is a BRUTAL WAY to kill a process.
#if DEBUG
                    // In debug mode this kills this process too??
#else
                    var pid = (int)localSettings.Values["pid"];
                    Process.GetProcessById(pid).Kill();
#endif

                    using Process process = new Process();
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.FileName = "explorer.exe";
                    process.StartInfo.CreateNoWindow = false;
                    process.StartInfo.Arguments = (string)localSettings.Values["ShellCommand"];
                    process.Start();

                    return true;
                }
            }
            return false;
        }

        private static string GetMtpPath(string executable)
        {
            if (executable.StartsWith("\\\\?\\"))
            {
                using var computer = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_ComputerFolder);
                using var device = computer.FirstOrDefault(i => executable.Replace("\\\\?\\", "").StartsWith(i.Name));
                var deviceId = device?.ParsingName;
                var itemPath = Regex.Replace(executable, @"^\\\\\?\\[^\\]*\\?", "");
                return deviceId != null ? Path.Combine(deviceId, itemPath) : executable;
            }
            return executable;
        }
    }

    public class DictionaryStringObjectJsonConverter : JsonConverter<Dictionary<string, object>>
    {
        public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"JsonTokenType was of type {reader.TokenType}, only objects are supported");
            }

            var dictionary = new Dictionary<string, object>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return dictionary;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("JsonTokenType was not PropertyName");
                }

                var propertyName = reader.GetString();

                if (string.IsNullOrWhiteSpace(propertyName))
                {
                    throw new JsonException("Failed to get property name");
                }

                reader.Read();

                dictionary.Add(propertyName, ExtractValue(ref reader, options));
            }

            return dictionary;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }

        private object ExtractValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    if (reader.TryGetDateTime(out var date))
                    {
                        return date;
                    }
                    return reader.GetString();
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var result))
                    {
                        return result;
                    }
                    return reader.GetDecimal();
                case JsonTokenType.StartObject:
                    return Read(ref reader, null, options);
                case JsonTokenType.StartArray:
                    var list = new List<object>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        list.Add(ExtractValue(ref reader, options));
                    }
                    return list;
                default:
                    throw new JsonException($"'{reader.TokenType}' is not supported");
            }
        }
    }
}