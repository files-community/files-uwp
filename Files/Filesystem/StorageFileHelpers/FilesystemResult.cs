using Files.Enums;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace Files.Filesystem
{
    public static class FilesystemErrorCodeExtensions
    {
        public static bool HasFlag(this FileSystemStatusCode errorCode, FileSystemStatusCode flag)
        {
            return (errorCode & flag) != FileSystemStatusCode.Success;
        }
    }

    public class FilesystemResult<T> : FilesystemResult
    {
        public FilesystemResult(T result, FileSystemStatusCode errorCode) : base(errorCode)
        {
            this.Result = result;
        }

        public T Result { get; private set; }

        public static implicit operator T(FilesystemResult<T> res) => res.Result;
    }

    public class FilesystemResult
    {
        public FilesystemResult(FileSystemStatusCode errorCode)
        {
            this.ErrorCode = errorCode;
        }

        public FileSystemStatusCode ErrorCode { get; private set; }

        public static implicit operator FileSystemStatusCode(FilesystemResult res) => res.ErrorCode;

        public static implicit operator FilesystemResult(FileSystemStatusCode res) => new FilesystemResult(res);

        public static implicit operator bool(FilesystemResult res) =>
            res.ErrorCode == FileSystemStatusCode.Success;

        public static explicit operator FilesystemResult(bool res) =>
            new FilesystemResult(res ? FileSystemStatusCode.Success : FileSystemStatusCode.Generic);
    }

    public static class FilesystemTasks
    {
        public static FileSystemStatusCode GetErrorCode(Exception ex, Type T = null)
        {
            if (ex is UnauthorizedAccessException)
            {
                return FileSystemStatusCode.Unauthorized;
            }
            else if (ex is FileNotFoundException // Item was deleted
                || ex is System.Runtime.InteropServices.COMException // Item's drive was ejected
                || (uint)ex.HResult == 0x8007000F) // The system cannot find the drive specified
            {
                return FileSystemStatusCode.NotFound;
            }
            else if (ex is IOException || ex is FileLoadException)
            {
                return FileSystemStatusCode.InUse;
            }
            else if (ex is PathTooLongException)
            {
                return FileSystemStatusCode.NameTooLong;
            }
            else if (ex is ArgumentException) // Item was invalid
            {
                return (T == typeof(IStorageFolder) || T.GetInterface("IStorageFolder") is not null) ?
                    FileSystemStatusCode.NotAFolder : FileSystemStatusCode.NotAFile;
            }
            else if ((uint)ex.HResult == 0x800700B7)
            {
                return FileSystemStatusCode.AlreadyExists;
            }
            else if ((uint)ex.HResult == 0x80071779)
            {
                return FileSystemStatusCode.ReadOnly;
            }
            else if ((uint)ex.HResult == 0x800700A1 // The specified path is invalid (usually an mtp device was disconnected)
                || (uint)ex.HResult == 0x8007016A // The cloud file provider is not running
                || (uint)ex.HResult == 0x8000000A) // The data necessary to complete this operation is not yet available)
            {
                return FileSystemStatusCode.Generic;
            }
            else
            {
                return FileSystemStatusCode.Generic;
            }
        }

        public async static Task<FilesystemResult<T>> Wrap<T>(Func<Task<T>> wrapped)
        {
            try
            {
                return new FilesystemResult<T>(await wrapped(), FileSystemStatusCode.Success);
            }
            catch (Exception ex)
            {
                return new FilesystemResult<T>(default, GetErrorCode(ex, typeof(T)));
            }
        }

        public async static Task<FilesystemResult> Wrap(Func<Task> wrapped)
        {
            try
            {
                await wrapped();
                return new FilesystemResult(FileSystemStatusCode.Success);
            }
            catch (Exception ex)
            {
                return new FilesystemResult(GetErrorCode(ex));
            }
        }

        public async static Task<FilesystemResult<V>> OnSuccess<V, T>(this Task<FilesystemResult<T>> wrapped, Func<T, Task<V>> func)
        {
            var res = await wrapped;
            if (res)
            {
                return await Wrap(() => func(res.Result));
            }
            return new FilesystemResult<V>(default, res.ErrorCode);
        }

        public async static Task<FilesystemResult> OnSuccess<T>(this Task<FilesystemResult<T>> wrapped, Func<T, Task> func)
        {
            var res = await wrapped;
            if (res)
            {
                return await Wrap(() => func(res.Result));
            }
            return res;
        }

        public async static Task<FilesystemResult> OnSuccess<T>(this Task<FilesystemResult<T>> wrapped, Action<T> func)
        {
            var res = await wrapped;
            if (res)
            {
                func(res.Result);
            }
            return res;
        }
    }
}