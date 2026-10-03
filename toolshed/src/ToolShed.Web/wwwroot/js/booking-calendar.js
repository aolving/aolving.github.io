/*
 * The booking calendar on a tool's page.
 *
 * You pick the loan dates on one calendar: the first day you click is the start, the second is the last
 * day, and you can never go backwards. A day that could not make a valid booking cannot be clicked at all
 * (the past, days already requested or booked, days before the start, an end beyond the owner's longest
 * loan, or one that would run across someone else's booking), so you are never offered a choice the
 * portal would then refuse. The server checks everything again regardless.
 *
 * This is progressive enhancement. Without scripts the page shows two plain date fields. The rules live
 * in createSelection(), which touches no DOM so it can be tested on its own (tests/js), and mirrors
 * DateSelection in ToolShed.Client, which the mobile app uses.
 *
 * Everything on the page is built with createElement and textContent, never innerHTML.
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.BookingCalendar = factory();
  }
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var HORIZON_DAYS = 180;
  var WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  var MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

  // ---- dates are 'YYYY-MM-DD' strings, which sort correctly as text. Arithmetic is done in UTC so a
  // daylight-saving change can never shift a day.
  function parse(s) {
    var p = s.split('-');
    return new Date(Date.UTC(+p[0], +p[1] - 1, +p[2]));
  }

  function format(d) {
    var m = d.getUTCMonth() + 1;
    var day = d.getUTCDate();
    return d.getUTCFullYear() + '-' + (m < 10 ? '0' : '') + m + '-' + (day < 10 ? '0' : '') + day;
  }

  function addDays(s, n) {
    var d = parse(s);
    d.setUTCDate(d.getUTCDate() + n);
    return format(d);
  }

  function diffDays(a, b) {
    return Math.round((parse(b) - parse(a)) / 86400000);
  }

  function describe(s) {
    var d = parse(s);
    return WEEKDAYS[d.getUTCDay()] + ' ' + d.getUTCDate() + ' ' + MONTHS[d.getUTCMonth()];
  }

  // ---- the rules (no DOM) ---------------------------------------------------------------------------

  /**
   * @param {{today: string, maxLoanDays: number, held: Array<{start: string, end: string, approved?: boolean}>}} options
   */
  function createSelection(options) {
    var today = options.today;
    var maxLoanDays = Math.max(1, options.maxLoanDays | 0);
    var held = (options.held || []).slice().sort(function (a, b) { return a.start < b.start ? -1 : a.start > b.start ? 1 : 0; });
    var lastBookable = addDays(today, HORIZON_DAYS);
    var sel = { start: null, end: null };

    function heldRangeAt(day) {
      for (var i = 0; i < held.length; i++) {
        if (held[i].start <= day && day <= held[i].end) return held[i];
      }
      return null;
    }

    function latestEnd() {
      if (sel.start === null) return null;
      var latest = addDays(sel.start, maxLoanDays - 1);
      for (var i = 0; i < held.length; i++) {
        if (held[i].start > sel.start) {
          var before = addDays(held[i].start, -1);
          return before < latest ? before : latest;
        }
      }
      return latest;
    }

    function canPickStart(day) {
      return day >= today && day <= lastBookable && heldRangeAt(day) === null;
    }

    function canPickEnd(day) {
      return day >= sel.start && day <= latestEnd() && heldRangeAt(day) === null;
    }

    function choosingEnd() {
      return sel.start !== null && sel.end === null;
    }

    return {
      today: today,
      get start() { return sel.start; },
      get end() { return sel.end; },
      isComplete: function () { return sel.start !== null && sel.end !== null; },
      days: function () { return sel.start !== null && sel.end !== null ? diffDays(sel.start, sel.end) + 1 : 0; },
      heldRangeAt: heldRangeAt,
      latestEnd: latestEnd,
      lastBookable: lastBookable,
      choosingEnd: choosingEnd,

      // With no start yet (or a finished selection, which a click restarts) it must be a free day in the
      // booking window. While choosing the last day it must also not be before the start, not be longer
      // than the owner allows, and not run into someone else's booking.
      canPick: function (day) { return choosingEnd() ? canPickEnd(day) : canPickStart(day); },

      pick: function (day) {
        if (!this.canPick(day)) return false;
        if (choosingEnd()) {
          sel.end = day;
        } else {
          // The first date is always the start, including after a finished selection.
          sel.start = day;
          sel.end = null;
        }
        return true;
      },

      clear: function () { sel.start = null; sel.end = null; },

      summary: function () {
        if (sel.start === null) return 'Pick the first day of the loan.';
        if (sel.end === null) return 'Collect on ' + describe(sel.start) + '. Now pick the last day. Earlier days cannot be chosen.';
        return sel.start === sel.end
          ? describe(sel.start) + ' (1 day)'
          : describe(sel.start) + ' to ' + describe(sel.end) + ' (' + this.days() + ' days)';
      }
    };
  }

  // ---- the calendar on the page ---------------------------------------------------------------------

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function init(container) {
    var held;
    try {
      held = JSON.parse(container.getAttribute('data-held') || '[]').map(function (h) {
        return { start: h[0], end: h[1], approved: !!h[2] };
      });
    } catch (e) {
      held = [];
    }

    var selection = createSelection({
      today: container.getAttribute('data-today'),
      maxLoanDays: parseInt(container.getAttribute('data-max-days'), 10) || 14,
      held: held
    });

    var mount = container.querySelector('[data-booking-widget]');
    var startInput = document.getElementById(container.getAttribute('data-start-input'));
    var endInput = document.getElementById(container.getAttribute('data-end-input'));
    var form = container.querySelector('form');
    var submit = container.querySelector('[data-booking-submit]');
    if (!mount || !startInput || !endInput) return;

    // From here the calendar is the date picker: hide the plain fields (they still carry the values to
    // the server) and let the calendar decide when the form can be sent.
    container.classList.add('booking-js');
    if (form) form.setAttribute('novalidate', 'novalidate');
    var strip = document.querySelector('[data-availability-strip]');
    if (strip) strip.hidden = true;

    var firstMonth = selection.today.slice(0, 7);
    var lastMonth = selection.lastBookable.slice(0, 7);
    var month = firstMonth;

    function sync() {
      startInput.value = selection.isComplete() ? selection.start : '';
      endInput.value = selection.isComplete() ? selection.end : '';
      if (submit) submit.disabled = !selection.isComplete();
    }

    function addMonths(ym, n) {
      var p = ym.split('-');
      var d = new Date(Date.UTC(+p[0], +p[1] - 1 + n, 1));
      return format(d).slice(0, 7);
    }

    function render() {
      mount.textContent = '';
      var wrap = el('div', 'cal');

      var head = el('div', 'cal-head');
      var prev = el('button', 'cal-nav', '‹');
      prev.type = 'button';
      prev.setAttribute('aria-label', 'Previous month');
      prev.disabled = month <= firstMonth;
      prev.addEventListener('click', function () { month = addMonths(month, -1); render(); });
      var next = el('button', 'cal-nav', '›');
      next.type = 'button';
      next.setAttribute('aria-label', 'Next month');
      next.disabled = month >= lastMonth;
      next.addEventListener('click', function () { month = addMonths(month, 1); render(); });
      var monthStart = parse(month + '-01');
      head.appendChild(prev);
      head.appendChild(el('strong', 'cal-title', MONTH_NAMES[monthStart.getUTCMonth()] + ' ' + monthStart.getUTCFullYear()));
      head.appendChild(next);
      wrap.appendChild(head);

      var grid = el('div', 'cal-grid');
      ['M', 'T', 'W', 'T', 'F', 'S', 'S'].forEach(function (d) {
        var h = el('span', 'cal-wd', d);
        h.setAttribute('aria-hidden', 'true');
        grid.appendChild(h);
      });

      var blanks = (monthStart.getUTCDay() + 6) % 7;
      for (var b = 0; b < blanks; b++) {
        var gap = el('span', 'cal-gap');
        gap.setAttribute('aria-hidden', 'true');
        grid.appendChild(gap);
      }

      var cursor = month + '-01';
      while (cursor.slice(0, 7) === month) {
        grid.appendChild(dayButton(cursor));
        cursor = addDays(cursor, 1);
      }
      wrap.appendChild(grid);

      var legend = el('p', 'cal-legend');
      [['cal-key-booked', 'Booked'], ['cal-key-requested', 'Requested'], ['cal-key-picked', 'Your dates']].forEach(function (k) {
        var item = el('span', 'cal-key ' + k[0], k[1]);
        legend.appendChild(item);
      });
      wrap.appendChild(legend);

      var status = el('p', 'cal-status', selection.summary());
      status.setAttribute('role', 'status');
      status.setAttribute('aria-live', 'polite');
      wrap.appendChild(status);

      if (selection.start !== null) {
        var clear = el('button', 'link-button', 'Clear dates');
        clear.type = 'button';
        clear.addEventListener('click', function () { selection.clear(); sync(); render(); });
        wrap.appendChild(clear);
      }

      mount.appendChild(wrap);
    }

    function dayButton(day) {
      var held = selection.heldRangeAt(day);
      var pickable = selection.canPick(day);
      var inRange = selection.start !== null && day >= selection.start &&
        (selection.end !== null ? day <= selection.end : day === selection.start);

      var classes = ['cal-day'];
      if (held) classes.push(held.approved ? 'cal-booked' : 'cal-requested');
      else if (!pickable) classes.push('cal-off');
      if (day === selection.today) classes.push('cal-today');
      if (inRange) classes.push('cal-range');
      if (day === selection.start) classes.push('cal-start');
      if (day === selection.end) classes.push('cal-end');

      var button = el('button', classes.join(' '), String(parse(day).getUTCDate()));
      button.type = 'button';
      button.disabled = !pickable;
      button.setAttribute('data-day', day);

      var state = held ? (held.approved ? 'booked' : 'requested') : !pickable ? 'not available' : 'free';
      if (day === selection.start) state = 'start of your loan';
      else if (day === selection.end) state = 'last day of your loan';
      button.setAttribute('aria-label', describe(day) + ', ' + state);
      if (inRange) button.setAttribute('aria-pressed', 'true');

      button.addEventListener('click', function () {
        if (selection.pick(day)) {
          sync();
          render();
          // Keep the keyboard where it was after the calendar is redrawn.
          var again = mount.querySelector('[data-day="' + day + '"]');
          if (again && !again.disabled) again.focus();
        }
      });
      return button;
    }

    sync();
    render();
  }

  function start() {
    var nodes = document.querySelectorAll('[data-booking-calendar]');
    for (var i = 0; i < nodes.length; i++) init(nodes[i]);
  }

  if (typeof document !== 'undefined') {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
  }

  return { createSelection: createSelection, addDays: addDays, diffDays: diffDays, parse: parse, format: format, describe: describe, init: init, HORIZON_DAYS: HORIZON_DAYS };
});
