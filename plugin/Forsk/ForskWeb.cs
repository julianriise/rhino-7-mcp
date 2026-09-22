using System;
using System.IO;
using Eto.Drawing;
using Eto.Forms;
using Newtonsoft.Json.Linq;
using Rhino.UI;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Floating WebKit page. CSS owns the type and the composer.
    /// The dock stays on Eto. This window is opened with the ForskWeb command.
    /// Rhino 7's WebViewLoadingEventArgs does not expose the URL, so the page
    /// sets document.title and the form reads DocumentTitleChanged.
    /// </summary>
    sealed class ForskWebForm : FloatingForm
    {
        static ForskWebForm _open;
        readonly WebView _web;
        bool _busy;

        public static void Open()
        {
            if (_open != null)
            {
                _open.Show();
                _open.Focus();
                return;
            }
            _open = new ForskWebForm();
            _open.Closed += (s, e) =>
            {
                if (ReferenceEquals(_open, s)) _open = null;
            };
            try
            {
                var owner = RhinoEtoApp.MainWindow;
                if (owner != null) _open.Owner = owner;
            }
            catch (Exception e)
            {
                Log("owner " + e.Message);
            }
            _open.Show();
        }

        ForskWebForm()
        {
            Title = "Forsk";
            ClientSize = new Size(400, 640);
            MinimumSize = new Size(320, 480);
            Resizable = true;
            Padding = new Padding(0);
            _web = new WebView();
            Content = _web;
            _web.DocumentTitleChanged += OnTitle;
            Shown += (s, e) => LoadPage();
        }

        void LoadPage()
        {
            try
            {
                _web.LoadHtml(Page(), new Uri("http://127.0.0.1/"));
                Log("loaded");
            }
            catch (Exception e)
            {
                Log("load " + e.Message);
            }
        }

        void OnTitle(object sender, WebViewTitleEventArgs e)
        {
            var title = e == null ? null : e.Title;
            if (string.IsNullOrEmpty(title)) title = _web.DocumentTitle;
            if (string.IsNullOrEmpty(title) || !title.StartsWith("forsk:")) return;
            if (_busy) return;
            string text;
            try
            {
                text = Uri.UnescapeDataString(title.Substring(6).Replace("+", "%2B"));
            }
            catch (Exception ex)
            {
                Log("title " + ex.Message);
                return;
            }
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 500) text = text.Substring(0, 500);
            _busy = true;
            Log("send " + text.Length);
            Application.Instance.AsyncInvoke(() => Run(text));
        }

        void Run(string text)
        {
            string line;
            try
            {
                var envelope = ForskTools.ExecuteAllowed("get_document_summary", new JObject());
                line = Summarize(envelope);
            }
            catch (Exception e)
            {
                line = e.Message;
            }
            var payload = new JObject
            {
                ["text"] = text,
                ["line"] = line
            };
            try
            {
                _web.ExecuteScript("window.forskShow(" + payload.ToString(Newtonsoft.Json.Formatting.None) + ")");
            }
            catch (Exception e)
            {
                Log("script " + e.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        static string Summarize(JObject envelope)
        {
            if (envelope == null) return "No reply from Rhino.";
            var status = envelope["status"]?.ToString();
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var err = envelope["message"]?.ToString();
                return string.IsNullOrEmpty(err) ? "Rhino returned an error." : err;
            }
            var result = envelope["result"] as JObject;
            var meta = result?["meta_data"] as JObject;
            var name = meta?["name"]?.ToString();
            var units = meta?["units"]?.ToString();
            var count = result?["object_count"]?.ToString();
            if (string.IsNullOrEmpty(name)) name = "Untitled";
            if (string.IsNullOrEmpty(units)) units = "units unknown";
            if (string.IsNullOrEmpty(count)) count = "?";
            return name + " · " + units + " · " + count + " objects";
        }

        static string Page()
        {
            var font = FontBase64();
            return PageHtml.Replace("FONTBASE64", font);
        }

        static string FontBase64()
        {
            var src = typeof(ForskWebForm).Assembly.GetManifestResourceStream("RhinoMCPPlugin.Fonts.Geist-Regular.otf");
            if (src == null) return "";
            using (src)
            using (var copy = new MemoryStream())
            {
                src.CopyTo(copy);
                return Convert.ToBase64String(copy.ToArray());
            }
        }

        static void Log(string line)
        {
            try
            {
                File.AppendAllText("/tmp/forsk-web.log", line + "\n");
            }
            catch
            {
                // The window still opens if the log cannot be written.
            }
        }

        const string PageHtml = @"<!doctype html>
<html>
<head>
<meta charset='utf-8'>
<title>Forsk</title>
<style>
@font-face {
  font-family: Geist;
  src: url(data:font/otf;base64,FONTBASE64) format('opentype');
  font-weight: 400;
  font-style: normal;
}
* { box-sizing: border-box; }
html, body { margin: 0; height: 100%; }
body {
  background: #F6F5F2;
  color: #1C1917;
  font-family: Geist, -apple-system, BlinkMacSystemFont, sans-serif;
  font-size: 16px;
  display: flex;
  flex-direction: column;
}
header { padding: 20px 20px 8px; }
h1 { font-size: 18px; font-weight: 400; margin: 0; letter-spacing: -0.02em; }
p.note { margin: 8px 0 0; color: #78716C; font-size: 13px; line-height: 1.4; }
#thread { flex: 1; overflow: auto; padding: 12px 20px 8px; }
.you, .reply { margin: 0 0 10px; line-height: 1.4; }
.you {
  margin-left: 48px;
  background: #fff;
  border: 1px solid #E4E0D8;
  border-radius: 0;
  padding: 10px 12px;
}
.reply { color: #78716C; font-size: 14px; }
form {
  padding: 12px 16px 16px;
}
.composer {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 48px;
  background: #fff;
  border: 1px solid #E4E0D8;
  border-radius: 0;
  padding: 0 8px 0 16px;
}
input {
  flex: 1;
  border: 0;
  outline: none;
  background: transparent;
  font: 16px Geist, -apple-system, BlinkMacSystemFont, sans-serif;
  color: #1C1917;
  min-width: 0;
}
button {
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 0;
  background: #1C1917;
  color: #fff;
  font: 16px Geist, -apple-system, sans-serif;
  cursor: pointer;
}
</style>
</head>
<body>
<header>
  <h1>Forsk</h1>
  <p class='note'>This window is WebKit, so the type and the field are ours. The dock is unchanged. Send reads the open document. Then click a wall. An extra click means the page kept the keyboard.</p>
</header>
<div id='thread'></div>
<form id='ask'>
  <div class='composer'>
    <input id='q' placeholder='Ask Rhino for this file…' autocomplete='off'>
    <button type='submit' aria-label='Send'>↑</button>
  </div>
</form>
<script>
document.getElementById('ask').addEventListener('submit', function (event) {
  event.preventDefault();
  var input = document.getElementById('q');
  var text = (input.value || '').trim();
  if (!text) return;
  input.value = '';
  document.title = 'forsk:' + encodeURIComponent(text);
});
window.forskShow = function (payload) {
  document.title = 'Forsk';
  var thread = document.getElementById('thread');
  var you = document.createElement('div');
  you.className = 'you';
  you.textContent = payload.text || '';
  var reply = document.createElement('div');
  reply.className = 'reply';
  reply.textContent = payload.line || '';
  thread.appendChild(you);
  thread.appendChild(reply);
  thread.scrollTop = thread.scrollHeight;
};
</script>
</body>
</html>";
    }
}
