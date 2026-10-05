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
   * Cmd+1..4 fire the slots, Cmd+/ opens "What can I do here?", Esc cancels a
   * form and closes any other card.
   */
  Forsk.keyAction = function (e, state) {
    if (!e) return null;
    if (e.isComposing || e.keyCode === 229 || e.key === 'Dead' || e.key === 'Process') return null;
    state = state || {};
    if (e.key === 'Enter') {
      if (!state.composer || e.shiftKey || e.metaKey || e.ctrlKey || e.altKey) return null;
      return { kind: 'send' };
    }
    if (e.key === 'Escape') {
      if (!state.card) return null;
      if (state.cancel) return { kind: 'card', card: state.card, pill: 'cancel' };
      return { kind: 'card.close', card: state.card };
    }
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
   * The face in the header, the same role the chat shows. The turn that just
   * started wins, before a reply exists. Idle keeps that role (shown), so it
   * does not snap back to an older message or to a pick. A thread with no
   * remembered role uses its latest mark, then a pick, then Planner.
   * A mark that is not a role is skipped.
   */
  Forsk.shownRole = function (model) {
    model = model || {};
    var faces = { planner: 1, modeller: 1, plotter: 1, analyser: 1, support: 1, render: 1 };
    var marks = { Planner: 'planner', Modeller: 'modeller', Plotter: 'plotter', Analyser: 'analyser', Support: 'support', Render: 'render' };
    function face(mark) {
      if (!mark) return '';
      if (marks[mark]) return marks[mark];
      return faces[mark] ? mark : '';
    }
    var active = face(model.turn) || (model.busy && face(model.busy.mark));
    if (active) return active;
    var kept = face(model.shown);
    if (kept) return kept;
    var thread = model.thread || [];
    for (var i = thread.length - 1; i >= 0; i--) {
      var id = face(thread[i].mark);
      if (id) return id;
    }
    var value = model.role && model.role.value;
    if (value && faces[value]) return value;
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

  /* An open card that asks for a typed or chosen value. A tick list is not one. No DOM. */
  Forsk.isForm = function (item) {
    if (!item || item.role !== 'card' || item.state !== 'open') return false;
    var fields = item.fields || [];
    for (var i = 0; i < fields.length; i++) {
      var kind = Forsk.fieldKind(fields[i]);
      if (kind === 'text' || kind === 'long' || kind === 'select') return true;
    }
    return false;
  };

  /*
   * An answered card's one line. A form stores its receipt ("Project info
   * saved · Test house, 2026-07") and that is the whole line: the pill is
   * not repeated. A choice card stays "question · answer". No DOM.
   */
  Forsk.cardLine = function (item) {
    if (!item) return '';
    if (item.state === 'answered' && item.receipt) return item.receipt;
    if (item.state === 'answered') return (item.question || '') + ' · ' + (item.answer || '');
    return item.question || '';
  };

  /* True when that card has a Cancel pill. Esc uses it. No DOM. */
  Forsk.cardCancels = function (model, id) {
    if (!id || !model || !model.thread) return false;
    var thread = model.thread;
    for (var i = 0; i < thread.length; i++) {
      if (thread[i].id !== id) continue;
      var pills = thread[i].pills || [];
      for (var p = 0; p < pills.length; p++) if (pills[p].id === 'cancel') return true;
    }
    return false;
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
  var barModel = null;
  var lastPrefill = 0;
  /* The render in progress is keeping the reader on the newest line. */
  var followLatest = true;
  /* The pinned form already drawn, so a later render does not rebuild it or steal the caret. */
  var pinKey = '';
  var pinNewest = '';
  /*
   * An overlay thumb over a scroller whose native bar is zero-width: the
   * message list and the ⋯ sheet. It fades after a scroll unless the pointer
   * is on the frame or the thumb is held. quiet: a render is restoring
   * scrollTop, and those events must not flash the thumb.
   */
  function overlayThumb(frameId, viewId, barId, thumbId) {
    var timer = 0;
    var hover = false;
    var drag = false;
    var t = { quiet: false };
    function get(id) { return document.getElementById(id); }
    t.sync = function () {
      var view = get(viewId);
      var bar = get(barId);
      var thumb = get(thumbId);
      if (!view || !bar || !thumb) return;
      if (!Forsk.scrollThumb(view.clientHeight, view.scrollHeight, view.scrollTop)) {
        bar.hidden = true;
        return;
      }
      bar.hidden = false;
      var geom = Forsk.scrollThumb(view.clientHeight, view.scrollHeight, view.scrollTop, bar.clientHeight);
      if (!geom) {
        bar.hidden = true;
        return;
      }
      thumb.style.height = geom.h + 'px';
      thumb.style.top = geom.y + 'px';
    };
    t.reveal = function () {
      var frame = get(frameId);
      if (!frame || !frame.classList) return;
      var bar = get(barId);
      if (!bar || bar.hidden) {
        frame.classList.remove('thumb-on');
        return;
      }
      frame.classList.add('thumb-on');
      if (timer) root.clearTimeout(timer);
      timer = 0;
      if (hover || drag) return;
      timer = root.setTimeout(function () {
        timer = 0;
        if (hover || drag) return;
        var box = get(frameId);
        if (box && box.classList) box.classList.remove('thumb-on');
      }, 700);
    };
    /* Listeners on the frame, bar and thumb. Call again after the frame's children are rebuilt. */
    t.bind = function () {
      var frame = get(frameId);
      var view = get(viewId);
      var bar = get(barId);
      var thumb = get(thumbId);
      if (!frame || !view || !bar || !thumb) return;
      if (!frame.getAttribute('data-thumb')) {
        frame.setAttribute('data-thumb', '1');
        frame.addEventListener('mouseenter', function () {
          hover = true;
          t.reveal();
        });
        frame.addEventListener('mouseleave', function () {
          hover = false;
          t.reveal();
        });
      }
      thumb.addEventListener('wheel', function (e) {
        var dy = e.deltaY || 0;
        if (e.deltaMode === 1) dy *= 16;
        else if (e.deltaMode === 2) dy *= view.clientHeight || 0;
        view.scrollTop += dy;
        e.preventDefault();
      });
      thumb.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        e.preventDefault();
        var height = view.clientHeight;
        var content = view.scrollHeight;
        var track = bar.clientHeight;
        var origin = Forsk.scrollThumb(height, content, view.scrollTop, track);
        if (!origin) return;
        var startY = e.clientY;
        var startTop = origin.y;
        drag = true;
        thumb.classList.add('drag');
        t.reveal();
        function move(ev) {
          view.scrollTop = Forsk.scrollForThumb(height, content, startTop + (ev.clientY - startY), track);
        }
        function up() {
          drag = false;
          thumb.classList.remove('drag');
          document.removeEventListener('mousemove', move);
          document.removeEventListener('mouseup', up);
          t.reveal();
        }
        document.addEventListener('mousemove', move);
        document.addEventListener('mouseup', up);
      });
    };
    return t;
  }

  var threadThumb = overlayThumb('thread-frame', 'thread', 'thread-bar', 'thread-thumb');
  var sheetThumb = overlayThumb('sheet', 'sheet-body', 'sheet-bar', 'sheet-thumb');
  function syncThreadThumb() { threadThumb.sync(); }
  function revealThreadThumb() { threadThumb.reveal(); }

  Forsk.nearEnd = function (scrollHeight, scrollTop, clientHeight) {
    return scrollHeight - scrollTop - clientHeight < 40;
  };

  /*
   * Overlay thumb for the message list. view and content are the scroller's
   * client and scroll heights, scroll is scrollTop, track is the indicator
   * height (the view, when omitted). The thumb is at least min pixels and
   * never taller than the track. y is 0 at the top and track-h at the end.
   * Null when the list fits, so nothing is drawn and the width stays put.
   */
  Forsk.scrollThumb = function (view, content, scroll, track, min) {
    view = +view;
    content = +content;
    scroll = +scroll;
    if (!(track > 0)) track = view;
    if (!(min > 0)) min = 24;
    if (!(view > 0) || !(track > 0) || !(content > view + 0.5)) return null;
    var maxScroll = content - view;
    if (!(scroll > 0)) scroll = 0;
    if (scroll > maxScroll) scroll = maxScroll;
    var h = track * view / content;
    if (h < min) h = min;
    if (h > track) h = track;
    var travel = track - h;
    var y = maxScroll > 0 && travel > 0 ? scroll / maxScroll * travel : 0;
    return { h: h, y: y };
  };

  /* scrollTop that puts the thumb at y. The inverse of scrollThumb. */
  Forsk.scrollForThumb = function (view, content, y, track, min) {
    var fitted = Forsk.scrollThumb(view, content, 0, track, min);
    if (!fitted) return 0;
    view = +view;
    content = +content;
    if (!(track > 0)) track = view;
    var travel = track - fitted.h;
    if (!(travel > 0) || !(content > view)) return 0;
    var top = +y;
    if (!(top > 0)) top = 0;
    if (top > travel) top = travel;
    return top / travel * (content - view);
  };

  /*
   * How many chips stay on the row. widths are natural widths, primary first.
   * The primary always stays; when it is wider than the row it wraps inside
   * itself and nothing else sits beside it. help is the ⋯ width. A later chip
   * stays only while the whole prefix still fits beside the ⋯.
   */
  Forsk.visibleSlots = function (row, widths, help, gap) {
    row = +row;
    help = +help;
    gap = +gap;
    if (!(row >= 0)) row = 0;
    if (!(help >= 0)) help = 0;
    if (!(gap >= 0)) gap = 0;
    if (!widths || !widths.length) return 0;
    var budget = row - help - (help > 0 ? gap : 0);
    if (!(budget > 0)) return 1;
    var used = 0;
    for (var i = 0; i < widths.length; i++) {
      var w = +widths[i];
      if (!(w >= 0)) w = 0;
      if (i === 0) {
        used = w;
        if (w > budget) return 1;
        continue;
      }
      if (used + gap + w > budget + 0.5) return i;
      used += gap + w;
    }
    return widths.length;
  };

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = text;
    // WebKit leaves a button out of the tab order unless it has tabindex.
    if (tag === 'button') node.tabIndex = 0;
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

  function card(item, opts) {
    if (item.state !== 'open') {
      var done = el('div', 'card ' + (item.state === 'stale' ? 'stale' : 'done'));
      done.textContent = Forsk.cardLine(item);
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
        if (field.placeholder) input.setAttribute('placeholder', field.placeholder);
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
    if (item.image) {
      var picture = el('img', 'logo-preview');
      picture.src = item.image;
      picture.alt = item.note || '';
      box.appendChild(picture);
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
      // A pinned form takes the first field wherever the thread is scrolled, and does not move it.
      if (opts && opts.pin) {
        if (!opts.focus) return;
        focus.focus({ preventScroll: true });
        if (focus.value && focus.tagName !== 'SELECT' && focus.type !== 'checkbox' && focus.select) focus.select();
        return;
      }
      // A reader who left the newest line keeps their place. select() would pull the field up.
      if (!followLatest) return;
      focus.focus({ preventScroll: true });
      if (focus.tagName !== 'SELECT' && focus.type !== 'checkbox' && focus.select) focus.select();
      document.getElementById('thread').scrollTop = document.getElementById('thread').scrollHeight;
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
    var fileEl = document.getElementById('file');
    fileEl.textContent = Forsk.roleSubtitle(model);
    fileEl.title = fileEl.textContent;
    setFace(shown);
    pill.className = 'role-pill' + (role && role.value && role.value !== 'auto' ? ' set' : '');
    pill.title = (role && role.title) || '';
    var spoken = (role && role.label ? role.label + ', ' : '') + name.textContent;
    var sub = fileEl.textContent;
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
    barModel = bar || null;
    if (!bar) return;
    var wrap = el('div', 'slots-wrap');
    var slots = el('div', 'slots');
    (bar.slots || []).forEach(function (slot, index) {
      var button = el('button', 'pill slot' + (index === 0 ? ' primary' : ''), slot.label);
      button.type = 'button';
      button.title = slot.label + '  ' + slot.key;
      button.setAttribute('data-slot', slot.id);
      button.addEventListener('click', function () { sender.send({ kind: 'action', id: slot.id }); });
      slots.appendChild(button);
    });
    if (bar.help) {
      var help = el('button', 'help-button' + (model && model.help ? ' open' : ''));
      help.id = 'help';
      help.type = 'button';
      help.title = bar.help.title + '  ' + bar.help.key;
      help.setAttribute('aria-label', bar.help.title);
      help.setAttribute('aria-expanded', 'false');
      help.appendChild(moreMark());
      help.addEventListener('click', function () {
        var menu = document.getElementById('slot-menu');
        var items = menu ? menu.getElementsByTagName('button').length : 0;
        if (menu && !menu.hidden) {
          closeMenus(true);
          return;
        }
        if (items) {
          openMenu('slot-menu', help);
          return;
        }
        sender.send({ kind: 'help' });
      });
      help.addEventListener('keydown', function (e) {
        if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
        var menu = document.getElementById('slot-menu');
        if (!menu || !menu.getElementsByTagName('button').length) return;
        e.preventDefault();
        openMenu('slot-menu', help);
      });
      slots.appendChild(help);
    }
    wrap.appendChild(slots);
    var menu = el('div', 'menu');
    menu.id = 'slot-menu';
    menu.setAttribute('role', 'menu');
    menu.hidden = true;
    wrap.appendChild(menu);
    nav.appendChild(wrap);
    if (bar.reason) {
      var reason = el('div', 'reason');
      reason.appendChild(el('b', null, bar.because || ''));
      reason.appendChild(document.createTextNode(' ' + bar.reason));
      reason.title = reason.textContent;
      nav.appendChild(reason);
    }
    fitSlots();
    if (root.requestAnimationFrame) root.requestAnimationFrame(fitSlots);
  }

  /* Hide every chip that does not fit, and list those chips in the ⋯ menu. */
  function fitSlots() {
    var nav = document.getElementById('bar');
    var row = nav ? nav.getElementsByClassName('slots')[0] : null;
    if (!row || !barModel) return;
    var chips = [];
    var nodes = row.getElementsByClassName('slot');
    for (var i = 0; i < nodes.length; i++) chips.push(nodes[i]);
    var help = document.getElementById('help');
    for (var n = 0; n < chips.length; n++) {
      chips[n].hidden = false;
      chips[n].classList.remove('wrap');
      chips[n].style.maxWidth = '';
    }
    var gap = 6;
    if (root.getComputedStyle) {
      var parsed = parseFloat(root.getComputedStyle(row).columnGap);
      if (parsed >= 0) gap = parsed;
    }
    var widths = [];
    for (var w = 0; w < chips.length; w++) widths.push(chips[w].offsetWidth);
    var helpW = help ? help.offsetWidth : 0;
    var count = Forsk.visibleSlots(row.clientWidth, widths, helpW, gap);
    var hidden = [];
    for (var h = 0; h < chips.length; h++) {
      if (h >= count) {
        chips[h].hidden = true;
        hidden.push(chips[h]);
      }
    }
    var budget = row.clientWidth - helpW - (helpW > 0 ? gap : 0);
    if (chips.length && widths[0] > budget && budget > 0) {
      chips[0].classList.add('wrap');
      chips[0].style.maxWidth = Math.floor(budget) + 'px';
    }
    fillSlotMenu(hidden);
  }

  function fillSlotMenu(hidden) {
    var menu = document.getElementById('slot-menu');
    if (!menu) return;
    var open = !menu.hidden;
    while (menu.firstChild) menu.removeChild(menu.firstChild);
    hidden.forEach(function (chip) {
      var id = chip.getAttribute('data-slot');
      var item = el('button', null, chip.textContent);
      item.type = 'button';
      item.setAttribute('role', 'menuitem');
      item.addEventListener('click', function () {
        closeMenus(false);
        sender.send({ kind: 'action', id: id });
      });
      menu.appendChild(item);
    });
    if (hidden.length && barModel && barModel.help) {
      var helpItem = el('button', 'split', barModel.help.title || barModel.help.label);
      helpItem.type = 'button';
      helpItem.setAttribute('role', 'menuitem');
      helpItem.addEventListener('click', function () {
        closeMenus(false);
        sender.send({ kind: 'help' });
      });
      menu.appendChild(helpItem);
    }
    var help = document.getElementById('help');
    if (!hidden.length) {
      menu.hidden = true;
      if (help) {
        help.setAttribute('aria-expanded', 'false');
        help.removeAttribute('aria-haspopup');
        help.removeAttribute('aria-controls');
      }
      return;
    }
    if (help) {
      help.setAttribute('aria-haspopup', 'menu');
      help.setAttribute('aria-controls', 'slot-menu');
    }
    menu.hidden = !open;
  }

  function renderSheet(help) {
    var sheet = document.getElementById('sheet');
    var body = document.getElementById('sheet-body');
    var keep = body && help ? body.scrollTop : 0;
    while (sheet.firstChild) sheet.removeChild(sheet.firstChild);
    sheet.className = help ? 'sheet' : '';
    if (!help) return;
    body = el('div');
    body.id = 'sheet-body';
    body.appendChild(el('h2', null, help.title || ''));
    (help.groups || []).forEach(function (group) {
      body.appendChild(el('h3', null, group.title));
      var pills = el('div', 'pills');
      group.actions.forEach(function (action) {
        pills.appendChild(pill(action.label, false, function () { sender.send({ kind: 'action', id: action.id, from: 'card' }); }));
      });
      body.appendChild(pills);
    });
    (help.hints || []).forEach(function (hint) { body.appendChild(el('div', 'hint', hint)); });
    var bar = el('div');
    bar.id = 'sheet-bar';
    bar.hidden = true;
    bar.setAttribute('aria-hidden', 'true');
    var thumb = el('div');
    thumb.id = 'sheet-thumb';
    bar.appendChild(thumb);
    sheet.appendChild(body);
    sheet.appendChild(bar);
    body.scrollTop = keep;
    body.addEventListener('scroll', function () {
      sheetThumb.sync();
      sheetThumb.reveal();
    });
    sheetThumb.bind();
    sheetThumb.sync();
  }

  /* A press outside the open ⋯ sheet closes it, as outside a menu. The ⋯ button and its menu toggle it themselves. */
  Forsk.closesSheet = function (inSheet, onHelp, inSlotMenu) {
    return !inSheet && !onHelp && !inSlotMenu;
  };

  function applyPrefill(prefill) {
    if (!prefill || prefill.n <= lastPrefill) return;
    lastPrefill = prefill.n;
    var q = document.getElementById('q');
    q.value = prefill.text;
    grow();
    q.focus();
    q.setSelectionRange(prefill.start, prefill.end);
  }

  /* Open forms, newest first. They draw in the pin, not in the thread. */
  function openForms() {
    var forms = [];
    (model.thread || []).forEach(function (entry) {
      if (Forsk.isForm(entry)) forms.push(entry);
    });
    forms.reverse();
    return forms;
  }

  function formKey(forms) {
    return forms.map(function (item) {
      return (item.id || '') + '|' + (item.question || '') + '|' + (item.note || '') + '|' + (item.image || '') + '|'
        + JSON.stringify(item.fields || []) + '|' + JSON.stringify(item.pills || []) + '|' + JSON.stringify(item.rows || []);
    }).join('\n');
  }

  function renderPin(forms) {
    var pin = document.getElementById('pin');
    if (!pin) return;
    if (!forms.length) {
      pinKey = '';
      pinNewest = '';
      pin.hidden = true;
      while (pin.firstChild) pin.removeChild(pin.firstChild);
      return;
    }
    var key = formKey(forms);
    if (key === pinKey && pin.firstChild) {
      pin.hidden = false;
      return;
    }
    var newest = forms[0].id || '';
    var focus = newest !== pinNewest;
    pinKey = key;
    pinNewest = newest;
    while (pin.firstChild) pin.removeChild(pin.firstChild);
    pin.hidden = false;
    forms.forEach(function (form, index) {
      pin.appendChild(card(form, { pin: true, focus: focus && index === 0 }));
    });
  }

  function guideBlock(spec) {
    var box = el('div', 'guide');
    var lines = spec.lines || [];
    for (var i = 0; i < lines.length; i++) box.appendChild(el('p', null, lines[i]));
    var dismiss = el('button', 'guide-x', 'Dismiss');
    dismiss.addEventListener('click', function () {
      sender.send({ kind: 'action', id: spec.dismiss || 'guide.dismiss' });
    });
    box.appendChild(dismiss);
    return box;
  }

  Forsk.render = function (next) {
    model = next || {};
    var thread = document.getElementById('thread');
    document.getElementById('target').textContent = model.target || '';
    document.getElementById('status').textContent = model.status || '';
    // A reader who has not moved stays with the newest line, including after a resize.
    var follow = followLatest || Forsk.nearEnd(thread.scrollHeight, thread.scrollTop, thread.clientHeight);
    followLatest = follow;
    var keep = thread.scrollTop;
    var seen = keep;
    var placed = keep;
    var forms = openForms();
    threadThumb.quiet = true;
    try {
      var skip = {};
      forms.forEach(function (form) { if (form.id) skip[form.id] = 1; });
      while (thread.firstChild) thread.removeChild(thread.firstChild);
      if (model.guide && model.guide.lines && model.guide.lines.length) thread.appendChild(guideBlock(model.guide));
      (model.thread || []).forEach(function (entry) {
        if (entry.id && skip[entry.id]) return;
        thread.appendChild(item(entry));
      });
      if (model.busy && model.busy.text) thread.appendChild(busy(model.busy));
      if (follow) thread.scrollTop = thread.scrollHeight;
      else thread.scrollTop = keep;
      placed = thread.scrollTop;
      syncThreadThumb();
      if (placed !== seen) revealThreadThumb();
    } finally {
      threadThumb.quiet = false;
    }
    // The write above lands about 15px short. Layout has finished on the next turn.
    if (follow) root.setTimeout(function () {
      if (Math.abs(thread.scrollTop - placed) > 2) return;
      var seenLate = thread.scrollTop;
      thread.scrollTop = thread.scrollHeight;
      followLatest = true;
      syncThreadThumb();
      if (thread.scrollTop !== seenLate) revealThreadThumb();
    }, 0);
    else followLatest = Forsk.nearEnd(thread.scrollHeight, thread.scrollTop, thread.clientHeight);
    renderPin(forms);
    // The bar never reorders under the pointer: it waits until the pointer leaves.
    if (!barHovered) renderBar(model.bar);
    renderRole(model);
    renderSettings(model);
    renderSheet(model.help);
    applyPrefill(model.prefill);
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
    var slotMenu = document.getElementById('slot-menu');
    if (slotMenu && !slotMenu.hidden) return slotMenu;
    return null;
  }

  function holds(id, node) {
    var box = document.getElementById(id);
    return !!(box && node && (box === node || box.contains(node)));
  }

  function closeMenus(back) {
    document.getElementById('role-menu').hidden = true;
    document.getElementById('more-menu').hidden = true;
    var slotMenu = document.getElementById('slot-menu');
    if (slotMenu) slotMenu.hidden = true;
    document.getElementById('role-pill').setAttribute('aria-expanded', 'false');
    document.getElementById('more').setAttribute('aria-expanded', 'false');
    var helpBtn = document.getElementById('help');
    if (helpBtn) helpBtn.setAttribute('aria-expanded', 'false');
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
      if (e.key === 'Tab') document.documentElement.setAttribute('data-kbd', '');
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
      var cardId = openCard();
      Forsk.handleKey(e, { composer: e.target === q, card: cardId, cancel: Forsk.cardCancels(model, cardId), text: q.value }, send);
    }, true);
    document.getElementById('composer').addEventListener('submit', function (e) {
      e.preventDefault();
      var text = q.value.replace(/^\s+|\s+$/g, '');
      if (text) send({ kind: 'send', text: text });
      q.focus();
    });
    document.getElementById('add').addEventListener('click', function () { sender.send({ kind: 'action', id: 'file.import' }); });
    var threadEl = document.getElementById('thread');
    threadThumb.bind();
    threadEl.addEventListener('scroll', function () {
      followLatest = Forsk.nearEnd(threadEl.scrollHeight, threadEl.scrollTop, threadEl.clientHeight);
      if (threadThumb.quiet) return;
      syncThreadThumb();
      revealThreadThumb();
    });
    // Shrinking the panel does not fire a scroll. A reader who was on the newest line still is.
    root.addEventListener('resize', function () {
      fitSlots();
      syncThreadThumb();
      if (!followLatest) return;
      threadEl.scrollTop = threadEl.scrollHeight;
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
      document.documentElement.removeAttribute('data-kbd');
      if (model && model.help && Forsk.closesSheet(holds('sheet', e.target), holds('help', e.target), holds('slot-menu', e.target)))
        sender.send({ kind: 'help' });
      if (!openMenuEl()) return;
      if (holds('role-menu', e.target) || holds('role-pill', e.target) || holds('more-menu', e.target) || holds('more', e.target) || holds('slot-menu', e.target) || holds('help', e.target)) return;
      closeMenus(false);
    });
    document.addEventListener('pointerdown', function () {
      document.documentElement.removeAttribute('data-kbd');
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
