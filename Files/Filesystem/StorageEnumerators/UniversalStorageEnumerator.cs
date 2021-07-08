using ByteSizeLib;
using Files.Extensions;
using Files.Helpers;
using Files.Views.LayoutModes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI.Xaml.Media.Imaging;

namespace Files.Filesystem.StorageEnumerators
{
    public static class UniversalStorageEnumerator
    {
        public static async Task<List<ListedItem>> ListEntries(
            StorageFolder rootFolder,
            IStorageFolder currentStorageFolder,
            string returnformat,
            Type sourcePageType,
            CancellationToken cancellationToken,
            int countLimit,
            Func<List<ListedItem>, Task> intermediateAction
        )
        {
            var sampler = new IntervalSampler(500);
            var tempList = new List<ListedItem>();
            uint count = 0;
            var firstRound = true;
            while (true)
            {
                IReadOnlyList<IStorageItem> items;
                uint maxItemsToRetrieve = 300;

                if (intermediateAction == null)
                {
                    // without intermediate action increase batches significantly
                    maxItemsToRetrieve = 1000;
                }
                else if (firstRound)
                {
                    maxItemsToRetrieve = 32;
                    firstRound = false;
                }
                try
                {
                    items = await rootFolder.GetItemsAsync(count, maxItemsToRetrieve);
                    if (items == null || items.Count == 0)
                    {
                        break;
                    }
                }
                catch (NotImplementedException)
                {
                    break;
                }
                catch (Exception ex) when (
                    ex is UnauthorizedAccessException
                    || ex is FileNotFoundException
                    || (uint)ex.HResult == 0x80070490) // ERROR_NOT_FOUND
                {
                    // If some unexpected exception is thrown - enumerate this folder file by file - just to be sure
                    items = await EnumerateFileByFile(rootFolder, count, maxItemsToRetrieve);
                }
                foreach (var item in items)
                {
                    if (item.IsOfType(StorageItemTypes.Folder))
                    {
                        var folder = await AddFolderAsync(item as StorageFolder, currentStorageFolder, returnformat, cancellationToken);
                        if (folder != null)
                        {
                            tempList.Add(folder);
                        }
                    }
                    else
                    {
                        var file = item as StorageFile;
                        var fileEntry = await AddFileAsync(file, currentStorageFolder, returnformat, true, sourcePageType, cancellationToken);
                        if (fileEntry != null)
                        {
                            tempList.Add(fileEntry);
                        }
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                count += maxItemsToRetrieve;

                if (countLimit > -1 && count >= countLimit)
                {
                    break;
                }

                if (intermediateAction != null && (items.Count == maxItemsToRetrieve || sampler.CheckNow()))
                {
                    await intermediateAction(tempList);
                    // clear the temporary list every time we do an intermediate action
                    tempList.Clear();
                }
            }
            return tempList;
        }

        private static async Task<IReadOnlyList<IStorageItem>> EnumerateFileByFile(StorageFolder rootFolder, uint startFrom, uint itemsToIterate)
        {
            var tempList = new List<IStorageItem>();
            for (var i = startFrom; i < startFrom + itemsToIterate; i++)
            {
                IStorageItem item;
                try
                {
                    var results = await rootFolder.GetItemsAsync(i, 1);
                    item = results?.FirstOrDefault();
                    if (item == null)
                    {
                        break;
                    }
                }
                catch (NotImplementedException)
                {
                    break;
                }
                catch (Exception ex) when (
                    ex is UnauthorizedAccessException
                    || ex is FileNotFoundException
                    || (uint)ex.HResult == 0x80070490) // ERROR_NOT_FOUND
                {
                    continue;
                }
                tempList.Add(item);
            }
            return tempList;
        }

        private static async Task<ListedItem> AddFolderAsync(StorageFolder folder, IStorageFolder currentStorageFolder, string dateReturnFormat, CancellationToken cancellationToken)
        {
            var basicProperties = await folder.GetBasicPropertiesAsync();
            var extraProps = await basicProperties.RetrievePropertiesAsync(new[] { "System.DateCreated" });
            DateTimeOffset.TryParse(extraProps["System.DateCreated"] as string, out var dateCreated);
            if (!cancellationToken.IsCancellationRequested)
            {
                return new ListedItem(folder.FolderRelativeId, dateReturnFormat)
                {
                    PrimaryItemAttribute = StorageItemTypes.Folder,
                    ItemName = folder.DisplayName,
                    ItemDateModifiedReal = basicProperties.DateModified,
                    ItemDateCreatedReal = dateCreated,
                    ItemType = folder.DisplayType,
                    IsHiddenItem = false,
                    Opacity = 1,
                    LoadFolderGlyph = true,
                    FileImage = null,
                    LoadFileIcon = false,
                    ItemPath = string.IsNullOrEmpty(folder.Path) ? Path.Combine(currentStorageFolder.Path, folder.Name) : folder.Path,
                    LoadUnknownTypeGlyph = false,
                    FileSize = null,
                    FileSizeBytes = 0
                };
            }
            return null;
        }

        private static async Task<ListedItem> AddFileAsync(
            StorageFile file,
            IStorageFolder currentStorageFolder,
            string dateReturnFormat,
            bool suppressThumbnailLoading,
            Type sourcePageType,
            CancellationToken cancellationToken
        )
        {
            var basicProperties = await file.GetBasicPropertiesAsync();
            var extraProperties = await basicProperties.RetrievePropertiesAsync(new[] { "System.DateCreated" });
            // Display name does not include extension
            var itemName = string.IsNullOrEmpty(file.DisplayName) || App.AppSettings.ShowFileExtensions ?
                file.Name : file.DisplayName;
            var itemModifiedDate = basicProperties.DateModified;
            DateTimeOffset.TryParse(extraProperties["System.DateCreated"] as string, out var itemCreatedDate);
            var itemPath = string.IsNullOrEmpty(file.Path) ? Path.Combine(currentStorageFolder.Path, file.Name) : file.Path;
            var itemSize = ByteSize.FromBytes(basicProperties.Size).ToBinaryString().ConvertSizeAbbreviation();
            var itemSizeBytes = basicProperties.Size;
            var itemType = file.DisplayType;
            var itemFolderImgVis = false;
            var itemFileExtension = file.FileType;

            BitmapImage icon = new BitmapImage();
            byte[] iconData = null;
            bool itemThumbnailImgVis;
            bool itemEmptyImgVis;

            if (sourcePageType != typeof(GridViewBrowser))
            {
                try
                {
                    using var itemThumbnailImg = suppressThumbnailLoading ? null :
                        await file.GetThumbnailAsync(ThumbnailMode.ListView, 40, ThumbnailOptions.UseCurrentScale);
                    if (itemThumbnailImg != null)
                    {
                        itemEmptyImgVis = false;
                        itemThumbnailImgVis = true;
                        icon.DecodePixelWidth = 40;
                        icon.DecodePixelHeight = 40;
                        await icon.SetSourceAsync(itemThumbnailImg);
                        iconData = await itemThumbnailImg.ToByteArrayAsync();
                    }
                    else
                    {
                        itemEmptyImgVis = true;
                        itemThumbnailImgVis = false;
                    }
                }
                catch
                {
                    itemEmptyImgVis = true;
                    itemThumbnailImgVis = false;
                    // Catch here to avoid crash
                }
            }
            else
            {
                try
                {
                    using var itemThumbnailImg = suppressThumbnailLoading ? null :
                        await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);
                    if (itemThumbnailImg != null)
                    {
                        itemEmptyImgVis = false;
                        itemThumbnailImgVis = true;
                        icon.DecodePixelWidth = 80;
                        icon.DecodePixelHeight = 80;
                        await icon.SetSourceAsync(itemThumbnailImg);
                        iconData = await itemThumbnailImg.ToByteArrayAsync();
                    }
                    else
                    {
                        itemEmptyImgVis = true;
                        itemThumbnailImgVis = false;
                    }
                }
                catch
                {
                    itemEmptyImgVis = true;
                    itemThumbnailImgVis = false;
                }
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (file.Name.EndsWith(".lnk") || file.Name.EndsWith(".url"))
            {
                // This shouldn't happen, StorageFile api does not support shortcuts
                Debug.WriteLine("Something strange: StorageFile api returned a shortcut");
            }
            // TODO: is this needed to be handled here?
            else if (App.LibraryManager.TryGetLibrary(file.Path, out LibraryLocationItem library))
            {
                return new LibraryItem(library)
                {
                    ItemDateModifiedReal = itemModifiedDate,
                    ItemDateCreatedReal = itemCreatedDate,
                };
            }
            else
            {
                return new ListedItem(file.FolderRelativeId, dateReturnFormat)
                {
                    PrimaryItemAttribute = StorageItemTypes.File,
                    FileExtension = itemFileExtension,
                    IsHiddenItem = false,
                    Opacity = 1,
                    LoadUnknownTypeGlyph = itemEmptyImgVis,
                    FileImage = icon,
                    CustomIconData = iconData,
                    LoadFileIcon = itemThumbnailImgVis,
                    LoadFolderGlyph = itemFolderImgVis,
                    ItemName = itemName,
                    ItemDateModifiedReal = itemModifiedDate,
                    ItemDateCreatedReal = itemCreatedDate,
                    ItemType = itemType,
                    ItemPath = itemPath,
                    FileSize = itemSize,
                    FileSizeBytes = (long)itemSizeBytes,
                };
            }
            return null;
        }
    }
}