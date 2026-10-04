/*
 * The Forsk window's page. C# owns the view model and pushes it with
 * Forsk.render; the page draws it and posts actions back over the page
 * channel. The page keeps nothing it cannot rebuild from the model.
 * keyAction, handleKey, hoverAction and makeSender touch no DOM, so they test headless.
 */
(function (root) {
  'use strict';
  var Forsk = root.Forsk = {};

  /*
   * The keyboard contract. state.composer: the key went to the composer.
   * state.card: the id of the open card, if any. Returns the action, or null
   * to leave the key to the field, so letters, æ ø å, a dead key and an IME
   * composition always type. Enter sends, Shift+Enter breaks the line,
   * Cmd+1..4 fire the slots, Cmd+/ opens "What can I do here?", Esc closes a card.
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
      if (!e.shiftKey && (e.key === '1' || e.key === '2' || e.key === '3' || e.key === '4')) return { kind: 'slot', slot: Number(e.key) };
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
   * Hover takes the keyboard, and leaving gives it back. Nothing moves while
   * a field that already has text is being typed in, or while a button is
   * down. enabled false is the ForskHoverFocus setting, default on.
   */
  Forsk.hoverAction = function (state) {
    state = state || {};
    if (state.enabled === false || state.dragging || state.typing) return null;
    if (state.edge !== 'enter' && state.edge !== 'leave') return null;
    return { kind: 'hover', edge: state.edge };
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

  /*
   * The face in the header, the same role the chat shows. The turn running
   * now wins (its busy line carries the role), then an override, then the
   * latest answer whose mark is a role, and Planner when the thread has none.
   * A mark that is not a role is skipped.
   */
  Forsk.shownRole = function (model) {
    model = model || {};
    var faces = { planner: 1, modeller: 1, plotter: 1, analyser: 1, support: 1, render: 1 };
    var marks = { Planner: 'planner', Modeller: 'modeller', Plotter: 'plotter', Analyser: 'analyser', Support: 'support', Render: 'render' };
    var active = model.busy && marks[model.busy.mark];
    if (active) return active;
    var value = model.role && model.role.value;
    if (value && faces[value]) return value;
    var thread = model.thread || [];
    for (var i = thread.length - 1; i >= 0; i--) {
      var id = marks[thread[i].mark];
      if (id) return id;
    }
    return 'planner';
  };

  /* The pill's second line: the file, or "auto · file" while the router picks. */
  Forsk.roleSubtitle = function (model) {
    model = model || {};
    var file = model.file || '';
    if (!model.role) return file;
    if (!model.role.value || model.role.value === 'auto') return file ? 'auto \u00b7 ' + file : 'auto';
    return file;
  };

  /* Auto is the clear action, so it sits last. The other options keep their order. */
  Forsk.roleMenu = function (options) {
    var rest = [];
    var auto = null;
    (options || []).forEach(function (option) {
      if (option.id === 'auto') auto = option;
      else rest.push(option);
    });
    if (auto) rest.push(auto);
    return rest;
  };

  /* A check field is ticked unless the card stored 0. No DOM, so it tests headless. */
  Forsk.fieldChecked = function (field) {
    if (!field || !field.check) return false;
    return field.value !== '0' && field.value !== 'false';
  };

  /* The keys with one moved a row up (-1) or down (+1). At an end it stays. No DOM, so it tests headless. */
  Forsk.moveKey = function (order, key, delta) {
    var next = (order || []).slice();
    var at = next.indexOf(key);
    var to = at + delta;
    if (at < 0 || to < 0 || to >= next.length) return next;
    next.splice(at, 1);
    next.splice(to, 0, key);
    return next;
  };

  /* Sheet rows only. A scale select on the same card is not a sheet id. */
  Forsk.orderKeys = function (fields) {
    return (fields || []).filter(function (f) { return f.order; }).map(function (f) { return f.key; });
  };

  /* A card answer: the pill, the values, and the rows' order when the card's rows move. No DOM. */
  Forsk.cardAction = function (item, pillId, values, order) {
    var action = { kind: 'card', card: item.id, pill: pillId };
    var fields = item.fields || [];
    if (fields.length) action.values = values;
    if (fields.some(function (f) { return f.order; })) action.order = order;
    return action;
  };

  /* check, select, long, or a one-line text field. No DOM, so it tests headless. */
  Forsk.fieldKind = function (field) {
    if (field && field.check) return 'check';
    if (field && field.options && field.options.length) return 'select';
    if (field && field.long) return 'long';
    return 'text';
  };

  /* A bullet's lead, up to the colon or the period, else its first four words. */
  Forsk.leadWords = function (line) {
    var match = /^([ \t]*[-*\u2013\u2022]\s+)(\S.*)$/.exec(line || '');
    if (!match) return null;
    var rest = match[2];
    var cut = rest.search(/[.:\u2014]/);
    var lead;
    var tail;
    if (cut > 0 && cut <= 48) {
      lead = rest.substring(0, cut);
      tail = rest.substring(cut);
    } else {
      var words = rest.split(/\s+/);
      lead = words.slice(0, Math.min(4, words.length)).join(' ');
      tail = rest.substring(lead.length);
    }
    return [match[1], lead, tail];
  };

  /*
   * An answer's lists. Unordered and ordered, one nested level. The marker
   * stays off the text: the list draws it. A lead is the same cut as a bullet.
   * No DOM, so it tests headless. Deeper indent stays in that one nested list.
   */
  function listItem(line) {
    var unordered = /^([ \t]*)([-*+]|\u2013|\u2022)[ \t]+(\S.*)$/.exec(line);
    if (unordered) return { t: 'ul', level: listLevel(unordered[1]), text: unordered[3] };
    var ordered = /^([ \t]*)(\d{1,3})[.)][ \t]+(\S.*)$/.exec(line);
    if (ordered) return { t: 'ol', level: listLevel(ordered[1]), text: ordered[3] };
    return null;
  }

  function listLevel(prefix) {
    var n = 0;
    for (var i = 0; i < prefix.length; i++) n += prefix.charAt(i) === '\t' ? 2 : 1;
    return n >= 2 ? 1 : 0;
  }

  function pushNested(parent, child) {
    var lists = parent.lists || (parent.lists = []);
    var last = lists.length ? lists[lists.length - 1] : null;
    if (!last || last.t !== child.t) {
      last = { t: child.t, items: [] };
      lists.push(last);
    }
    last.items.push({ text: child.text });
  }

  Forsk.answerBlocks = function (text) {
    var lines = String(text || '').split('\n');
    var blocks = [];
    var i = 0;
    while (i < lines.length) {
      var item = listItem(lines[i]);
      if (item) {
        var list = { t: item.t, items: [] };
        while (i < lines.length) {
          if (String(lines[i]).replace(/[ \t]/g, '') === '') {
            var look = i + 1;
            var blanks = 1;
            while (look < lines.length && String(lines[look]).replace(/[ \t]/g, '') === '') {
              blanks += 1;
              look += 1;
            }
            var ahead = look < lines.length ? listItem(lines[look]) : null;
            if (blanks === 1 && ahead && (ahead.level > 0 || ahead.t === list.t)) {
              i = look;
              continue;
            }
            break;
          }
          var parsed = listItem(lines[i]);
          if (!parsed) break;
          if (parsed.level === 0 && parsed.t !== list.t) break;
          if (parsed.level === 0 || !list.items.length) {
            list.items.push({ text: parsed.text });
            i += 1;
            continue;
          }
          pushNested(list.items[list.items.length - 1], parsed);
          i += 1;
        }
        blocks.push(list);
        continue;
      }
      var para = [];
      while (i < lines.length && !listItem(lines[i])) {
        para.push(lines[i]);
        i += 1;
      }
      var body = para.join('\n').replace(/\n+$/, '');
      if (body !== '') blocks.push({ t: 'p', text: body });
    }
    return blocks;
  };

  function escapeHtml(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
  }

  function itemHtml(text) {
    var lead = Forsk.leadWords('- ' + String(text || ''));
    if (!lead) return escapeHtml(text);
    return '<b>' + escapeHtml(lead[1]) + '</b>' + escapeHtml(lead[2]);
  }

  function listHtml(block) {
    var html = '<' + block.t + '>';
    var items = block.items || [];
    for (var n = 0; n < items.length; n++) {
      html += '<li><span>' + itemHtml(items[n].text) + '</span>';
      var nested = items[n].lists || [];
      for (var k = 0; k < nested.length; k++) html += listHtml(nested[k]);
      html += '</li>';
    }
    return html + '</' + block.t + '>';
  }

  Forsk.answerHtml = function (text) {
    var blocks = Forsk.answerBlocks(text);
    var html = '';
    for (var n = 0; n < blocks.length; n++) {
      if (blocks[n].t === 'p') html += escapeHtml(blocks[n].text);
      else html += listHtml(blocks[n]);
    }
    return html;
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
    if (item.id) row.setAttribute('data-item', item.id);
    var mark = item.ok === false ? ['cross', '✗'] : item.ok === true ? ['tick', '✓'] : ['tick', '–'];
    row.appendChild(el('span', mark[0], mark[1]));
    var parts = Forsk.splitSubject(item.text, item.subject);
    if (parts[0]) row.appendChild(document.createTextNode(parts[0]));
    if (parts[1]) row.appendChild(el('b', null, parts[1]));
    if (parts[2]) row.appendChild(document.createTextNode(parts[2]));
    return row;
  }

  function pill(label, primary, onClick, icon) {
    var button = el('button', 'pill' + (primary ? ' primary' : ''), label);
    button.type = 'button';
    var drawn = icon ? iconNode(icon) : null;
    if (drawn) button.insertBefore(drawn, button.firstChild);
    button.addEventListener('click', onClick);
    return button;
  }

  /* The holder of a pill icon in window.html. No DOM, so it tests headless. */
  Forsk.iconId = function (name) {
    return 'icon-' + name;
  };

  /* A copy of a pill icon. Icons are strokes with no ids, so a copy needs no renaming. */
  function iconNode(name) {
    var holder = document.getElementById(Forsk.iconId(name));
    var src = holder ? first(holder, 'svg') : null;
    if (!src) return null;
    var node = src.cloneNode(true);
    node.setAttribute('class', 'icon');
    return node;
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
    var order = Forsk.orderKeys(item.fields);
    var wraps = {};
    function moveRow(key, delta) {
      var next = Forsk.moveKey(order, key, delta);
      if (next.join('\n') === order.join('\n')) return;
      // The rows sit together after the question: lay them out again from the node before the first.
      var after = wraps[order[0]].previousSibling;
      order = next;
      order.forEach(function (k) {
        box.insertBefore(wraps[k], after ? after.nextSibling : box.firstChild);
        after = wraps[k];
      });
    }
    (item.fields || []).forEach(function (field) {
      var kind = Forsk.fieldKind(field);
      var wrap = el('label', 'field-row' + (kind === 'check' ? ' check' : kind === 'long' ? ' long' : ''));
      var input = el(kind === 'select' ? 'select' : kind === 'long' ? 'textarea' : 'input');
      input.dataset.key = field.key;
      input.setAttribute('aria-label', field.label || item.question);
      if (kind === 'check') {
        input.type = 'checkbox';
        input.checked = Forsk.fieldChecked(field);
        wrap.appendChild(input);
        if (field.label) wrap.appendChild(el('span', 'field-label', field.label));
      } else if (kind === 'select') {
        if (field.label) wrap.appendChild(el('span', 'field-label', field.label));
        (field.options || []).forEach(function (option) {
          var choice = el('option', null, option);
          choice.value = option;
          if (option === field.value) choice.selected = true;
          input.appendChild(choice);
        });
        wrap.appendChild(input);
      } else {
        if (field.label) wrap.appendChild(el('span', 'field-label', field.label));
        if (kind === 'text') {
          input.type = 'text';
          if (field.unit === 'mm') input.inputMode = 'decimal';
        } else input.rows = 4;
        input.value = field.value || '';
        wrap.appendChild(input);
        if (field.unit) wrap.appendChild(el('span', 'unit', field.unit));
      }
      if (field.order) {
        ['\u2191', '\u2193'].forEach(function (arrow, i) {
          var move = el('button', 'move', arrow);
          move.type = 'button';
          move.setAttribute('aria-label', (i === 0 ? 'Up: ' : 'Down: ') + (field.label || field.key));
          move.addEventListener('click', function (e) {
            e.preventDefault();
            moveRow(field.key, i === 0 ? -1 : 1);
          });
          wrap.appendChild(move);
        });
      }
      wraps[field.key] = wrap;
      box.appendChild(wrap);
      inputs.push(input);
    });
    function values() {
      var v = {};
      inputs.forEach(function (input) {
        v[input.dataset.key] = input.type === 'checkbox' ? (input.checked ? '1' : '0') : input.value;
      });
      return v;
    }
    var pills = el('div', 'pills');
    (item.pills || []).forEach(function (p, index) {
      pills.appendChild(pill(p.label, index === 0, function () {
        sender.send(Forsk.cardAction(item, p.id, values(), order));
      }, p.icon));
    });
    box.appendChild(pills);
    if (item.note) box.appendChild(el('div', 'note', item.note));
    inputs.forEach(function (input) {
      input.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' || e.isComposing) return;
        e.preventDefault();
        if (item.pills && item.pills.length) sender.send(Forsk.cardAction(item, item.pills[0].id, values(), order));
      });
    });
    if (inputs.length) setTimeout(function () {
      var focus = inputs[0];
      for (var i = 0; i < inputs.length; i++) if (inputs[i].type !== 'checkbox') { focus = inputs[i]; break; }
      focus.focus();
      if (focus.tagName !== 'SELECT' && focus.type !== 'checkbox' && focus.select) focus.select();
    }, 0);
    return box;
  }

  /*
   * A face is inline SVG, painted at device pixels. The same drawing in an
   * img is a CSS-pixel bitmap, soft on Retina. Every copy renames its mask
   * and gradient: the faces share one document, and a repeated face would
   * collide. faceStamp touches no DOM, so it tests headless.
   */
  var avatarStamp = 0;

  Forsk.faceStamp = function (id, n) {
    return { mask: id + '-' + n + '-mask', fill: id + '-' + n + '-fill' };
  };

  /* HTML getElementsByTagName lowercases, so it misses radialGradient. */
  function first(node, name) {
    var list = node.getElementsByTagName('*');
    var want = name.toLowerCase();
    for (var i = 0; i < list.length; i++) {
      var tag = list[i].localName || list[i].tagName || '';
      if (String(tag).toLowerCase() === want) return list[i];
    }
    return null;
  }

  function avatarNode(id, cls) {
    var holder = document.getElementById('avatar-' + id);
    if (!holder) return null;
    var src = first(holder, 'svg');
    if (!src) return null;
    avatarStamp += 1;
    var stamp = Forsk.faceStamp(id, avatarStamp);
    var node = src.cloneNode(true);
    node.removeAttribute('id');
    /* Fill the CSS box. With no size, an SVG falls back to 300 by 150. */
    node.setAttribute('width', '100%');
    node.setAttribute('height', '100%');
    if (cls) node.setAttribute('class', cls);
    node.setAttribute('aria-hidden', 'true');
    var mask = first(node, 'mask');
    var fill = first(node, 'radialGradient');
    var maskId = mask ? mask.getAttribute('id') : '';
    var fillId = fill ? fill.getAttribute('id') : '';
    if (mask) mask.setAttribute('id', stamp.mask);
    if (fill) fill.setAttribute('id', stamp.fill);
    var groups = node.getElementsByTagName('g');
    for (var i = 0; i < groups.length; i++) {
      if (groups[i].getAttribute('mask') === 'url(#' + maskId + ')')
        groups[i].setAttribute('mask', 'url(#' + stamp.mask + ')');
    }
    var rects = node.getElementsByTagName('rect');
    for (var j = 0; j < rects.length; j++) {
      if (rects[j].getAttribute('fill') === 'url(#' + fillId + ')')
        rects[j].setAttribute('fill', 'url(#' + stamp.fill + ')');
    }
    return node;
  }

  function setFace(id) {
    var slot = document.getElementById('avatar');
    if (!slot || !id || slot.getAttribute('data-face') === id) return;
    var node = avatarNode(id);
    if (!node) return;
    while (slot.firstChild) slot.removeChild(slot.firstChild);
    slot.setAttribute('data-face', id);
    slot.appendChild(node);
  }

  function textBlock(text, cls) {
    var node = el('div', cls);
    var blocks = Forsk.answerBlocks(text);
    var listed = false;
    for (var i = 0; i < blocks.length; i++) if (blocks[i].t !== 'p') listed = true;
    if (!listed) {
      node.textContent = text || '';
      return node;
    }
    node.innerHTML = Forsk.answerHtml(text);
    return node;
  }

  /* The role that answered, once, above the first reply of a turn. */
  function marked(node, mark) {
    if (!mark) return node;
    var wrap = el('div', 'marked');
    var meta = el('div', 'meta');
    var id = { Planner: 'planner', Modeller: 'modeller', Plotter: 'plotter', Analyser: 'analyser', Support: 'support', Render: 'render' }[mark];
    var face = avatarNode(id, 'mini');
    if (face) meta.appendChild(face);
    meta.appendChild(el('span', 'role', mark));
    wrap.appendChild(meta);
    wrap.appendChild(node);
    return wrap;
  }

  function item(entry) {
    if (entry.role === 'user') {
      var row = el('div', 'row user');
      row.appendChild(el('div', 'bubble', entry.text));
      return row;
    }
    if (entry.role === 'assistant') return marked(textBlock(entry.text, 'answer'), entry.mark);
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

  function optionLabel(role, id) {
    var options = (role && role.options) || [];
    for (var i = 0; i < options.length; i++) if (options[i].id === id) return options[i].label;
    return id ? id.charAt(0).toUpperCase() + id.slice(1) : 'Planner';
  }

  /* The header picker. The menu is left alone while it is open, as the old select was while it had focus. */
  function renderRole(model) {
    var role = model && model.role;
    var pill = document.getElementById('role-pill');
    var menu = document.getElementById('role-menu');
    var name = document.getElementById('role-name');
    var shown = Forsk.shownRole(model);
    name.textContent = optionLabel(role, shown);
    document.getElementById('file').textContent = Forsk.roleSubtitle(model);
    setFace(shown);
    pill.className = 'role-pill' + (role && role.value && role.value !== 'auto' ? ' set' : '');
    pill.title = (role && role.title) || '';
    var spoken = (role && role.label ? role.label + ', ' : '') + name.textContent;
    var sub = document.getElementById('file').textContent;
    if (sub) spoken += ', ' + sub;
    pill.setAttribute('aria-label', spoken);
    if (role && role.label) menu.setAttribute('aria-label', role.label);
    if (!menu.hidden) return;
    while (menu.firstChild) menu.removeChild(menu.firstChild);
    Forsk.roleMenu(role && role.options).forEach(function (option) {
      var button = el('button');
      button.type = 'button';
      button.setAttribute('role', 'menuitemradio');
      button.setAttribute('aria-checked', role && option.id === role.value ? 'true' : 'false');
      if (option.id !== 'auto') {
        var icon = avatarNode(option.id, 'mini');
        if (icon) button.appendChild(icon);
      }
      button.appendChild(document.createTextNode(option.label));
      button.addEventListener('click', function () {
        closeMenus(false);
        sender.send({ kind: 'role', role: option.id });
      });
      menu.appendChild(button);
    });
  }

  function attentionOn(list, id) {
    for (var i = 0; i < list.length; i++) {
      if (!list[i] || !list[i].needs) continue;
      if (id == null || list[i].id === id) return true;
    }
    return false;
  }

  function renderGear(model) {
    var on = attentionOn((model && model.attention) || [], null);
    var dot = document.getElementById('gear-dot');
    if (dot) dot.hidden = !on;
    var gear = document.getElementById('more');
    if (!gear) return;
    var label = on ? 'Settings, needs attention' : 'Settings';
    gear.setAttribute('aria-label', label);
    gear.title = label;
  }

  function renderSettings(model) {
    renderGear(model);
    var menu = document.getElementById('more-menu');
    if (!menu.hidden) return;
    var box = document.getElementById('settings');
    while (box.firstChild) box.removeChild(box.firstChild);
    var items = (model && model.settings) || [];
    var attention = (model && model.attention) || [];
    items.forEach(function (item) {
      var button = el('button', null, item.label);
      button.type = 'button';
      button.setAttribute('role', 'menuitem');
      if (attentionOn(attention, item.id)) {
        var dot = el('span', 'notice');
        dot.setAttribute('aria-hidden', 'true');
        button.appendChild(dot);
        button.setAttribute('aria-label', item.label + ', needs attention');
      }
      button.addEventListener('click', function () {
        closeMenus(false);
        sender.send({ kind: 'action', id: item.id });
      });
      box.appendChild(button);
    });
    var help = model && model.bar && model.bar.help;
    if (!help) return;
    var h = el('button', items.length ? 'split' : null, help.title || help.label);
    h.type = 'button';
    h.setAttribute('role', 'menuitem');
    h.addEventListener('click', function () {
      closeMenus(false);
      sender.send({ kind: 'help' });
    });
    box.appendChild(h);
  }

  /* Horizontal three dots, 16×16, same weight as the header icons. */
  function moreMark() {
    var ns = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('width', '16');
    svg.setAttribute('height', '16');
    svg.setAttribute('aria-hidden', 'true');
    [5, 12, 19].forEach(function (cx) {
      var dot = document.createElementNS(ns, 'circle');
      dot.setAttribute('cx', String(cx));
      dot.setAttribute('cy', '12');
      dot.setAttribute('r', '1.25');
      dot.setAttribute('fill', 'currentColor');
      svg.appendChild(dot);
    });
    return svg;
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
      var help = el('button', 'help-button' + (model && model.help ? ' open' : ''));
      help.type = 'button';
      help.title = bar.help.title + '  ' + bar.help.key;
      help.setAttribute('aria-label', bar.help.title);
      help.appendChild(moreMark());
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
    document.getElementById('target').textContent = model.target || '';
    document.getElementById('status').textContent = model.status || '';
    while (thread.firstChild) thread.removeChild(thread.firstChild);
    (model.thread || []).forEach(function (entry) { thread.appendChild(item(entry)); });
    if (model.busy && model.busy.text) thread.appendChild(busy(model.busy));
    if (atEnd) thread.scrollTop = thread.scrollHeight;
    // The bar never reorders under the pointer: it waits until the pointer leaves.
    if (!barHovered) renderBar(model.bar);
    renderRole(model);
    renderSettings(model);
    renderSheet(model.help);
    applyPrefill(model.prefill);
    showHover();
  };

  /*
   * The viewport's hover, answered here: C# sends the thread items that name
   * the element under the pointer (ids, empty to clear). The ids stay, so a
   * render that redraws the thread lights the same rows again.
   */
  Forsk.hoverStyle = 'hl';

  var hovered = [];

  function showHover(reveal) {
    var thread = document.getElementById('thread');
    if (!thread) return;
    var lit = thread.querySelectorAll('.' + Forsk.hoverStyle);
    for (var i = 0; i < lit.length; i++) lit[i].classList.remove(Forsk.hoverStyle);
    var first = null;
    for (var j = 0; j < hovered.length; j++) {
      var row = thread.querySelector('[data-item="' + String(hovered[j]).replace(/[^A-Za-z0-9_-]/g, '') + '"]');
      if (!row) continue;
      row.classList.add(Forsk.hoverStyle);
      if (!first) first = row;
    }
    if (reveal && first && first.scrollIntoView) first.scrollIntoView({ block: 'nearest' });
  }

  Forsk.hover = function (ids) {
    hovered = ids || [];
    showHover(true);
  };

  Forsk.focus = function () {
    var q = document.getElementById('q');
    if (q) q.focus();
  };

  /** The composer's height: whole lines of its own line height, at least one. */
  Forsk.composerHeight = function (scroll, line) {
    line = +line;
    if (!(line > 0)) line = 14 * 1.4;
    var lines = Math.max(1, Math.round((+scroll) / line));
    return lines * line;
  };

  function grow() {
    var q = document.getElementById('q');
    var line = 14 * 1.4;
    if (root.getComputedStyle) {
      var read = parseFloat(root.getComputedStyle(q).lineHeight);
      if (read > 0) line = read;
    }
    q.style.height = line + 'px';
    q.style.height = Forsk.composerHeight(q.scrollHeight, line) + 'px';
    document.getElementById('send').disabled = !q.value.replace(/\s+/g, '');
  }

  var menuOwner = null;

  function menuButtons(menu) {
    var list = [];
    var nodes = menu.getElementsByTagName('button');
    for (var i = 0; i < nodes.length; i++) list.push(nodes[i]);
    return list;
  }

  function openMenuEl() {
    var roleMenu = document.getElementById('role-menu');
    if (roleMenu && !roleMenu.hidden) return roleMenu;
    var moreMenu = document.getElementById('more-menu');
    if (moreMenu && !moreMenu.hidden) return moreMenu;
    return null;
  }

  function holds(id, node) {
    var box = document.getElementById(id);
    return !!(box && node && (box === node || box.contains(node)));
  }

  function closeMenus(back) {
    document.getElementById('role-menu').hidden = true;
    document.getElementById('more-menu').hidden = true;
    document.getElementById('role-pill').setAttribute('aria-expanded', 'false');
    document.getElementById('more').setAttribute('aria-expanded', 'false');
    var owner = menuOwner;
    menuOwner = null;
    if (back && owner) owner.focus();
  }

  function openMenu(id, owner) {
    closeMenus(false);
    var menu = document.getElementById(id);
    menu.hidden = false;
    menuOwner = owner;
    owner.setAttribute('aria-expanded', 'true');
    var items = menuButtons(menu);
    var current = null;
    for (var i = 0; i < items.length; i++) if (items[i].getAttribute('aria-checked') === 'true') current = items[i];
    (current || items[0] || owner).focus();
  }

  function boot() {
    setFace('planner');
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
      var menu = openMenuEl();
      if (menu && e.key === 'Escape') {
        e.preventDefault();
        if (e.stopPropagation) e.stopPropagation();
        closeMenus(true);
        return;
      }
      if (menu && (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Home' || e.key === 'End')) {
        var items = menuButtons(menu);
        if (items.length) {
          e.preventDefault();
          var index = -1;
          for (var i = 0; i < items.length; i++) if (items[i] === document.activeElement) index = i;
          var next = index;
          if (e.key === 'ArrowDown') next = index + 1;
          if (e.key === 'ArrowUp') next = index - 1;
          if (e.key === 'Home' || (index < 0 && e.key === 'ArrowDown')) next = 0;
          if (e.key === 'End' || (index < 0 && e.key === 'ArrowUp')) next = items.length - 1;
          if (next < 0) next = items.length - 1;
          if (next >= items.length) next = 0;
          items[next].focus();
        }
        return;
      }
      Forsk.handleKey(e, { composer: e.target === q, card: openCard(), text: q.value }, send);
    }, true);
    document.getElementById('composer').addEventListener('submit', function (e) {
      e.preventDefault();
      var text = q.value.replace(/^\s+|\s+$/g, '');
      if (text) send({ kind: 'send', text: text });
      q.focus();
    });
    document.getElementById('add').addEventListener('click', function () { sender.send({ kind: 'action', id: 'file.import' }); });
    // One thread per file. There is no separate history list, so this scrolls that thread.
    document.getElementById('history').addEventListener('click', function () {
      var thread = document.getElementById('thread');
      thread.scrollTop = 0;
    });
    var pill = document.getElementById('role-pill');
    var more = document.getElementById('more');
    pill.addEventListener('click', function () {
      if (document.getElementById('role-menu').hidden) openMenu('role-menu', pill);
      else closeMenus(true);
    });
    more.addEventListener('click', function () {
      if (document.getElementById('more-menu').hidden) openMenu('more-menu', more);
      else closeMenus(true);
    });
    pill.addEventListener('keydown', function (e) {
      if (e.key !== 'ArrowDown') return;
      e.preventDefault();
      openMenu('role-menu', pill);
    });
    document.addEventListener('mousedown', function (e) {
      if (!openMenuEl()) return;
      if (holds('role-menu', e.target) || holds('role-pill', e.target) || holds('more-menu', e.target) || holds('more', e.target)) return;
      closeMenus(false);
    });
    var hoverDown = false;
    function hoverTyping() {
      var node = document.activeElement;
      if (!node) return false;
      if (node.tagName === 'SELECT') return true;
      if (node.tagName !== 'INPUT' && node.tagName !== 'TEXTAREA') return false;
      var type = String(node.type || '').toLowerCase();
      if (type === 'checkbox' || type === 'radio' || type === 'button' || type === 'submit') return false;
      return String(node.value || '').length > 0;
    }
    function postHover(edge, e) {
      var state = {
        edge: edge,
        enabled: !model || model.hoverFocus !== false,
        typing: hoverTyping(),
        dragging: hoverDown || !!(e && e.buttons)
      };
      var action = Forsk.hoverAction(state);
      if (!action) {
        // The button is still down. C# gives the keyboard back when the drag ends outside.
        if (edge === 'leave' && state.dragging && !state.typing && state.enabled !== false)
          sender.send({ kind: 'hover', edge: 'leave', dragging: true });
        return;
      }
      if (action.edge === 'enter') Forsk.focus();
      sender.send(action);
    }
    document.documentElement.addEventListener('mouseenter', function (e) {
      if (!e.buttons) hoverDown = false;
      postHover('enter', e);
    });
    document.documentElement.addEventListener('mouseleave', function (e) { postHover('leave', e); });
    document.addEventListener('pointerdown', function () { hoverDown = true; });
    document.addEventListener('pointerup', function () { hoverDown = false; });
    document.addEventListener('pointercancel', function () { hoverDown = false; });
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
