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

        internal Application? TryGetApp(ExecutableSubject fromSubject, out FirewallExceptionV3? fwex, bool matchSpecial)
        {
            foreach (var app in KnownApplications)
            {
                if (!matchSpecial && app.HasFlag("TWUI:Special"))
                    continue;

                foreach (var id in app.Components)
                {
                    if (id.DoesExecutableSatisfy(fromSubject))
                    {
                        fwex = id.InstantiateException(fromSubject);
                        return app;
                    }
                }
            }

            fwex = null;
            return null;
        }

        internal List<FirewallExceptionV3> GetExceptionsForApp(ExceptionSubject fromSubject, bool guiPrompt, out Application? app)
        {
            app = null;
            var exceptions = new List<FirewallExceptionV3>();

            if (fromSubject is AppContainerSubject)
            {
                exceptions.Add(new FirewallExceptionV3(fromSubject, new TcpUdpPolicy(true)));
                return exceptions;
            }
            else if (fromSubject is ExecutableSubject exeSubject)
            {
                app = TryGetApp(exeSubject, out FirewallExceptionV3? _, false);
                if (app == null)
                {
                    exceptions.Add(new FirewallExceptionV3(exeSubject, new TcpUdpPolicy(true)));
                    return exceptions;
                }

                string pathHint = System.IO.Path.GetDirectoryName(exeSubject.ExecutablePath);
                foreach (SubjectIdentity id in app.Components)
                {
                    List<ExceptionSubject> foundSubjects = id.SearchForFile(pathHint);
                    foreach (ExceptionSubject subject in foundSubjects)
                    {
                        var tmp = id.InstantiateException(subject);
                        if (fromSubject.Equals(subject))
                            exceptions.Insert(0, tmp);
                        else
                            exceptions.Add(tmp);
                    }
                }

                if ((exceptions.Count > 1) && guiPrompt)
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
            }
            else
            {
                throw new NotImplementedException();
            }

            return exceptions;
        }
    }
}