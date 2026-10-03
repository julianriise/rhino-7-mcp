using System;
using System.IO;
using System.Text;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The address a report is answered at. Rhino 7.34 on Mac exposes
    /// RhinoApp.LoggedInUserName and LoggedInUserAvatar: the Cloud Zoo display
    /// name and picture, not an email. IOpenIDConnectToken.Emails is only
    /// reachable with a Rhino Accounts client id and secret, which this plugin
    /// does not have, so the card asks. The address is kept in ~/.forsk, never
    /// as a document string.
    /// </summary>
    public static class ForskReplyEmail
    {
        /// <summary>False: Rhino 7 does not give this plugin an account email.</summary>
        public const bool RhinoGivesReplyEmail = false;

        /// <summary>~/.forsk/reply-email</summary>
        public static string DefaultPath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".forsk", "reply-email");
            }
        }

        /// <summary>The stored address when it is a valid email, else empty.</summary>
        public static string Read(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                if (!File.Exists(path)) return "";
                var text = File.ReadAllText(path, Encoding.UTF8).Trim();
                return ForskSupport.EmailOk(text) ? text : "";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "";
            }
        }

        /// <summary>Remember a valid address. An invalid one is left untouched. Returns whether it was written.</summary>
        public static bool Remember(string path, string email)
        {
            var text = (email ?? "").Trim();
            if (string.IsNullOrEmpty(path) || !ForskSupport.EmailOk(text)) return false;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
            File.WriteAllText(tmp, text + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return true;
        }
    }
}
