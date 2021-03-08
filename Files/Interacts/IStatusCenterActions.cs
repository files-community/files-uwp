﻿using Files.Enums;
using Files.UserControls;
using System;

namespace Files.Interacts
{
    public interface IStatusCenterActions
    {
        event EventHandler ProgressBannerPosted;

        /// <summary>
        /// Posts a new banner to the Status Center control for an operation.
        /// It may be used to return the progress, success, or failure of the respective operation.
        /// </summary>
        /// <param name="title">Reserved for success and error banners. Otherwise, pass an empty string for this argument.</param>
        /// <param name="message"></param>
        /// <param name="initialProgress"></param>
        /// <param name="status"></param>
        /// <param name="operation"></param>
        /// <returns>A StatusBanner object which may be used to track/update the progress of an operation.</returns>
        PostedStatusBanner PostBanner(string title, string message, float initialProgress, ReturnResult status, FileOperationType operation);

        /// <summary>
        /// Posts a new banner with expanded height to the Status Center control. This is typically
        /// used to represent a failure during a prior operation which must be acted upon.
        /// </summary>
        /// <param name="title"></param>
        /// <param name="message"></param>
        /// <param name="primaryButtonText"></param>
        /// <param name="cancelButtonText"></param>
        /// <param name="primaryAction"></param>
        /// <returns>A StatusBanner object which may be used to automatically remove the banner from UI.</returns>
        PostedStatusBanner PostActionBanner(string title, string message, string primaryButtonText, string cancelButtonText, Action primaryAction);

        /// <summary>
        /// Dismisses <paramref name="banner"/> and removes it from the collection
        /// </summary>
        /// <param name="banner">The banner to close</param>
        /// <returns>true if operation completed successfully; otherwise false</returns>
        bool CloseBanner(StatusBanner banner);
    }
}