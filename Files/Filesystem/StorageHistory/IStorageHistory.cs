using Files.Enums;
using System;
using System.Collections.Generic;
using Windows.Storage;

namespace Files.Filesystem.FilesystemHistory
{
    public interface IStorageHistory : IDisposable
    {
        /// <summary>
        /// Type of operation that took place
        /// </summary>
        FileOperationType OperationType { get; }

        /// <summary>
        /// Source file/folder
        /// <br/>
        /// <br/>
        /// Attention!
        /// <br/>
        /// May contain more that one item
        /// </summary>
        IEnumerable<IStorageItem> Source { get; }

        /// <summary>
        /// Destination file/folder
        /// <br/>
        /// <br/>
        /// Attention!
        /// <br/>
        /// May contain more that one item
        /// </summary>
        IEnumerable<IStorageItem> Destination { get; }

        #region Modify

        void Modify(IStorageHistory newHistory);

        void Modify(FileOperationType operationType, IEnumerable<IStorageItem> source, IEnumerable<IStorageItem> destination);

        void Modify(FileOperationType operationType, IStorageItem source, IStorageItem destination);

        #endregion Modify
    }
}