using Files.Enums;
using Files.Filesystem;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Files.Helpers
{
    /// <summary>
    /// <see cref="IStorageItem"/> related Helpers
    /// </summary>
    public static class StorageItemHelpers
    {
        public static async Task<IStorageItem> ToStorageItem(this IStorageItemWithPath item, IShellPage associatedInstance = null)
        {
            return (await item.ToStorageItemResult(associatedInstance)).Result;
        }

        public static async Task<TOut> ToStorageItem<TOut>(string path, IShellPage associatedInstance = null) where TOut : IStorageItem
        {
            FilesystemResult<StorageFile> file = null;
            FilesystemResult<StorageFolder> folder = null;

            if (path.ToLower().EndsWith(".lnk") || path.ToLower().EndsWith(".url"))
            {
                // TODO: In the future, when IStorageItemWithPath will inherit from IStorageItem,
                //      we could implement this code here for getting .lnk files
                //      for now, we can't

                return default;

                if (false) // Prevent unnecessary exceptions
                {
                    Debugger.Break();
                    throw new ArgumentException("Function ToStorageItem<TOut>() does not support converting from .lnk and .url files");
                }
            }

            if (typeof(IStorageFile).IsAssignableFrom(typeof(TOut)))
            {
                await GetFile();
            }
            else if (typeof(IStorageFolder).IsAssignableFrom(typeof(TOut)))
            {
                await GetFolder();
            }
            else if (typeof(IStorageItem).IsAssignableFrom(typeof(TOut)))
            {
                if (System.IO.Path.HasExtension(path)) // Probably a file
                {
                    await GetFile();
                }
                else // Possibly a folder
                {
                    await GetFolder();

                    if (!folder)
                    {
                        // It wasn't a folder, so check file then because it wasn't checked
                        await GetFile();
                    }
                }
            }

            if (file != null && file)
            {
                return (TOut)(IStorageItem)file.Result;
            }
            else if (folder != null && folder)
            {
                return (TOut)(IStorageItem)folder.Result;
            }

            return default;

            // Extensions

            async Task GetFile()
            {
                if (associatedInstance == null)
                {
                    file = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(path));
                }
                else
                {
                    file = await associatedInstance?.FilesystemViewModel?.GetFileFromPathAsync(path);
                }
            }

            async Task GetFolder()
            {
                if (associatedInstance == null)
                {
                    folder = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(path));
                }
                else
                {
                    folder = await associatedInstance?.FilesystemViewModel?.GetFolderFromPathAsync(path);
                }
            }
        }

        public static async Task<long> GetFileSize(this IStorageFile file)
        {
            BasicProperties properties = await file.GetBasicPropertiesAsync();
            return (long)properties.Size;
        }

        public static async Task<FilesystemResult<IStorageItem>> ToStorageItemResult(this IStorageItemWithPath item, IShellPage associatedInstance = null)
        {
            var returnedItem = new FilesystemResult<IStorageItem>(null, FileSystemStatusCode.Generic);
            if (!string.IsNullOrEmpty(item.Path))
            {
                returnedItem = (item.ItemType == FilesystemItemType.File) ?
                    ToType<IStorageItem, StorageFile>(associatedInstance != null ?
                        await associatedInstance.FilesystemViewModel.GetFileFromPathAsync(item.Path) :
                        await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(item.Path))) :
                    ToType<IStorageItem, StorageFolder>(associatedInstance != null ?
                        await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(item.Path) :
                        await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(item.Path)));
            }
            if (returnedItem.Result == null && item.Item != null)
            {
                returnedItem = new FilesystemResult<IStorageItem>(item.Item, FileSystemStatusCode.Success);
            }
            return returnedItem;
        }

        public static IStorageItem FromPathAndType(string customPath, FilesystemItemType? itemType)
        {
            return (itemType == FilesystemItemType.File) ?
                    (IStorageItemWithPath)new StorageFileWithPath(null, customPath) :
                    (IStorageItemWithPath)new StorageFolderWithPath(null, customPath);
        }

        public static async Task<FilesystemItemType> GetTypeFromPath(string path, IShellPage associatedInstance = null)
        {
            IStorageItem item = await ToStorageItem<IStorageItem>(path, associatedInstance);

            return item == null ? FilesystemItemType.File : (item.IsOfType(StorageItemTypes.Folder) ? FilesystemItemType.Directory : FilesystemItemType.File);
        }

        public static bool Exists(string path)
        {
            return NativeFileOperationsHelper.GetFileAttributesExFromApp(path, NativeFileOperationsHelper.GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard, out _);
        }

        public static IStorageItemWithPath FromStorageItem(this IStorageItem item, string customPath = null, FilesystemItemType? itemType = null)
        {
            if (item == null)
            {
                return FromPathAndType(customPath, itemType);
            }
            else if (item.IsOfType(StorageItemTypes.File))
            {
                return new StorageFileWithPath(item as StorageFile, string.IsNullOrEmpty(item.Path) ? customPath : item.Path);
            }
            else if (item.IsOfType(StorageItemTypes.Folder))
            {
                return new StorageFolderWithPath(item as StorageFolder, string.IsNullOrEmpty(item.Path) ? customPath : item.Path);
            }
            return null;
        }

        public static FilesystemResult<T> ToType<T, V>(FilesystemResult<V> result) where T : class
        {
            return new FilesystemResult<T>(result.Result as T, result.ErrorCode);
        }
    }
}