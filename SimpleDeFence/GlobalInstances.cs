using System;
using System.Diagnostics.CodeAnalysis;
using SimpleDeFence.DatabaseClasses;

namespace SimpleDeFence
{
    /// <summary>
    /// What is left of the WinForms-era global state: the application database the service reads,
    /// and the changeset the settings protocol hands to clients.
    ///
    /// The twelve cached Bitmap properties that used to live here (ApplyBtnIcon, CancelBtnIcon,
    /// AddBtnIcon and the rest) were toolbar icons for the deleted WinForms windows, along with the
    /// Controller/ClientChangeset pair and InitClient - none referenced by anything since that GUI
    /// went. They were the last consumers of Resources.Icons and of Utils.ScaleImage.
    /// </summary>
    internal static class GlobalInstances
    {
        [AllowNull]
        internal static AppDatabase AppDatabase;
        internal static Guid ServerChangeset;
    }
}
