/*
 * The Forsk window's page. C# owns the view model and pushes it with
 * Forsk.render; the page draws it and posts actions back over the page
 * channel. The page keeps nothing it cannot rebuild from the model.
 * keyAction, handleKey and makeSender touch no DOM, so they test headless.
 */
(function (root) {
  'use strict';
  var Forsk = root.Forsk = {};

  /*
   * The keyboard contract. state.composer: the key went to the composer.
   * state.card: the id of the open card, if any. Returns the action, or null
   * to leave the key to the field, so letters, æ ø å, a dead key and an IME
   * composition always type. Enter sends, Shift+Enter breaks the line,
   * Cmd+1..3 fire the slots, Cmd+/ opens "What can I do here?", Esc closes a card.
   */
  Forsk.keyAction = function (e, state) {
    if (!e) return null;
    if (e.isComposing || e.keyCode === 229 || e.key === 'Dead' || e.key === 'Process') return null;
    state = state || {};
    if (e.key === 'Enter') {
      if (!state.composer || e.shiftKey || e.metaKey || e.ctrlKey || e.altKey) return null;
      return { kind: 'send' };
    }
    if (e.key === 'Escape') return state.card ? { kind: 'card.close', card: state.card } : null;
    if (e.metaKey && !e.ctrlKey && !e.altKey) {
      if (!e.shiftKey && (e.key === '1' || e.key === '2' || e.key === '3')) return { kind: 'slot', slot: Number(e.key) };
      // Slash is Shift+7 on a Norwegian keyboard, so Shift does not matter here.
      if (e.key === '/') return { kind: 'help' };
    }
    return null;
  };

  /*
   * One keydown. A key the contract maps is eaten here (preventDefault), so it
   * reaches neither the field nor Rhino, and becomes one Forsk action on the
   * channel. Enter on an empty composer is eaten and sends nothing.
   */
  Forsk.handleKey = function (e, state, send) {
    var action = Forsk.keyAction(e, state);
    if (!action) return null;
    e.preventDefault();
    if (e.stopPropagation) e.stopPropagation();
    if (action.kind === 'send') {
      var text = String((state && state.text) || '').replace(/^\s+|\s+$/g, '');
      if (!text) return action;
      action.text = text;
    }
    send(action);
    return action;
  };

  /*
   * The page side of the channel. Each action gets the next sequence number
   * and waits in one queue. One post is in flight at a time; a failed post is
   * tried again, so none is dropped and the order holds. A 4xx answer is final
   * (a stale page or a bad action) and moves on. post(body) returns a promise
   * of the HTTP status. later(fn, ms) schedules a retry.
   */
  Forsk.makeSender = function (token, post, later) {
    var seq = 0;
    var queue = [];
    var inFlight = false;
    var tries = 0;
    later = later || function (fn, ms) { root.setTimeout(fn, ms); };

    function retry() {
      tries += 1;
      later(pump, Math.min(2000, 50 * tries));
    }

    function settle(status) {
      inFlight = false;
      if ((status >= 200 && status < 300) || (status >= 400 && status < 500)) {
        queue.shift();
        tries = 0;
        pump();
      } else {
        retry();
      }
    }

    function pump() {
      if (inFlight || queue.length === 0) return;
      inFlight = true;
      var body = JSON.stringify(queue[0]);
      try {
        post(body).then(settle, function () { inFlight = false; retry(); });
      } catch (err) {
        inFlight = false;
        retry();
      }
    }

    return {
      send: function (action) {
        var msg = {};
        for (var key in action) if (Object.prototype.hasOwnProperty.call(action, key)) msg[key] = action[key];
        seq += 1;
        msg.seq = seq;
        msg.t = token;
        queue.push(msg);
        pump();
        return seq;
      },
      waiting: function () { return queue.length; }
    };
  };

  // ---------------------------------------------------------------- DOM

  var model = null;
  var sender = null;

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = text;
    return node;
  }

  function openCard() {
    if (!model || !model.thread) return null;
    for (var i = model.thread.length - 1; i >= 0; i--) {
      var item = model.thread[i];
      if (item.role === 'card' && item.state === 'open') return item.id;
    }
    return null;
  }

  function receipt(item) {
    var row = el('div', 'receipt');
    row.appendChild(el('span', item.ok === false ? 'cross' : 'tick', item.ok === false ? '✗' : '✓'));
    if (item.subject) {
      row.appendChild(el('b', null, item.subject));
      row.appendChild(document.createTextNode(' '));
    }
    row.appendChild(document.createTextNode(item.text || ''));
    return row;
  }

  function card(item) {
    if (item.state !== 'open') {
      var done = el('div', 'card ' + (item.state === 'stale' ? 'stale' : 'done'));
      done.textContent = item.state === 'answered' ? (item.question + ' ' + (item.answer || '')) : item.question;
      return done;
    }
    var box = el('div', 'card');
    box.appendChild(el('div', 'question', item.question));
    var pills = el('div', 'pills');
    (item.pills || []).forEach(function (pill, index) {
      var button = el('button', 'pill' + (index === 0 ? ' primary' : ''), pill.label);
      button.type = 'button';
      button.addEventListener('click', function () {
        sender.send({ kind: 'card', card: item.id, pill: pill.id });
      });
      pills.appendChild(button);
    });
    box.appendChild(pills);
    return box;
  }

  function item(entry) {
    if (entry.role === 'user' || entry.role === 'assistant') {
      var row = el('div', 'row ' + entry.role);
      var bubble = el('div', 'bubble', entry.text);
      if (entry.role === 'assistant' && entry.mark) {
        var wrap = el('div');
        wrap.appendChild(el('span', 'role', entry.mark));
        wrap.appendChild(bubble);
        row.appendChild(wrap);
      } else {
        row.appendChild(bubble);
      }
      return row;
    }
    if (entry.role === 'receipt') return receipt(entry);
    if (entry.role === 'card') return card(entry);
    return el('div', 'line', entry.text);
  }

  Forsk.render = function (next) {
    model = next || {};
    var thread = document.getElementById('thread');
    var atEnd = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 40;
    document.getElementById('file').textContent = model.file || '';
    document.getElementById('target').textContent = model.target || '';
    document.getElementById('status').textContent = model.status || '';
    while (thread.firstChild) thread.removeChild(thread.firstChild);
    (model.thread || []).forEach(function (entry) { thread.appendChild(item(entry)); });
    if (model.busy && model.busy.text) thread.appendChild(el('div', 'step', model.busy.text));
    if (atEnd) thread.scrollTop = thread.scrollHeight;
  };

  Forsk.focus = function () {
    var q = document.getElementById('q');
    if (q) q.focus();
  };

  function grow() {
    var q = document.getElementById('q');
    q.style.height = 'auto';
    q.style.height = q.scrollHeight + 'px';
    document.getElementById('send').disabled = !q.value.replace(/\s+/g, '');
  }

  function boot() {
    sender = Forsk.makeSender(root.FORSK_TOKEN, function (body) {
      return root.fetch('action', { method: 'POST', body: body }).then(function (r) { return r.status; });
    });
    var q = document.getElementById('q');
    function send(action) {
      if (action.kind === 'send') {
        q.value = '';
        grow();
      }
      sender.send(action);
    }
    document.addEventListener('keydown', function (e) {
      Forsk.handleKey(e, { composer: e.target === q, card: openCard(), text: q.value }, send);
    }, true);
    document.getElementById('composer').addEventListener('submit', function (e) {
      e.preventDefault();
      var text = q.value.replace(/^\s+|\s+$/g, '');
      if (text) send({ kind: 'send', text: text });
      q.focus();
    });
    q.addEventListener('input', grow);
    sender.send({ kind: 'ready' });
  }

  if (typeof document !== 'undefined' && document.getElementById && document.getElementById('thread')) boot();
})(this);
