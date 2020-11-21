﻿using Windows.Storage;

namespace Files.Filesystem
{
    public class PathWithType
    {
        public string Path { get; private set; }

        public FilesystemItemType ItemType { get; private set; }

        public PathWithType(string path, FilesystemItemType itemType)
        {
            this.Path = path;
            this.ItemType = itemType;
        }

        public static explicit operator string(PathWithType pathWithType) => pathWithType.Path;

        public static explicit operator FilesystemItemType(PathWithType pathWithType) => pathWithType.ItemType;

        public static explicit operator PathWithType(StorageFile storageFile) => new PathWithType(storageFile.Path, storageFile.IsOfType(StorageItemTypes.File) ? FilesystemItemType.File : FilesystemItemType.Directory);

        public static explicit operator PathWithType(StorageFolder storageFolder) => new PathWithType(storageFolder.Path, storageFolder.IsOfType(StorageItemTypes.File) ? FilesystemItemType.File : FilesystemItemType.Directory);

        public override string ToString()
        {
            return Path;
        }
    }
}
