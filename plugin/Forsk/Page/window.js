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

  /*
   * A receipt's object, bold where the text names it, or before the text.
   * Returns [before, bold, after]; no DOM, so it tests headless.
   */
  Forsk.splitSubject = function (text, subject) {
    text = text || '';
    if (!subject) return [text, '', ''];
    var at = text.indexOf(subject);
    if (at < 0) return ['', subject, text ? ' ' + text : ''];
    return [text.substring(0, at), subject, text.substring(at + subject.length)];
  };

  // ---------------------------------------------------------------- DOM

  var model = null;
  var sender = null;
  var barHovered = false;
  var lastPrefill = 0;

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = text;
    return node;
  }

  function openCard() {
    if (model && model.help) return 'help';
    if (!model || !model.thread) return null;
    for (var i = model.thread.length - 1; i >= 0; i--) {
      var item = model.thread[i];
      if (item.role === 'card' && item.state === 'open') return item.id;
    }
    return null;
  }

  function receipt(item) {
    var row = el('div', 'receipt');
    var mark = item.ok === false ? ['cross', '✗'] : item.ok === true ? ['tick', '✓'] : ['tick', '–'];
    row.appendChild(el('span', mark[0], mark[1]));
    var parts = Forsk.splitSubject(item.text, item.subject);
    if (parts[0]) row.appendChild(document.createTextNode(parts[0]));
    if (parts[1]) row.appendChild(el('b', null, parts[1]));
    if (parts[2]) row.appendChild(document.createTextNode(parts[2]));
    return row;
  }

  function pill(label, primary, onClick) {
    var button = el('button', 'pill' + (primary ? ' primary' : ''), label);
    button.type = 'button';
    button.addEventListener('click', onClick);
    return button;
  }

  function card(item) {
    if (item.state !== 'open') {
      var done = el('div', 'card ' + (item.state === 'stale' ? 'stale' : 'done'));
      done.textContent = item.state === 'answered' ? item.question + ' · ' + (item.answer || '') : item.question;
      return done;
    }
    var box = el('div', 'card');
    box.appendChild(el('div', 'question', item.question));
    if (item.rows && item.rows.length) {
      var list = el('ul', 'rows');
      item.rows.forEach(function (row) { list.appendChild(el('li', null, row)); });
      box.appendChild(list);
    }
    var inputs = [];
    (item.fields || []).forEach(function (field) {
      var wrap = el('label', 'field-row');
      if (field.label) wrap.appendChild(el('span', 'field-label', field.label));
      var input = el('input');
      input.type = 'text';
      if (field.unit === 'mm') input.inputMode = 'decimal';
      input.value = field.value || '';
      input.setAttribute('aria-label', field.label || item.question);
      input.dataset.key = field.key;
      wrap.appendChild(input);
      if (field.unit) wrap.appendChild(el('span', 'unit', field.unit));
      box.appendChild(wrap);
      inputs.push(input);
    });
    function values() {
      var v = {};
      inputs.forEach(function (input) { v[input.dataset.key] = input.value; });
      return v;
    }
    var pills = el('div', 'pills');
    (item.pills || []).forEach(function (p, index) {
      pills.appendChild(pill(p.label, index === 0, function () {
        var action = { kind: 'card', card: item.id, pill: p.id };
        if (inputs.length) action.values = values();
        sender.send(action);
      }));
    });
    box.appendChild(pills);
    if (item.note) box.appendChild(el('div', 'note', item.note));
    inputs.forEach(function (input) {
      input.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' || e.isComposing) return;
        e.preventDefault();
        if (item.pills && item.pills.length) sender.send({ kind: 'card', card: item.id, pill: item.pills[0].id, values: values() });
      });
    });
    if (inputs.length) setTimeout(function () { inputs[0].focus(); inputs[0].select(); }, 0);
    return box;
  }

  /* The role that answered, once, above the first reply of a turn. */
  function marked(node, mark) {
    if (!mark) return node;
    var wrap = el('div', 'marked');
    wrap.appendChild(el('span', 'role', mark));
    wrap.appendChild(node);
    return wrap;
  }

  function item(entry) {
    if (entry.role === 'user' || entry.role === 'assistant') {
      var row = el('div', 'row ' + entry.role);
      row.appendChild(el('div', 'bubble', entry.text));
      return marked(row, entry.mark);
    }
    if (entry.role === 'receipt') return marked(receipt(entry), entry.mark);
    if (entry.role === 'card') return marked(card(entry), entry.mark);
    return el('div', 'line', entry.text);
  }

  function busy(state) {
    if (state.kind === 'thinking') {
      var dots = el('div', 'dots');
      dots.setAttribute('aria-label', state.text || '');
      dots.appendChild(el('span'));
      dots.appendChild(el('span'));
      dots.appendChild(el('span'));
      return marked(dots, state.mark);
    }
    return marked(el('div', 'step', state.text), state.mark);
  }

  /* The role control: Auto, or a pick that stays until Auto clears it. Left alone while it has focus. */
  function renderRole(role) {
    var select = document.getElementById('role');
    if (!role) {
      select.style.display = 'none';
      return;
    }
    select.style.display = '';
    if (document.activeElement === select) return;
    var ids = (role.options || []).map(function (o) { return o.id + ':' + o.label; }).join('|');
    if (select.dataset.ids !== ids) {
      while (select.firstChild) select.removeChild(select.firstChild);
      (role.options || []).forEach(function (o) {
        var option = el('option', null, o.label);
        option.value = o.id;
        select.appendChild(option);
      });
      select.dataset.ids = ids;
    }
    select.value = role.value;
    select.className = 'role-pick' + (role.value !== 'auto' ? ' set' : '');
    select.setAttribute('aria-label', role.label || '');
    select.title = role.title || '';
  }

  function renderBar(bar) {
    var nav = document.getElementById('bar');
    while (nav.firstChild) nav.removeChild(nav.firstChild);
    if (!bar) return;
    var slots = el('div', 'slots');
    (bar.slots || []).forEach(function (slot, index) {
      var button = el('button', 'pill slot' + (index === 0 ? ' primary' : ''), slot.label);
      button.type = 'button';
      button.title = slot.label + '  ' + slot.key;
      button.addEventListener('click', function () { sender.send({ kind: 'action', id: slot.id }); });
      slots.appendChild(button);
    });
    if (bar.help) {
      var help = el('button', 'help-button' + (model && model.help ? ' open' : ''), bar.help.label);
      help.type = 'button';
      help.title = bar.help.title + '  ' + bar.help.key;
      help.setAttribute('aria-label', bar.help.title);
      help.addEventListener('click', function () { sender.send({ kind: 'help' }); });
      slots.appendChild(help);
    }
    nav.appendChild(slots);
    if (bar.reason) {
      var reason = el('div', 'reason');
      reason.appendChild(el('b', null, bar.because || ''));
      reason.appendChild(document.createTextNode(' ' + bar.reason));
      nav.appendChild(reason);
    }
  }

  function renderSheet(help) {
    var sheet = document.getElementById('sheet');
    while (sheet.firstChild) sheet.removeChild(sheet.firstChild);
    sheet.className = help ? 'sheet' : '';
    if (!help) return;
    sheet.appendChild(el('h2', null, help.title || ''));
    (help.groups || []).forEach(function (group) {
      sheet.appendChild(el('h3', null, group.title));
      var pills = el('div', 'pills');
      group.actions.forEach(function (action) {
        pills.appendChild(pill(action.label, false, function () { sender.send({ kind: 'action', id: action.id, from: 'card' }); }));
      });
      sheet.appendChild(pills);
    });
    (help.hints || []).forEach(function (hint) { sheet.appendChild(el('div', 'hint', hint)); });
  }

  function applyPrefill(prefill) {
    if (!prefill || prefill.n <= lastPrefill) return;
    lastPrefill = prefill.n;
    var q = document.getElementById('q');
    q.value = prefill.text;
    grow();
    q.focus();
    q.setSelectionRange(prefill.start, prefill.end);
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
    if (model.busy && model.busy.text) thread.appendChild(busy(model.busy));
    if (atEnd) thread.scrollTop = thread.scrollHeight;
    // The bar never reorders under the pointer: it waits until the pointer leaves.
    if (!barHovered) renderBar(model.bar);
    renderRole(model.role);
    renderSheet(model.help);
    applyPrefill(model.prefill);
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
      // Absolute: loadHTMLString does not make the document's URL the base URL, so a relative "action" never hits the channel.
      return root.fetch(root.FORSK_ORIGIN + 'action', { method: 'POST', body: body }).then(function (r) { return r.status; });
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
    document.getElementById('add').addEventListener('click', function () { sender.send({ kind: 'action', id: 'file.import' }); });
    var role = document.getElementById('role');
    role.addEventListener('change', function () {
      role.className = 'role-pick' + (role.value !== 'auto' ? ' set' : '');
      sender.send({ kind: 'role', role: role.value });
    });
    var nav = document.getElementById('bar');
    nav.addEventListener('mouseenter', function () { barHovered = true; });
    nav.addEventListener('mouseleave', function () {
      barHovered = false;
      if (model) renderBar(model.bar);
    });
    q.addEventListener('input', grow);
    sender.send({ kind: 'ready' });
  }

  if (typeof document !== 'undefined' && document.getElementById && document.getElementById('thread')) boot();
})(this);
