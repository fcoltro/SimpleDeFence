namespace SimpleDeFence
{
    // Possible message types from controller to service
    public enum MessageType
    {
        // General responses
        INVALID_COMMAND,
        RESPONSE_ERROR,
        RESPONSE_LOCKED,
        COM_ERROR,

        /// <summary>
        /// Client-side only - the service never sends this over the wire. A PUT_SETTINGS reply
        /// still carries type PUT_SETTINGS even when TwMessagePutSettings.Warning is true (the
        /// caller's changeset was stale, so the service applied nothing); SimpleDeFence.UI's
        /// FirewallClient.CommitProfileChangesAsync recognises that case and returns this instead,
        /// so a discarded change can never read as PUT_SETTINGS success.
        /// </summary>
        RESPONSE_STALE_CHANGESET,

        // Read commands (>31)
        GET_SETTINGS = 32,
        GET_PROCESS_PATH,
        READ_FW_LOG,
        IS_LOCKED,
        BACKGROUND_ITEMS,

        // Unprivileged write commands (>1023)
        UNLOCK = 1024,

        // Privileged write commands (>2047)
        MODE_SWITCH = 2048,
        REINIT,
        PUT_SETTINGS,
        LOCK,
        SET_PASSPHRASE,
        STOP_SERVICE,
        MINUTE_TIMER,
        REENUMERATE_ADDRESSES,

        // Service-to-client messages
        DATABASE_UPDATED,

        // Privileged write, added after DATABASE_UPDATED so no existing number moves
        SET_BACKGROUND_ITEMS,

        // Privileged too (>2047). Numbered explicitly so commands added above cannot move them.
        TELEMETRY_APPLY = 3000,
        TELEMETRY_UNDO = 3001,

        // Service-to-service only (>4095)
        ADD_TEMPORARY_EXCEPTION = 4096,
        RELOAD_WFP_FILTERS,
        DISPLAY_POWER_EVENT,
    }
}
