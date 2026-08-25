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
                InstallHostsFile(HOSTS_BACKUP);
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

        public bool DisableHostsFile()
        {
            try
            {
                InstallHostsFile(HOSTS_ORIGINAL);

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

        private void InstallHostsFile(string sourcePath)
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    FileLocker.Unlock(HOSTS_PATH);
                    File.Copy(sourcePath, HOSTS_PATH, true);
                }
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
