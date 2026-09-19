using System;
using System.IO;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    internal class HostsFileManager : Disposable
    {
        // Active system hosts file
        private readonly static string HOSTS_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        // Local copy of active hosts file
        private readonly static string HOSTS_BACKUP = Path.Combine(Utils.AppDataPath, "hosts.bck");
        // User's original hosts file
        private readonly static string HOSTS_ORIGINAL = Path.Combine(Utils.AppDataPath, "hosts.orig");

        public readonly FileLocker FileLocker = new();

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            if (disposing)
            {
                FileLocker.Dispose();
            }

            base.Dispose(disposing);
        }


        private bool _EnableProtection;
        public bool EnableProtection
        {
            get => _EnableProtection;
            set
            {
                _EnableProtection = value;
                if (File.Exists(HOSTS_PATH))
                {
                    if (_EnableProtection)
                        FileLocker.Lock(HOSTS_PATH, FileAccess.Read, FileShare.Read);
                    else
                        FileLocker.Unlock(HOSTS_PATH);
                }

                if (File.Exists(HOSTS_BACKUP))
                    FileLocker.Lock(HOSTS_BACKUP, FileAccess.Read, FileShare.Read);

                if (File.Exists(HOSTS_ORIGINAL))
                    FileLocker.Lock(HOSTS_ORIGINAL, FileAccess.Read, FileShare.Read);
            }
        }

        /// <summary>
        /// Saves the user's own hosts file so it can be handed back when the blocklist is switched
        /// off. Refuses when what is currently installed is our blocklist rather than theirs.
        ///
        /// That case is reachable: hosts.orig is deleted on every disable and only its file lock
        /// protects it in between, and the lock goes away with the service - stopped, upgraded,
        /// uninstalled. Copying blindly at that point would record the blocklist as the user's
        /// original, and the next disable would write it back permanently, with nothing left on
        /// disk to tell the two apart. Better to keep the blocklist installed and say so than to
        /// overwrite the only copy of something we cannot reconstruct.
        /// </summary>
        private bool CreateOriginalBackup()
        {
            if (!File.Exists(HOSTS_PATH))
            {
                Utils.Log("No hosts file to back up; leaving the hosts blocklist alone.", Utils.LOG_ID_SERVICE);
                return false;
            }

            if (CurrentHostsIsOurBlocklist())
            {
                Utils.Log("The installed hosts file is our own blocklist and the backup of the original is gone. "
                    + "Not overwriting it with the blocklist; the original must be restored by hand.", Utils.LOG_ID_SERVICE);
                return false;
            }

            try
            {
                FileLocker.Unlock(HOSTS_ORIGINAL);
                File.Copy(HOSTS_PATH, HOSTS_ORIGINAL, true);
                FileLocker.Lock(HOSTS_ORIGINAL, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                return false;
            }
        }

        /// <summary>Whether the hosts file in force is byte-for-byte the blocklist we installed.</summary>
        private static bool CurrentHostsIsOurBlocklist()
        {
            if (!File.Exists(HOSTS_BACKUP) || !File.Exists(HOSTS_PATH))
                return false;

            try
            {
                return string.Equals(Hasher.HashFile(HOSTS_PATH), Hasher.HashFile(HOSTS_BACKUP), StringComparison.Ordinal);
            }
            catch (Exception e)
            {
                // Unreadable means we cannot prove it is safe to overwrite, so treat it as unsafe.
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                return true;
            }
        }

        public void UpdateHostsFile(string path)
        {
            // We keep a copy of the hosts file for ourself, so that
            // we can re-install it any time without a net connection.
            FileLocker.Unlock(HOSTS_BACKUP);
            using (var afu = new AtomicFileUpdater(HOSTS_BACKUP))
            {
                File.Copy(path, afu.TemporaryFilePath, true);
                afu.Commit();
            }
            FileLocker.Lock(HOSTS_BACKUP, FileAccess.Read, FileShare.Read);
        }

        public static string GetHostsHash()
        {
            if (File.Exists(HOSTS_BACKUP))
                return Hasher.HashFile(HOSTS_BACKUP);
            else
                return string.Empty;
        }

        /// <summary>Installs the blocklist. True when it is in force afterwards.</summary>
        public bool EnableHostsFile()
        {
            // No backup of the user's original means no way back, so the backup is made first and
            // its failure stops the install. This used to run for its side effect and ignore the
            // outcome, which is how a missing original turned into an unrecoverable one.
            if (!File.Exists(HOSTS_ORIGINAL) && !CreateOriginalBackup())
                return false;

            try
            {
                // No blocklist on disk is a failure to enable, not a quiet success. The MSI ships
                // hosts.bck, so reaching this means it was removed or the download never landed -
                // either way the user's hosts file is untouched and the setting must not report
                // itself as in force.
                if (!InstallHostsFile(HOSTS_BACKUP))
                {
                    Utils.Log("The hosts blocklist is enabled in the configuration but no blocklist file is present; nothing was installed.", Utils.LOG_ID_SERVICE);
                    return false;
                }

                FlushDNSCache();
                return true;
            }
            catch (Exception e)
            {
                // Both exits used to be `return false`, success included, so the result said
                // nothing at all - and the opposite of what DisableHostsFile's result means.
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                return false;
            }
        }

        /// <summary>Puts the user's own hosts file back. False when it could not be - which,
        /// crucially, includes having no saved original to put back while our blocklist is the file
        /// in force.</summary>
        public bool DisableHostsFile()
        {
            try
            {
                if (!InstallHostsFile(HOSTS_ORIGINAL))
                {
                    // Nothing was restored. If what is installed is our own blocklist, the machine
                    // is left with it as its permanent hosts file and there is no copy of the
                    // user's anywhere - so this is the last moment anyone can be told. Reported
                    // rather than returned quietly because the uninstaller calls this, and a
                    // product that removes itself and leaves a blocklist behind has no later
                    // opportunity to explain where it came from.
                    if (CurrentHostsIsOurBlocklist())
                    {
                        Utils.Log("The hosts blocklist is still installed and no copy of the original hosts file remains. "
                            + $"The current hosts file is SimpleDeFence's blocklist; replace it by hand from a known-good copy. Path: {HOSTS_PATH}",
                            Utils.LOG_ID_SERVICE);
                        return false;
                    }

                    // Otherwise the hosts file in force is not ours and there was nothing to undo.
                    return true;
                }

                // Delete backup of original so that it can be
                // recreated next time we install a custom hosts.
                if (File.Exists(HOSTS_ORIGINAL))
                {
                    FileLocker.Unlock(HOSTS_ORIGINAL);
                    File.Delete(HOSTS_ORIGINAL);
                }

                FlushDNSCache();
                return true;
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                return false;
            }
        }

        private static void FlushDNSCache()
        {
            try
            {
                // Flush DNS cache
                Utils.FlushDnsCache();
            }
            catch
            {
                // We just want to block exceptions.
            }
        }

        /// <summary>
        /// Copies <paramref name="sourcePath"/> over the system hosts file. False when there was no
        /// source to copy, in which case nothing was installed.
        ///
        /// The result is the point. This used to return void and treat a missing source as nothing
        /// worth mentioning, so both callers reported success for work that never happened: an
        /// enable with no blocklist on disk left the settings page asserting the blocklist was
        /// active, and - the one that outlives the product - a disable with no saved original left
        /// our blocklist installed as the machine's hosts file and told the uninstaller it had been
        /// put back.
        /// </summary>
        private bool InstallHostsFile(string sourcePath)
        {
            try
            {
                if (!File.Exists(sourcePath))
                    return false;

                FileLocker.Unlock(HOSTS_PATH);
                File.Copy(sourcePath, HOSTS_PATH, true);
                return true;
            }
            finally
            {
                if (_EnableProtection)
                    FileLocker.Lock(HOSTS_PATH, FileAccess.Read, FileShare.Read);
                else
                    FileLocker.Unlock(HOSTS_PATH);
            }
        }

    }
}
