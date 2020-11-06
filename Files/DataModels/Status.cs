﻿namespace Files
{
    /// <summary>
    /// Contains all kinds of return status
    /// </summary>
    public enum Status : byte
    {
        /// <summary>
        /// Informs that operation is still in progress
        /// </summary>
        InProgress = 0,

        /// <summary>
        /// Informs that operation completed sucessfully
        /// </summary>
        Success = 1,

        /// <summary>
        /// Informs that operation failed
        /// </summary>
        Failed = 2,

        /// <summary>
        /// Informs that operation failed during integrity check
        /// </summary>
        IntegrityCheckFailed = 3,

        /// <summary>
        /// Informs that operation resulted in an unknown exception
        /// </summary>
        UnknownException = 4,

        /// <summary>
        /// Informs that operation provided argument is illegal
        /// </summary>
        IllegalArgumentException = 5,

        /// <summary>
        /// Informs that operation provided/returned value is null
        /// </summary>
        NullException = 6,

        /// <summary>
        /// Infoms that operation tried to access restricted resources
        /// </summary>
        AccessUnauthorized = 7,

        /// <summary>
        /// Informs that operation has been cancelled
        /// </summary>
        Cancelled = 8,
    }
}
