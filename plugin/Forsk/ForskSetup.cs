namespace RhinoMCPPlugin.Forsk
{
    public enum SetupPhase
    {
        NotSetUp,
        Running,
        Ready,
        Failed
    }

    /// <summary>
    /// Settings → Set up Forsk's daylight and AI detection row: not set up,
    /// setting up (with the step), ready, or failed (with the reason). One
    /// value at a time, replaced whole, so the install thread and the card
    /// never see half a change. No RhinoCommon, so it tests headless.
    ///
    ///   NotSetUp / Failed --Start--> Running --Step--> Running
    ///   Running --Done--> Ready      Running --Fail--> Failed
    ///   not Running --Seen(uv found)--> Ready, --Seen(none)--> NotSetUp (Failed stays)
    /// </summary>
    public sealed class SetupState
    {
        public readonly SetupPhase Phase;
        /// <summary>Running: the step under way. Failed: the reason.</summary>
        public readonly string Text;

        SetupState(SetupPhase phase, string text = null)
        {
            Phase = phase;
            Text = text;
        }

        /// <summary>Before any install this session: Ready when a uv is found, else not set up.</summary>
        public static SetupState From(bool uvFound) => new SetupState(uvFound ? SetupPhase.Ready : SetupPhase.NotSetUp);

        /// <summary>The Set up click. A second click while it runs changes nothing.</summary>
        public SetupState Start() => Phase == SetupPhase.Running ? this : new SetupState(SetupPhase.Running, ForskText.Get("setup.step.download"));

        public SetupState Step(string step) => Phase == SetupPhase.Running ? new SetupState(SetupPhase.Running, step) : this;

        public SetupState Done() => Phase == SetupPhase.Running ? new SetupState(SetupPhase.Ready) : this;

        public SetupState Fail(string reason) => Phase == SetupPhase.Running ? new SetupState(SetupPhase.Failed, reason) : this;

        /// <summary>What a fresh look at the disk says. An install under way, and a failure with no uv, stay.</summary>
        public SetupState Seen(bool uvFound)
        {
            if (Phase == SetupPhase.Running) return this;
            if (uvFound) return Phase == SetupPhase.Ready ? this : new SetupState(SetupPhase.Ready);
            return Phase == SetupPhase.Failed ? this : new SetupState(SetupPhase.NotSetUp);
        }

        public bool NeedsAction => Phase == SetupPhase.NotSetUp || Phase == SetupPhase.Failed;
    }

    /// <summary>The Set up Forsk card's words: one row each for chat, for daylight and AI detection, and for the account.</summary>
    public static class ForskSetup
    {
        /// <summary>The Settings menu row, the first-run button and the card's kind.</summary>
        public const string MenuId = "forsk.setup";
        /// <summary>Where a Grok key comes from.</summary>
        public const string KeySite = "https://console.x.ai";

        public static string ChatRow(string key)
        {
            var state = string.IsNullOrEmpty(key)
                ? ForskText.Get("setup.chat.needs")
                : ForskText.Format("setup.chat.ready", "tail", ForskKeyFile.Tail(key));
            return ForskText.Get("setup.chat") + " · " + state;
        }

        /// <summary>The Account row: not connected, the code while the browser confirms, connected as whom, or why not.</summary>
        public static string AccountRow(AccountState account)
        {
            string state;
            switch (account?.Phase ?? AccountPhase.NotConnected)
            {
                case AccountPhase.Waiting: state = ForskText.Format("setup.account.waiting", "code", account.Text); break;
                case AccountPhase.Connected:
                    state = ForskText.Format("setup.account.connected", "email", account.Email);
                    if (!string.IsNullOrEmpty(account.Plan)) state += " · " + account.Plan;
                    break;
                case AccountPhase.Failed: state = ForskText.Format("setup.account.failed", "reason", account.Text); break;
                default: state = ForskText.Get("setup.account.none"); break;
            }
            return ForskText.Get("setup.account") + " · " + state;
        }

        public static string ToolsRow(SetupState tools)
        {
            string state;
            switch (tools?.Phase ?? SetupPhase.NotSetUp)
            {
                case SetupPhase.Ready: state = ForskText.Get("setup.ready"); break;
                case SetupPhase.Running: state = ForskText.Format("setup.tools.running", "step", tools.Text); break;
                case SetupPhase.Failed: state = ForskText.Format("setup.tools.failed", "reason", tools.Text); break;
                default: state = ForskText.Get("setup.tools.none"); break;
            }
            return ForskText.Get("setup.tools") + " · " + state;
        }
    }
}
