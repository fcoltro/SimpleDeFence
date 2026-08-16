using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Samples.TaskDialog;

namespace SimpleDeFence.DatabaseClasses
{
    public partial class AppDatabase
    {
        public static string DBPath
        {
            get { return System.IO.Path.Combine(Utils.AppDataPath, "profiles.json"); }
        }

        public static AppDatabase Load()
        {
            return Load(DBPath);
        }

        internal List<FirewallExceptionV3> GetExceptionsForApp(ExceptionSubject fromSubject, bool guiPrompt, out Application? app)
        {
            var exceptions = GetExceptionsForApp(fromSubject, out app);

            if ((exceptions.Count > 1) && guiPrompt && app is not null && fromSubject is ExecutableSubject exeSubject)
            {
                string localizedAppName = Resources.Exceptions.ResourceManager.GetString(app.Name);
                localizedAppName = string.IsNullOrEmpty(localizedAppName) ? app.Name : localizedAppName;

                Utils.SplitFirstLine(string.Format(CultureInfo.InvariantCulture, Resources.Messages.UnblockApp, localizedAppName), out string firstLine, out string contentLines);

                var dialog = new TaskDialog
                {
                    CustomMainIcon = Resources.Icons.firewall,
                    WindowTitle = Resources.Messages.SimpleDeFence,
                    MainInstruction = firstLine,
                    Content = contentLines,
                    DefaultButton = 1,
                    ExpandedControlText = Resources.Messages.UnblockAppShowRelated,
                    ExpandFooterArea = true,
                    AllowDialogCancellation = false,
                    UseCommandLinks = true
                };

                var button1 = new TaskDialogButton(101, Resources.Messages.UnblockAppUnblockAllRecommended);
                var button2 = new TaskDialogButton(102, Resources.Messages.UnblockAppUnblockOnlySelected);
                var button3 = new TaskDialogButton(103, Resources.Messages.UnblockAppCancel);
                dialog.Buttons = new TaskDialogButton[] { button1, button2, button3 };

                string fileListStr = string.Empty;
                foreach (FirewallExceptionV3 fwex in exceptions)
                    fileListStr += fwex.Subject.ToString() + Environment.NewLine;
                dialog.ExpandedInformation = fileListStr.Trim();

                switch (dialog.Show())
                {
                    case 101:
                        break;
                    case 102:
                        for (int i = exceptions.Count - 1; i >= 0; --i)
                        {
                            if (exceptions[i].Subject is ExecutableSubject exesub)
                            {
                                if (!exesub.ExecutablePath.Equals(exeSubject.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    exceptions.RemoveAt(i);
                                    continue;
                                }
                            }
                            else
                            {
                                exceptions.RemoveAt(i);
                                continue;
                            }
                        }
                        exceptions.RemoveRange(1, exceptions.Count - 1);
                        break;
                    case 103:
                        exceptions.Clear();
                        break;
                }
            }

            return exceptions;
        }
    }
}