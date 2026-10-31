using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    // The default value must have value Zero.
    public enum AppExceptionTimer
    {
        Permanent = 0,
        Until_Reboot = -1,
        For_5_Minutes = 5,
        For_30_Minutes = 30,
        For_1_Hour = 60,
        For_4_Hours = 240,
        For_9_Hours = 540,
        For_24_Hours = 1440,   // minutes; was 1140, which pruned "24 hours" after 19
        Invalid
    }

    /// <summary>
    /// The durations the GUI offers, and what each one is called. The service has honoured these
    /// since the WinForms days (PruneExpiredRules), but nothing in the WinUI screens set one, so
    /// every rule they wrote was permanent - including the ones a user only wanted for a single
    /// installer run.
    /// </summary>
    public static class AppExceptionTimers
    {
        /// <summary>In the order they are listed. Permanent first, because it is the default and
        /// what every screen wrote before there was a choice.</summary>
        public static System.Collections.Generic.IReadOnlyList<AppExceptionTimer> Offered { get; } = new[]
        {
            AppExceptionTimer.Permanent,
            AppExceptionTimer.For_5_Minutes,
            AppExceptionTimer.For_30_Minutes,
            AppExceptionTimer.For_1_Hour,
            AppExceptionTimer.For_4_Hours,
            AppExceptionTimer.For_9_Hours,
            AppExceptionTimer.For_24_Hours,
            AppExceptionTimer.Until_Reboot,
        };

        public static string Label(AppExceptionTimer timer) => timer switch
        {
            AppExceptionTimer.Permanent => Localization.Loc.T(Localization.LocKeys.Duration.Permanent),
            AppExceptionTimer.For_5_Minutes => Localization.Loc.T(Localization.LocKeys.Duration.Minutes5),
            AppExceptionTimer.For_30_Minutes => Localization.Loc.T(Localization.LocKeys.Duration.Minutes30),
            AppExceptionTimer.For_1_Hour => Localization.Loc.T(Localization.LocKeys.Duration.Hour1),
            AppExceptionTimer.For_4_Hours => Localization.Loc.T(Localization.LocKeys.Duration.Hours4),
            AppExceptionTimer.For_9_Hours => Localization.Loc.T(Localization.LocKeys.Duration.Hours9),
            AppExceptionTimer.For_24_Hours => Localization.Loc.T(Localization.LocKeys.Duration.Hours24),
            AppExceptionTimer.Until_Reboot => Localization.Loc.T(Localization.LocKeys.Duration.UntilRestart),
            _ => Localization.Loc.T(Localization.LocKeys.Common.Unknown),
        };

        /// <summary>When the service will prune this rule, by the same arithmetic PruneExpiredRules
        /// uses; null for a permanent rule and for one that lasts until a restart, which has no
        /// clock time.</summary>
        public static DateTime? ExpiresAt(FirewallExceptionV3 ex)
        {
            int minutes = (int)ex.Timer;
            if (minutes <= 0 || ex.Timer == AppExceptionTimer.Invalid)
                return null;

            // Counted in UTC so a daylight-saving change in between neither adds nor takes away
            // an hour; CreationDate is local time.
            return ex.CreationDate.ToUniversalTime().AddMinutes(minutes).ToLocalTime();
        }
    }

    [DataContract(Namespace = "SimpleDeFence")]
    public class FirewallExceptionV3 : ISerializable<FirewallExceptionV3>
    {
        public static FirewallExceptionV3 Default { get; } = new FirewallExceptionV3(InvalidSubject.Instance, new TcpUdpPolicy(true));

        [DataMember(EmitDefaultValue = false)]
        public Guid Id { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public DateTime CreationDate { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public AppExceptionTimer Timer { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public ExceptionSubject Subject { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public ExceptionPolicy Policy { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public bool ChildProcessesInherit { get; set; }

        public FirewallExceptionV3(ExceptionSubject subject, ExceptionPolicy policy)
        {
            Timer = AppExceptionTimer.Permanent;
            CreationDate = DateTime.Now;
            RegenerateId();

            Subject = subject;
            Policy = policy;
        }

        public void RegenerateId()
        {
            Id = Guid.NewGuid();
        }

        public override string ToString()
        {
            return Subject.ToString();
        }

        public JsonTypeInfo<FirewallExceptionV3> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.FirewallExceptionV3;
        }
    }
}
