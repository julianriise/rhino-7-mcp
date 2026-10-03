using System;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>What one POST came back as. Network is a timeout or no answer at all.</summary>
    public sealed class SupportHttpResult
    {
        public int Status;
        public string Body;
        public string RetryAfter;
        public bool Network;
    }

    /// <summary>The POST. The real one talks to forsk.app. Tests pass a stand-in.</summary>
    public interface ISupportPost
    {
        SupportHttpResult Post(string json);
    }

    /// <summary>
    /// Sends a report, and anything the outbox still holds, oldest first.
    /// One pass at a time. A report is claimed before it is posted and removed
    /// only after a 200, so a retry does not send one that already went.
    /// </summary>
    public static class ForskSupportSend
    {
        /// <summary>
        /// Queues the report, then sends the queue. The outcome is this report's.
        /// An earlier failure leaves this one queued and says so.
        /// </summary>
        public static ForskSupport.Outcome Deliver(JObject payload, ISupportPost post, ForskOutbox box, DateTimeOffset now, string language)
        {
            if (box == null) throw new ArgumentNullException(nameof(box));
            lock (box.Gate)
            {
                var item = box.Enqueue(payload, now);
                ForskSupport.Outcome focus = null;
                FlushCore(post, box, now, language, item.Id, ref focus);
                return focus ?? ForskSupport.Network(language);
            }
        }

        /// <summary>Sends what is waiting. Returns how many the server accepted. Stops at the first retry or rate limit.</summary>
        public static int Flush(ISupportPost post, ForskOutbox box, DateTimeOffset now, string language)
        {
            if (box == null) return 0;
            lock (box.Gate)
            {
                ForskSupport.Outcome ignored = null;
                return FlushCore(post, box, now, language, null, ref ignored);
            }
        }

        static int FlushCore(ISupportPost post, ForskOutbox box, DateTimeOffset now, string language, string focusId, ref ForskSupport.Outcome focus)
        {
            var accepted = 0;
            foreach (var item in box.Pending(now))
            {
                if (!box.TryClaim(item)) continue;
                var result = Ask(post, item.Body);
                var outcome = result.Network
                    ? ForskSupport.Network(language)
                    : ForskSupport.Parse(result.Status, result.Body, result.RetryAfter, language, now);
                if (focusId != null && item.Id == focusId) focus = outcome;
                if (outcome.Accepted)
                {
                    box.Complete(item);
                    accepted++;
                    continue;
                }
                if (outcome.Retry || outcome.Code == "rate_limited")
                {
                    box.Release(item);
                    if (focusId != null && focus == null) focus = outcome;
                    break;
                }
                box.Complete(item);
            }
            return accepted;
        }

        static SupportHttpResult Ask(ISupportPost post, string json)
        {
            if (post == null) return new SupportHttpResult { Network = true };
            try
            {
                return post.Post(json) ?? new SupportHttpResult { Network = true };
            }
            catch (Exception)
            {
                return new SupportHttpResult { Network = true };
            }
        }
    }
}
