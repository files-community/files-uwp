using Files.Common;
using Files.DataModels.NavigationControlItems;
using Files.Extensions;
using Files.Helpers;
using Files.UserControls;
using Files.ViewModels;
using Files.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Search;

namespace Files.Filesystem
{
    public static class StorageFileExtensions
    {
        private static SettingsViewModel AppSettings => App.AppSettings;

        private static PathBoxItem GetPathItem(string component, string path)
        {
            if (component.StartsWith(AppSettings.RecycleBinPath))
            {
                // Handle the recycle bin: use the localized folder name
                return new PathBoxItem()
                {
                    Title = ApplicationData.Current.LocalSettings.Values.Get("RecycleBin_Title", "Recycle Bin"),
                    Path = path,
                };
            }
            else if (component.Contains(":"))
            {
                var allDrives = SidebarControl.SideBarItems.Where(x => (x as LocationItem)?.ChildItems != null).SelectMany(x => (x as LocationItem).ChildItems);
                return new PathBoxItem()
                {
                    Title = allDrives.FirstOrDefault(y => y.ItemType == NavigationControlItemType.Drive && y.Path.Contains(component, StringComparison.OrdinalIgnoreCase)) != null ?
                            allDrives.FirstOrDefault(y => y.ItemType == NavigationControlItemType.Drive && y.Path.Contains(component, StringComparison.OrdinalIgnoreCase)).Text : $@"Drive ({component}\)",
                    Path = path,
                };
            }
            else
            {
                return new PathBoxItem
                {
                    Title = component,
                    Path = path
                };
            }
        }

        public static List<PathBoxItem> GetDirectoryPathComponents(string value)
        {
            List<PathBoxItem> pathBoxItems = new List<PathBoxItem>();

            if (value.Contains("/"))
            {
                if (!value.EndsWith("/"))
                {
                    value += "/";
                }
            }
            else if (!value.EndsWith("\\"))
            {
                value += "\\";
            }

            int lastIndex = 0;

            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == Path.DirectorySeparatorChar || value[i] == Path.AltDirectorySeparatorChar || value[i] == '?')
                {
                    if (lastIndex == i)
                    {
                        lastIndex = i + 1;
                        continue;
                    }

                    var component = value.Substring(lastIndex, i - lastIndex);
                    var path = value.Substring(0, i + 1);
                    if (!path.Equals("ftp:/", StringComparison.OrdinalIgnoreCase))
                    {
                        pathBoxItems.Add(GetPathItem(component, path));
                    }

                    lastIndex = i + 1;
                }
            }

            return pathBoxItems;
        }

        public async static Task<IStorageFolder> DangerousGetFolderWithPathFromPathAsync(string value, IStorageFolder rootFolder = null, IStorageFolder parentFolder = null)
        {
            if (rootFolder != null)
            {
                var currComponents = GetDirectoryPathComponents(value);

                if (rootFolder.Path == value)
                {
                    return rootFolder;
                }
                else if (parentFolder != null && value.IsSubPathOf(parentFolder.Path))
                {
                    var folder = parentFolder;
                    var prevComponents = GetDirectoryPathComponents(parentFolder.Path);
                    foreach (var component in currComponents.ExceptBy(prevComponents, c => c.Path))
                    {
                        folder = await folder.GetFolderAsync(component.Title);
                    }
                    return folder;
                }
                else if (value.IsSubPathOf(rootFolder.Path))
                {
                    var folder = rootFolder;
                    foreach (var component in currComponents.Skip(1))
                    {
                        folder = await folder.GetFolderAsync(component.Title);
                    }
                    return folder;
                }
            }

            if (parentFolder != null && !Path.IsPathRooted(value))
            {
                // Relative path
                var fullPath = Path.GetFullPath(Path.Combine(parentFolder.Path, value));
                return await StorageFolder.GetFolderFromPathAsync(fullPath);
            }
            else
            {
                return await StorageFolder.GetFolderFromPathAsync(value);
            }
        }

        public async static Task<IStorageFolder> DangerousGetFolderFromPathAsync(string value,
                                                                                IStorageFolder rootFolder = null,
                                                                                IStorageFolder parentFolder = null)
        {
            return await DangerousGetFolderWithPathFromPathAsync(value, rootFolder, parentFolder);
        }

        public async static Task<IStorageFile> DangerousGetFileWithPathFromPathAsync(string value,
                                                                                            IStorageFolder rootFolder = null,
                                                                                            IStorageFolder parentFolder = null)
        {
            if (rootFolder != null)
            {
                var currComponents = GetDirectoryPathComponents(value);

                if (parentFolder != null && value.IsSubPathOf(parentFolder.Path))
                {
                    var folder = parentFolder;
                    var prevComponents = GetDirectoryPathComponents(parentFolder.Path);
                    foreach (var component in currComponents.ExceptBy(prevComponents, c => c.Path).SkipLast(1))
                    {
                        folder = await folder.GetFolderAsync(component.Title);
                    }
                    var file = await folder.GetFileAsync(currComponents.Last().Title);
                    return file;
                }
                else if (value.IsSubPathOf(rootFolder.Path))
                {
                    var folder = rootFolder;
                    foreach (var component in currComponents.Skip(1).SkipLast(1))
                    {
                        folder = await folder.GetFolderAsync(component.Title);
                    }
                    var file = await folder.GetFileAsync(currComponents.Last().Title);
                    return file;
                }
            }

            if (parentFolder != null && !Path.IsPathRooted(value))
            {
                // Relative path
                var fullPath = Path.GetFullPath(Path.Combine(parentFolder.Path, value));
                return await StorageFile.GetFileFromPathAsync(fullPath);
            }
            else
            {
                return await StorageFile.GetFileFromPathAsync(value);
            }
        }

        public async static Task<IStorageFile> DangerousGetFileFromPathAsync(string value,
                                                                            IStorageFolder rootFolder = null,
                                                                            IStorageFolder parentFolder = null)
        {
            return await DangerousGetFileWithPathFromPathAsync(value, rootFolder, parentFolder);
        }

        public async static Task<IList<StorageFolder>> GetFoldersWithPathAsync(this StorageFolder parentFolder, string nameFilter, uint maxNumberOfItems = uint.MaxValue)
        {
            var queryOptions = new QueryOptions();
            queryOptions.ApplicationSearchFilter = $"System.FileName:{nameFilter}*";
            StorageFolderQueryResult queryResult = parentFolder.CreateFolderQueryWithOptions(queryOptions);

            return (await queryResult.GetFoldersAsync(0, maxNumberOfItems)).ToList();
        }

        public static string GetPathWithoutEnvironmentVariable(string path)
        {
            if (path.StartsWith("~\\"))
            {
                path = $"{AppSettings.HomePath}{path.Remove(0, 1)}";
            }

            if (path.Contains("%temp%", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace("%temp%", AppSettings.TempPath, StringComparison.OrdinalIgnoreCase);
            }

            if (path.Contains("%tmp%", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace("%tmp%", AppSettings.TempPath, StringComparison.OrdinalIgnoreCase);
            }

            if (path.Contains("%localappdata%", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace("%localappdata%", AppSettings.LocalAppDataPath, StringComparison.OrdinalIgnoreCase);
            }

            if (path.Contains("%homepath%", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Replace("%homepath%", AppSettings.HomePath, StringComparison.OrdinalIgnoreCase);
            }

            return Environment.ExpandEnvironmentVariables(path);
        }

        public static bool AreItemsInSameDrive(this IEnumerable<IStorageItem> storageItems, string destinationPath)
        {
            try
            {
                return storageItems.Any(storageItem =>
                    Path.GetPathRoot(storageItem.Path).Equals(
                        Path.GetPathRoot(destinationPath),
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        public static bool AreItemsAlreadyInFolder(this IEnumerable<IStorageItem> storageItems, string destinationPath)
        {
            try
            {
                return storageItems.All(storageItem =>
                    Path.GetDirectoryName(storageItem.Path).Equals(
                        destinationPath.TrimPath(), StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}